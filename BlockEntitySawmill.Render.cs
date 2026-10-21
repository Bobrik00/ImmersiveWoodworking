using System.Collections.Generic;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

/// <summary>
/// E07/T07.01 — the master's render layer. Variant B (HelveHammer-idiomatic): the STATIC frame is baked into the
/// chunk mesh here (<see cref="OnTesselation"/>), and the MOVING parts are drawn by <see cref="SawmillRenderer"/>,
/// one <see cref="MeshData"/> per part so T07.02/T07.03 can animate each from the network angle / feed progress.
///
/// The authored model (<c>shapes/block/sawmill/sawmill.json</c>) is split into parts by <see cref="SawmillParts"/>
/// (a 1:1 mirror of the tuned <c>sawmill-animator.html</c> layout). Because the model is flat-with-a-few-groups and
/// Blender-suffixed, the engine's path-based <c>selectiveElements</c> can't reproduce the animator's
/// match-any-ancestor-name membership — so each per-part mesh is built by loading the shape, computing membership,
/// BLANKING the faces of every non-member element, and tesselating the result. Union of all parts == the whole model,
/// each element exactly once.
///
/// The block carries this BE as its own <see cref="ITexPositionSource"/> (exactly like <c>BEHelveHammer</c>): the
/// blade kit's <c>"metal"</c> faces take the installed <c>sawmillblade-{metal}</c> texture, everything else proxies to
/// the block's texture source. T07.01 renders the installed parts and animates the movers from the network angle /
/// feed progress (T07.02); the workpiece log IS drawn here too — body + slices on the carriage, species-textured and
/// conditional on a loaded log (T07.03).
/// </summary>
public partial class BlockEntitySawmill : ITexPositionSource
{
    static readonly AssetLocation ShapeLoc = new AssetLocation("immersivewoodworking", "shapes/block/sawmill/sawmill.json");

    // Client-only render handles (capi lives in the main partial). blockTexSource proxies non-metal faces to the
    // block's own textures; renderer draws the movers; rendererKey dedups rebuilds — it folds the blade metal (the saw
    // texture) AND the install state (which movers are drawn, since T07.01 now renders only INSTALLED parts).
    ITexPositionSource? blockTexSource;
    SawmillRenderer? renderer;
    string? rendererKey;

    // Per-part rotation pivot (block units), indexed to SawmillParts.Rules — the geometric centre of each mover part's
    // mesh in the WORLD-BLOCK-LOCAL (facing-baked) frame, since the meshes are tesselated with the facing rotateY baked
    // in. Used by the effects layer for the facing-correct sound/particle anchor points; the ANIMATION uses
    // partPivotsNative instead. Computed once per mesh build (BuildMoverMeshes). Null until the first build.
    float[]?[]? partPivots;

    // T07.06 — the same pivots in the NATIVE (south) frame (partPivots inverse-rotated by facingMatInv), fed to
    // SawmillAnim.BuildMatricesFacing: the driver table is authored native, so the animation must run on native pivots
    // and then be conjugated by the facing rotation to act on the facing-baked meshes. South (rotateY 0) → identical to
    // partPivots. Null until the first build.
    float[]?[]? partPivotsNative;

    // T07.06 — the facing rotation R (and its inverse) about the block centre (0.5,0.5,0.5), as column-major float[16],
    // matching the rotateY baked into the meshes (and Block.Shape.rotateY, verified 1:1 with RotateToFacing). null on a
    // south frame (rotateY 0 → identity, no conjugation needed). Built once per mesh build from the facing rotateY.
    float[]? facingMat;
    float[]? facingMatInv;

    // T07.02 — the render's own copy of the drive angle, advanced from AngleRad only while the machine is actually
    // cutting (IsComplete && EffectiveSawSpeed() > 0). Frozen otherwise, so a network spinning below the torque gate (or
    // a half-built frame) leaves the saw still rather than creeping/jittering. Client-only.
    //
    // It is the UNWRAPPED drive angle. The engine's AngleRad wraps to [0,2π) each revolution, but the feed-ratchet
    // rotors (wheel/spring, SawmillAnim) run an ACCUMULATING ratchet that snaps backward on a wrapping source — the
    // visible "wheel resets at its axle spokes" bug. AdvanceAnimAngle integrates the wrap-corrected per-frame delta so
    // the angle grows smoothly (kept bounded to a full-symmetry period so float precision can't drift over a long
    // session); the periodic movers read it mod 2π and are unaffected, while the rotors spin continuously AND stay
    // phase-locked to the θ-driven pawl. animAnglePrev is the previous frame's raw AngleRad sample (re-seeded while idle
    // so a resume banks no jump).
    float animAngle;
    // Companion RECTIFIED accumulator (+|Δ| per frame, only ever grows) for the GEARED "fixed-direction" mechanism
    // (SawmillAnim Src="fwd"): the crank disc rotorDefault4 AND — via the crank the connecting rod pins to it — the whole
    // slider-crank + feed-ratchet chain (crank/sash/saw/levers/wheel/spring). They spin while the machine runs but do NOT
    // reverse when the input shaft is driven the other way (user 2026-07-10/14 — ONLY the mainRotorTwoway coupling
    // reverses). animAngle is now the SIGNED accumulator for mainRotorTwoway ALONE (the twoway input coupling). Both
    // advance under the same gate in AdvanceAnimAngle, but ONLY animAngle takes FacingDriveSign — this one reaches the
    // drivers RAW (T07.06 / F-L2-16: signing a monotonically growing clock runs the feed ratchet backward on NORTH and
    // EAST). It folds at the M-DEPENDENT SawmillFwdWrapBound so the GEARED reading animAngleFwd·SawmillWorkScale lands on a
    // whole ratchet revolution at the fold (T02.01). (Earlier the crank/wheel rode animAngle → they inverted with the
    // input shaft; moved to animAngleFwd 2026-07-14.) The three cosmetic connection discs rotorDefault1..3 are NO LONGER on
    // this clock — SpeedDecouplingPlan T02.02 splits them onto animAngleDisc below (raw shaft rate).
    float animAngleFwd;
    // SpeedDecouplingPlan T02.02 — the RAW-shaft-rate rectified clock for the three cosmetic connection discs
    // (rotorDefault1..3): they hold 1:1 with the shaft rather than gearing with the mechanism (the dev knob that used to
    // pick this vs the geared clock was baked to that 1:1 look, T04.03). Same +|Δ| accumulation as animAngleFwd (both grow while cutting, both raw of
    // FacingDriveSign), but folded at the M-INDEPENDENT TWOPI·SawmillDefaultWork — a whole SawmillDefaultWork shaft
    // revolutions — so a plain disc reading it never snaps at the fold, at ANY multiplier. animAngleFwd cannot serve this:
    // it folds at the M-DEPENDENT SawmillFwdWrapBound (=TWOPI·50·M), chosen so the GEARED reading animAngleFwd·workScale
    // lands on a whole ratchet revolution — but that same fold jumps a 1:1 disc by 50·M revolutions, a visible snap when
    // 50·M isn't an integer (e.g. the band edge M=1/3). At M=1 the two bounds coincide (workScale=1, SawmillFwdWrapBound=
    // TWOPI·50), so animAngleDisc ≡ animAngleFwd bit-for-bit and the discs are bit-identical to the pre-feature pose.
    // The gear-the-discs-with-the-mechanism alternative was baked out to this 1:1 look after the author's live judgment
    // (SpeedDecouplingPlan T04.03); this is now the only clock the cosmetic discs read.
    float animAngleDisc;
    float animAnglePrev;

    // T07.03 — the client's own smooth feed progress for the carriage/log, advanced locally each render frame
    // (AdvanceClientFeed) from the SHAFT rotation (the same per-revolution coupling the server cut uses), so the carriage
    // tracks the saw stroke. The server only syncs sawmillProgress at pass boundaries (MarkDirty on completion /
    // inventory change), so reading the raw field would jump; this advances between syncs and snaps back to the
    // authoritative value on every FromTreeAttributes (which also resets it to 0 at pass completion). Client-only.
    float clientProgress;

    // Client mirror of lastFeedAngle (the server's per-tick drive-angle reference): the previous frame's AngleRad, so
    // AdvanceClientFeed advances the visual feed by the shaft's rotation rather than a frame-time integral. Client-only.
    float lastClientFeedAngle;

    // AuditFixPlan T07.07 / F-L4-10 — the mirror of feedSeeded, for the reference above. Same defect on this side, with a
    // visual rather than an economic cost: a mill that comes into view already cutting differenced the live network angle
    // against 0 and jumped the carriage forward by up to half a pass, held until the next server sync snapped it back.
    // Closed by the !CanSaw() branch of AdvanceClientFeed exactly as the server's is. Client-only.
    bool clientFeedSeeded;

    // T07.03 — the workpiece log mesh, species-textured via LogTexSource, uploaded only while a sawable log sits in the
    // input and rebuilt when the input log changes (UpdateLogMesh, keyed on logMeshKey). The authored log* part is split
    // into the body (log_main*, always visible) and the working slices (log_working_k_*), which vanish in cut order as
    // the carriage feeds them through the saw (HiddenLogSliceCount) — the progressive cut. Lives on the BE (not the
    // renderer) because the log changes far more often than the blade metal the renderer keys on, so it must survive a
    // renderer rebuild.
    //
    // AuditFixPlan T05.07 / F-L10-06 — the split lives on the CPU side only. All the visible pieces ride the SAME
    // (carriage) matrix, so they go to the GPU as ONE mesh: 25 draw calls per stage became 1, i.e. 40 → 16 per stage and
    // 120 → 48 per frame. The merge is redone only when the hidden-slice count actually changes — at most 24 times over
    // a pass, roughly once a second, against the 180 draws a second the old split was paying. The CPU group meshes come
    // from the process cache (BuildLogGroupMeshes), so a rebuild is a merge + upload with no shape parse.
    MeshData?[]? logGroups;             // CPU meshes, cached per species: [0] = body, 1..N = working slices (absent → null)
    MultiTextureMeshRef? logMeshRef;    // GPU: body + the slices the saw hasn't reached yet, merged
    int logMeshHidden = -1;             // the HiddenLogSliceCount logMeshRef was merged for (-1 = nothing uploaded)
    string? logMeshKey;

    // log_working_<N>_* → slice number N (the cut order: 1 nearest the saw, hidden first). Drives the log split.
    static readonly Regex WorkingSliceRe = new Regex(@"log_working_(\d+)", RegexOptions.CultureInvariant);

    // Index of the log part in SawmillParts.Rules (the workpiece; ride-along on the carriage). Computed once.
    static readonly int LogRuleIndexValue = System.Array.FindIndex(SawmillParts.Rules, r => r.Kind == SawmillPartKind.Log);

    // ───────────────────────── ITexPositionSource (blade metal, else proxy) ─────────────────────────

    public Size2i AtlasSize => capi?.BlockTextureAtlas.Size ?? new Size2i(1, 1);

    public TextureAtlasPosition this[string textureCode]
    {
        get
        {
            // Blade kit faces follow the installed sawmillblade-{metal} texture (the speed tier made visible) — copper,
            // iron and steel kits read differently. Mirrors BEHelveHammer's this["metal"].
            if (capi != null && textureCode == "metal" && bladeStack?.Item?.Textures != null
                && bladeStack.Item.Textures.TryGetValue("metal", out CompositeTexture? ctex))
            {
                return capi.BlockTextureAtlas[ctex.Base];
            }
            ITexPositionSource src = blockTexSource ?? capi!.Tesselator.GetTextureSource(Block);
            // The ITexPositionSource indexer is nullable; fall back to unknown.png for a missing code, mirroring
            // vanilla ContainedTextureSource.getOrCreateTexPos (never hand a null TextureAtlasPosition to the tesselator).
            return src[textureCode] ?? capi!.BlockTextureAtlas.UnknownTexturePosition;
        }
    }

    // ───────────────────────── Static frame → chunk mesh (variant B) ─────────────────────────

    /// <summary>
    /// Bake the STATIC parts (frame/housing/static springs/passive pawl) into the chunk mesh and suppress the default
    /// full-model block shape (return true) — the moving parts are drawn separately by <see cref="SawmillRenderer"/>.
    /// Mirrors <c>BEPulverizer.OnTesselation</c> (build a sub-mesh, <c>AddMeshData</c>, return true). The static mesh
    /// has no per-BE state, so it is cached per facing rotation (<c>ObjectCacheUtil</c>). Only the master cell tesselates
    /// here; the ghost fillers stay invisible.
    /// </summary>
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        base.OnTesselation(mesher, tesselator);

        if (Api is not ICoreClientAPI c) return false;

        int rotY = (int)(Block.Shape?.rotateY ?? 0f);
        ITexPositionSource blockTex = blockTexSource ?? c.Tesselator.GetTextureSource(Block);
        // Conditional build-up (T07.01): bake only the static parts whose install gate is satisfied — a bare frame
        // shows just the frame, installing the levers adds their static housing, the carriage adds its spring. The
        // only static parts that gate are the leveler housing/passive pawl (levers) and the carriage spring (carriage);
        // the frame is always in. The cache key carries those two flags so the chunk mesh rebuilds on install
        // (OnPartInstalled → MarkDirty(true) retesselates the block client-side).
        string smask = (hasLevers ? "L" : "") + (hasCarriage ? "R" : "");
        MeshData? staticMesh = ObjectCacheUtil.GetOrCreate(c, "iw-sawmill-static-" + rotY + "-" + smask,
            () => BuildMesh(c, idx => SawmillParts.Rules[idx].Kind == SawmillPartKind.Static && PartVisible(idx), blockTex, rotY));

        if (staticMesh != null) mesher.AddMeshData(staticMesh);
        return true; // movers come from the renderer; default full-model shape is replaced
    }

    /// <summary>Whether the part at <paramref name="ruleIdx"/> (into <see cref="SawmillParts.Rules"/>) is currently
    /// VISIBLE — pulverizer-style conditional build-up (T07.01): a part is drawn only once its crafted item is
    /// installed. ONLY the frame is always present; the rest map to the install flags exactly as the authored per-part
    /// meshes (<c>sawmill_*_parts</c>) group the model: the rotor discs (<c>Rotor_default_1..4</c>) + crank +
    /// rod → crankshaft, sash → sash, blades → kit, every leveler + the feed wheel + their static housing → levers, the
    /// carriage + its spring → carriage. The log workpiece has its own loaded-log gate (<see cref="UpdateLogMesh"/>)
    /// and never reaches here.
    ///
    /// The rotor discs (<c>Rotor_default_1..4</c>, movers restored 2026-07-09) are the visible mechanical-power connection
    /// gear (the MP node's own <c>mechPartShape</c> is <c>null</c> so the engine builds no second gear here — see
    /// <c>ghostpower.json</c>, T07.08). They are
    /// gated on the crankshaft — the drive train they head (Rotor_default → MainRotor_twoway → crankpit) — per the user's 2026-07-05 decision, so a bare
    /// frame shows only the frame. By the time the machine is <see cref="IsComplete"/> (and can actually draw power) the
    /// crankshaft is always in, so a usable machine always shows its connection gear; a half-built frame without the
    /// crankshaft simply shows no gear (the accepted trade-off of hiding it).</summary>
    bool PartVisible(int ruleIdx) => SawmillParts.Rules[ruleIdx].Id switch
    {
        "rotorDefault1" or "rotorDefault2" or "rotorDefault3" or "rotorDefault4" or "mainRotorTwoway" or "crankpit" => hasCrankshaft,
        "sash" => hasSash,
        "saw" => bladeStack != null,
        "lever_top" or "lever_connector" or "lever_bottom" or "lever_active" or "wheel" or "passive" or "lever_static" => hasLevers,
        "spring_rotor" or "spring" or "carriage" => hasCarriage,
        _ => true, // frame only (always present)
    };

    /// <summary>A compact key of the installed parts that change what the renderer draws — the mover gates plus the
    /// blade kit. <see cref="UpdateRenderer"/> rebuilds the mover meshes whenever this changes (a part (un)installs or
    /// the blade metal differs). Pairs with the static mesh's own <c>smask</c> in <see cref="OnTesselation"/>.</summary>
    string InstallMask() => (hasSash ? "S" : "") + (hasCrankshaft ? "C" : "") + (hasLevers ? "L" : "") + (hasCarriage ? "R" : "") + (bladeStack != null ? "B" : "");

    // ───────────────────────── Mover meshes + renderer lifecycle ─────────────────────────

    /// <summary>(Re)create the mover renderer on the client if the blade metal it was built with has changed (or it
    /// doesn't exist yet). T07.01 always renders the full assembled machine, so the only state that changes the mover
    /// meshes is the blade kit's metal (the saw faces). Called from <c>Initialize</c>, on part (un)install
    /// (<c>OnPartInstalled</c>) and on every client tree sync (<c>FromTreeAttributes</c>); a no-op on the server.</summary>
    public void UpdateRenderer()
    {
        if (capi == null || Block == null || Block.BlockId == 0) return;

        string key = BladeMetalKey() + "|" + InstallMask();
        if (renderer != null && key == rendererKey) return;

        DisposeRenderer();
        blockTexSource ??= capi.Tesselator.GetTextureSource(Block);
        MeshData?[] moverMeshes = BuildMoverMeshes(capi, out int[] moverRuleIdx);
        renderer = new SawmillRenderer(capi, this, moverMeshes, moverRuleIdx);
        capi.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "sawmill");
        capi.Event.RegisterRenderer(renderer, EnumRenderStage.ShadowFar, "sawmill");
        capi.Event.RegisterRenderer(renderer, EnumRenderStage.ShadowNear, "sawmill");
        rendererKey = key;
    }

    void DisposeRenderer()
    {
        renderer?.Dispose();
        renderer = null;
        rendererKey = null;
    }

    /// <summary>One <see cref="MeshData"/> per MOVER part (static parts are in the chunk mesh; the log workpiece is
    /// deferred to T07.03), each built by blanking every non-member face. Cached per part + facing (the saw part also
    /// keys on the blade metal). Also fills <see cref="partPivots"/> (each mover's mesh-bbox centre, the rotate pivot for
    /// parts without an explicit one) and returns the mover→rule index map so the renderer can fetch each part's matrix.
    /// Mover order follows <see cref="SawmillParts.Rules"/>.</summary>
    MeshData?[] BuildMoverMeshes(ICoreClientAPI c, out int[] ruleIdx)
    {
        int rotY = (int)(Block.Shape?.rotateY ?? 0f);
        blockTexSource ??= c.Tesselator.GetTextureSource(Block); // baseline source (oak + frame.json metal) for non-saw movers
        partPivots = new float[SawmillParts.Rules.Length][];
        var meshes = new List<MeshData?>();
        var idxs = new List<int>();

        // F-S04-06: parse the ~700-element sawmill model at most ONCE per cold build, not once per mover. Each mover that
        // misses the client mesh cache tesselates from a fresh Clone of that single parse — the same single-parse idiom
        // the log path uses (BuildLogGroupMeshes), instead of a Shape.TryGet re-parse per part. A warm cache still parses
        // nothing at all (GetOrCreate returns the cached mesh and never calls this). Membership is recomputed per clone (a
        // cheap name-chain walk, no JSON parse); the master is only ever Cloned, never blanked, so it stays pristine.
        Shape? master = null;
        MeshData? BuildMoverMesh(int keep, ITexPositionSource tex)
        {
            master ??= Shape.TryGet(c, ShapeLoc);
            if (master == null) return null;
            Shape work = master.Clone();
            var partOf = new Dictionary<ShapeElement, int>();
            ComputeMembership(work.Elements, new List<string>(), partOf);
            BlankElements(work.Elements, el => partOf[el] != keep);
            c.Tesselator.TesselateShape("sawmill", work, out MeshData mesh, tex, new Vec3f(0, rotY, 0));
            return mesh;
        }

        for (int i = 0; i < SawmillParts.Rules.Length; i++)
        {
            SawmillPartRule rule = SawmillParts.Rules[i];
            if (rule.Kind != SawmillPartKind.Mover) continue;
            if (!PartVisible(i)) continue; // conditional build-up — draw a mover only once its part is installed (T07.01)
            int idx = i;
            // T09.01: ONLY the saw blades follow the installed blade metal (the this["metal"] override); every other
            // mover paints from the block's own textures (frame.json baseline — oak + iron plate), so the blade metal
            // can't leak onto the crank/wheel/levers/input after a reload. Non-saw cache keys omit BladeMetalKey() —
            // now correct by construction (the blade metal no longer affects their mesh), not a latent bug.
            ITexPositionSource tex = rule.IsSaw ? this : blockTexSource!;
            string key = rule.IsSaw
                ? "iw-sawmill-part-" + rule.Id + "-" + rotY + "-" + BladeMetalKey()
                : "iw-sawmill-part-" + rule.Id + "-" + rotY;
            MeshData? mesh = ObjectCacheUtil.GetOrCreate(c, key, () => BuildMoverMesh(idx, tex));
            meshes.Add(mesh);
            idxs.Add(i);
            partPivots[i] = MeshCenter(mesh);
        }

        // T07.06 — facing support. The meshes above are tesselated with the rotateY baked in (about the block centre),
        // but the animation driver table (SawmillAnim) is authored in the native/south frame. Build the facing rotation R
        // about the block centre and the native pivots (= R⁻¹ · baked pivot) so GetPartMatrices can run the animation
        // native and conjugate each matrix by R (BuildMatricesFacing). On a south frame R is identity → native == baked,
        // a no-op. The rotateY here is the SAME value used to bake the meshes, so R and the meshes always agree.
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
    /// meshes (<c>RotateYDeg</c> about the centre), so the animation lines up with the rendered geometry.</summary>
    void BuildFacingMatrices(int rotYDeg)
    {
        if (rotYDeg == 0) { facingMat = null; facingMatInv = null; return; }
        facingMat = new Matrixf().Translate(0.5f, 0.5f, 0.5f).RotateYDeg(rotYDeg).Translate(-0.5f, -0.5f, -0.5f).Values;
        facingMatInv = new Matrixf().Translate(0.5f, 0.5f, 0.5f).RotateYDeg(-rotYDeg).Translate(-0.5f, -0.5f, -0.5f).Values;
    }

    /// <summary>Transform a point (block units) by a column-major <c>float[16]</c> matrix (index 4*col+row) — the same
    /// layout SawmillAnim / Matrixf use. Returns a fresh 3-vector.</summary>
    static float[] TransformPoint(float[] m, float[] p) => new float[]
    {
        m[0] * p[0] + m[4] * p[1] + m[8]  * p[2] + m[12],
        m[1] * p[0] + m[5] * p[1] + m[9]  * p[2] + m[13],
        m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14],
    };

    /// <summary>Geometric centre (block units, mesh frame) of a part mesh's vertex bounding box, or null for an empty
    /// mesh (→ the assembler falls back to a default pivot). For the south facing (rotY=0) this equals the native-frame
    /// member-bbox centre the animator computes as <c>pivotAuto</c>.</summary>
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

    // Per-frame matrix cache (F-S04-08): the last-built tree + the drive scalars (and the pivots reference) it was built
    // from. GetPartMatrices returns this instance unchanged when nothing moved (idle/unpowered machine), skipping the
    // whole rebuild. See there.
    float[]?[]? matsCache;
    float[]?[]? matsCachePivots;
    float matsCacheAngle, matsCacheAngleFwd, matsCacheAngleDisc, matsCacheProgress;
    // SpeedDecouplingPlan T02.03 — the config-derived pose input the tree was built from: SawmillWorkScale (multiplies the
    // fwd clock → thetaFwdGeared). It belongs in the dirty-check too, so a config change of M at a FROZEN clock — a mill
    // sitting idle between syncs, so animAngleFwd/Disc don't move — rebuilds the tree instead of holding the stale-tempo
    // pose until the clock next advances. This makes matsCache a true pure-function cache, correct on its own rather than
    // relying on OnClientConfigApplied nulling it (which it still does — belt-and-braces). SawmillFwdWrapBound is
    // deliberately NOT keyed: it never enters the pose formula, only WHERE animAngleFwd folds, i.e. the value of
    // animAngleFwd — already keyed above. The disc-gearing dev knob was baked to false (T04.03), so the discs always read
    // animAngleDisc (already keyed) and there is no flag to key. Seeded to the config defaults so the first frame's
    // pivots-ref miss (not a value miss) is what builds the initial cache.
    float matsCacheWorkScale = 1f;

    /// <summary>SpeedDecouplingPlan T01.04 — invalidate the client render when the server config is adopted after this
    /// machine already tesselated (the ConfigSyncPacket and the chunk are not ordered; see the subscription in
    /// <see cref="Initialize"/>). The M-derived constants live in <see cref="ImmersiveWoodworkingConfig.SawmillWorkScale"/>
    /// / <see cref="ImmersiveWoodworkingConfig.SawmillFwdWrapBound"/>, which the clocks (E02) read live each
    /// frame. Since T02.03 keyed the matrix cache on workScale directly, a changed tempo is now caught by
    /// <see cref="GetPartMatrices"/>'s own dirty-check on the next frame — even at a frozen
    /// clock; nulling <see cref="matsCache"/> + <see cref="matsCachePivots"/> here is belt-and-braces (a guaranteed clean
    /// rebuild), not the sole trigger it was at T01.04. The <c>MarkBlockDirty</c> is the job still unique to this hook: it
    /// re-tesselates on config arrival (so the interaction-help HUD refreshes and any future config-dependent MESH would
    /// rebuild). No CURRENT mesh depends on M — T02.03 established the hidden-slice count is driven by
    /// <see cref="clientProgress"/> (itself geared through <see cref="FeedAdvance"/>), the same at a given progress at any
    /// M — so MarkBlockDirty is cheap insurance here (once per join, and on a live dev /reload), not a live need.
    /// Client-only: server BEs never subscribe, and <see cref="GetPartMatrices"/> is a no-op there.</summary>
    void OnClientConfigApplied()
    {
        if (capi == null) return;
        matsCache = null; matsCachePivots = null;
        capi.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    // AuditFixPlan T05.07 / F-L10-05 — THIS machine's animation buffers, reused every frame (one per BE, never shared:
    // a static would let two sawmills overwrite each other's pose). Built lazily on the first client frame; on the
    // server nothing ever asks for matrices, so the arrays are never allocated there.
    SawmillAnimState? animState;

    /// <summary>
    /// T07.02 — one animation matrix per part (<c>float[16]</c>, indexed to <see cref="SawmillParts.Rules"/>), built by
    /// <see cref="SawmillAnim.BuildMatrices"/> from the drive angle and feed progress. The renderer calls this each
    /// frame and applies each mover's matrix to its mesh. Returns null before the meshes (and pivots) are built, so the
    /// renderer falls back to drawing the rest pose.
    ///
    /// The angle is FROZEN when the machine isn't cutting (<see cref="IsComplete"/> &amp;&amp;
    /// <see cref="EffectiveSawSpeed"/> &gt; 0): below the torque gate or on a half-built frame the saw stands rather than
    /// creeping. At a frozen angle of 0 every driver collapses to identity, so an idle machine renders at exactly its
    /// authored rest pose (the T07.01 look). The feed (carriage/log) follows the synced <see cref="sawmillProgress"/>.
    /// </summary>
    public float[]?[]? GetPartMatrices(float dt)
    {
        if (partPivotsNative == null) return null;
        AdvanceAnimAngle(dt);
        // Run the animation in the native frame (native pivots) and conjugate each matrix by the facing rotation, so the
        // motion is correct on the facing-baked meshes (T07.06). facingMat/Inv are null on a south frame → BuildMatrices
        // verbatim, no per-part cost. The result is in the world-block-local (baked) frame, exactly what the renderer
        // and the effects layer (which reads the baked partPivots) expect — so neither needed any change.
        // THREE drive clocks (the dual-source rectifier of BlockEntityChopper.GetPartMatrices, plus the T02.02 disc split):
        // (1) the SIGNED animAngle (mainRotorTwoway ONLY — the twoway coupling, reverses with the shaft) × FacingDriveSign,
        //     so that ONE disc turns the same way as the connected network axle on the facings where the conjugated spin
        //     axis lands on the NEGATIVE world axle axis (T07.06);
        // (2) the RECTIFIED animAngleFwd × SawmillWorkScale — the GEARED WORK CLOCK (Src="fwd") for the crank disc
        //     rotorDefault4 and the whole crank/sash/saw/levers/wheel/spring chain it heads: one fixed spin direction
        //     regardless of shaft direction, geared to 1/M of the shaft (SpeedDecouplingPlan T02.01; workScale=1/M, M=1 →
        //     ×1.0f exact = bit-identical to the pre-feature pose);
        // (3) the DISC CLOCK (Src="disc") for the three cosmetic discs rotorDefault1..3 — SpeedDecouplingPlan T02.02: the
        //     RAW shaft-rate rectified animAngleDisc, so the discs hold 1:1 with the shaft (the honest coupling look, the
        //     rate that matches the connected vanilla gear). The dev knob that used to also allow gearing them with the
        //     mechanism was baked to this 1:1 look after the author's live judgment (T04.03). At M=1 animAngleDisc equals
        //     animAngleFwd, bit-identical to the pre-feature pose.
        // (2) and (3) are handed in RAW of FacingDriveSign (AuditFixPlan T07.06 / F-L2-16): a rectified clock only ever
        // grows, so × −1 makes it shrink and the feed ratchet — a floor() accumulator over the fwd clock (SawmillAnim
        // CyclicStep) — steps BACKWARD on NORTH and EAST while the prog-driven carriage keeps feeding forward. The accepted
        // cosmetic cost: the rectified discs ignore the line direction, mirroring it on NORTH/EAST exactly as the
        // confirmed-good chopper does. Splitting the ratchet onto its own raw clock would desync it from the pawl
        // (lever_active), which rides the wheel rim and inherits its stroke from the crank disc's MATRIX — the rods have no
        // θ of their own.
        //
        // Dirty-check (F-S04-08): the matrix tree is a pure function of the drive scalars below, the config-derived pose
        // input workScale, and partPivotsNative (the facing matrices + FacingDriveSign are per-BE constants). Read the
        // config value ONCE here so the guard and the build below see the same snapshot within the frame. Since
        // SpeedDecouplingPlan T02.03 keys workScale directly, a config M change is caught here on the next frame even at a
        // frozen clock; OnClientConfigApplied (T01.04) still nulls the cache on a sync — belt-and-braces, no longer the
        // sole trigger. animAngleDisc is keyed too though it advances in lockstep with animAngleFwd — cheap and
        // self-documenting. The disc-gearing dev knob was baked to false (T04.03), so the discs always read animAngleDisc
        // (already keyed) — no flag to key. Skip the ~100-array rebuild when none changed — an idle/unpowered machine holds
        // a frozen pose every frame (the clocks only advance while cutting). Keyed also on the partPivotsNative REFERENCE
        // so a mover re-tesselation (a new array) invalidates the cache automatically. Every consumer — the renderer's
        // PartModel and UpdateMotionAudio — treats the result read-only, so returning the same instance across frozen
        // frames is safe.
        float workScale = Config.SawmillWorkScale;
        if (ReferenceEquals(matsCachePivots, partPivotsNative) && animAngle == matsCacheAngle
            && animAngleFwd == matsCacheAngleFwd && animAngleDisc == matsCacheAngleDisc && clientProgress == matsCacheProgress
            && workScale == matsCacheWorkScale)
            return matsCache;
        matsCachePivots = partPivotsNative;
        matsCacheAngle = animAngle; matsCacheAngleFwd = animAngleFwd; matsCacheAngleDisc = animAngleDisc; matsCacheProgress = clientProgress;
        matsCacheWorkScale = workScale;
        animState ??= SawmillAnimState.Create();
        float thetaFwdGeared = animAngleFwd * workScale;
        // Discs hold 1:1 with the shaft — the raw-shaft rectified animAngleDisc (dev knob SawmillGearConnectionDiscs baked
        // to false, T04.03). The geared alternative (thetaFwdGeared) is no longer selectable.
        float thetaDisc = animAngleDisc;
        matsCache = SawmillAnim.BuildMatricesFacing(animState, partPivotsNative, animAngle * FacingDriveSign(), thetaFwdGeared, thetaDisc, clientProgress, facingMat, facingMatInv);
        return matsCache;
    }

    /// <summary>Advance <see cref="animAngle"/> and its two rectified companions <see cref="animAngleFwd"/> /
    /// <see cref="animAngleDisc"/> by the shaft's wrap-corrected rotation while the machine is cutting, keeping them
    /// UNWRAPPED so the feed-ratchet rotors' accumulating ratchet (SawmillAnim) spins continuously instead of snapping back
    /// each revolution — yet each folded at a bound where the fold is invisible and float precision can't drift over a long
    /// session. The SIGNED animAngle drives only <c>mainRotorTwoway</c>, which reads it mod 2π, so its bound just has to be
    /// a whole-turn multiple: it folds at the M-independent TWOPI·<see cref="ImmersiveWoodworkingConfig.SawmillDefaultWork"/>.
    /// The RECTIFIED animAngleFwd (geared mechanism) folds at <see cref="ImmersiveWoodworkingConfig.SawmillFwdWrapBound"/>
    /// (=TWOPI·SawmillDefaultWork·M), so the GEARED work clock <c>animAngleFwd × SawmillWorkScale</c> lands on exactly one
    /// whole ratchet revolution at the fold at any M (SpeedDecouplingPlan T02.01). The RECTIFIED animAngleDisc (the raw
    /// shaft-rate clock the cosmetic connection discs hold 1:1 with, SpeedDecouplingPlan T02.02) folds at the SAME
    /// M-independent TWOPI·SawmillDefaultWork as the signed clock — a 1:1 disc reads it directly, so its bound must be a
    /// whole shaft revolution multiple, NOT the geared TWOPI·50·M (which snaps a 1:1 disc when 50·M isn't integer). M=1 →
    /// all three bounds = TWOPI·50 and animAngleDisc ≡ animAngleFwd, the pre-feature value. Frozen — with the reference
    /// re-seeded — when not cutting, so a resume banks no jump. Mirrors <see cref="FeedAdvance"/>'s wrap correction AND
    /// its Nyquist hybrid (below): the two RECTIFIED clocks step by the continuous-speed magnitude above π/frame so the
    /// visible mechanism can't freeze at high RPM while the (hybridised) cut keeps going (ModDbBugfixPlan T02.03, folded
    /// into SpeedDecouplingPlan T04.02 — the chopper's AdvanceAnimAngle twin, T03.05). Client-only (called once per render
    /// frame from <see cref="GetPartMatrices"/> with that frame's <paramref name="dt"/>).</summary>
    void AdvanceAnimAngle(float dt)
    {
        float ang = AngleRad;
        if (IsComplete && EffectiveSawSpeed() > 0f)
        {
            float d = ang - animAnglePrev;
            if (d > GameMath.PI) d -= GameMath.TWOPI;       // fold the [0,2π) wrap back to the true short-arc delta
            else if (d < -GameMath.PI) d += GameMath.TWOPI;

            // Nyquist hybrid for the RECTIFIED clocks (ModDbBugfixPlan T02.03, folded into SpeedDecouplingPlan T04.02 — the
            // chopper's AdvanceAnimAngle T03.05 twin): the wrapped |Δ| is EXACT only below π/frame; above it the fold reads
            // a forward step as a small reverse, |Δ| collapses and the whole geared mechanism FREEZES while the server (now
            // also hybridised, FeedAdvance) keeps cutting. The continuous NetworkSpeed gives the true magnitude with no
            // Nyquist limit. Below π/frame keep the exact wrapped |Δ| (speed=1 / any normal RPM stays bit-identical to the
            // pre-fix render — invariant I8); only above it fall back to the speed-derived one. The per-side dt differs
            // (client frame vs 25 ms server tick), so each side crosses its own Nyquist at a different RPM, but each stays
            // exact below its own limit, so the cut and the mechanism accumulate the same forward rate. The SIGNED animAngle
            // (Part B) is deliberately LEFT on the raw wrapped d — like the chopper's animAngleSigned, its sign aliasing at
            // very high TrueSpeed is the ratified cosmetic edge (the multiplier / Nyquist fix never touches the coupling).
            float absD = d < 0f ? -d : d;
            float speedInc = NetworkSpeed * ShaftRadPerNetworkSpeedSecond * dt;
            float inc = speedInc > GameMath.PI ? speedInc : absD;

            // SIGNED clock (mainRotorTwoway only, read mod 2π): a whole-revolution bound, M-independent — the multiplier
            // never touches the twoway coupling. Raw d (see the hybrid note above): Part B tracks the shaft both ways.
            animAngle += d;
            float signedWrap = GameMath.TWOPI * ImmersiveWoodworkingConfig.SawmillDefaultWork;
            if (animAngle >= signedWrap) animAngle -= signedWrap;
            else if (animAngle <= -signedWrap) animAngle += signedWrap;

            // GEARED rectified clock: advance by the hybrid magnitude inc so the fixed-direction mechanism (Src="fwd" — the
            // crank disc + the crank/sash/saw/levers/wheel/spring chain it heads) never reverses when the shaft is driven
            // the other way; only the mainRotorTwoway coupling (on animAngle) does. It folds at the M-dependent
            // SawmillFwdWrapBound (=TWOPI·SawmillDefaultWork·M), so the geared work clock animAngleFwd·SawmillWorkScale
            // completes exactly one SawmillDefaultWork-tooth revolution per fold at any M (SpeedDecouplingPlan T02.01; at
            // M=1 this equals the signed bound TWOPI·50, bit-identical to the pre-feature single wrap).
            animAngleFwd += inc;
            // WHILE, not IF: the speed band now reaches 100×, where SawmillFwdWrapBound = 2π·50/speed shrinks to ~π — of
            // the same order as a single frame's step. A lone subtraction could then leave a residue ≥ the bound and the
            // accumulator would creep unbounded on a very fast network at the top of the band; and the hybrid inc above can
            // itself carry several revolutions in one frame at high RPM. The loop folds however many whole periods a frame
            // jumped. Guarded on fwdWrap > 0 so a degenerate 0 bound can't spin forever (the clamp keeps it ≥ π, this is
            // belt-and-braces). At normal speeds inc ≪ bound so it runs 0–1 times, bit-identical to the old IF.
            float fwdWrap = Config.SawmillFwdWrapBound;
            while (fwdWrap > 0f && animAngleFwd >= fwdWrap) animAngleFwd -= fwdWrap;

            // DISC rectified clock (SpeedDecouplingPlan T02.02): the same +inc as animAngleFwd, but folded at the
            // M-INDEPENDENT signedWrap (=TWOPI·SawmillDefaultWork = a whole SawmillDefaultWork shaft revolutions) — the raw
            // shaft-rate clock the three cosmetic discs hold 1:1 with (the gear-them alternative was baked out, T04.03). A 1:1
            // disc reads it directly, so the fold has to land on a whole revolution; animAngleFwd's M-dependent bound
            // would jump a 1:1 disc by 50·M revolutions and visibly snap when 50·M isn't integer (e.g. M=1/3). At M=1
            // fwdWrap == signedWrap and inc == |Δ|, so this is bit-identical to animAngleFwd — the discs match the
            // pre-feature pose. WHILE, not IF: the hybrid inc can jump several whole revolutions at extreme RPM (the bound
            // is large, TWOPI·50, but a single subtract could still leave a residue ≥ bound); at normal speeds it runs 0–1
            // times, bit-identical to the old IF.
            animAngleDisc += inc;
            while (animAngleDisc >= signedWrap) animAngleDisc -= signedWrap;
        }
        animAnglePrev = ang;
    }

    /// <summary>
    /// T07.06 — per-facing sign for the SIGNED input drive (animAngle — the mainRotorTwoway coupling disc alone), so its
    /// visible spin matches the connected mechanical-network axle. The driver table turns the shaft about the native +X; the facing
    /// conjugation rotates that axis to R·(+X), which lands on the NEGATIVE world axle axis for NORTH (−X) and EAST
    /// (−Z). The connected vanilla axle is drawn by the engine's own mech renderer about the POSITIVE axis (its
    /// <c>AxisSign</c>, ours plays no part — <see cref="BEBehaviorSawmillMP"/> renders nothing and keeps no axis,
    /// T07.08), so on those two facings the conjugated spin is reversed vs the gears and
    /// the drive angle must be flipped. SOUTH/WEST already agree. Confirmed in-game (the user saw NORTH + the
    /// west↔east placement spinning the wrong way relative to the connected gears). It multiplies ONLY the SIGNED
    /// animAngle — never the RECTIFIED animAngleFwd (that would step the feed ratchet backward on those two facings —
    /// T07.06 / F-L2-16, see <see cref="GetPartMatrices"/>) and never the <c>prog</c>-driven feed (carriage/log), which
    /// keeps the facing-forward direction untouched.
    /// </summary>
    float FacingDriveSign() => (Facing == BlockFacing.NORTH || Facing == BlockFacing.EAST) ? -1f : 1f;

    /// <summary>
    /// T07.03 — advance the client's smooth feed progress for the carriage/log, integrated locally between server syncs
    /// (called once per render frame by <see cref="SawmillRenderer"/> on the opaque stage). Mirrors the server tick's
    /// accrual gate (<see cref="OnSawmillTick"/>): only while a sawable, product-resolvable log is loaded
    /// (<see cref="GetPassBatch"/>) and the drive turns above the torque gate (<see cref="EffectiveSawSpeed"/>).
    /// Holds at 1 until the server completes the pass and syncs a reset
    /// (which <see cref="FromTreeAttributes"/> snaps us to). Same accrual formula as the server so the visual tracks the
    /// real cut; tiny drift is corrected on the next sync. Client-only (no renderer / no caller on the server).
    /// </summary>
    public void AdvanceClientFeed(float dt)
    {
        // Same single start gate as the server tick (T06.05) so the visual can't drift from the cut; re-seed the
        // angle reference while idle (no banked jump on resume). The increment is the shaft's wrap-corrected rotation
        // (FeedAdvance), NOT a time integral — the carriage/wheel/spring step in lockstep with the saw stroke exactly as
        // the server paces the cut. dt is passed only to FeedAdvance's Nyquist hybrid (ModDbBugfixPlan T02.03, folded into
        // SpeedDecouplingPlan T04.02): the continuous-speed fallback that keeps the feed from freezing at high RPM needs
        // this frame's step. Signature kept: the renderer calls this once per opaque frame. The !CanSaw() branch also
        // closes the seed latch (T07.07), like the server tick's: the sample it takes is a real reference, so only the
        // first frame of a mill that loads already cutting is spent seeding.
        if (!CanSaw()) { lastClientFeedAngle = AngleRad; clientFeedSeeded = true; return; }
        clientProgress += FeedAdvance(ref lastClientFeedAngle, ref clientFeedSeeded, dt);
        if (clientProgress > 1f) clientProgress = 1f; // hold at full; the pass-complete sync snaps it back to 0
    }

    /// <summary>The workpiece-log mesh as the renderer draws it: the body plus every working slice the saw has not fed
    /// through yet, merged into ONE upload (T05.07) and riding the carriage matrix (<see cref="LogRuleIndex"/>). Null
    /// until <see cref="SyncLogMesh"/> has merged one, and whenever no sawable log is loaded.</summary>
    public MultiTextureMeshRef? LogMeshRef => logMeshRef;

    /// <summary>True when a workpiece log is loaded — keyed on the CPU groups, not on the GPU mesh, because the renderer's
    /// "nothing to draw" gate reads this BEFORE <see cref="SyncLogMesh"/> gets to upload the first merge.</summary>
    public bool HasLogMesh => logGroups != null;

    // The cut runs LINEARLY over this window of the carriage feed, not the full [0,1] pass: nothing is cut until the
    // carriage reaches CutStart, and the whole log is through by CutEnd. The leading/trailing fractions are where the log
    // overhangs the blades before they bite / after they clear, so the slices map to [CutStart, CutEnd] — tuned to the
    // model so a slice vanishes exactly as the carriage carries it through the saw.
    const float CutStart = 0.1f;
    const float CutEnd = 0.9f;

    /// <summary>How many working slices have been fed through the saw and are now hidden, from the smooth client feed
    /// progress remapped onto the cut window [<see cref="CutStart"/>, <see cref="CutEnd"/>]:
    /// <c>floor(clamp01((clientProgress − CutStart)/(CutEnd − CutStart)) · N)</c>. Slice 1 (nearest the saw) hides first,
    /// slice N (farthest) last, in lockstep with the carriage (both read <see cref="clientProgress"/>) so a slice
    /// vanishes exactly as the carriage carries it through the blades — the progressive cut. 0 before CutStart, N (all
    /// hidden) at/after CutEnd (the log is then consumed and the whole mesh cleared on the pass-complete sync).</summary>
    public int HiddenLogSliceCount
    {
        get
        {
            if (logGroups == null) return 0;
            int n = logGroups.Length - 1; // slice numbers 1..n
            if (n <= 0) return 0;
            float t = GameMath.Clamp((GameMath.Clamp(clientProgress, 0f, 1f) - CutStart) / (CutEnd - CutStart), 0f, 1f);
            int hidden = (int)(t * n);
            return hidden > n ? n : hidden;
        }
    }

    /// <summary>Matrix index (into <see cref="SawmillParts.Rules"/> / <see cref="GetPartMatrices"/>) of the workpiece
    /// log — its driver rides the carriage, so this is the carriage's transform.</summary>
    public int LogRuleIndex => LogRuleIndexValue;

    /// <summary>(Re)build the workpiece-log meshes when the loaded input log changes (or clear them when none): tesselate
    /// the model's <c>log*</c> part — split into the body (<c>log_main*</c>) and one mesh per working slice
    /// (<c>log_working_N_*</c>) — with a species-bound <see cref="LogTexSource"/> built from the input block (reuse of the
    /// sawhorse texturing logic via <see cref="LogTexSource.ForLog"/> — mod-independent because the textures come from
    /// the log itself, <c>source-conventions.md</c>). The split lets
    /// the renderer hide slices in order as the carriage feeds them through the saw WITHOUT re-tesselating — the cut is
    /// a merge-time toggle (<see cref="HiddenLogSliceCount"/>, <see cref="SyncLogMesh"/>). Keyed on the input log code+kind so it rebuilds only on
    /// an actual change; cleared when the input is empty / not sawable. Client-only; called from <c>Initialize</c> and
    /// every tree sync (<c>FromTreeAttributes</c>) alongside <see cref="UpdateRenderer"/>.
    ///
    /// The CPU meshes (body + every slice) are identical for a given species + kind + facing, so they are built ONCE per
    /// species and cached (<see cref="ObjectCacheUtil"/>, like the mover meshes) — see <see cref="BuildLogGroupMeshes"/>.
    /// Loading a log then only merges + uploads the cached meshes instead of re-parsing + re-tesselating the whole
    /// ~700-element model once per slice (24 slices + body = 25× on the main thread), which was the place-a-log freeze.</summary>
    public void UpdateLogMesh()
    {
        if (capi == null || Block == null || Block.BlockId == 0 || LogRuleIndexValue < 0) return;

        ItemStack? log = InputSlot.Itemstack;
        SawInput kind = InputSawKind;
        string? wood = InputWoodSpecies;
        string key = (log?.Block != null && kind != SawInput.None && wood != null)
            ? log.Block.Code + "|" + kind
            : "none";
        if (key == logMeshKey) return; // unchanged → keep the current meshes (incl. the null/no-log case)

        DisposeLogMeshes();
        logMeshKey = key;
        if (key == "none") return;

        int rotY = (int)(Block.Shape?.rotateY ?? 0f);
        // The CPU meshes (index 0 = body, 1..N = working slices) depend only on species+kind+facing, so build them once
        // and cache (like the mover meshes). A re-load of the same wood is then a pure merge + upload — no shape re-parse /
        // re-tesselate. This is what removes the place-a-log freeze; the merged GPU mesh is still built and disposed
        // per machine.
        MeshData?[] groups = ObjectCacheUtil.GetOrCreate(capi,
            "iw-sawmill-log-" + key + "-" + rotY,
            () => BuildLogGroupMeshes(capi, log!.Block, kind, wood!, rotY));

        logGroups = groups.Length > 0 ? groups : null;
        SyncLogMesh(); // upload the first merge now, so the log is on screen the same frame it lands in the input
    }

    /// <summary>
    /// T05.07 / F-L10-06 — keep the merged workpiece mesh in step with the cut: rebuild it when (and only when) the
    /// number of slices the saw has fed through changes. Everything visible rides the carriage matrix, so it can be ONE
    /// upload; the split stays on the CPU side, where the progressive cut costs nothing per frame.
    ///
    /// Called once per frame from the renderer's opaque stage, BEFORE the off-screen gate: an unseen machine still casts
    /// a shadow, and the shadow stages draw this very mesh. Over a full pass this fires at most once per slice (24), each
    /// time a merge of already-tesselated meshes plus one upload — against the 24 extra draw calls per stage, on three
    /// stages, every frame, that the old per-slice split paid.
    /// </summary>
    public void SyncLogMesh()
    {
        if (capi == null || logGroups == null) return;
        int hidden = HiddenLogSliceCount;
        // logMeshHidden is -1 until the first merge and is reset there by DisposeLogMeshes, so this alone is the whole
        // "is the GPU mesh current" test — including the degenerate case where every group came back empty and the merge
        // legitimately produced nothing (retrying it every frame would be the only alternative).
        if (hidden == logMeshHidden) return;

        MeshData?[] groups = logGroups;
        MeshData? merged = null;
        // Body (group 0) first, then every slice the saw hasn't reached yet (slice numbers hidden+1..N, in cut order).
        for (int k = 0; k < groups.Length; k++)
        {
            if (k > 0 && k <= hidden) continue;              // already fed through the blades
            MeshData? g = groups[k];
            if (g == null || g.VerticesCount == 0) continue;
            if (merged == null) merged = g.Clone();          // clone: the group meshes belong to the shared process cache
            else merged.AddMeshData(g);
        }

        logMeshRef?.Dispose();
        logMeshRef = merged != null ? capi.Render.UploadMultiTextureMesh(merged) : null;
        logMeshHidden = hidden;
    }

    /// <summary>Build every workpiece-log sub-mesh — index 0 = body (<c>log_main*</c>), 1..N = working slices
    /// (<c>log_working_N_*</c>) — in ONE shape parse: load the model + compute the per-element log grouping ONCE, then
    /// tesselate each group from a FRESH clone (so a destructive blank never crosses between groups, keeping the
    /// fresh-shape-per-tesselation guarantee the old per-call reload had). Replaces the old path that parsed AND regrouped
    /// the whole ~700-element model once per slice (25 full JSON parses + 25 regroupings on the main thread — the
    /// place-a-log freeze). Cached per species+kind+facing by <see cref="UpdateLogMesh"/>, so this whole build runs at
    /// most once per wood. Returns an empty array if the shape fails to load.</summary>
    MeshData?[] BuildLogGroupMeshes(ICoreClientAPI c, Block logBlock, SawInput kind, string wood, int rotY)
    {
        Shape? master = Shape.TryGet(c, ShapeLoc);
        if (master == null) return System.Array.Empty<MeshData?>();

        // Per-element log group, flattened in PRE-ORDER once (was recomputed per group). Replayed onto each clone by
        // index — Shape.Clone preserves element order, so the i-th element of a clone has groups[i]. A log SECTION
        // (quarter of a giant trunk) draws the quarter body (log_main_quad*) and blanks the round body (log_main*); any
        // other input does the reverse — the working slices are shared. The cache key (UpdateLogMesh) folds in `kind`,
        // so the quarter and whole-log group meshes never collide.
        var groups = new List<int>();
        FlattenLogGroups(master.Elements, new List<string>(), groups, kind == SawInput.LogSection);
        int max = 0;
        for (int i = 0; i < groups.Count; i++) if (groups[i] > max) max = groups[i];

        LogTexSource tex = LogTexSource.ForLog(c, logBlock, kind, wood);
        var meshes = new MeshData?[max + 1];
        for (int g = 0; g <= max; g++)
        {
            // Fresh clone per tesselation (one JSON parse total, not one per group): keep only group g, blank the rest.
            Shape work = master.Clone();
            int idx = 0;
            BlankByFlatGroup(work.Elements, groups, ref idx, g);
            c.Tesselator.TesselateShape("sawmill", work, out MeshData mesh, tex, new Vec3f(0, rotY, 0));
            meshes[g] = mesh;
        }
        return meshes;
    }

    /// <summary>Pre-order walk filling <paramref name="outGroups"/> with each element's log group
    /// (<see cref="LogGroupFor"/>): -1 = not a log element (or the wrong body for this input), 0 = body, k≥1 = working
    /// slice k. Order matches <see cref="Shape.Clone"/>, so the flat list can be replayed onto a clone by index.
    /// <paramref name="wantQuad"/> selects which body (round vs quarter) is group 0 (see <see cref="LogGroupFor"/>).</summary>
    static void FlattenLogGroups(ShapeElement[]? els, List<string> chain, List<int> outGroups, bool wantQuad)
    {
        if (els == null) return;
        foreach (ShapeElement el in els)
        {
            chain.Add(el.Name ?? "");
            outGroups.Add(LogGroupFor(chain, wantQuad));
            FlattenLogGroups(el.Children, chain, outGroups, wantQuad);
            chain.RemoveAt(chain.Count - 1);
        }
    }

    /// <summary>Blank (no faces → no quads) every element whose pre-order group in <paramref name="groups"/> isn't
    /// <paramref name="keepGroup"/>, walking <paramref name="els"/> in the same pre-order as <see cref="FlattenLogGroups"/>.
    /// Mutates the passed (cloned) elements only.</summary>
    static void BlankByFlatGroup(ShapeElement[]? els, List<int> groups, ref int idx, int keepGroup)
    {
        if (els == null) return;
        foreach (ShapeElement el in els)
        {
            if (groups[idx++] != keepGroup) el.FacesResolved = new ShapeElementFace[6];
            BlankByFlatGroup(el.Children, groups, ref idx, keepGroup);
        }
    }

    void DisposeLogMeshes()
    {
        logMeshRef?.Dispose();
        logMeshRef = null;
        logMeshHidden = -1;
        logGroups = null;   // the group meshes themselves belong to the process cache — never disposed here
        logMeshKey = null;
    }

    string BladeMetalKey() => bladeStack?.Collectible?.Code?.ToString() ?? "none";

    // ───────────────────────── Shape split (membership + face-blank tesselate) ─────────────────────────

    /// <summary>Tesselate only the elements whose part index satisfies <paramref name="keepPart"/>: load a fresh shape,
    /// compute each element's part (the animator's match-any-ancestor-name rules), blank the faces of every element NOT
    /// kept, then tesselate with <paramref name="tex"/>. A fresh shape per call avoids any resolve-caching cross-talk
    /// between subsets. Facing is baked in via <paramref name="rotY"/> so renderer parts align with the chunk frame.</summary>
    MeshData? BuildMesh(ICoreClientAPI c, System.Func<int, bool> keepPart, ITexPositionSource tex, int rotY)
    {
        Shape? shape = Shape.TryGet(c, ShapeLoc);
        if (shape == null) return null;

        var partOf = new Dictionary<ShapeElement, int>();
        ComputeMembership(shape.Elements, new List<string>(), partOf);
        BlankElements(shape.Elements, el => !keepPart(partOf[el]));

        c.Tesselator.TesselateShape("sawmill", shape, out MeshData mesh, tex, new Vec3f(0, rotY, 0));
        return mesh;
    }

    static void ComputeMembership(ShapeElement[]? els, List<string> chain, Dictionary<ShapeElement, int> outMap)
    {
        if (els == null) return;
        foreach (ShapeElement el in els)
        {
            chain.Add(el.Name ?? "");
            outMap[el] = SawmillParts.IndexFor(chain);
            ComputeMembership(el.Children, chain, outMap);
            chain.RemoveAt(chain.Count - 1);
        }
    }

    /// <summary>Per-element log sub-group: -1 = not a log element (or the body that DOESN'T match this input); 0 = log
    /// body (always visible during a pass); k≥1 = working slice k (<c>log_working_k_*</c>, hidden once the carriage has
    /// fed it through the saw). Decided from the same ancestor chain as <see cref="ComputeMembership"/>, gated on the
    /// element actually belonging to the log part (so only the log is ever drawn here). Used per element by
    /// <see cref="FlattenLogGroups"/>.
    ///
    /// The model carries TWO body variants: the round body (<c>log_main*</c>) and the quarter body (<c>log_main_quad*</c>,
    /// added for log-section inputs — a quarter of a giant trunk). <paramref name="wantQuad"/> (true for a
    /// <see cref="SawInput.LogSection"/> input) picks which one is the drawn body (group 0); the other is left ungrouped
    /// (-1 → blanked), so exactly one body draws. The working slices are shared by both. For a whole/debarked log
    /// (<c>wantQuad</c> false) this is identical to the old single-body behaviour: <c>log_main*</c> → 0, and the quarter
    /// body simply never draws. <c>log_main_quad</c> also starts with <c>log_main</c>, so the longer prefix is tested first.</summary>
    static int LogGroupFor(IReadOnlyList<string> chain, bool wantQuad)
    {
        if (SawmillParts.Rules[SawmillParts.IndexFor(chain)].Kind != SawmillPartKind.Log) return -1;
        for (int c = chain.Count - 1; c >= 0; c--)
        {
            Match m = WorkingSliceRe.Match(chain[c]);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        // A body element (no working-slice number). Draw only the body that matches the loaded input's kind.
        bool quadBody = ChainHasName(chain, "log_main_quad");
        return quadBody == wantQuad ? 0 : -1;
    }

    /// <summary>Whether any name in <paramref name="chain"/> (root..self) starts with <paramref name="prefix"/> —
    /// the match-any-ancestor test <see cref="LogGroupFor"/> uses to tag an element (and its children) as the quarter
    /// body. Ordinal, case-sensitive, matching the model's Blender-exported names.</summary>
    static bool ChainHasName(IReadOnlyList<string> chain, string prefix)
    {
        for (int c = 0; c < chain.Count; c++)
            if (chain[c].StartsWith(prefix, System.StringComparison.Ordinal)) return true;
        return false;
    }

    static void BlankElements(ShapeElement[]? els, System.Func<ShapeElement, bool> blank)
    {
        if (els == null) return;
        foreach (ShapeElement el in els)
        {
            // Blank the RESOLVED faces (what the client tesselator reads). Faces->FacesResolved is an [OnDeserialized]
            // step that already ran in Shape.TryGet, and TesselateShape never re-resolves, so a 6-null FacesResolved
            // makes this element contribute no quads. (Faces itself is obsolete.)
            if (blank(el)) el.FacesResolved = new ShapeElementFace[6];
            BlankElements(el.Children, blank);
        }
    }

    // ───────────────────────── Interaction-help live refresh ─────────────────────────
    //
    // The vanilla interaction-help HUD (HudElementInteractionHelp) does NOT poll: it recomposes its bottom-screen
    // hints only when the looked-at block instance / position / selection-box index changes, or when the client
    // BlockChanged event fires for the selected cell (decompiled HudElementInteractionHelp.Every15ms / OnBlockChanged).
    // So a state change WITHOUT a re-hover — installing the final part, loading a log, product appearing — leaves the
    // hints stale until the player looks away and back. (The block-INFO box has no such issue: HudElementBlockAndEntity-
    // Info recomposes unconditionally every 500ms.) We force the hints to refresh by firing that BlockChanged ourselves.

    // Last help-relevant state signature (client). Seeded in Initialize; compared on every client tree sync so we only
    // re-fire when a hint would actually change (a running machine's per-pass / inventory syncs otherwise don't).
    string? lastHelpSig;

    /// <summary>The state the master block's <c>GetPlacedBlockInteractionHelp</c> branches on: the four mandatory part
    /// flags, whether a blade kit is in, whether the input holds a log, and — as a BOOLEAN, so a running machine's
    /// per-tick progress sync doesn't refire — whether the loaded log is still recoverable (under the recovery grace), which
    /// flips the Ctrl+RMB hint from "take" to "discard" at that boundary. (No product term: there is no output slot —
    /// the product is ejected forward.)</summary>
    string HelpSignature() => $"{hasSash}{hasCrankshaft}{hasLevers}{hasCarriage}{bladeStack != null}{InputEmpty}{LogRecoverable}";

    /// <summary>
    /// Recompose the interaction-help hints if <see cref="HelpSignature"/> changed since the last client sync. On the
    /// client, <c>BlockAccessor.MarkBlockDirty(pos)</c> raises <c>BlockChanged(pos)</c> (verified in the decompiled
    /// <c>ClientWorldMap.MarkBlockDirty</c>), and <c>HudElementInteractionHelp.OnBlockChanged</c> recomposes when the
    /// changed pos equals the player's selected cell — so marking just that cell is enough (a ghost filler forwards the
    /// help to the master). We only fire it when THIS client's player is actually looking at the machine: otherwise the
    /// next hover recomposes the hints anyway, and we avoid re-tesselating an unobserved (e.g. auto-fed) machine on
    /// every pass-boundary / inventory sync. The signature is still updated when not looking, so the first change after
    /// the player looks away doesn't spuriously fire. Client-only.
    /// </summary>
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
        DisposeLogMeshes();
        DisposeSound(); // E07/T07.05 — stop + free the loop so it doesn't hang in the world / leak
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        if (capi != null && modSystem != null) modSystem.ClientConfigApplied -= OnClientConfigApplied; // T01.04 — see OnBlockUnloaded
        DisposeRenderer();
        DisposeLogMeshes();
        DisposeSound(); // E07/T07.05
    }
}
