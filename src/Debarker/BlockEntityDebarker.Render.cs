using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

/// <summary>
/// The master's client render (AutoDebarkerPlan E07), a port of <see cref="BlockEntityChopper"/>'s Render partial.
/// T07.02: the static frame baked into the chunk mesh (<see cref="OnTesselation"/>) and the spindle and installed
/// movers drawn by <see cref="DebarkerRenderer"/>. T07.03: their poses from the network angle and the lift
/// (<see cref="GetPartMatrices"/>, <see cref="DebarkerAnim"/>). T07.04: the log painted by its species, turned by the
/// client's estimate of the progress, its bark strips going out one by one (<see cref="UpdateLogMesh"/>,
/// <see cref="AdvanceClientProgress"/>). T08.01: the interaction help refreshed after a sync
/// (<see cref="RefreshInteractionHelpOnChange"/>).
/// Unlike the chopper, the frame mesh is built on the main thread in <see cref="InitRenderer"/>: OnTesselation runs on
/// the tesselation thread, where neither ObjectCacheUtil (memory vs-objectcache-not-threadsafe-dies-with-clientmain)
/// nor capi.Tesselator (the doc of BlockEntity.OnTesselation) is safe.
/// </summary>
public partial class BlockEntityDebarker
{
    static readonly AssetLocation ShapeLoc = new("immersivewoodworking", "shapes/block/debarker/debarker.json");

    ICoreClientAPI? capi;
    volatile MeshData? frameMesh;
    DebarkerRenderer? renderer;
    (bool Drive, bool Disc, bool Feed, AssetLocation? Knives) renderedParts;

    // T07.03 clocks, client only. The signed clock has no field: GetPartMatrices reads the shaft's phase straight off
    // AngleRad, ungated, so the coupling keeps the phase of the vanilla axle it meets.
    //
    // animAngleFwd is the rectified clock of the reducer and the rollers (Part A). It grows by the shaft's turn while
    // EffectiveDebarkSpeed() > 0, whichever way the line turns, and stands otherwise. It folds at DebarkerFwdWrapBound,
    // where the geared read animAngleFwd × DebarkerWorkScale has turned every geared part a whole number of times
    // (DebarkerAnim, invariant I6). animAnglePrev is the previous frame's AngleRad, taken on every frame so that a
    // restart banks no jump; animAnglePrimed keeps the first frame from differencing against the 0 it starts at.
    float animAngleFwd;
    float animAnglePrev;
    bool animAnglePrimed;

    // The lift: 0 with a log seated (the model's pose), 1 for an empty machine with the lift frame up (decision В11).
    // It runs toward InputEmpty linearly over DebarkerAnim.LiftTravelSeconds and starts at its target in InitRenderer, so
    // a chunk load shows the frame where it belongs. No gate on power or parts: it pictures the input slot, and the
    // server never waits for it. T07.04 seats the log on the frame's arms by it.
    float lift;

    // T07.04: the client's estimate of how far the loaded log is stripped, 0..1, the log's progress clock. The server
    // syncs debarkProgress only on its 5 % grid, so the estimate runs on its own every frame by the server's step and
    // gate. A sync of the same log only pulls it forward (AdvanceClientProgress); a sync of another log restarts it
    // (ReconcileClientProgress). It stops short of 1: the last strip goes out when the server's finish empties the slot
    // and the log goes with it. The block info's percent reads it too (T08.03). Client only.
    float clientProgress;
    const float ClientProgressCeiling = 0.9999f;

    // The catch-up rate, per second, accepted by the author at T07.07. Each frame the estimate closes dt times this of
    // its lag behind the synced progress, all of it at most: an exponential approach with a 0.1 s time constant, 90 %
    // of a lag in about a fifth of a second. 1 % of a log is 32° of its turn, so a lag is eased in rather than jumped.
    const float ClientCatchUpPerSecond = 10f;

    // T07.04: the log on the input. logGroups are the CPU meshes from the process cache, [0] the core and [k] bark strip
    // k (DebarkerParts.LogGroupFor), painted by the loaded log's species. They live on the BE rather than in the renderer,
    // which every part change rebuilds. The GPU gets one mesh, the core merged with the strips still on, re-merged only
    // when the number of strips out changes (SyncLogMesh): the sawmill's workpiece since AuditFixPlan T05.07.
    MeshData?[]? logGroups;
    // The bark strips' #wood position in the block atlas, the colour source of the chips (T07.05). Block.GetRandomColor
    // with UP would give the end grain: a picked-up log is the -ud variant, whose "up" is the treetrunk rings. Null
    // with no log or when the bark texture is unknown, and then no chips fly.
    TextureAtlasPosition? barkTexture;
    MultiTextureMeshRef? logMeshRef;
    int logMeshHidden = -1; // the number of strips out when logMeshRef was merged, -1 before the first merge
    string? logMeshKey;

    // The facing rotation R about the block centre and its inverse, matching the rotateY the movers are tesselated with
    // (GetPartMesh); null on a south frame. Set in InitRenderer.
    float[]? facingMat;
    float[]? facingMatInv;

    float[][]? animMats;
    // The last pose, handed back while no clock moved (a stopped line, a settled lift) and the work scale held.
    float[][]? matsCache;
    float cacheSigned, cacheFwd, cacheLift, cacheProgress, cacheWorkScale;

    void InitRenderer(ICoreClientAPI client)
    {
        capi = client;
        frameMesh = GetPartMesh(client, DebarkerParts.FrameIndex);
        // A chunk build that saw the BE before this mesh was published needs another pass.
        client.World.BlockAccessor.MarkBlockDirty(Pos);
        BuildFacingMatrices((int)Block.Shape.rotateY);
        lift = InputEmpty ? 1f : 0f;
        // Like the lift, a chunk load shows the log where the server left it, with no catch-up spin from 0.
        clientProgress = SyncedProgress;
        // The server's config can land after this BE is up: the config packet and the chunk are not ordered.
        // DisposeRenderer unsubscribes.
        modSystem ??= client.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        modSystem.ClientConfigApplied += OnClientConfigApplied;
        // T08.01: the help signature's baseline. On a load FromTreeAttributes ran before this with capi still null, so the
        // first sync compares against this.
        lastHelpSig = HelpSignature();
        // T07.06: the mechanism bed. The engine drops the listener on removal and unload; the scraping runs per frame.
        RegisterGameTickListener(OnBedSoundTick, 100);
        UpdateRenderer();
        UpdateLogMesh();
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        base.OnTesselation(mesher, tesselator);
        // A clone per chunk build: BlockEntity.OnTesselation's doc forbids reusing the MeshData instance. The cached
        // mesh itself only comes from InitRenderer on the main thread; nothing here touches the cache or a tesselator.
        if (frameMesh is { } mesh) mesher.AddMeshData(mesh.Clone());
        return true; // The default block shape includes the spindle, which has its own renderer mesh.
    }

    bool IsMoverVisible(DebarkerPartGroup group) => group switch
    {
        // The shaft and coupling belong to the bare frame yet turn with the network even on an incomplete machine
        // (decision В5, EffectiveDebarkSpeed's doc in the Power partial), so they are a mover with no part gate.
        DebarkerPartGroup.Spindle => true,
        DebarkerPartGroup.Disc => hasDisc,
        DebarkerPartGroup.Knives => knifeStack?.Item != null,
        DebarkerPartGroup.Drive => hasDrive,
        DebarkerPartGroup.Feed => hasFeed,
        _ => false // Static is in the chunk mesh; the Workpiece log has a mesh of its own (UpdateLogMesh)
    };

    void UpdateRenderer()
    {
        if (capi == null) return;
        var state = (hasDrive, hasDisc, hasFeed, knifeStack?.Item?.Code);
        if (renderer != null && renderedParts == state) return;

        var movers = new List<(int Rule, MeshData Mesh)>();
        for (int i = 0; i < DebarkerParts.Rules.Length; i++)
        {
            var group = DebarkerParts.Rules[i].Group;
            if (!IsMoverVisible(group)) continue;
            Item? knives = group == DebarkerPartGroup.Knives ? knifeStack?.Item : null;
            if (GetPartMesh(capi, i, knives) is { VerticesCount: > 0 } mesh) movers.Add((i, mesh));
        }

        renderer?.Dispose();
        // Cleared first so a throwing constructor leaves no disposed renderer in the field and the next call rebuilds.
        // A throw from a sync or Initialize is caught by the engine; one from the clicker's prediction is not.
        renderer = null;
        renderer = new DebarkerRenderer(capi, this, movers);
        renderedParts = state;
    }

    MeshData? GetPartMesh(ICoreClientAPI client, int ruleIndex, Item? knives = null)
    {
        int rotY = (int)Block.Shape.rotateY;
        string key = $"iw-debarker-{DebarkerParts.Rules[ruleIndex].Id}-{rotY}";
        if (knives != null) key += "-" + knives.Code;

        return ObjectCacheUtil.GetOrCreate(client, key, () =>
        {
            Shape? source = SourceShape(client);
            if (source == null) return null;

            Shape part = source.Clone();
            BlankElements(part.Elements, name => DebarkerParts.IndexFor(name) == ruleIndex);
            // All knife faces use #metal. Only that mesh may read the installed item's block-atlas texture.
            ITexPositionSource textures = knives == null
                ? client.Tesselator.GetTextureSource(Block)
                : new LaidContentItemTexSource(client, knives);
            client.Tesselator.TesselateShape("debarker", part, out MeshData mesh, textures, new Vec3f(0, rotY, 0));
            return mesh;
        });
    }

    // The parsed model, shared: callers clone it before they blank anything.
    static Shape? SourceShape(ICoreClientAPI client)
        => ObjectCacheUtil.GetOrCreate(client, "iw-debarker-shape", () => Shape.TryGet(client, ShapeLoc));

    static void BlankElements(ShapeElement[] elements, System.Func<string?, bool> keep)
    {
        foreach (var element in elements)
        {
            // Deserialization has already resolved the face dictionary into this six-slot array.
            if (!keep(element.Name)) element.FacesResolved = new ShapeElementFace[6];
            if (element.Children != null) BlankElements(element.Children, keep);
        }
    }

    void BuildFacingMatrices(int rotYDeg)
    {
        if (rotYDeg == 0) { facingMat = null; facingMatInv = null; return; }
        facingMat = new Matrixf().Translate(0.5f, 0.5f, 0.5f).RotateYDeg(rotYDeg).Translate(-0.5f, -0.5f, -0.5f).Values;
        facingMatInv = new Matrixf().Translate(0.5f, 0.5f, 0.5f).RotateYDeg(-rotYDeg).Translate(-0.5f, -0.5f, -0.5f).Values;
    }

    /// <summary>
    /// One matrix per rule of <see cref="DebarkerParts.Rules"/>, by index, for this frame. The renderer calls it once a
    /// frame on the opaque pass (the shadow passes reuse the last pose) with that frame's <paramref name="deltaTime"/>.
    ///
    /// Four clocks (spec T07.01). The signed one is the shaft's phase, <c>GameMath.Mod(AngleRad, 2π)</c> times
    /// <see cref="FacingDriveSign"/>: the spindle, the disc and the knives turn 1:1 with it whenever the network turns,
    /// on an incomplete machine too (decision В5). The rectified one, <see cref="animAngleFwd"/> times
    /// <c>DebarkerWorkScale</c>, turns the reducer and the rollers, which stand while <see cref="EffectiveDebarkSpeed"/>
    /// is 0: past a broken knife set or a missing feed the shaft and the disc turn while the big gear on that shaft
    /// stands (author 2026-11-22). The lift moves the pedal, the rod, the cam and the lift frame, and seats the log on the
    /// frame's arms. The progress estimate (<see cref="clientProgress"/>, T07.04) turns the log about its axis.
    /// </summary>
    internal float[][] GetPartMatrices(float deltaTime)
    {
        float angle = AngleRad;
        AdvanceClocks(angle, deltaTime);
        AdvanceClientProgress(deltaTime);
        float signed = GameMath.Mod(angle, GameMath.TWOPI) * FacingDriveSign();
        float workScale = Config.DebarkerWorkScale;
        if (matsCache != null && signed == cacheSigned && animAngleFwd == cacheFwd && lift == cacheLift
            && clientProgress == cacheProgress && workScale == cacheWorkScale) return matsCache;

        cacheSigned = signed;
        cacheFwd = animAngleFwd;
        cacheLift = lift;
        cacheProgress = clientProgress;
        cacheWorkScale = workScale;
        matsCache = DebarkerAnim.BuildMatricesFacing(animMats ??= DebarkerAnim.CreateBuffers(), signed,
            animAngleFwd * workScale, lift, clientProgress, facingMat, facingMatInv);
        return matsCache;
    }

    /// <summary>
    /// Moves the rectified clock and the lift by one frame. The rectified clock steps by the shaft's turn since the
    /// previous frame, the difference of <paramref name="ang"/> reduced to [−π, π) by <c>GameMath.AngleRadDistance</c>,
    /// so it keeps pace with the angle the vanilla axles show. The siblings fold that difference once, which holds while
    /// the angle only wraps by 2π; a node whose rotation reverses when its network is rebuilt flips its phase from x to
    /// 2π − x (<c>BEBehaviorMPBase.IsRotationReversed</c>), a jump a single fold leaves at up to 3π. A step past π
    /// aliases either way, so above π a frame the step comes from the speed, <c>NetworkSpeed·5·dt</c>: the Nyquist
    /// hybrid of the chopper's and the sawmill's AdvanceAnimAngle. Either direction of the line moves the clock forward.
    /// The fold at <c>DebarkerFwdWrapBound</c> loops, because at the top of the speed band the bound is about 0.75 rad
    /// and one fast frame can carry several bounds.
    /// </summary>
    void AdvanceClocks(float ang, float dt)
    {
        if (!animAnglePrimed) { animAnglePrev = ang; animAnglePrimed = true; }
        float speed = EffectiveDebarkSpeed(); // NetworkSpeed itself above the gate
        if (speed > 0f)
        {
            float d = GameMath.AngleRadDistance(animAnglePrev, ang);
            float speedStep = speed * ShaftRadPerNetworkSpeedSecond * dt;
            animAngleFwd += speedStep > GameMath.PI ? speedStep : System.Math.Abs(d);
            float bound = Config.DebarkerFwdWrapBound; // positive across the whole speed band (Sanitize)
            while (animAngleFwd >= bound) animAngleFwd -= bound;
        }
        animAnglePrev = ang;

        float step = dt / DebarkerAnim.LiftTravelSeconds;
        lift = GameMath.Clamp(InputEmpty ? 1f : 0f, lift - step, lift + step);
    }

    /// <summary>The synced progress as the estimate may show it: below <see cref="ClientProgressCeiling"/>, and 0 with no
    /// log in.</summary>
    float SyncedProgress => InputEmpty ? 0f : Math.Min(debarkProgress, ClientProgressCeiling);

    /// <summary>
    /// Moves the estimate of the log's progress by one frame (T07.04, spec T07.01). While <see cref="CanDebark"/> holds it
    /// takes the server tick's own step, <see cref="DebarkAdvance"/> of this frame's <paramref name="dt"/>, so between
    /// syncs it strips as fast as the server does. It never moves back here. Left behind the last synced
    /// <c>debarkProgress</c> (the line sped up, a sync arrived late), it closes <c>dt ×</c>
    /// <see cref="ClientCatchUpPerSecond"/> of the lag each frame, also while the gate is shut, since the server is known
    /// to have got that far. It stops at <see cref="ClientProgressCeiling"/> and waits there for the finish to empty the
    /// slot. The estimate, <see cref="SpawnBarkChips"/> (T07.05) and the scraping sound (<see cref="UpdateScrapeSound"/>,
    /// T07.06) share the frame's single <see cref="CanDebark"/> check. An empty input skips the check, and the scraping
    /// hears that as a stop.
    ///
    /// It stands while the game is paused, as vanilla <c>MechanicalPowerMod.OnRenderFrame</c> stops turning the client's
    /// networks: a singleplayer pause suspends the built-in server, so its progress stands, while the client keeps
    /// rendering with a real <paramref name="dt"/> and the network keeps its last speed. Without the check the log would
    /// strip on through the pause menu, and no sync would pull it back. ParticlePoolQuads.OnNewFrame also
    /// stops the main-thread particle pool during pause: chips emitted then would wait unseen and fly out in one clump
    /// once the game resumes. The scraping stands with them: the clip that is playing plays out and the next one waits for
    /// the resume. The bed (<see cref="OnBedSoundTick"/>) plays on, as the chopper's does.
    /// </summary>
    void AdvanceClientProgress(float dt)
    {
        if (capi is not { IsGamePaused: false } client) return;
        SampleChipFrameTime(dt); // A newly loaded log inherits the cadence observed while the machine was empty.
        bool debarking = !InputEmpty && CanDebark();
        UpdateScrapeSound(client, dt, debarking);
        if (InputEmpty) return;
        if (debarking)
        {
            clientProgress += DebarkAdvance(dt);
            SpawnBarkChips(client, dt);
        }
        float synced = SyncedProgress;
        if (clientProgress < synced) clientProgress += (synced - clientProgress) * Math.Min(1f, dt * ClientCatchUpPerSecond);
        if (clientProgress > ClientProgressCeiling) clientProgress = ClientProgressCeiling;
    }

    /// <summary>
    /// Restarts the estimate after a sync that belongs to another log (T07.04). <see cref="FromTreeAttributes"/> passes the
    /// progress it held before the sync. Within one log the server's progress only grows (the tick adds, and only a load,
    /// a take, a scrap, a break or the finish zero it), so a synced value below the one before, a 0, or an empty slot means
    /// the log changed or left, and the estimate starts over from the synced value. Any other sync goes forward only:
    /// it is the server's value as of the send, a network transit old, at or behind the estimate of a client in step,
    /// and pulling the estimate back to it would turn the log back. A lagging estimate is pulled up by <see cref="AdvanceClientProgress"/>.
    ///
    /// A 0 alone would not do: the server sends a dirty block entity every 100 ms
    /// (<c>ServerSystemBlockSimulation.UpdateEvery100ms</c>), and a hopper checks every 200 ms, so a hopper often loads the
    /// next log, and the 25 ms tick strips a little of it, between the finish and the send. The client then never sees
    /// the empty slot or the 0, only the new log's small progress. A new log of the same wood keeps the mesh too
    /// (<see cref="UpdateLogMesh"/> keys on the species): every strip comes back at once, the new log in place of the
    /// old. Client only; before <see cref="InitRenderer"/> this does nothing, and it seeds the estimate itself.
    /// </summary>
    void ReconcileClientProgress(float syncedBefore)
    {
        if (capi == null) return;
        if (InputEmpty || debarkProgress <= 0f || debarkProgress < syncedBefore) clientProgress = SyncedProgress;
    }

    /// <summary>The log's mesh as the renderer draws it, with the matrix of <see cref="DebarkerParts.LogIndex"/>: the
    /// core and the strips still on. Null with no log in and before <see cref="SyncLogMesh"/> merged one.</summary>
    internal MultiTextureMeshRef? LogMeshRef => logMeshRef;

    /// <summary>True while a log is loaded and its CPU meshes are built. <see cref="UpdateLogMesh"/> uploads the first
    /// merge in the same call, so this agrees with <see cref="LogMeshRef"/> unless every group mesh came out
    /// empty.</summary>
    internal bool HasLogMesh => logGroups != null;

    /// <summary>
    /// Builds or drops the log's meshes when the loaded log's species changes (T07.04): from <see cref="InitRenderer"/>
    /// and after every sync (<see cref="FromTreeAttributes"/>). The meshes of a species are built once per facing and
    /// kept in the process cache, so a log of a species already seen costs one merge and one upload, never a shape parse
    /// on the main thread (memory vs-sawmill-logmesh-placement-freeze). Both callers run on the main thread (the client
    /// handles block entity packets there), the only thread where the process cache is safe (memory
    /// vs-objectcache-not-threadsafe-dies-with-clientmain). The key is the species and its domain, all that
    /// <see cref="LogTexSource"/> reads: two orientations of one wood share a mesh. An empty input draws no log. It also
    /// keeps the strips' <c>#wood</c> atlas position for the bark chips of T07.05 (<see cref="barkTexture"/>). Client
    /// only.
    /// </summary>
    void UpdateLogMesh()
    {
        if (capi == null) return;

        Block? log = InputSlot.Itemstack?.Block;
        string? wood = InputWoodSpecies;
        string key = log != null && wood != null ? (log.Code?.Domain ?? "game") + ":" + wood : "none";
        if (key == logMeshKey) return;

        DisposeLogMesh();
        logMeshKey = key;
        if (log == null || wood == null) return;

        ICoreClientAPI client = capi;
        int rotY = (int)Block.Shape.rotateY;
        LogTexSource textures = LogTexSource.ForLog(client, log, SawInput.Log, wood);
        TextureAtlasPosition side = textures["wood"];
        barkTexture = side == client.BlockTextureAtlas.UnknownTexturePosition ? null : side;
        MeshData?[] groups = ObjectCacheUtil.GetOrCreate(client, $"iw-debarker-log-{key}-{rotY}",
            () => BuildLogGroups(client, textures, rotY));
        logGroups = groups.Length > 0 ? groups : null;
        SyncLogMesh(); // the first merge now, so the log shows on the frame the sync brings it
    }

    /// <summary>
    /// Keeps the uploaded log in step with the estimate: <c>floor(progress × 8)</c> strips are out, <c>bark1</c> first,
    /// and the mesh is re-merged only when that number changes, eight times a log. The estimate stays below 1, so the last
    /// strip stays on until the log itself goes. The renderer calls this once a frame on the opaque pass, after
    /// <see cref="GetPartMatrices"/> has moved the estimate and before its view gate: the shadow passes draw the log of a
    /// machine off screen too.
    /// </summary>
    internal void SyncLogMesh()
    {
        if (capi == null || logGroups == null) return;
        int strips = DebarkerParts.LogStripCount;
        int hidden = Math.Min((int)(clientProgress * strips), strips - 1);
        if (hidden == logMeshHidden) return;

        MeshData? merged = null;
        for (int k = 0; k < logGroups.Length; k++)
        {
            if (k >= 1 && k <= hidden) continue; // the strips already out
            MeshData? group = logGroups[k];
            if (group == null || group.VerticesCount == 0) continue;
            // Cloned, never added to: the group meshes belong to the process cache.
            if (merged == null) merged = group.Clone();
            else merged.AddMeshData(group);
        }

        logMeshRef?.Dispose();
        logMeshRef = merged != null ? capi.Render.UploadMultiTextureMesh(merged) : null;
        logMeshHidden = hidden;
    }

    /// <summary>The log's CPU meshes for one species and facing, from one parse of the model: [0] the core, [k] bark strip
    /// k, each tesselated from a fresh clone with the other elements blanked. The textures come from the shared
    /// <see cref="LogTexSource.ForLog"/> of a barked log, the input the filter lets in (<see cref="IsValidInput"/>): the
    /// strips' <c>#wood</c> is the bark, the core's <c>#debarked_wood</c> the bare side and its <c>#wood_corner</c> the end
    /// grain, each found by <see cref="WoodTextures"/> (<c>docs/source-conventions.md</c> §2). The facing is baked in the
    /// way the movers bake it, so the log's matrix is conjugated like theirs. Empty when the model fails to load.</summary>
    static MeshData?[] BuildLogGroups(ICoreClientAPI client, LogTexSource textures, int rotY)
    {
        Shape? source = SourceShape(client);
        if (source == null) return Array.Empty<MeshData?>();

        var groups = new MeshData?[DebarkerParts.LogStripCount + 1];
        for (int g = 0; g < groups.Length; g++)
        {
            int keep = g;
            Shape part = source.Clone();
            BlankElements(part.Elements, name => DebarkerParts.LogGroupFor(name) == keep);
            client.Tesselator.TesselateShape("debarker", part, out MeshData mesh, textures, new Vec3f(0, rotY, 0));
            groups[g] = mesh;
        }
        return groups;
    }

    void DisposeLogMesh()
    {
        logMeshRef?.Dispose();
        logMeshRef = null;
        logGroups = null;
        barkTexture = null;
        logMeshHidden = -1;
        logMeshKey = null;
    }

    /// <summary>
    /// The sign of the signed clock per facing, so the spindle turns with the vanilla axle at the coupling. The facing
    /// conjugation carries the native +X spin axis to world −X on NORTH and to −Z on EAST, while the engine draws the
    /// axle about the positive world axis, so those two facings flip. The ternary is the sawmill's, confirmed in game
    /// there, and the chopper's; for the debarker the author confirmed it in game on all four facings at T07.07. The
    /// rectified clock, the lift and the progress clock never take it: the conjugation alone carries the reducer, the
    /// rollers, the linkage and the log, its surface running down at the knives, onto every facing.
    /// </summary>
    float FacingDriveSign() => (Facing == BlockFacing.NORTH || Facing == BlockFacing.EAST) ? -1f : 1f;

    /// <summary>The server's config adopted after this BE initialized: the config packet and the chunk are not ordered
    /// (SpeedDecouplingPlan T01.04 on the chopper). The clocks read <c>DebarkerFwdWrapBound</c> and
    /// <c>DebarkerMinTorque</c> live each frame and the pose cache keys on <c>DebarkerWorkScale</c>, so dropping the
    /// cache here only guarantees a clean rebuild, as on the chopper and the sawmill. No mesh depends on the config, so
    /// nothing is re-tesselated.</summary>
    void OnClientConfigApplied() => matsCache = null;

    // T08.01, the chopper's F-S05-03. The help HUD recomposes on an aim change and on a client BlockChanged at the aimed
    // cell, never by polling (memory vs-interaction-help-no-poll). A BE sync raises BlockChanged at the master cell only,
    // so a player aiming at a filler would keep the old lines until the aim moves. Set in InitRenderer, client only.
    string? lastHelpSig;

    /// <summary>The state <c>BlockDebarker.SelectInteractions</c> branches on: the three part flags, the knife set, the
    /// input and, as a boolean so the 5 % progress syncs do not refire, whether the log still comes back whole. The tick
    /// syncs the moment <see cref="LogRecoverable"/> flips (T06.01), so the take line turns into the discard line at the
    /// grace itself (memory vs-hint-threshold-needs-own-sync).</summary>
    string HelpSignature() => $"{hasDrive}{hasDisc}{hasFeed}{knifeStack != null}{InputEmpty}{LogRecoverable}";

    /// <summary>After every sync (<see cref="FromTreeAttributes"/>): when the sync changed <see cref="HelpSignature"/> and
    /// this client aims at a cell of the machine, mark that cell dirty, which raises BlockChanged there; a filler forwards
    /// its help to the master, so that one cell is enough. A machine nobody looks at is not touched, and the signature
    /// moves on anyway, so a change seen while looking away does not fire on a later sync. Client only, a no-op before
    /// <see cref="InitRenderer"/> and after <see cref="DisposeRenderer"/>.</summary>
    void RefreshInteractionHelpOnChange()
    {
        if (capi == null || Block == null || Block.BlockId == 0) return;
        string sig = HelpSignature();
        if (sig == lastHelpSig) return;
        lastHelpSig = sig;
        BlockSelection? sel = capi.World.Player?.CurrentBlockSelection;
        // The master cell needs no mark: the engine raises BlockChanged there itself after this sync
        // (GeneralPacketHandler.HandleBlockEntities), and a mark would only retesselate its chunk once more.
        // GetCellCollisionBoxes is null exactly for a cell outside this machine's footprint.
        if (sel != null && !sel.Position.Equals(Pos) && GetCellCollisionBoxes(sel.Position) != null)
        {
            capi.World.BlockAccessor.MarkBlockDirty(sel.Position);
        }
    }

    void DisposeRenderer()
    {
        // Before capi goes: only a client BE that ran InitRenderer subscribed.
        if (capi != null && modSystem != null) modSystem.ClientConfigApplied -= OnClientConfigApplied;
        // Without capi a tree sync that lands after the removal or unload cannot build the renderer again.
        capi = null;
        renderer?.Dispose();
        renderer = null;
        DisposeLogMesh();
        // T07.06: the sound's only teardown, since the overrides below are the class's one pair.
        DisposeSound();
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        DisposeRenderer();
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        DisposeRenderer();
    }
}
