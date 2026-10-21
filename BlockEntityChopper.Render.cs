using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

/// <summary>
/// E07/T07.01 — the master's render layer. Variant B (HelveHammer-idiomatic, 1:1 with
/// <see cref="BlockEntitySawmill"/>'s Render partial): the STATIC frame is baked into the chunk mesh here
/// (<see cref="OnTesselation"/>), and the MOVING parts are drawn by <see cref="ChopperRenderer"/>, one
/// <see cref="MeshData"/> per part so T07.02 can animate each from the network angle.
///
/// The authored monolith (<c>shapes/block/chopper/chopper.json</c>) is split into parts by <see cref="ChopperParts"/>
/// (grounded on the tuned <c>chopper-animator.html</c> layout + the anim spec §1). Because the model is
/// flat-with-groups and Blender-suffixed, the engine's path-based <c>selectiveElements</c> can't reproduce the
/// animator's match-any-ancestor-name membership — so each per-part mesh is built by loading the shape, computing
/// membership, BLANKING the faces of every non-member element, and tesselating the result. Union of all parts == the
/// whole model, each element exactly once.
///
/// The block carries this BE as its own <see cref="ITexPositionSource"/> (exactly like <c>BEHelveHammer</c>): the
/// head's <c>"metal"</c> faces take the installed <c>chopperhead-{metal}</c> texture, everything else proxies to the
/// block's texture source. <see cref="GetPartMatrices"/> animates the movers off the drive angle (T07.02), and this
/// file also builds the workpiece log mesh and the species-painted bed re-texture (T07.03, LogMeshRef / BedMeshRef).
/// </summary>
public partial class BlockEntityChopper : ITexPositionSource
{
    static readonly AssetLocation ShapeLoc = new AssetLocation("immersivewoodworking", "shapes/block/chopper/chopper.json");

    // Client-only render handles (capi lives in the main partial, set in Initialize; null on the dedicated server so
    // every render path no-ops). blockTexSource proxies non-metal faces to the block's own textures (frame.json: oak +
    // iron plate + the "0" fallback); renderer draws the movers; rendererKey dedups rebuilds — it folds the head metal
    // AND the install state (T07.01 renders only INSTALLED movers).
    ITexPositionSource? blockTexSource;
    ChopperRenderer? renderer;
    string? rendererKey;

    // Per-part rotation pivot (block units), indexed to ChopperParts.Rules — the geometric centre of each mover part's
    // mesh in the WORLD-BLOCK-LOCAL (facing-baked) frame, since the meshes are tesselated with the facing rotateY baked
    // in. Used by the effects layer (T07.04) for the facing-correct chip anchor points; the ANIMATION uses
    // partPivotsNative instead. Computed once per mesh build (BuildMoverMeshes). Null until built.
    float[]?[]? partPivots;

    // T07.02/T07.06 — the same pivots in the NATIVE (south) frame (partPivots inverse-rotated by facingMatInv), fed to
    // ChopperAnim.BuildMatricesFacing: the driver table is authored native, so the animation runs on native pivots and
    // is then conjugated by the facing rotation to act on the facing-baked meshes. South (rotateY 0) → identical to
    // partPivots. (Every animated chopper part actually carries an EXPLICIT native pivot in ChopperAnim, so this is only
    // a fallback — but it is threaded through 1:1 with the sawmill for parity.) Null until the first build.
    float[]?[]? partPivotsNative;

    // T07.06 — the facing rotation R (and its inverse) about the block centre (0.5,0.5,0.5), as column-major float[16],
    // matching the rotateY baked into the meshes (and Block.Shape.rotateY, verified 1:1 with RotateToFacing). null on a
    // south frame (rotateY 0 → identity, no conjugation needed). Built once per mesh build from the facing rotateY.
    float[]? facingMat;
    float[]? facingMatInv;

    // T07.02 — the render's own copy of the drive angle, advanced from AngleRad while the machine is powered and turning
    // (IsComplete && EffectiveChopSpeed() > 0 — the IsMechanismTurning predicate, LOG OR NO LOG). Frozen otherwise, so a
    // network spinning below the torque gate (or a half-built frame) leaves the axe still rather than creeping/jittering —
    // exactly like vanilla BEHelveHammer, whose head holds its angle when the network stops. Client-only.
    //
    // DUAL-SOURCE RECTIFIER (user's 2026-07-05 design, modeled 2026-07-09) + SpeedDecouplingPlan T03.03 gearing. THREE
    // unwrapped accumulators advanced together in AdvanceAnimAngle from the same per-frame shaft delta. That delta is the
    // Nyquist-HYBRID step (SpeedDecouplingPlan T03.05, the client twin of the server's OnChopperTick T03.04): the
    // wrapped-angle |Δ| below π/frame, the continuous NetworkSpeed·5·dt above it — so the client render never freezes at
    // TrueSpeed>25 while the server keeps splitting (the "firewood before the axe" desync at high RPM):
    //   • animAngle  — the RECTIFIED FORWARD accumulator (+|Δ| per frame, always increasing). It is the RAW shaft-rate
    //     clock; the mechanism (gear train → pawls → axe beam → head, all ChopperDriver.Src="fwd") reads the GEARED clock
    //     animAngle × Config.ChopperWorkScale (T03.03), so the asymmetric rise-then-strike ALWAYS plays down whichever way
    //     the network drives the shaft, and runs at N/M of the shaft. Bounded at the M-dependent ChopperCycleRevs·2π so the
    //     geared read folds on exactly one whole arm lift-cycle (invisible; float can't drift over a session). The effects
    //     layer + the server strike tracker key on this geared read — the arm/strike lives here. speed=1 → workScale 1.0 and
    //     bound 48·2π, the pre-feature ÷48 behaviour bit-for-bit.
    //   • animAngleDisc — a SECOND rectified accumulator (+|Δ|), M-INDEPENDENT, bounded at ChopperCycleCap·2π (a whole 48
    //     shaft revolutions, so a 1:1 disc never snaps at the fold). The three cosmetic discs (rotorDefault/2/3, Src="disc")
    //     read it so they hold 1:1 with the shaft (the gear-them-with-the-mechanism dev option was baked out, T04.03). The
    //     twin of the sawmill's animAngleDisc (T02.02): animAngle's M-dependent bound would jump a 1:1 disc by cycleRevs
    //     revolutions and snap when cycleRevs isn't integer, so the discs need their own whole-revolution fold.
    //   • animAngleSigned — the SIGNED accumulator (+Δ per frame, follows the network both ways), bounded to one revolution
    //     (its only consumer, the twoway input coupling mainRotorTwoway, is 1:1 and reads it mod 2π). Drives ONLY
    //     mainRotorTwoway (Src="signed"), × FacingDriveSign so the visible input coupling co-turns with the connected
    //     network gear on every facing and reverses with a reverse-driven line.
    // animAnglePrev is the previous frame's raw AngleRad sample (re-seeded while idle so a resume banks no jump). The
    // signed↔forward direction change at the junction is hidden by the modeled static housing (rotorStatic).
    float animAngle;
    float animAngleDisc;
    float animAngleSigned;
    float animAnglePrev;
    // SpeedDecouplingPlan T03.05 — THIS frame's NET forward advance of animAngle (the Nyquist-hybrid shaft step inc PLUS
    // the anchor-convergence pull, both applied to animAngle this frame), handed to the effects layer so its strike/tooth
    // detector reads the SAME motion the visible axe makes — dph = frameAnimAdvance·ChopperWorkScale/(ChopperCycleCap·2π),
    // the aliasing-free form of the wrapped phase difference the effects used to take, and the client twin of the server's
    // inc-only dph (BlockEntityChopper.Chopping T03.04; the client adds convergence because it chases the authoritative
    // server phase). 0 on any frame the mechanism isn't turning (no strike). Reading the wrapped phase difference instead
    // would alias exactly as the raw accumulator did — dropping every strike once the geared step exceeds half a cycle
    // (speed>48, workScale up to 100) — so the effects must share this precomputed advance, not re-derive it from the angle.
    float frameAnimAdvance;

    // ── Continuously anchor the render phase to the server strike accumulator (T12.03b desync fix, user 2026-07-13/14) ──
    // animAngle (this render's ÷48-reduction accumulator) and serverAnimAngle (the server's twin, BlockEntityChopper.
    // Chopping) are INDEPENDENT re-integrations of |ΔAngleRad|: the reducer made the arm cycle span ChopperAnim.ReducerRevs
    // (48) shaft revolutions, so the cycle phase can no longer be read off the per-revolution-wrapping AngleRad — each side
    // accumulates its own unwrapped angle. The server STARTS accumulating when the SERVER first turns; the client when IT
    // first turns — and the client's network speed ramps up a beat later, so the client starts a few revolutions behind and
    // that gap PERSISTS. In-game logs (2026-07-14) measured a rock-steady ~25.9 rad (~8.6% of the 48-rev cycle) offset:
    // both strike at phase 0.84, but the client reaches it ~8.6% of a cycle after the server → the firewood ejects, then
    // ~a second later the axe visibly lands ("дрова выпадают, потом анимация"). A ONE-TIME seed can't fix it (the offset
    // develops AFTER the seed). The SAME logs showed the mechanical network's own unwrapped angle (network.AngleRad) is
    // synced server↔client to ~0.05 rad — so the fix anchors to the server's value:
    //   • serverAnimEst — the client's live estimate of serverAnimAngle: RE-BASED to the freshly arrived value on every
    //     anchor packet (BlockEntityChopper.Chopping.OnReceivedServerPacket) and on every tree sync
    //     (BlockEntityChopper.FromTreeAttributes), and extrapolated forward by the same |ΔAngleRad| the render
    //     integrates. The server pushes the anchor on a lightweight ~1 s cadence while turning
    //     (BlockEntityChopper.Chopping.SendAnimAnchor) so the estimate is re-based well before the once-per-cycle strike
    //     sync — otherwise the offset wouldn't close until the first split. The anchor rides its OWN packet rather than a
    //     tree resend (AuditFixPlan T07.05): a MarkDirty at that cadence re-dirtied the chunk for saving and pinned it
    //     unpacked in server RAM, to move four bytes.
    //   • animAngle — the DISPLAYED phase: advances by |ΔAngleRad| (feed-forward, so it never lags the rotation) PLUS a
    //     gentle convergence toward serverAnimEst (AnimConvergeRate), which closes the ~25.9 rad offset smoothly over ~1 s
    //     instead of snapping the axe. Once closed it holds (the offset is constant), so the visible strike coincides with
    //     the server split to within the network's own ~0.05 rad sync. Keeping animAngle a |Δ|-re-integration (not a raw
    //     function of network.AngleRad) preserves the rectifier's direction-agnostic strike on reverse-driven lines.
    // lastSyncedServerAnim holds the value most recently arrived from the server; serverAnimEstSeeded gates the estimate
    // until the first sync (before it, plain re-integration); animAngleSeeded hard-aligns animAngle once at that first sync
    // so it doesn't have to converge from 0. animAnglePrimed makes the first turning frame bank no spurious ΔAngleRad−0.
    // Client-only.
    float lastSyncedServerAnim;
    float serverAnimEst;
    bool serverAnimEstSeeded;
    bool animAngleSeeded;
    bool animAnglePrimed;

    /// <summary>Per-frame fraction of the residual offset (serverAnimEst − animAngle) the display closes each render
    /// frame — the anchor's convergence gain. ~0.05 pulls a ~25.9 rad startup offset in over roughly a second of turning
    /// (a brief, smooth catch-up, not a snap); the steady-state residual after that is just the network's ~0.05 rad sync.</summary>
    const float AnimConvergeRate = 0.05f;

    // ── Deferred chop-vanish (client) ─────────────────────────────────────────────────────────────────────────────
    // The log's visual removal must land ON the client's visible axe-strike, NOT at the server's consuming strike: the
    // two drive-angle accumulators (serverAnimAngle and this render's animAngle) are independent and the server's phase
    // tends to LEAD, so a server-synced empty slot would drop the log mesh while the visible axe is still up (the user's
    // "log disappears before the head falls"). So when a CHOP empties the slot (chopsCompleted bumped, DetectChopVanish)
    // we HOLD the log mesh + its stack here and drop them on the client's own OnStrikeBottom (ResolveChopVanish),
    // throwing the final chips/crack there too (the CanChop gate otherwise suppresses them once the server empties the
    // slot). A player take/discard/break (no bump), a new log, or the mechanism stopping resolve it immediately. Client-only.
    bool pendingChopVanish;    // a chop consumed the log; the mesh is held until the visible strike (or a safety) drops it
    ItemStack? pendingChopLog; // the consumed log, kept for the deferred final chips/crack (the slot is already empty)
    // Last synced chopsCompleted the client processed; detects a bump. SEEDED IN Initialize from the tree-restored
    // chopsCompleted (AuditFixPlan T07.05) — DetectChopVanish runs only when capi != null, i.e. only after Initialize,
    // so the -1 sentinel and the `lastSeenChops >= 0` test below are defensive belt-and-braces that no live client path
    // now reaches. Before that seed the FIRST tree sync after a load was swallowed as the baseline instead of read as a
    // bump, and whether that cost a log its deferral depended on how soon a sync happened to arrive.
    int lastSeenChops = -1;
    // CLIENT-LEADS latch: set true when the client's OWN visible strike already fired this log's split (CanChop true in
    // OnStrikeBottom) BEFORE the server's consume-sync arrives. Then we must NOT defer — the visible landing + its
    // chips/crack already happened, so deferring would hold the consumed billet ~a full cam cycle and double-fire the
    // burst (the client-leads phase inversion). Reset each time a fresh log is shown. Exact for the default one strike
    // per log (ChopperStrikes=1), where the only strike IS the splitting one. With >1 an intermediate strike also
    // latches, and it is cleared by that step's own MarkDirty (a sync with a non-empty slot); should that sync land
    // BEFORE the client's own crossing, the latch survives to the splitting sync and takes the client-leads branch on
    // stale grounds — the billet then drops at the sync rather than at the visible landing, AND that log's final chips
    // and wood-crack do not play at all (by then CanChop is false and nothing was deferred). A non-default config only,
    // and at most the last strike of a log.
    bool clientSplitFiredThisLog;

    // T07.03 — the workpiece log + the species-painted clamp-bed, drawn STATIC on the bed by the renderer. Uploaded
    // while a log-placed/debarkedlog input sits in the slot (the billet) / while the bed part is installed (the bed),
    // rebuilt when the input log or its species changes (UpdateLogMesh, keyed on workpieceKey). Unlike the sawmill there
    // is NO carriage feed and NO kerf slices: the chopper log stands WHOLE on the bed until the pass consumes it — so it
    // is a single body mesh at a fixed transform, not a sliced ride-along on a moving carriage. Both live on the BE (not
    // the renderer) because the input changes far more often than the head metal the renderer keys on, so they must
    // survive a renderer rebuild — exactly like the sawmill's logMeshRef.
    MultiTextureMeshRef? logMeshRef;  // the input rendered like a workpiece laid on the hand stand (BuildWorkpieceMesh); null unless a choppable input (block OR firewood item) is loaded
    MultiTextureMeshRef? bedMeshRef;  // the clamp-bed choppingblock, painted with the BED's OWN species (bedWood; default oak); null unless the bed part is installed
    string? workpieceKey;             // folds (bed installed + its species) and (input collectible code); a change rebuilds both

    // ───────────────────────── ITexPositionSource (head metal, else proxy) ─────────────────────────

    public Size2i AtlasSize => capi?.BlockTextureAtlas.Size ?? new Size2i(1, 1);

    public TextureAtlasPosition this[string textureCode]
    {
        get
        {
            // Head faces (dropmaul-head, all #metal) follow the installed chopperhead-{metal} texture (the wear tier
            // made visible) — copper, iron and steel heads read differently. Mirrors BEHelveHammer's this["metal"] /
            // the sawmill blade kit. Every OTHER mover + the static frame paints from the block's own textures (they
            // get blockTexSource, not this — see BuildMoverMeshes / OnTesselation), so the frame/gear #metal faces stay
            // iron and the head metal can't leak onto them.
            if (capi != null && textureCode == "metal" && headStack?.Item?.Textures != null
                && headStack.Item.Textures.TryGetValue("metal", out CompositeTexture? ctex))
            {
                return capi.BlockTextureAtlas[ctex.Base];
            }
            ITexPositionSource src = blockTexSource ?? capi!.Tesselator.GetTextureSource(Block);
            // The ITexPositionSource indexer is nullable; fall back to unknown.png for a missing code, mirroring vanilla
            // ContainedTextureSource.getOrCreateTexPos (never hand a null TextureAtlasPosition to the tesselator).
            return src[textureCode] ?? capi!.BlockTextureAtlas.UnknownTexturePosition;
        }
    }

    // ───────────────────────── Static frame → chunk mesh (variant B) ─────────────────────────

    /// <summary>
    /// Bake the STATIC frame (the machine housing + the folded static base) into the chunk mesh and suppress the
    /// default full-model block shape (return true) — the moving parts are drawn separately by
    /// <see cref="ChopperRenderer"/>. Mirrors <c>BEPulverizer.OnTesselation</c> / the sawmill (build a sub-mesh,
    /// <c>AddMeshData</c>, return true). Unlike the sawmill (whose lever housing / carriage spring gate on install),
    /// the chopper's ONLY static part is the frame and it is always present on a placed frame block — so there is NO
    /// conditional static build-up and the cache key carries only the facing rotation. Every gated part (drive gears,
    /// arm, head) is a MOVER. Only the master cell tesselates here; the ghost fillers stay invisible.
    /// </summary>
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        base.OnTesselation(mesher, tesselator);

        if (Api is not ICoreClientAPI c) return false;

        int rotY = (int)(Block.Shape?.rotateY ?? 0f);
        ITexPositionSource blockTex = blockTexSource ?? c.Tesselator.GetTextureSource(Block);
        MeshData? staticMesh = ObjectCacheUtil.GetOrCreate(c, "iw-chopper-static-" + rotY,
            () => BuildMesh(c, idx => ChopperParts.Rules[idx].Kind == ChopperPartKind.Static, blockTex, rotY));

        if (staticMesh != null) mesher.AddMeshData(staticMesh);
        return true; // movers come from the renderer; default full-model (icon) shape is replaced
    }

    /// <summary>Whether the MOVER part at <paramref name="ruleIdx"/> (into <see cref="ChopperParts.Rules"/>) is
    /// currently VISIBLE — pulverizer-style conditional build-up (T07.01): a mover is drawn only once its crafted part
    /// is installed. The whole gear train + pawls are the drive-cam (<c>chopperdrive</c> — the rotation→lift
    /// mechanism); the beam is the axe-arm (<c>chopperarm</c>); the head is the installed <c>chopperhead</c>. The frame
    /// is static (baked in <see cref="OnTesselation"/>) and never reaches here; the catch-all default is defensive.</summary>
    bool PartVisible(int ruleIdx) => ChopperParts.Rules[ruleIdx].Id switch
    {
        // The whole drive coupling — the split twoway rotor rings + static housing (re-tuned 2026-07-09) and the gear
        // train + pawls it drives — appears only once the chopperdrive part is installed.
        "mainRotorTwoway" or "rotorDefault" or "rotorDefault2" or "rotorDefault3" or "rotorStatic"
            or "clutch1" or "clutch2" or "gearActive" or "gearPassive" or "levelerL" or "levelerR" => hasDrive,
        "arm" => hasArm,
        "head" => headStack != null,
        _ => true, // frame (never a mover here)
    };

    /// <summary>A compact key of the installed parts that change what the renderer draws — the mover gates plus the
    /// head metal. <see cref="UpdateRenderer"/> rebuilds the mover meshes whenever this changes (a part (un)installs or
    /// the head metal differs). (The bed is the separate <c>choppingblock</c>, not a monolith mover, so it is absent.)</summary>
    string InstallMask() => (hasDrive ? "D" : "") + (hasArm ? "A" : "") + (headStack != null ? "H" : "");

    // ───────────────────────── Mover meshes + renderer lifecycle ─────────────────────────

    /// <summary>(Re)create the mover renderer on the client if the mover set (install state) or the head metal it was
    /// built with has changed (or it doesn't exist yet). Called from <c>Initialize</c>, on part (un)install
    /// (<c>OnPartInstalled</c>/<c>TryTake</c>), on head break (<c>DamageHead</c>) and on every client tree sync
    /// (<c>FromTreeAttributes</c>); a no-op on the server (capi == null) and when the key is unchanged.</summary>
    public void UpdateRenderer()
    {
        if (capi == null || Block == null || Block.BlockId == 0) return;

        string key = HeadMetalKey() + "|" + InstallMask();
        if (renderer != null && key == rendererKey) return;

        DisposeRenderer();
        blockTexSource ??= capi.Tesselator.GetTextureSource(Block);
        MeshData?[] moverMeshes = BuildMoverMeshes(capi, out int[] moverRuleIdx);
        renderer = new ChopperRenderer(capi, this, moverMeshes, moverRuleIdx);
        capi.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "chopper");
        capi.Event.RegisterRenderer(renderer, EnumRenderStage.ShadowFar, "chopper");
        capi.Event.RegisterRenderer(renderer, EnumRenderStage.ShadowNear, "chopper");
        rendererKey = key;
    }

    void DisposeRenderer()
    {
        renderer?.Dispose();
        renderer = null;
        rendererKey = null;
    }

    /// <summary>One <see cref="MeshData"/> per VISIBLE MOVER part (the static frame is in the chunk mesh; the log is
    /// deferred to T07.03), each built by blanking every non-member face. Only the head keys on its metal (via
    /// <c>this["metal"]</c>); every other mover paints from the block's own textures, so the head metal can't leak onto
    /// the gears/arm after a reload. Also fills <see cref="partPivots"/> (each mover's mesh-bbox centre) and returns the
    /// mover→rule index map so the renderer can fetch each part's matrix. Mover order follows
    /// <see cref="ChopperParts.Rules"/>.</summary>
    MeshData?[] BuildMoverMeshes(ICoreClientAPI c, out int[] ruleIdx)
    {
        int rotY = (int)(Block.Shape?.rotateY ?? 0f);
        blockTexSource ??= c.Tesselator.GetTextureSource(Block); // baseline source (oak + frame.json metal + "0") for non-head movers
        partPivots = new float[ChopperParts.Rules.Length][];
        var meshes = new List<MeshData?>();
        var idxs = new List<int>();

        // F-S05-18 (twin F-S04-06): parse the ~423-element chopper model at most ONCE per cold build, not once per mover.
        // Each mover that misses the client mesh cache tesselates from a fresh Clone of that single parse — the same
        // single-parse idiom the sawmill's mover/log paths use, instead of a Shape.TryGet re-parse per part. A warm cache
        // still parses nothing (GetOrCreate returns the cached mesh and never calls this). Membership is recomputed per
        // clone (a cheap name-chain walk, no JSON parse); the master is only ever Cloned, never blanked, so it stays pristine.
        Shape? master = null;
        MeshData? BuildMoverMesh(int keep, ITexPositionSource tex)
        {
            master ??= Shape.TryGet(c, ShapeLoc);
            if (master == null) return null;
            Shape work = master.Clone();
            var partOf = new Dictionary<ShapeElement, int>();
            ComputeMembership(work.Elements, new List<string>(), partOf);
            BlankElements(work.Elements, el => partOf[el] != keep);
            c.Tesselator.TesselateShape("chopper", work, out MeshData mesh, tex, new Vec3f(0, rotY, 0));
            return mesh;
        }

        for (int i = 0; i < ChopperParts.Rules.Length; i++)
        {
            ChopperPartRule rule = ChopperParts.Rules[i];
            if (rule.Kind != ChopperPartKind.Mover) continue;
            bool partVisible = PartVisible(i);
#if IW_DEBUG
            c.Logger.Notification("[immersivewoodworking] [chopper-render] mover rule[{0}] '{1}' visible={2} (hasDrive={3} hasArm={4} head={5}) rotY={6}", i, rule.Id, partVisible, hasDrive, hasArm, headStack != null, rotY);
#endif
            if (!partVisible) continue; // conditional build-up — draw a mover only once its part is installed (T07.01)
            int idx = i;
            // ONLY the head follows the installed metal (the this["metal"] override); every other mover paints from the
            // block's own textures (frame.json: oak + iron plate), so the head metal can't leak onto the gears/arm and
            // their cache keys correctly omit HeadMetalKey().
            ITexPositionSource tex = rule.IsHead ? this : blockTexSource!;
            string keyName = rule.IsHead
                ? "iw-chopper-part-" + rule.Id + "-" + rotY + "-" + HeadMetalKey()
                : "iw-chopper-part-" + rule.Id + "-" + rotY;
            MeshData? mesh = ObjectCacheUtil.GetOrCreate(c, keyName, () => BuildMoverMesh(idx, tex));
            meshes.Add(mesh);
            idxs.Add(i);
            partPivots[i] = MeshCenter(mesh);
#if IW_DEBUG
            float[]? pv = partPivots[i];
            c.Logger.Notification("[immersivewoodworking] [chopper-render]   built '{0}': vertices={1} pivot={2}", rule.Id,
                mesh?.VerticesCount ?? -1, pv == null ? "null" : "(" + pv[0].ToString("0.00") + "," + pv[1].ToString("0.00") + "," + pv[2].ToString("0.00") + ")");
#endif
        }

        // T07.02/T07.06 — facing support. The meshes above are tesselated with the rotateY baked in (about the block
        // centre), but the animation driver table (ChopperAnim) is authored in the native/south frame. Build the facing
        // rotation R about the block centre and the native pivots (= R⁻¹ · baked pivot) so GetPartMatrices can run the
        // animation native and conjugate each matrix by R (BuildMatricesFacing). On a south frame R is identity → native
        // == baked, a no-op. The rotateY here is the SAME value used to bake the meshes, so R and the meshes always
        // agree. (Chopper movers all carry an explicit native pivot in ChopperAnim, so partPivotsNative is a fallback
        // only — but it is built 1:1 with the sawmill.)
        BuildFacingMatrices(rotY);
        partPivotsNative = new float[partPivots.Length][];
        for (int i = 0; i < partPivots.Length; i++)
            partPivotsNative[i] = (partPivots[i] == null || facingMatInv == null) ? partPivots[i] : TransformPoint(facingMatInv, partPivots[i]!);

        ruleIdx = idxs.ToArray();
        return meshes.ToArray();
    }

    /// <summary>Build <see cref="facingMat"/> / <see cref="facingMatInv"/> — the facing rotation about the block centre
    /// (0.5,0.5,0.5) for <paramref name="rotYDeg"/> and its inverse — as column-major <c>float[16]</c>. Null on a south
    /// frame (rotateY 0): the conjugation is then skipped entirely. Uses the same rotation convention as the baked
    /// meshes (<c>RotateYDeg</c> about the centre), so the animation lines up with the rendered geometry. Mirrors
    /// <see cref="BlockEntitySawmill"/>.BuildFacingMatrices.</summary>
    void BuildFacingMatrices(int rotYDeg)
    {
        if (rotYDeg == 0) { facingMat = null; facingMatInv = null; return; }
        facingMat = new Matrixf().Translate(0.5f, 0.5f, 0.5f).RotateYDeg(rotYDeg).Translate(-0.5f, -0.5f, -0.5f).Values;
        facingMatInv = new Matrixf().Translate(0.5f, 0.5f, 0.5f).RotateYDeg(-rotYDeg).Translate(-0.5f, -0.5f, -0.5f).Values;
    }

    /// <summary>Transform a point (block units) by a column-major <c>float[16]</c> matrix (index 4*col+row) — the same
    /// layout ChopperAnim / Matrixf use. Returns a fresh 3-vector. Mirrors <see cref="BlockEntitySawmill"/>.TransformPoint.</summary>
    static float[] TransformPoint(float[] m, float[] p) => new float[]
    {
        m[0] * p[0] + m[4] * p[1] + m[8]  * p[2] + m[12],
        m[1] * p[0] + m[5] * p[1] + m[9]  * p[2] + m[13],
        m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14],
    };

    /// <summary>Geometric centre (block units, mesh frame) of a part mesh's vertex bounding box, or null for an empty
    /// mesh (→ the assembler falls back to a default pivot). Mirrors <see cref="BlockEntitySawmill"/>'s MeshCenter.</summary>
    static float[]? MeshCenter(MeshData? m)
    {
        if (m == null || m.VerticesCount <= 0 || m.xyz == null) return null;
        float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
        int n = m.VerticesCount;
        for (int v = 0; v < n; v++)
        {
            float x = m.xyz[v * 3], y = m.xyz[v * 3 + 1], z = m.xyz[v * 3 + 2];
            if (x < mnx) mnx = x; if (y < mny) mny = y; if (z < mnz) mnz = z;
            if (x > mxx) mxx = x; if (y > mxy) mxy = y; if (z > mxz) mxz = z;
        }
        return new float[] { (mnx + mxx) / 2f, (mny + mxy) / 2f, (mnz + mxz) / 2f };
    }

    // Per-frame matrix cache (F-S05-20, twin F-S04-08): the last-built tree + the drive scalars (and the pivots
    // reference) it was built from. GetPartMatrices hands this instance back unchanged when nothing moved (idle/unpowered),
    // skipping the rebuild. See there.
    float[]?[]? matsCache;
    float[]?[]? matsCachePivots;
    float matsCacheAngle, matsCacheAngleSigned, matsCacheAngleDisc;
    // SpeedDecouplingPlan T03.03 — the config-derived pose input the tree was built from (twin of the sawmill T02.03):
    // ChopperWorkScale multiplies the fwd clock (→ the geared read), so a config change that flips M at a FROZEN clock (an
    // idle chopper between syncs, animAngle/Disc not moving) rebuilds the tree instead of holding the stale-tempo pose.
    // This makes matsCache a true pure-function cache; OnClientConfigApplied nulling it is then belt-and-braces. The
    // disc-gearing dev knob was baked to false (T04.03), so the discs always read animAngleDisc (already keyed) and there
    // is no flag to key. Seeded to the speed=1 defaults so the first frame's pivots-ref miss (not a value miss) is what
    // builds the initial cache.
    float matsCacheWorkScale = 1f;

    /// <summary>SpeedDecouplingPlan T01.04 — invalidate the client render when the server config is adopted after this
    /// machine already tesselated (the ConfigSyncPacket and the chunk are not ordered; see the subscription in
    /// <see cref="Initialize"/>). The M-derived strike constants live in
    /// <see cref="ImmersiveWoodworkingConfig.ChopperStrikes"/> / <see cref="ImmersiveWoodworkingConfig.ChopperCycleRevs"/>
    /// / <see cref="ImmersiveWoodworkingConfig.ChopperSplitPerStrike"/>, which the parametric strike cycle (E03) reads
    /// live each frame — so dropping the per-frame matrix cache is enough to switch the tempo without a relog. The pivot
    /// key is cleared too, not just <see cref="matsCache"/>: <see cref="GetPartMatrices"/> hands the cached tree back
    /// whenever the pivot REFERENCE and the drive scalars are unchanged, so clearing the tree alone would return null —
    /// clearing the key forces the guard to miss and rebuild. The <c>MarkBlockDirty</c> re-tesselates so an M-dependent
    /// MESH (E03) rebuilds as well; it is cheap (once per join, and on a live dev /reload). Client-only: server BEs never
    /// subscribe, and <see cref="GetPartMatrices"/> is a no-op there. Twin of the sawmill's OnClientConfigApplied.</summary>
    void OnClientConfigApplied()
    {
        if (capi == null) return;
        matsCache = null; matsCachePivots = null;
        capi.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    // AuditFixPlan T05.07 / F-L10-05 — THIS machine's animation buffers, reused every frame (one per BE, never shared:
    // a static would let two choppers overwrite each other's pose). Built lazily on the first client frame; the server
    // never asks for matrices, so nothing is allocated there.
    ChopperAnimState? animState;

    /// <summary>
    /// T07.02 — one animation matrix per part (<c>float[16]</c>, indexed to <see cref="ChopperParts.Rules"/>), built by
    /// <see cref="ChopperAnim.BuildMatricesFacing"/> from the drive angle (the gear-train → pawls → axe-beam kinematics).
    /// The renderer calls this each frame and applies each mover's matrix to its mesh. Returns null before the meshes
    /// (and pivots) are built, so the renderer falls back to drawing the rest pose.
    ///
    /// The angle is FROZEN unless the machine is powered and TURNING — <see cref="IsComplete"/> &amp;&amp;
    /// <see cref="EffectiveChopSpeed"/> &gt; 0 (see <see cref="AdvanceAnimAngle"/>), the SAME predicate the effects layer
    /// reads as <c>IsMechanismTurning</c>: on a half-built frame or below the torque gate the axe stands, but a powered,
    /// assembled machine runs its WHOLE mechanism LOG OR NO LOG — a powered chopper visibly spins even over an empty bed
    /// (user 2026-07-05: "всё крутится, просто рубить нечего"). Only the WORK gates on the log — the server chop accrual
    /// (<see cref="CanChop"/>, E06) and the client chips + wood-crack (<see cref="UpdateCutEffects"/>) — so an empty bed
    /// thuds but throws no chips and consumes nothing. At a frozen angle of 0 every driver collapses to identity (rotate
    /// 0 = identity, trip lift 0 = no offset), so an idle machine renders at exactly its authored rest pose (the T07.01
    /// look). There is no feed/progress term — the chopper log stands still. Now mirrors
    /// <see cref="BlockEntitySawmill.GetPartMatrices"/>: the gears idle whenever powered; only the workpiece action gates
    /// on the loaded log (there the carriage feed, here the chips/crack/accrual).
    /// </summary>
    public float[]?[]? GetPartMatrices(float dt)
    {
        if (partPivotsNative == null) return null;
        AdvanceAnimAngle(dt);
        // Run the animation in the native frame (native pivots) and conjugate each matrix by the facing rotation, so the
        // motion is correct on the facing-baked meshes (T07.06). facingMat/Inv are null on a south frame → BuildMatrices
        // verbatim, no per-part cost. The result is in the world-block-local (baked) frame, exactly what the renderer and
        // the effects layer (which reads the baked partPivots) expect. THREE drive clocks (SpeedDecouplingPlan T03.03,
        // mirror of the sawmill):
        //   (1) the GEARED work clock animAngle × ChopperWorkScale (Src="fwd") for the whole gear train → pawls → axe beam:
        //       one fixed spin direction regardless of shaft direction, geared to N/M of the shaft (workScale = N/M, M=1 →
        //       ×1.0f exact = bit-identical to the pre-feature pose). The conjugation handles facing;
        //   (2) the SIGNED animAngleSigned × FacingDriveSign for mainRotorTwoway ONLY, so that one coupling turns the same
        //       way as the connected network axle on every facing and reverses with a reverse-driven line;
        //   (3) the DISC clock (Src="disc") for the three cosmetic discs rotorDefault/2/3 — the RAW shaft-rate rectified
        //       animAngleDisc, so the discs hold 1:1 with the shaft (the gear-them-with-the-mechanism dev option was baked
        //       out to this look after the author's live judgment, T04.03). At M=1 it coincides with the pre-feature pose.
        // FacingDriveSign multiplies ONLY the signed drive — a global θ flip would reverse the asymmetric strike, and the
        // geared/disc clocks reach the drivers RAW of it (the rectified clocks only ever grow; × −1 would shrink them and
        // run the strike backward). See FacingDriveSign / BuildMatrices.
        //
        // Dirty-check (F-S05-20, twin F-S04-08): the tree is a pure function of the three drive scalars + workScale + the
        // disc flag below plus partPivotsNative (facing matrices + FacingDriveSign are per-BE constants; there is no
        // feed/progress term). Since T03.03 keys workScale + the flag directly, a live M change or a flag flip is caught
        // here on the next frame even at a frozen clock; OnClientConfigApplied (T01.04) still nulls the cache on a sync —
        // belt-and-braces. animAngleDisc is keyed too though it advances in lockstep with animAngle. Skip the rebuild when
        // none changed — an unpowered/half-built chopper holds a frozen pose (the clocks advance only while powered+turning).
        // Keyed also on the partPivotsNative REFERENCE so a mover re-tesselation invalidates it. Renderer PartModel +
        // UpdateCutEffects read the result read-only, so a shared frozen-frame instance is safe.
        float workScale = Config.ChopperWorkScale;
        if (ReferenceEquals(matsCachePivots, partPivotsNative) && animAngle == matsCacheAngle && animAngleSigned == matsCacheAngleSigned
            && animAngleDisc == matsCacheAngleDisc && workScale == matsCacheWorkScale)
            return matsCache;
        matsCachePivots = partPivotsNative;
        matsCacheAngle = animAngle; matsCacheAngleSigned = animAngleSigned; matsCacheAngleDisc = animAngleDisc;
        matsCacheWorkScale = workScale;
        animState ??= ChopperAnimState.Create();
        float thetaFwdGeared = animAngle * workScale;
        // Discs hold 1:1 with the shaft — the raw-shaft rectified animAngleDisc (dev knob ChopperGearConnectionDiscs baked
        // to false, T04.03). The geared alternative (thetaFwdGeared) is no longer selectable.
        float thetaDisc = animAngleDisc;
        matsCache = ChopperAnim.BuildMatricesFacing(animState, partPivotsNative, thetaFwdGeared, animAngleSigned * FacingDriveSign(), thetaDisc, facingMat, facingMatInv);
        return matsCache;
    }

    /// <summary>Advance the three drive accumulators by the shaft's wrap-corrected rotation while the machine is powered
    /// and TURNING, keeping them UNWRAPPED so a non-rotationally-symmetric gear/pawl spins continuously instead of
    /// snapping at the 2π wrap — yet each bounded where its fold is invisible and float precision can't drift over a long
    /// session. <see cref="animAngle"/> (raw shaft-rate, rectified) folds at the M-dependent <c>ChopperCycleRevs·2π</c> so
    /// the GEARED read <c>animAngle × ChopperWorkScale</c> lands on exactly one whole arm cycle at the fold
    /// (SpeedDecouplingPlan T03.03); <see cref="animAngleDisc"/> (raw, M-independent) folds at <c>ChopperCycleCap·2π</c> =
    /// a whole 48 shaft revolutions, so a 1:1 cosmetic disc reading it never snaps; <see cref="animAngleSigned"/> folds at
    /// 2π (the twoway coupling reads it mod 2π). At speed=1 all three folds coincide at 48·2π and the geared factor is 1.0,
    /// the pre-feature behaviour bit-for-bit. Frozen — with the reference re-seeded — when NOT turning, so a resume banks
    /// no jump.
    ///
    /// The gate is <see cref="IsComplete"/> &amp;&amp; <see cref="EffectiveChopSpeed"/> &gt; 0 — assembled, head fitted,
    /// drive above the torque floor — the EXACT predicate the effects layer reads as
    /// <see cref="BlockEntityChopper.IsMechanismTurning"/>, deliberately kept in lockstep (a mismatch is what froze the
    /// mechanism while the ambient loop hummed). There is NO log term: a powered, assembled chopper runs its whole
    /// mechanism LOG OR NO LOG — it visibly spins over an empty bed and simply chops nothing (user 2026-07-05: "всё
    /// крутится, просто рубить нечего", reversing the brief 07-05 no-log freeze). The WORK, not the mechanism, gates on
    /// the log: the SERVER accrual (<see cref="CanChop"/>) and the client chips + wood-crack
    /// (<see cref="UpdateCutEffects"/>, on <see cref="CanChop"/>) — so an empty bed thuds (the maul on the bed, user spec)
    /// but throws no chips and consumes no wood. This now matches the sawmill, which idles its drive gears on the same
    /// <c>EffectiveSawSpeed()&gt;0</c> and gates only its CARRIAGE FEED on <see cref="BlockEntitySawmill.CanSaw"/>. All
    /// three accumulators (the rectified <c>animAngle</c>, the rectified <c>animAngleDisc</c> and the signed
    /// <c>animAngleSigned</c>) advance under this one gate, so "turning" moves the whole machine — gear train, pawls, axe
    /// beam AND the twoway input coupling / cosmetic discs — together.
    /// The per-frame step is the Nyquist-HYBRID of the server's OnChopperTick (SpeedDecouplingPlan T03.05): the
    /// wrapped-angle |Δ| below π/frame, the continuous <c>NetworkSpeed·5·dt</c> above it — so a fast network
    /// (<c>TrueSpeed&gt;25</c>, or a low frame rate) can't fold a forward step into a small reverse and freeze the axe
    /// while the server keeps splitting. Client-only (called once per render frame from <see cref="GetPartMatrices"/> with
    /// that frame's <paramref name="dt"/>).</summary>
    void AdvanceAnimAngle(float dt)
    {
        float ang = AngleRad;
        frameAnimAdvance = 0f; // no advance banked unless the mechanism is turning (the effects layer reads this)
        // First advance banks NO delta: seed animAnglePrev from the live angle so a machine ALREADY turning on the
        // client's first render frame (a mid-cycle chunk load) doesn't integrate a spurious ang−0 (up to π) on top of the
        // seeded animAngle (animAnglePrev defaults to 0). Primed with the frame's real AngleRad — valid here because a
        // turning machine has a resolved network — so it is robust even if AngleRad wasn't ready at the Initialize seed.
        // Mirrors the server's serverStrikeSeeded first-tick seed (.Chopping). Thereafter animAnglePrev self-updates below.
        if (!animAnglePrimed) { animAnglePrev = ang; animAnglePrimed = true; }
        if (IsComplete && EffectiveChopSpeed() > 0f)
        {
            float d = ang - animAnglePrev;
            if (d > GameMath.PI) d -= GameMath.TWOPI;       // fold the [0,2π) wrap back to the true short-arc delta
            else if (d < -GameMath.PI) d += GameMath.TWOPI;

            // Rectified-forward step, Nyquist-HYBRID (SpeedDecouplingPlan T03.05, the client twin of the server's
            // OnChopperTick T03.04): the wrapped |Δ| above is EXACT only while the shaft turns less than π per frame; above
            // that the [−π,π] fold reads a genuine forward step as a small backward one, |Δ| collapses and the accumulator
            // (and with it the whole visible mechanism + the effects that read frameAnimAdvance) FREEZES — the "chopper breaks
            // at high speeds" report, on the client this time. It aliases at |shaft Δ| > π ⇒ NetworkSpeed·5·dt > π; the
            // continuous NetworkSpeed gives the true magnitude with no Nyquist limit (ShaftRadPerNetworkSpeedSecond, shared
            // with the server tracker). Below π/frame keep the exact wrapped |Δ| (speed=1 / any normal RPM stays
            // bit-identical to the pre-T03.05 render — invariant I8), only above it fall back to the speed-derived
            // magnitude. Both are rectified (|·|), so the strike still always plays down whichever way the shaft turns. The
            // per-side dt differs (client frame vs 25 ms server tick), so each side crosses its own Nyquist at a different
            // RPM — but each stays exact below its own limit, so the two accumulate the SAME forward rate and never diverge.
            float absD = d < 0f ? -d : d;
            float speedInc = NetworkSpeed * ShaftRadPerNetworkSpeedSecond * dt;
            float inc = speedInc > GameMath.PI ? speedInc : absD;

            // E12/T12.03b + SpeedDecouplingPlan T03.03: with the ÷48 reducer the movers are NO LONGER periodic mod 2π — the
            // cam/pawls/axe complete one cycle per ChopperAnim.ReducerRevs (48) revolutions of the GEARED work clock. The
            // raw accumulator now folds at the M-dependent ChopperCycleRevs·2π (not the fixed ReducerRevs·2π), so the geared
            // read animAngle·ChopperWorkScale lands on exactly one whole arm cycle at the fold — every mover period
            // (1/4/16/48) divides ReducerRevs so the fold is invisible. speed=1 → ChopperCycleRevs = ChopperCycleCap =
            // ReducerRevs = 48, i.e. the pre-feature 48·2π bound bit-for-bit. This is the server twin's bound too (Chopping).
            float animBound = Config.ChopperCycleRevs * GameMath.TWOPI;

            // Feed-forward: advance the display by the shaft step so it never lags the visible rotation.
            animAngle = WrapPos(animAngle + inc, animBound);

            // The NET forward advance of animAngle this frame, handed to the effects layer (SpeedDecouplingPlan T03.05).
            // It starts at the feed-forward inc and picks up the convergence pull below — the effects' strike/tooth detector
            // reads dph = frameAnimAdvance·workScale/(cap·2π), so its phase advance is the SAME motion the VISIBLE axe makes
            // (animAngle drives the beam via TripLift), convergence included. Computed here from the known terms rather than
            // by re-differencing animAngle, so it never fold-aliases the way a wrapped [ph − startPh] would at a small
            // M-dependent bound (speed>48). The old effects path measured this same net advance via that wrapped phase
            // difference — this is the aliasing-free form of it. The SERVER twin (Chopping) has no convergence term (it is
            // authoritative), so its dph is inc-only; the two match in steady state (converged → conv ≈ 0).
            float frameAdvance = inc;

            // Anchor: once the server's accumulator has arrived (serverAnimEstSeeded), extrapolate that estimate by the
            // same step and pull the display gently toward it (AnimConvergeRate). This CLOSES the persistent ~25.9 rad
            // startup offset between the two independent |Δ| re-integrations (measured in-game 2026-07-14) smoothly rather
            // than snapping the axe — so the visible strike lands with the server's split/eject. Before the first sync we
            // just free-run (feed-forward only); the offset closes once the estimate is re-based (the anchor packet, or a
            // tree sync) and the server's ~1 s anchor cadence keeps it fresh between the once-per-cycle strike syncs.
            if (serverAnimEstSeeded)
            {
                serverAnimEst = WrapPos(serverAnimEst + inc, animBound);
                float conv = AnimConvergeRate * ShortArc(serverAnimEst - animAngle, animBound);
                animAngle = WrapPos(animAngle + conv, animBound);
                frameAdvance += conv; // the convergence moves the visible axe too → count it in the effects' phase advance
            }
            frameAnimAdvance = frameAdvance;

            // Signed accumulator: advance by the true Δ so the input coupling follows the network both ways.
            animAngleSigned += d;
            if (animAngleSigned >= GameMath.TWOPI) animAngleSigned -= GameMath.TWOPI;
            else if (animAngleSigned <= -GameMath.TWOPI) animAngleSigned += GameMath.TWOPI;

            // DISC rectified clock (SpeedDecouplingPlan T03.03, twin of the sawmill's animAngleDisc): the same +|Δ| as
            // animAngle but folded at the M-INDEPENDENT ChopperCycleCap·2π (a whole 48 shaft revolutions) — the raw
            // shaft-rate clock the three cosmetic discs hold 1:1 with (the gear-them alternative was baked out, T04.03). A 1:1
            // disc reads it directly, so the fold has to land on a whole revolution; animAngle's M-dependent ChopperCycleRevs·2π
            // bound would jump a 1:1 disc by cycleRevs revolutions and visibly snap when cycleRevs isn't integer (e.g.
            // speed 1.55). At speed=1 the bound equals animBound (both 48·2π), so the discs match the pre-feature spin.
            animAngleDisc += inc;
            float discBound = ImmersiveWoodworkingConfig.ChopperCycleCap * GameMath.TWOPI;
            if (animAngleDisc >= discBound) animAngleDisc -= discBound;
        }
        animAnglePrev = ang;
    }

    /// <summary>Reduce <paramref name="x"/> into <c>[0, bound)</c> (a proper modulo, unlike a single subtract, since the
    /// convergence term can push slightly past either end).</summary>
    static float WrapPos(float x, float bound)
    {
        x %= bound;            // C# % keeps the sign
        if (x < 0f) x += bound;
        return x;
    }

    /// <summary>The signed short-arc distance of <paramref name="x"/> on a cycle of length <paramref name="bound"/>,
    /// folded into <c>[-bound/2, bound/2]</c> — so the anchor converges the display toward the server estimate the SHORT
    /// way around the 48-rev cycle (never a long-way-around lurch when the two straddle the wrap).</summary>
    static float ShortArc(float x, float bound)
    {
        x %= bound;
        if (x > bound * 0.5f) x -= bound;
        else if (x < -bound * 0.5f) x += bound;
        return x;
    }

    /// <summary>
    /// T12.03b desync fix (user 2026-07-13/14) — RE-BASE the client's live estimate of the server strike accumulator
    /// (<see cref="serverAnimEst"/>) to the freshly synced value (<see cref="lastSyncedServerAnim"/>), so
    /// <see cref="AdvanceAnimAngle"/>'s convergence always pulls the displayed <see cref="animAngle"/> toward the current
    /// server phase and the visible axe-fall lands with the server's split/eject (the firewood). Three callers: the anchor
    /// PACKET (<c>OnReceivedServerPacket</c>, id <c>AnimAnchorPacketId</c>) — which carries the ~1 s cadence — plus every
    /// client tree sync (<see cref="FromTreeAttributes"/>) and once from <see cref="Initialize"/> on load. Unlike the
    /// earlier one-time seed this must run repeatedly, because the ~25.9 rad offset between the two independent |Δ|
    /// re-integrations develops AFTER placement (the client starts turning a beat behind the server) and only a re-base
    /// catches it. The server pushes that anchor about once a second while turning
    /// (<see cref="BlockEntityChopper.SendAnimAnchor"/>, throttled in <see cref="BlockEntityChopper.OnChopperTick"/>) so
    /// the estimate is re-based well before the once-per-cycle strike sync — otherwise the offset wouldn't close until the
    /// first split. Tree syncs now happen only at real state boundaries (a strike step, pass completion, an input move, a
    /// part install), so the packet is what keeps the estimate fresh in between (AuditFixPlan T07.05).
    ///
    /// The DISPLAY (<see cref="animAngle"/>) is hard-aligned to the server value exactly ONCE (<see cref="animAngleSeeded"/>)
    /// so it starts on-phase instead of converging in from 0 (a large first convergence would briefly spin the arm fast);
    /// thereafter the gentle per-frame convergence in <see cref="AdvanceAnimAngle"/> owns it and this only moves the
    /// estimate. Client-only (a no-op on the server). <see cref="animAnglePrev"/> is left untouched — it self-seeds from
    /// <c>AngleRad</c> and only sets the per-frame delta, never the absolute phase.
    /// </summary>
    void AnchorAnimToServer()
    {
        if (capi == null) return;
        serverAnimEst = lastSyncedServerAnim;
        serverAnimEstSeeded = true;
        if (!animAngleSeeded) { animAngle = lastSyncedServerAnim; animAngleSeeded = true; }
    }

    /// <summary>
    /// T07.06 — per-facing sign for the SIGNED input drive (<c>animAngleSigned</c>), so the visible input rotor co-turns
    /// with the connected mechanical-network gear. The signed driver turns the twoway rotor about its native axis; the
    /// facing conjugation rotates that axis to <c>R·axis</c>, which lands on the NEGATIVE world axle axis for NORTH and
    /// EAST, while the connected vanilla gear/axle is drawn by the engine's own mech renderer about the POSITIVE axle
    /// axis (its <c>AxisSign</c>, ours plays no part — <see cref="BEBehaviorChopperMP"/> renders nothing and keeps no
    /// axis, T07.08). So on those two facings the conjugated input spin is reversed
    /// vs the network gear and the signed angle must be flipped; SOUTH/WEST already agree. Mirrors the working
    /// <see cref="BlockEntitySawmill"/>.FacingDriveSign (confirmed in-game there) — but here it multiplies ONLY the signed
    /// input drive, NEVER the rectified-forward mechanism: a global θ flip would reverse the drop-maul's asymmetric
    /// rise-then-strike (that is the whole reason the input is split onto its own signed accumulator — the dual-source
    /// rectifier). The exact sign per facing is the calibration seam to confirm in-game (T07.06); flip this ternary if the
    /// input rotor spins against the network gear.
    /// </summary>
    float FacingDriveSign() => (Facing == BlockFacing.NORTH || Facing == BlockFacing.EAST) ? -1f : 1f;

    string HeadMetalKey() => headStack?.Collectible?.Code?.ToString() ?? "none";

    // ───────────────────────── Workpiece log + species-painted bed (T07.03) ─────────────────────────

    /// <summary>The workpiece mesh — the input rendered like a workpiece laid on the hand chopping block (its own
    /// textures + <see cref="BlockEntityChoppingBlock.BuildLaidWorkpieceMesh"/>), or null when no choppable input is
    /// loaded. ANY accepted input draws (a BLOCK log/section/debarked or a firewood ITEM), not just blocks.
    /// Drawn STATIC by <see cref="ChopperRenderer"/> on the bed — the chopper log doesn't move (no carriage). The rough
    /// analogue of <see cref="BlockEntitySawmill.LogMeshRef"/>, minus the carriage ride-along and the kerf slices.</summary>
    public MultiTextureMeshRef? LogMeshRef => logMeshRef;

    /// <summary>The clamp-bed mesh — the hand <c>choppingblock</c> geometry painted with its OWN installed species
    /// (<c>bedWood</c>, captured at install; default oak when the bed block carried none), NOT the fed log's species
    /// (user reversal 2026-07-05: the bed keeps the wood it was crafted from), or null when the bed part isn't
    /// installed. Drawn STATIC by <see cref="ChopperRenderer"/> under the billet. The sawmill has no counterpart — its
    /// base isn't a re-textured hand block.</summary>
    public MultiTextureMeshRef? BedMeshRef => bedMeshRef;

    /// <summary>
    /// Client-only: on a tree sync, decide whether the input slot emptied because of a CHOP (the server-authoritative
    /// <see cref="chopsCompleted"/> bumped) and, if so, HOLD the log mesh (<see cref="pendingChopVanish"/>) instead of
    /// letting <see cref="UpdateLogMesh"/> drop it — the drop is deferred to the client's own visible axe-strike
    /// (<see cref="ResolveChopVanish"/>) so the log vanishes when the axe you SEE lands, not at the server's leading
    /// consuming phase. A slot that empties WITHOUT a bump (player Ctrl+RMB take/discard, a break) is removed normally;
    /// a non-empty slot (a fresh log arrived) supersedes any pending vanish. <paramref name="prevLog"/> is the slot's
    /// contents captured BEFORE this sync overwrote it (the log to keep showing + throw the final chips for). Called
    /// from <see cref="FromTreeAttributes"/> right before <see cref="UpdateLogMesh"/>.
    /// </summary>
    void DetectChopVanish(int syncedChops, ItemStack? prevLog)
    {
        bool chopBumped = lastSeenChops >= 0 && syncedChops != lastSeenChops;
        lastSeenChops = syncedChops;

        // A non-empty slot (a fresh log arrived, or the log never left) supersedes a pending vanish — the old log's
        // landing is moot; drop it silently and let UpdateLogMesh render whatever is loaded now. Reset the client-leads
        // latch here: the shown log is (or has just become) the current one, so any earlier strike is not "this log".
        if (!InputEmpty) { clientSplitFiredThisLog = false; ResolveChopVanish(false); return; }

        // Empty now: defer ONLY a genuine chop consumption while the axe is turning (a manual removal / a stopped or
        // resuming machine clears instantly). pendingChopLog carries the consumed stack for the deferred chips/crack.
        if (chopBumped && prevLog?.Collectible != null && IsMechanismTurning())
        {
            if (clientSplitFiredThisLog)
            {
                // CLIENT-LEADS: the visible axe already struck this log and threw its chips/crack before this consume-
                // sync arrived. Do NOT defer (that would linger the billet + double the burst) — leave pendingChopVanish
                // false so UpdateLogMesh (called next in FromTreeAttributes) drops the now-stale billet at once, no new
                // effects. Clear the latch for the next log.
                clientSplitFiredThisLog = false;
            }
            else
            {
                // SERVER-LEADS: the visible strike is still ahead — hold the billet and drop it (with the final
                // chips/crack) on the client's own OnStrikeBottom.
                pendingChopVanish = true;
                pendingChopLog = prevLog.Clone();
            }
        }
    }

    /// <summary>
    /// Client-only: resolve a deferred chop-vanish (<see cref="pendingChopVanish"/>) — drop the held log mesh (via
    /// <see cref="UpdateLogMesh"/>, now that the flag is cleared) and, when <paramref name="fireEffects"/>, throw the
    /// consumed log's final chips + wood-crack (the effects the <see cref="CanChop"/> gate suppressed once the server
    /// emptied the slot). Called from the client's visible strike (<see cref="OnStrikeBottom"/>, fireEffects true — the
    /// axe LANDED), from the mechanism stopping (<see cref="UpdateCutEffects"/>, false — the axe froze, no landing) and
    /// from a superseding new log (<see cref="DetectChopVanish"/>, false).
    /// </summary>
    void ResolveChopVanish(bool fireEffects)
    {
        if (!pendingChopVanish) return;
        ItemStack? log = pendingChopLog;
        pendingChopVanish = false;
        pendingChopLog = null;
        clientSplitFiredThisLog = false; // the log is gone; start the next one with a clean latch
        if (fireEffects && log != null) SpawnDeferredChopEffects(log);
        UpdateLogMesh(); // flag cleared → the (server-empty) slot now drops the held log mesh (or swaps in a new one)
    }

    /// <summary>
    /// (Re)build the workpiece-log mesh and the species-painted bed mesh when the loaded input (or its species) changes,
    /// or clear the ones that no longer apply. Keyed on <see cref="workpieceKey"/> so it runs on every refresh
    /// (<c>Initialize</c> / tree sync) but only re-tesselates + re-uploads when something visible actually changed.
    /// Client-only; a no-op on the dedicated server (<c>capi == null</c>). Loosely mirrors
    /// <see cref="BlockEntitySawmill.UpdateLogMesh"/> — but simpler: no carriage feed, no kerf slices (the log is split
    /// whole, not sawn through), plus the bed re-texture the sawmill has no counterpart for. It DOES stage its workpiece
    /// off <see cref="chopProgress"/> since T04.07, through the hand stand's own seam.
    ///
    /// The CPU meshes are cached per species(+kind)+facing via <see cref="ObjectCacheUtil"/> (like the mover meshes), so
    /// a re-load of an already-seen wood is a pure GPU upload — the shape is parsed + tesselated at most once per wood,
    /// never per-frame or in a loop (memory <c>vs-sawmill-logmesh-placement-freeze</c>). The per-BE GPU refs are still
    /// uploaded/disposed per machine.
    ///
    /// PLACEHOLDER seating: the bed is drawn at the master cell and the billet at its authored rest; the exact fit onto
    /// the footprint's clamp-bed (offset / height / scale) is calibrated in-game in T07.06 (the DoD leaves seating to
    /// it). Facing (rotY) IS baked in here, consistently with the movers, so the workpiece already turns with the machine.
    /// </summary>
    public void UpdateLogMesh()
    {
        if (capi == null || Block == null || Block.BlockId == 0) return;
        if (pendingChopVanish) return; // hold the just-chopped log mesh until the client's visible strike drops it (deferred vanish)

        ItemStack? log = InputSlot.Itemstack;

        // The bed renders in the installed choppingblock's OWN species (bedWood, captured at install in TryAddPart) —
        // NOT the fed log's species and NOT a hardcoded oak (user-reported 2026-07-05, reversing the T07.03 "bed follows
        // the input log" choice). A bare/legacy bed (null bedWood) paints the default oak.
        string bedSpecies = bedWood ?? "oak";
        string bedSpeciesDomain = bedWood != null ? bedWoodDomain : "game";

        // Draw the workpiece EXACTLY as the hand chopping block draws a laid workpiece — the SHARED
        // BlockEntityChoppingBlock.BuildLaidWorkpieceMesh (NOT the sawmill's standalone log shape), which since T04.07 is
        // the ONE dispatcher both stations come through: the staged log / placed half-log shapes for the two workpieces
        // that have them, the plain laid path for everything else. ANY accepted input draws — a choppable BLOCK
        // (log-placed / debarked / section / choppingblock) OR an ITEM (firewood → sticks): the laid path re-tesselates
        // an item into the block atlas and stands firewood upright, so the chopper shows the SAME billet as the hand
        // колода (user-reported 2026-07-05: firewood laid on the chopper showed nothing — the old path only tesselated
        // blocks).
        bool drawLog = log?.Collectible != null;

        // Cache identity for the workpiece render — the SHARED one, so it cannot drift from the shared builder. It folds
        // in what the code alone doesn't carry: a half-log's species + bark state (one data-item, every species sharing
        // one code, so a code-only key would paint whichever was tesselated first and — this cache being process-wide —
        // keep painting it all session), and the chop STAGE, which drives this machine's mesh exactly as it does the
        // hand stand's. Facing is NOT in it: a placed machine never turns; the ObjectCacheUtil key below appends rotY
        // because THAT cache is process-wide and shared with machines that can face elsewhere.
        string logIdentity = drawLog ? BlockEntityChoppingBlock.LaidWorkpieceIdentity(log!, chopProgress, 0f) : "nolog";

        string key = (hasBed ? "bed:" + bedSpeciesDomain + ":" + bedSpecies : "nobed")
            + "|" + logIdentity;
        if (key == workpieceKey) return; // unchanged → keep the current meshes (incl. the null/none cases)

        DisposeWorkpieceMeshes();
        workpieceKey = key;

        int rotY = (int)(Block.Shape?.rotateY ?? 0f);

        // The clamp-bed (only once the choppingblock part is installed): the hand-stand geometry painted with the bed's
        // OWN species. Cached per species+facing — identical for every chopper of that wood/facing.
        if (hasBed)
        {
            MeshData? bed = ObjectCacheUtil.GetOrCreate(capi, "iw-chopper-bed-" + bedSpeciesDomain + ":" + bedSpecies + "-" + rotY,
                () => BuildBedMesh(capi, bedSpecies, bedSpeciesDomain, rotY));
            if (bed != null && bed.VerticesCount > 0) bedMeshRef = capi.Render.UploadMultiTextureMesh(bed);
        }

        // The workpiece: the input rendered like it's laid on the hand stand (BLOCK or ITEM). Cached per collectible-code+facing.
        if (drawLog)
        {
            MeshData? logMesh = ObjectCacheUtil.GetOrCreate(capi, "iw-chopper-log-" + logIdentity + "-" + rotY,
                () => BuildWorkpieceMesh(capi, log!, chopProgress, rotY)); // drawLog guarantees log != null (log?.Collectible != null)
            if (logMesh != null && logMesh.VerticesCount > 0) logMeshRef = capi.Render.UploadMultiTextureMesh(logMesh);
        }
    }

    /// <summary>Build the species-painted clamp-bed CPU mesh: the hand chopping block's own geometry+wood
    /// (<see cref="BlockEntityChoppingBlock.BuildStandMesh"/>, the SINGLE source of truth for "choppingblock + species" —
    /// bark <c>M1</c> / tree-ring <c>M2</c>, cross-domain), rotated onto the placed facing about the block centre — the
    /// same rotY the movers and the billet bake in (the <c>facingMat</c> convention), so the bed turns with the machine.
    /// Null if the stand shape can't be loaded. The geometry depends only on species+facing, so the caller caches this
    /// per species+facing.</summary>
    static MeshData? BuildBedMesh(ICoreClientAPI c, string wood, string domain, int rotY)
    {
        MeshData? mesh = BlockEntityChoppingBlock.BuildStandMesh(c, wood, domain);
        if (mesh == null) return null;
        // Facing, about the block centre — equivalent to the rotY TesselateShape bakes for the movers/billet (the
        // facingMat convention, verified 1:1 with RotateToFacing). Identity on a south frame.
        if (rotY != 0) mesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0f, rotY * GameMath.DEG2RAD, 0f);
        return mesh;
    }

    /// <summary>Build the workpiece mesh EXACTLY as the hand chopping block renders a laid workpiece — the shared
    /// <see cref="BlockEntityChoppingBlock.BuildLaidWorkpieceMesh"/>, the ONE dispatcher for "what a workpiece laid on a
    /// chopping block looks like" (T04.07): the staged full-log / placed half-log shapes at
    /// <paramref name="progress"/>'s stage, the plain laid path (tesselate the ACTUAL input with its own textures — a
    /// BLOCK straight, an ITEM re-tesselated into the block atlas — then scale/centre/seat it, firewood stood upright)
    /// for everything else. So the automated bed and the hand колода show the SAME workpiece, at the same size and
    /// the same stage, for EVERY accepted input — they no longer merely agree on the plain path (user-reported
    /// 2026-07-05: firewood showed nothing because the old path tesselated only blocks; F-L9-05: the half-log came out
    /// at 64 % floating 1.55 px above the bed, because the machine took the plain path for it too).
    ///
    /// The yaw goes in as 0 and the machine's facing is baked in HERE instead, about the block centre exactly like the
    /// movers/bed: it is the one rotation that must reach all three branches, and the plain laid path ignores the
    /// builder's yaw. Composing two Y-turns about the same centre, so passing 0 there and rotating here is the same
    /// mesh. ONE tesselation per call; the caller caches per identity+facing — never in a loop (memory
    /// <c>vs-sawmill-logmesh-placement-freeze</c>).</summary>
    static MeshData? BuildWorkpieceMesh(ICoreClientAPI c, ItemStack stack, float progress, int rotY)
    {
        MeshData? mesh = BlockEntityChoppingBlock.BuildLaidWorkpieceMesh(c, stack, progress, 0f);
        if (mesh == null) return null;
        if (rotY != 0) mesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0f, rotY * GameMath.DEG2RAD, 0f);
        return mesh;
    }

    void DisposeWorkpieceMeshes()
    {
        logMeshRef?.Dispose();
        logMeshRef = null;
        bedMeshRef?.Dispose();
        bedMeshRef = null;
        workpieceKey = null;
    }

    // ───────────────────────── Shape split (membership + face-blank tesselate) ─────────────────────────

    /// <summary>Tesselate only the elements whose part index satisfies <paramref name="keepPart"/>: load a fresh shape,
    /// compute each element's part (the animator's match-any-ancestor-name rules), blank the faces of every element NOT
    /// kept, then tesselate with <paramref name="tex"/>. A fresh shape per call avoids any resolve-caching cross-talk
    /// between subsets. Facing is baked in via <paramref name="rotY"/> so renderer parts align with the chunk frame.
    /// Mirrors <see cref="BlockEntitySawmill"/>'s BuildMesh.</summary>
    MeshData? BuildMesh(ICoreClientAPI c, System.Func<int, bool> keepPart, ITexPositionSource tex, int rotY)
    {
        Shape? shape = Shape.TryGet(c, ShapeLoc);
        if (shape == null) return null;

        var partOf = new Dictionary<ShapeElement, int>();
        ComputeMembership(shape.Elements, new List<string>(), partOf);
        BlankElements(shape.Elements, el => !keepPart(partOf[el]));

        c.Tesselator.TesselateShape("chopper", shape, out MeshData mesh, tex, new Vec3f(0, rotY, 0));
        return mesh;
    }

    static void ComputeMembership(ShapeElement[]? els, List<string> chain, Dictionary<ShapeElement, int> outMap)
    {
        if (els == null) return;
        foreach (ShapeElement el in els)
        {
            chain.Add(el.Name ?? "");
            outMap[el] = ChopperParts.IndexFor(chain);
            ComputeMembership(el.Children, chain, outMap);
            chain.RemoveAt(chain.Count - 1);
        }
    }

    static void BlankElements(ShapeElement[]? els, System.Func<ShapeElement, bool> blank)
    {
        if (els == null) return;
        foreach (ShapeElement el in els)
        {
            // Blank the RESOLVED faces (what the client tesselator reads). Faces->FacesResolved is an [OnDeserialized]
            // step that already ran in Shape.TryGet, and TesselateShape never re-resolves, so a 6-null FacesResolved
            // makes this element contribute no quads. Mirrors the sawmill.
            if (blank(el)) el.FacesResolved = new ShapeElementFace[6];
            BlankElements(el.Children, blank);
        }
    }

    // ───────────────────────── Interaction-help live refresh (F-S05-03) ─────────────────────────
    //
    // The vanilla interaction-help HUD (HudElementInteractionHelp) does NOT poll — it recomposes its bottom-screen hints
    // only on a re-hover or on a client BlockChanged for the selected cell (memory vs-interaction-help-no-poll). So a
    // state change WITHOUT a re-hover (part installed, log loaded, the log crossing the recover→discard grace) leaves the
    // hints stale until the player looks away and back. We force a refresh by marking the looked-at cell dirty, which
    // raises BlockChanged. The sawmill and the sawhorse both do this; the chopper was the one station that didn't — its
    // Ctrl+RMB "take vs discard" hint (LogRecoverable) rode on the chopProgress MarkDirty(true) retesselation instead,
    // which F-S12-03 makes data-only. Direct mirror of BlockEntitySawmill.RefreshInteractionHelpOnChange (see there for
    // the full decompiled-HUD rationale); kept parallel per the sawmill↔chopper machine clone (F-ARCH-03).

    // Last help-relevant state signature (client). Seeded in Initialize; compared on every client tree sync so we only
    // re-fire when a hint would actually change (a running machine's per-strike / inventory syncs otherwise don't).
    string? lastHelpSig;

    /// <summary>The state <see cref="BlockChopper.SelectInteractions"/> branches on: the three mandatory part flags,
    /// whether a head is installed, whether the input holds a log, and — as a BOOLEAN, so a per-strike progress sync
    /// doesn't refire — whether the loaded log is still recoverable (which flips the Ctrl+RMB hint from "take" to
    /// "discard"). Mirror of <see cref="BlockEntitySawmill.HelpSignature"/> (blade kit → head; four part flags → three;
    /// no product term — the product is ejected forward, there is no output slot). The recoverable term is CONSTANT on
    /// the shipped default (<see cref="LogRecoverable"/> cannot go false at <c>ChopperStrikes = 1</c>), so out of
    /// the box this signature only moves on parts / head / log; it is kept for parity with the sawmill and for
    /// non-default step counts, where the hint really does flip mid-pass.</summary>
    string HelpSignature() => $"{hasDrive}{hasArm}{hasBed}{headStack != null}{InputEmpty}{LogRecoverable}";

    /// <summary>Recompose the interaction-help hints if <see cref="HelpSignature"/> changed since the last client sync, by
    /// marking the player's selected footprint cell dirty (client <c>MarkBlockDirty</c> raises <c>BlockChanged</c>, and a
    /// ghost filler forwards the help to the master, so marking just that cell is enough). Only fires when THIS client is
    /// looking at the machine — otherwise the next hover recomposes anyway, and an unobserved (auto-fed) machine isn't
    /// touched every sync; the signature is still updated when not looking, so the first change after looking away doesn't
    /// spuriously fire. Client-only. Direct mirror of <see cref="BlockEntitySawmill.RefreshInteractionHelpOnChange"/>.</summary>
    void RefreshInteractionHelpOnChange()
    {
        if (capi == null || Block == null || Block.BlockId == 0) return;
        string sig = HelpSignature();
        if (sig == lastHelpSig) return;
        lastHelpSig = sig;
        BlockSelection? sel = capi.World.Player?.CurrentBlockSelection;
        if (sel != null && IsFootprintCell(sel.Position)) capi.World.BlockAccessor.MarkBlockDirty(sel.Position);
    }

    // ───────────────────────── Teardown ─────────────────────────

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        if (capi != null && modSystem != null) modSystem.ClientConfigApplied -= OnClientConfigApplied; // T01.04 — release the config-arrival hook so an unloaded BE isn't pinned by the mod-system event
        DisposeRenderer();
        DisposeWorkpieceMeshes(); // T07.03 — free the per-BE log/bed GPU meshes
        DisposeSound();           // T07.05 — stop + free the ambient loop, clear the effect trackers
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        if (capi != null && modSystem != null) modSystem.ClientConfigApplied -= OnClientConfigApplied; // T01.04 — see OnBlockUnloaded
        DisposeRenderer();
        DisposeWorkpieceMeshes(); // T07.03
        DisposeSound();           // T07.05
    }
}
