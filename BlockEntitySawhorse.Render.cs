using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

public partial class BlockEntitySawhorse
{
    // Authored stage models (Blender; source art/shapes/sawhorses_p*.json, copied to
    // shapes/block/). Indexed by SawhorseStage (Stage1=0 → one stick, Stage2=1 → two sticks, Complete=2 →
    // finished trestle with rope). Full asset paths, the form Shape.TryGet wants.
    static readonly AssetLocation[] StageShapes =
    {
        new AssetLocation("immersivewoodworking", "shapes/block/sawhorses_p1.json"),
        new AssetLocation("immersivewoodworking", "shapes/block/sawhorses_p2.json"),
        new AssetLocation("immersivewoodworking", "shapes/block/sawhorses_p3.json"),
    };

    // T05.04 / refactor — the number of discrete visual cut STATES the saw steps through on ONE log model: state 1 =
    // the whole log, state 20 = the last section (part_4) just before the log is consumed and nothing draws (E06). The
    // value is unchanged from the old "20-model" decision so the stage-yield boundaries {5,14,19,20} (E06) and the
    // fast-rip decimation still line up — but the states are now produced by HIDING element groups of a single
    // authored model (sawhorse_log.json), not by swapping 20 separate shape files. See HideSequence / EnsureLogState.
    const int LogStateCount = 20;

    // The single authored log model (Blender; source art/shapes/sawhorse_log.json). ONE
    // geometry set serves every wood species AND both inputs (log / debarked) — the species is bound by TEXTURE
    // (LogTexSource), same texture keys as before — so we never duplicate it per species. The cut is shown by blanking
    // element groups of THIS shape (the sawmill's face-blank idiom), not by 20 separate files.
    static readonly AssetLocation LogShape = new AssetLocation("immersivewoodworking", "shapes/block/sawhorse/sawhorse_log.json");

    // The quarter-log variant of the cut model (Blender; source art/shapes/sawhorse-log-quad.json,
    // copied to shapes/block/sawhorse/). Used INSTEAD of LogShape when the laid input is a log SECTION
    // (SawInput.LogSection — a quarter of a giant 2×2 trunk, redwood in vanilla), so a quarter reads visibly as a quarter
    // rather than a whole round billet. Its composition is IDENTICAL to LogShape — the same sawing_1..16 / part_1..4
    // element groups (verified) — only the UV/material layout differs (a quarter's #wood_corner_redwood corners still
    // prefix-match LogTexSource's wood_corner→end-grain binding), so it drives through the very SAME HideSequence /
    // 20-state cut progression (EnsureLogState) and the same species texturing (LogTexSource) with no other change.
    static readonly AssetLocation LogQuadShape = new AssetLocation("immersivewoodworking", "shapes/block/sawhorse/sawhorse_log_quad.json");

    // The dedicated progressive-debark model (Blender; source art/shapes/sawhorse-log-debarking.json,
    // copied to shapes/block/sawhorse/). A bare core (base_*) under nine bark bands (part_1_*..part_9_*) that hide in
    // ascending order as the strip progresses (T04.02). SEPARATE from the cut model: the bark-strip is shown only while
    // a still-barked log is NOT being sawn (see SelectLogState / ShowDebarkModel) — a rip uses LogShape, which textures
    // the bark itself, so the two never overlap and there is no cut/bark element interaction to author around. Same
    // three texture keys (wood / wood_corner / debarked_wood) as LogShape, so the SAME LogTexSource binds the species.
    static readonly AssetLocation DebarkShape = new AssetLocation("immersivewoodworking", "shapes/block/sawhorse/sawhorse_log_debarking.json");

    // The 20 visual events of a full rip, in order — each an element-group prefix (trailing '_' so "sawing_1_" can
    // never also match "sawing_10_"). Visual state V (1..LogStateCount) hides the first V-1 of these: four "sawing"
    // slices then part_1 (yield 1 at 5/20), eight slices then part_2 (14/20), four slices then part_3 (19/20); part_4
    // is the final section, removed by ConsumeLog at 20/20 (so it is never blanked here — the whole log just stops
    // drawing). This is a 1:1 re-labelling of the old 20-model swap onto element-hiding: the {5,14,19,20} yield
    // boundaries are unchanged, so the rip's gameplay (progress, stages, yield, wear) is identical — only the visual
    // differs. The per-stage slice counts (4 / 8 / 4 / 0) come straight from the authored sawing_1..16 grouping.
    static readonly string[] HideSequence =
    {
        "sawing_1_", "sawing_2_", "sawing_3_", "sawing_4_", "part_1_",
        "sawing_5_", "sawing_6_", "sawing_7_", "sawing_8_", "sawing_9_", "sawing_10_", "sawing_11_", "sawing_12_", "part_2_",
        "sawing_13_", "sawing_14_", "sawing_15_", "sawing_16_", "part_3_",
        "part_4_",
    };

    // Directional sawing (sawCutReversed): a reversed cut consumes the log from the OTHER end (bottom-up instead of
    // top-down). Getting there is NOT a plain Array.Reverse of HideSequence — two things must hold, both learned the
    // hard way:
    //  (1) The kerf GROUPS reverse ORDER, but the slivers WITHIN a group stay ASCENDING: 13→16, then 5→12, then 1→4
    //      (a full reverse gave 16→15→…→1, the wrong within-group direction — the saw appeared to run backwards).
    //  (2) The four boards must still drop on the SAME event indices {5, 14, 19, 20} as forward, so a board vanishes
    //      exactly when its plank batch drops. A full reverse pushed part_4 to event 1 and part_3 to event 2, so TWO
    //      sections vanished almost immediately, well before the first yield — the "части скрываются раньше чем надо"
    //      bug. Here each board sits right after ITS kerf group, so the drops stay at {5,14,19,20}.
    // Net: the boards come off bottom-up (part_4 → part_3 → part_2 → part_1) in lockstep with the reversed kerf, at
    // the forward timing. The eject side (SpawnSawnItem) flips off the SAME sawCutReversed flag, so planks fall from
    // the side the log is consumed. Yield/wear fire off sawProgress (StagesPassed), independent of this array, so the
    // order only changes which faces blank per visual state — never what a log drops.
    static readonly string[] HideSequenceReversed =
    {
        "sawing_13_", "sawing_14_", "sawing_15_", "sawing_16_", "part_4_",
        "sawing_5_", "sawing_6_", "sawing_7_", "sawing_8_", "sawing_9_", "sawing_10_", "sawing_11_", "sawing_12_", "part_3_",
        "sawing_1_", "sawing_2_", "sawing_3_", "sawing_4_", "part_2_",
        "part_1_",
    };

    // The nine bark bands of DebarkShape, hidden in ascending order as the strip progresses — the debark counterpart to
    // HideSequence. Bark-state V hides the first V-1 of these (trailing '_' so "part_1_" can't also match a longer name;
    // the bands are single-digit part_1..part_9, and the always-present base_* core is never listed, so it always
    // draws). State 1 = full bark (none hidden), state DebarkStateCount = the bare core (all nine hidden) = the
    // debarkedlog look (then CompleteDebark drops the bare log out, so this last state shows only momentarily).
    static readonly string[] DebarkHideSequence =
    {
        "part_1_", "part_2_", "part_3_", "part_4_", "part_5_", "part_6_", "part_7_", "part_8_", "part_9_",
    };

    // Directional debarking (barkReversed): peel the bands from the OTHER end. Unlike the rip's HideSequenceReversed
    // (which had to keep kerf slivers ascending within reversed GROUPS and align board drops to yield stages), this is
    // a PLAIN full reverse — the nine bands are single monotonic length-slices with NO interleaved elements and a
    // SINGLE yield stage that fires only at completion (BarkStageFractions = { 1.0 }), so there is nothing to keep
    // aligned: reversing the band order just peels −axis end → +axis end. The moving chip edge (SpawnBarkChips) flips
    // off the same barkReversed flag, so bands and chips always come off the same end.
    static readonly string[] DebarkHideSequenceReversed =
    {
        "part_9_", "part_8_", "part_7_", "part_6_", "part_5_", "part_4_", "part_3_", "part_2_", "part_1_",
    };

    // Bark-strip visual states: 0..9 bands hidden = 10 states (state 1 = full bark, state 10 = the bare core).
    const int DebarkStateCount = 10;

    /// <summary>
    /// Refresh the in-world meshes from the current state and flag the block for re-tesselation. Two independently
    /// change-gated parts: the trestle stage mesh (<see cref="RebuildStageMesh"/>, rebuilt only when the build
    /// stage / pairing changes) and the laid-log mesh (its cache reset only when the held log changes, otherwise a
    /// cheap state swap that lazily builds the one shown state). Client-side, main thread (Initialize /
    /// FromTreeAttributes); both gates exist because this runs on every MarkDirty, incl. each saw-progress tick.
    /// </summary>
    void UpdateMesh()
    {
        if (capi == null) return; // client-only

        // Stage mesh (the trestle itself): depends ONLY on the build stage + pair orientation, so rebuild it only
        // when one of those actually changed. UpdateMesh runs on every MarkDirty (incl. each saw-progress tick),
        // and re-tesselating the trestle shape every tick was wasted work; the log mesh below has its own gate.
        if (stageMesh == null || stage != stageMeshStage || inPair != stageMeshInPair || pairAxis != stageMeshAxis)
        {
            RebuildStageMesh();
        }

        // E05/E06: the laid-log mesh. The per-state arrays are re-resolved only when the held log itself changed —
        // placed / taken / consumed / different species; while the SAME log is sawn, a progress change (T06.07) is just
        // a cheap re-point to (and lazy build of) the one shown state, not a 20-shape re-tesselation.
        string? logKey = LogCacheKey();
        if (logKey != logMeshesFor) BuildLogMesh(); // log, a hide direction OR the axis changed → re-resolve (BuildLogMesh sets logMeshesFor)
        else SelectLogState();                       // same log + directions + axis, maybe new progress → swap only the shown state

        capi.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    /// <summary>The tag this BE's mesh-array references were last pointed at: the held log's block code, BOTH
    /// directional flags — the rip's cut side (<see cref="sawCutReversed"/>) and the debark peel end
    /// (<see cref="barkReversed"/>) — and the pair's axis. <see cref="UpdateMesh"/> re-points (not rebuilds) while all
    /// four are unchanged, and re-resolves both per-state arrays (the rip states in <c>logMeshes</c> AND the bark
    /// states in <c>debarkMeshes</c>) when the log changes, either direction flips (each blanks a different sequence)
    /// or the axis turns (it is baked into the mesh by <see cref="PlaceLogMesh"/>). Null when no log is held. Each
    /// direction is locked once its action has begun and syncs with its progress, so a flip only ever lands before any
    /// state &gt; 1 is shown (a fresh log at progress 0 draws the whole billet / full bark regardless).
    ///
    /// <para>This is the LOCAL re-point gate only. Since T05.01 the arrays themselves live in the process-wide cache
    /// under their own, COARSER keys (<see cref="ResolveStateCaches"/>) — the block code carries an orientation and a
    /// section's segment, which change nothing about the mesh — so a re-point here is normally a cache HIT that
    /// rebuilds nothing.</para></summary>
    string? LogCacheKey()
    {
        string? code = heldLog?.Block?.Code?.ToString();
        return code == null
            ? null
            : code + (sawCutReversed ? "|sr" : "|sf") + (barkReversed ? "|br" : "|bf") + (pairAxis == EnumAxis.X ? "|ax" : "|az");
    }

    /// <summary>
    /// (Re)build the trestle's stage mesh by tesselating the authored shape for the current
    /// <see cref="SawhorseStage"/> (sawhorses_p1/p2/p3), rotating it onto the pair axis when paired. Extracted from
    /// <see cref="UpdateMesh"/> and change-gated there (stage / inPair / pairAxis) so it runs only when the trestle's
    /// appearance actually changes — not on every saw-progress sync. Client-side, main thread. The block's own
    /// textures resolve the shapes' #oak / #rope keys (see sawhorse.json). If the shape can't be loaded (shouldn't
    /// happen — they ship with the mod), stageMesh is left null so OnTesselation draws the block's own shape instead
    /// of nothing.
    /// </summary>
    void RebuildStageMesh()
    {
        if (capi == null) return; // client-only

        int idx = GameMath.Clamp((int)stage, 0, StageShapes.Length - 1);
        Shape? shape = GetStageShape(capi, idx); // F-S03-10: cached parse, shared across sawhorses (mirrors GetLogShape)
        stageMesh = null;
        if (shape != null)
        {
            capi.Tesselator.TesselateShape(Block, shape, out MeshData mesh);
            stageMesh = mesh;
        }

        // E04 (T04.02): a paired Complete trestle rotates onto the shared axis so both cells align. Both BEs of a
        // pair carry the SAME pairAxis, so they end up parallel. The north-south (Z) axis keeps the authored
        // orientation; the east-west (X) axis turns it a quarter-turn about the vertical. Which axis takes the
        // turn is a visual call to confirm in-game (it depends on the authored frame's default facing) — if they
        // read rotated the wrong way, flip the `== EnumAxis.X` below to `== EnumAxis.Z`.
        if (stage == SawhorseStage.Complete && inPair && stageMesh != null && pairAxis == EnumAxis.X)
        {
            stageMesh.Rotate(0f, GameMath.PIHALF, 0f); // rotates about the block centre (0.5, 0.5, 0.5)
        }

        // Remember the inputs this mesh was built for, so UpdateMesh's gate skips a rebuild until one changes.
        stageMeshStage = stage;
        stageMeshInPair = inPair;
        stageMeshAxis = pairAxis;
    }

    /// <summary>
    /// Re-resolve the master's per-state laid-log meshes for the current <see cref="heldLog"/> — point
    /// <see cref="logMeshes"/> / <see cref="debarkMeshes"/> at this log's process-wide arrays
    /// (<see cref="ResolveStateCaches"/>) and build the species-specific <see cref="LogTexSource"/> — then show the
    /// state matching the current <see cref="sawProgress"/> via <see cref="SelectLogState"/> (T05.04/T06.07). The
    /// states themselves are NOT built here: each is tesselated lazily on first use (<see cref="EnsureLogState"/>),
    /// so placing a log builds only the one shown state instead of all 20 — the 20-shape burst on the main thread
    /// was the placement micro-freeze — and since T05.01 even that one is skipped when another pair already built it.
    /// The <see cref="LogTexSource"/> paints the held species' real textures onto
    /// the single shared geometry set; a state swap (E06) is then a lazy build (once) + reference re-point, not a
    /// re-tesselation — which is why this runs only on a held-log change, never per swap. Client-side, main thread
    /// (from <see cref="UpdateMesh"/>). Leaves the cache and <see cref="logMesh"/> null when there's no log — a
    /// slave / empty pair never has one — so only the master ever draws it. The 45° roll, the diamond cross-section,
    /// the per-state shape and the resting height all come from the authored model; only the whole-billet placement
    /// onto the pair axis is code.
    /// </summary>
    void BuildLogMesh()
    {
        logMesh = null;
        logMeshes = null;
        debarkMeshes = null;
        logMeshesFor = null;
        logTexSource = null;
        logIsQuad = false;
        if (capi == null || heldLog?.Block == null) return; // client-only; only the master holds a log

        SawInput kind = GetSawInput(heldLog);
        string? wood = GetWoodSpecies(heldLog);
        if (kind == SawInput.None || wood == null) return; // not a sawable input (the place gate prevents this)

        // A log SECTION (quarter of a giant trunk) draws its cut states from the quarter model instead of the round one
        // (EnsureLogState); everything else — textures, HideSequence, the 20-state progression — is identical.
        logIsQuad = kind == SawInput.LogSection;

        // Species binding by texture (T05.04): a barked log shows bark on the round sides and tree-ring end grain on
        // the cut faces, a debarked log its bare surface and debarked end grain, and the freshly-ripped INNER faces
        // (#debarked_wood in the authored shapes) bare wood either way. The same mapping the sawmill uses, so it is
        // the same call; WoodTextures decides which domain each texture comes from.
        string logDomain = heldLog.Block.Code?.Domain ?? "game";
        logTexSource = LogTexSource.ForLog(capi, heldLog.Block, kind, wood);

        // Point at the per-state caches — allocated EMPTY, states tesselated lazily by EnsureLogState / EnsureDebarkState
        // the first time the cut (resp. the strip) reaches each (the per-state PlaceLogMesh transform is identical for
        // all states; the geometry difference is baked into each shape). SelectLogState builds + shows the state for the
        // current progress (state 1 at rest).
        ResolveStateCaches(kind, wood, logDomain);
        logMeshesFor = LogCacheKey(); // tag what we pointed at, so UpdateMesh re-points only when the log / a direction / the axis changes
        SelectLogState();             // build + show the state matching the current progress
    }

    /// <summary>
    /// Point <see cref="logMeshes"/> / <see cref="debarkMeshes"/> at the PROCESS-WIDE per-state mesh arrays for this
    /// log, creating them empty on first use (T05.01, F-L10-04). Before this the arrays were allocated per BlockEntity,
    /// so a second pair holding the same wood re-tesselated every state from scratch and so did the same pair after a
    /// chunk reload; the sawmill's workpiece meshes had been in <see cref="ObjectCacheUtil"/> since
    /// AutomatedSawmillPlan T07.03 (<c>iw-sawmill-log-*</c>) and this brings the hand pair onto the same footing.
    ///
    /// <para>What may share an array is exactly what the finished mesh is a function of: the input KIND (round billet /
    /// quarter / bark-strip model), the log's DOMAIN and SPECIES (the three texture paths — <see cref="LogTexSource"/>
    /// paints them onto species-independent geometry), the hide DIRECTION for that model, and the pair AXIS
    /// (<see cref="PlaceLogMesh"/> bakes the turn and the one-cell slide into the vertices). Deliberately NOT the block
    /// code: its orientation part (<c>ud</c>/<c>we</c>/<c>ns</c>) and a section's segment (<c>ne</c>/<c>se</c>/…) change
    /// neither geometry nor texture, and keying on them would split the cache three or four ways for one wood. The two
    /// models get SEPARATE keys because their direction flags are independent — a rip flip must not blank the bark
    /// states and vice versa.</para>
    ///
    /// <para>The arrays stay lazily filled: building all 20 rip states up front on the main thread was the
    /// log-placement micro-freeze, and that is no less true for a shared array. Client-side, MAIN thread — the only
    /// caller is <see cref="BuildLogMesh"/> (via <see cref="UpdateMesh"/>, from Initialize / FromTreeAttributes), so
    /// this adds no off-main-thread writer to <c>api.ObjectCache</c>: it is a plain <c>Dictionary</c> with a
    /// non-atomic read-then-write <c>GetOrCreate</c> (verified in <c>vsapi/Util/ObjectCacheUtil.cs</c>), see F-L5-07 and
    /// the corrected note in <c>BlockChoppingBlock</c>. Nothing frees these: they hold CPU <see cref="MeshData"/>, not
    /// GPU refs, and <c>ObjectCache</c> is an instance field of the client's <c>APIBase</c>, built fresh with each
    /// <c>ClientMain</c> — so leaving to the main menu drops the whole cache with it. (A GPU-ref cache does need an
    /// explicit <c>ObjectCacheUtil.Delete</c>, which is why <see cref="BlockChoppingBlock"/> has one and this does not.)
    /// </para>
    ///
    /// <para>The KNOWN cost of that: a full set of rip states is a few megabytes of <see cref="MeshData"/>, and it now
    /// lives until the player leaves the world instead of until the log leaves the pair — so a session that rips many
    /// woods, both cut directions and both axes accumulates one set per combination. It is the same unbounded shape the
    /// sawmill's <c>iw-sawmill-log-*</c> cache has had since AutomatedSawmillPlan T07.03 (per species, per kind, per
    /// facing), and it is bounded in practice by the states actually reached: laying a log builds ONE state, only a
    /// full rip walks all 20. Freeing sooner is what the old per-BE array did, and that is exactly the bug F-L10-03
    /// reports — the restock pause emptying the pair wiped every state between two IDENTICAL logs.</para>
    /// </summary>
    void ResolveStateCaches(SawInput kind, string wood, string logDomain)
    {
        // domain + species + kind == everything the textures and the model choice depend on; the axis is what
        // PlaceLogMesh bakes in. Both are the same for every pair that could share these meshes.
        string subject = logDomain + ":" + wood + "|" + kind + (pairAxis == EnumAxis.X ? "|x" : "|z");

        logMeshes = ObjectCacheUtil.GetOrCreate(capi!, "iw-sawhorse-logstates-" + subject + (sawCutReversed ? "|sr" : "|sf"),
            () => new MeshData?[LogStateCount]);

        // The bark-strip model is only ever shown for a still-barked LOG (ShowDebarkModel), so a debarked log or a
        // section never needs this array — don't create a key that would stay empty for the whole session.
        debarkMeshes = kind != SawInput.Log
            ? null
            : ObjectCacheUtil.GetOrCreate(capi!, "iw-sawhorse-barkstates-" + subject + (barkReversed ? "|br" : "|bf"),
                () => new MeshData?[DebarkStateCount]);
    }

    /// <summary>
    /// Return the laid-log mesh for visual cut state <paramref name="model"/> (1..<see cref="LogStateCount"/>),
    /// building it on FIRST use and caching it in <see cref="logMeshes"/> (lazy build). The state is produced from the
    /// SINGLE authored model (<see cref="LogShape"/>) by HIDING element groups — the sawmill's face-blank idiom
    /// (<c>FacesResolved = new ShapeElementFace[6]</c>): state V blanks the elements named by the first V-1 entries of
    /// <see cref="HideSequence"/>, tesselates, then RESTORES the shared shape so the next state (and every other
    /// sawhorse) sees it pristine. Blank→tesselate→restore is safe because it all runs synchronously on the main
    /// thread, and chunk tesselation (OnTesselation, another thread) reads the finished MeshData, never the Shape. The
    /// parsed <see cref="Shape"/> comes from a process-wide cache (<see cref="GetLogShape"/>) — Shape.TryGet re-parses
    /// on every call, so we deserialize the model at most once per session. Each fresh mesh is painted with the held
    /// species' <see cref="logTexSource"/> and placed onto the pair (<see cref="PlaceLogMesh"/>). Null when there's no
    /// log / no cache / the model failed to load. Client-side, main thread (the only mutator of <see cref="logMeshes"/>
    /// AND the transient shared-shape blank/restore).
    /// <para>Since T05.01 <see cref="logMeshes"/> is the PROCESS-WIDE array for this species+direction+axis
    /// (<see cref="ResolveStateCaches"/>), not this BE's own, so a state another pair already built is a plain hit here
    /// and this body runs at most once per state per session. Filling a shared array is safe for the same reason
    /// filling a private one was: every writer is this main-thread path, and the tesselation thread only ever reads the
    /// finished <see cref="logMesh"/> reference.</para>
    /// </summary>
    MeshData? EnsureLogState(int model)
    {
        if (capi == null || logMeshes == null || logTexSource == null) return null; // no log / not client

        int v = GameMath.Clamp(model, 1, LogStateCount);
        int idx = v - 1;
        MeshData? cached = logMeshes[idx];
        if (cached != null) return cached;

        // A quarter (log section) uses the quarter model; every other input uses the round one. Both carry the SAME
        // sawing_1..16 / part_1..4 groups, so the identical HideSequence drives either through the 20-state cut.
        Shape? shape = logIsQuad ? GetLogQuadShape(capi) : GetLogShape(capi);
        if (shape == null) return null; // model failed to load → nothing to draw for it (slot left null)

        DbgCountStateBuild("cut", v); // T05.01 acceptance counter — past the guards, so it counts real tesselations only

        // Hide the first v-1 cut events by blanking their elements' faces; remember the originals so the shared shape
        // is put back exactly as it was the instant the tesselator has read it. Directional sawing picks which
        // across-the-log side the boards peel from (HideSequenceReversed mirrors the board order); the shared array is
        // keyed on sawCutReversed (ResolveStateCaches) so each direction gets its own states, built with its own sequence.
        string[] hideSeq = sawCutReversed ? HideSequenceReversed : HideSequence;
        var saved = new List<(ShapeElement el, ShapeElementFace[]? faces)>();
        BlankBySequence(shape.Elements, hideSeq, v - 1, saved);

        capi.Tesselator.TesselateShape(logIsQuad ? "immersivewoodworking:sawhorse-log-quad" : "immersivewoodworking:sawhorse-log", shape, out MeshData mesh, logTexSource);

        foreach ((ShapeElement el, ShapeElementFace[]? faces) in saved) el.FacesResolved = faces; // restore pristine

        PlaceLogMesh(mesh);
        logMeshes[idx] = mesh;
        return mesh;
    }

    /// <summary>
    /// Return the laid-log mesh for bark-strip state <paramref name="model"/> (1..<see cref="DebarkStateCount"/>),
    /// building it on FIRST use and caching it in <see cref="debarkMeshes"/> (lazy build) — the debark counterpart to
    /// <see cref="EnsureLogState"/>, over the dedicated debarking model (<see cref="DebarkShape"/>). State V blanks the
    /// first V-1 bark bands named by <see cref="DebarkHideSequence"/> (the <c>base_*</c> core is never listed, so it
    /// always draws), tesselates, then RESTORES the shared shape — the same blank→tesselate→restore idiom as
    /// <see cref="EnsureLogState"/>, safe because it all runs synchronously on the main thread. Painted with the held
    /// species' <see cref="logTexSource"/> (so the bark / bare-wood / end-grain keys bind exactly as the cut model) and
    /// placed by <see cref="PlaceLogMesh"/>; the parsed shape comes from a process-wide cache
    /// (<see cref="GetDebarkShape"/>). Null when there's no log / no cache / the model failed to load. Client-side, main
    /// thread (the only mutator of <see cref="debarkMeshes"/> AND the transient shared-shape blank/restore).
    /// <para>As with <see cref="EnsureLogState"/>, <see cref="debarkMeshes"/> is the PROCESS-WIDE array for this
    /// species+peel-direction+axis since T05.01 (<see cref="ResolveStateCaches"/>), so each bark state is tesselated at
    /// most once per session however many pairs strip that wood.</para>
    /// </summary>
    MeshData? EnsureDebarkState(int model)
    {
        if (capi == null || debarkMeshes == null || logTexSource == null) return null; // no log / not client

        int v = GameMath.Clamp(model, 1, DebarkStateCount);
        int idx = v - 1;
        MeshData? cached = debarkMeshes[idx];
        if (cached != null) return cached;

        Shape? shape = GetDebarkShape(capi);
        if (shape == null) return null; // model failed to load → nothing to draw for it (slot left null)

        DbgCountStateBuild("bark", v); // T05.01 acceptance counter — past the guards, so it counts real tesselations only

        // Hide the first v-1 bark bands by blanking their elements' faces; remember the originals so the shared shape
        // is put back exactly as it was the instant the tesselator has read it (the base_* core is never touched).
        // Directional debarking picks which end the bark peels from (DebarkHideSequenceReversed); the shared array is
        // keyed on barkReversed (ResolveStateCaches) so each end gets its own states, built with its own sequence.
        string[] bandSeq = barkReversed ? DebarkHideSequenceReversed : DebarkHideSequence;
        var saved = new List<(ShapeElement el, ShapeElementFace[]? faces)>();
        BlankBySequence(shape.Elements, bandSeq, v - 1, saved);

        capi.Tesselator.TesselateShape("immersivewoodworking:sawhorse-log-debarking", shape, out MeshData mesh, logTexSource);

        foreach ((ShapeElement el, ShapeElementFace[]? faces) in saved) el.FacesResolved = faces; // restore pristine

        PlaceLogMesh(mesh);
        debarkMeshes[idx] = mesh;
        return mesh;
    }

    /// <summary>Blank (no faces → no quads, the sawmill's <c>BlankElements</c> idiom) every element whose name starts
    /// with one of the FIRST <paramref name="hideCount"/> <paramref name="sequence"/> prefixes, recording each
    /// element's original <c>FacesResolved</c> in <paramref name="saved"/> so the caller (<see cref="EnsureLogState"/> /
    /// <see cref="EnsureDebarkState"/>) can restore the shared shape after tesselation. The trailing '_' in every prefix
    /// keeps "sawing_1_" / "part_1_" from also matching a longer name. Recurses into children (mirrors the sawmill's
    /// recursion). Parameterised by <paramref name="sequence"/> so the cut model hides by <see cref="HideSequence"/> and
    /// the bark-strip model by <see cref="DebarkHideSequence"/> through the same code.</summary>
    static void BlankBySequence(ShapeElement[]? els, string[] sequence, int hideCount, List<(ShapeElement, ShapeElementFace[]?)> saved)
    {
        if (els == null) return;
        foreach (ShapeElement el in els)
        {
            string name = el.Name ?? "";
            for (int i = 0; i < hideCount; i++)
            {
                if (name.StartsWith(sequence[i], System.StringComparison.Ordinal))
                {
                    saved.Add((el, el.FacesResolved));
                    el.FacesResolved = new ShapeElementFace[6];
                    break;
                }
            }
            BlankBySequence(el.Children, sequence, hideCount, saved);
        }
    }

    // Process-wide cache of the parsed log Shape. The geometry is species-INDEPENDENT — textures are applied per BE at
    // tesselation via LogTexSource — so ONE parsed Shape is safely shared across every sawhorse and wood type (each
    // state build blanks then RESTORES it under the main-thread lock, so it is always left pristine). This matters
    // because Shape.TryGet re-parses the JSON on every call (no engine cache for these — they aren't a block's
    // `shape`), so without the cache every lazy state build would re-deserialize the whole model. Filled lazily on the
    // main thread (the UpdateMesh path); the unsynchronized check-then-set mirrors EntityBehaviorChopAnim's source-anim
    // cache — a rare double parse on first use is harmless (identical data). Stays null only if the file fails to load,
    // and is re-attempted next time (cheap; it ships with the mod).
    static Shape? sharedLogShape;

    static Shape? GetLogShape(ICoreClientAPI capi) => sharedLogShape ??= Shape.TryGet(capi, LogShape);

    // Process-wide cache of the parsed quarter-log Shape (LogQuadShape), the log-section counterpart to sharedLogShape —
    // same sharing/pristine-restore rationale (species-INDEPENDENT geometry, blanked-then-restored under the main-thread
    // lock, so ONE parse is safely shared across every sawhorse). A SEPARATE cache from sharedLogShape so a quarter and a
    // whole log never blank each other's shared shape; a BE holds one log at a time and its logMeshes cache resets on any
    // log change (logMeshesFor), so the two never collide in a single BE either.
    static Shape? sharedLogQuadShape;

    static Shape? GetLogQuadShape(ICoreClientAPI capi) => sharedLogQuadShape ??= Shape.TryGet(capi, LogQuadShape);

    // Whether the currently held log is a log SECTION (SawInput.LogSection — a quarter of a giant trunk), so EnsureLogState
    // builds its cut states from the quarter model (LogQuadShape) rather than the round one (LogShape). Set once per
    // held-log change in BuildLogMesh (derived from the input kind, so stable for a given log) and read by the lazy
    // per-state build. Master-only (only the master holds a log); false while none is held.
    bool logIsQuad;

    // Per bark-state meshes for the debarking model (T04.02), the debark counterpart to logMeshes — re-pointed and
    // cleared together with it in BuildLogMesh, indexed by bark-state (1..DebarkStateCount), filled lazily by
    // EnsureDebarkState. Like logMeshes this is a REFERENCE to a process-wide array since T05.01 (ResolveStateCaches),
    // shared by every pair stripping the same species from the same end on the same axis. Only ever populated while a
    // still-barked log is shown mid-strip (SelectLogState); a debarked input or a log being sawn draws from logMeshes
    // instead, and for those inputs this stays null — no array is created for a model they can never show.
    // Master-only, main-thread (logMesh is what the tess thread reads). Null while no log is held.
    MeshData?[]? debarkMeshes;

    // Process-wide cache of the parsed debarking Shape, the debark counterpart to sharedLogShape — species-INDEPENDENT
    // (textures applied per BE via logTexSource), so ONE parsed Shape is shared across every sawhorse and wood type
    // (each state build blanks then RESTORES it under the main-thread lock). Re-parsing on every Shape.TryGet is what we
    // avoid (these aren't a block's `shape`, so the engine doesn't cache them); a rare double parse on first use is
    // harmless (identical data). Stays null only if the file fails to load, re-attempted next time.
    static Shape? sharedDebarkShape;

    static Shape? GetDebarkShape(ICoreClientAPI capi) => sharedDebarkShape ??= Shape.TryGet(capi, DebarkShape);

    // Process-wide cache of the parsed stage Shapes (sawhorses_p1/p2/p3), the trestle counterpart to sharedLogShape /
    // sharedDebarkShape (F-S03-10). Species-INDEPENDENT — the block's own #oak / #rope textures resolve at tesselation —
    // so ONE parsed Shape per stage is shared across every sawhorse; RebuildStageMesh only tesselates it (never blanks),
    // so it stays pristine and needs no clone. Without this each build-stage change re-deserialized the whole model via
    // Shape.TryGet (no engine cache — these aren't a block's `shape`), diverging from the cached log/debark shapes. Filled
    // lazily on the main thread (RebuildStageMesh); a rare double parse on first use is harmless (identical data).
    static readonly Shape?[] sharedStageShapes = new Shape?[StageShapes.Length];

    static Shape? GetStageShape(ICoreClientAPI capi, int idx) => sharedStageShapes[idx] ??= Shape.TryGet(capi, StageShapes[idx]);

    // [IW-SAWHORSE-MESH] dev counter for the T05.01 acceptance check ("two logs of one wood on DIFFERENT pairs build
    // the mesh once, not twice"): the number of laid-log states actually TESSELATED this session, which the shared
    // per-state arrays are supposed to hold flat. Counting hits instead would prove nothing — a hit is the normal case
    // even with a per-BE cache. Read the running total in the log: lay a log on one pair, note the total, lay the same
    // wood on a second pair, and the total must not move. [Conditional("IW_DEBUG")] strips every call (arguments
    // included) unless IW_DEBUG is defined, so Release pays nothing.
#if IW_DEBUG
    static int stateBuildCount;
#endif

    [Conditional("IW_DEBUG")]
    void DbgCountStateBuild(string model, int state)
    {
#if IW_DEBUG
        stateBuildCount++;
        // The log code goes in as an ARGUMENT, never inlined into the format string (vs-logger-notification-formats).
        capi!.Logger.Notification("[immersivewoodworking] [sawhorse-mesh] tesselating {0} state {1} for {2} (total this session: {3})",
            model, state, logMeshesFor ?? "?", stateBuildCount);
#endif
    }

    /// <summary>
    /// Point <see cref="logMesh"/> at the authored state matching the current progress: the progressive bark-strip
    /// model (<see cref="EnsureDebarkState"/>, from <see cref="barkProgress"/>) for a still-barked log not yet being
    /// sawn (<see cref="ShowDebarkModel"/>), otherwise the cut model (<see cref="EnsureLogState"/>, from
    /// <see cref="sawProgress"/>; whole log at rest, last sliver when fully cut). The two are mutually exclusive per log
    /// — a rip at an end vs a strip at a side, locked by whichever began first (T05.03) — so exactly one applies. No log
    /// → state 1 and a null logMesh (nothing drawn). Both branches lazily tesselate just the one shown state and cache
    /// it. Client-side; shared by <see cref="BuildLogMesh"/> (after a cache reset) and <see cref="UpdateMesh"/> (a state
    /// swap on the same log). The caller flags the re-tesselation.
    /// </summary>
    void SelectLogState()
    {
        if (heldLog == null) { visualModel = 1; logMesh = null; return; }
        if (ShowDebarkModel())
        {
            visualModel = BarkVisualFromProgress(barkProgress);
            logMesh = EnsureDebarkState(visualModel);
        }
        else
        {
            visualModel = VisualModelFromProgress(sawProgress);
            logMesh = EnsureLogState(visualModel);
        }
    }

    /// <summary>Whether the laid log should render with the progressive bark-strip model (<see cref="DebarkShape"/>)
    /// rather than the cut model: a still-BARKED input (<see cref="SawInput.Log"/>) that no rip has begun on
    /// (<see cref="sawProgress"/> ≤ 0) and whose strip has not yet fully fired
    /// (<c>barkStagesFired &lt; <see cref="BarkStageFractions"/>.Length</c>). Once a rip begins the cut model takes over
    /// (it textures the bark itself), and an already-debarked input always uses the cut model — so the bark-strip model
    /// shows only while bark is actually coming off (or at rest before either action), and the saw/strip mutual
    /// exclusion (T05.03) keeps the two from ever both applying. The one stuck case — a barked wood that ships NO
    /// <c>debarkedlog</c> variant, so <see cref="CompleteDebark"/> can't swap — is handled inside CompleteDebark,
    /// which warns then calls <see cref="ResetDebarkState"/> to ZERO <see cref="barkProgress"/> AND <c>barkStagesFired</c>:
    /// the log renders as FULL bark again (state 1) and stays barked and recoverable by Ctrl+RMB, never the fully-bare
    /// core on a still-barked log. That reset keeps <c>barkStagesFired</c> back under <c>Length</c>, so the guard above
    /// is now vestigial (always true while a barked log is held) — kept as belt-and-suspenders and still correct if E04
    /// later subdivides <see cref="BarkStageFractions"/>. Caller guards <c>heldLog != null</c>;
    /// master-only state (only the master holds the log).</summary>
    bool ShowDebarkModel() => sawProgress <= 0f
                              && barkStagesFired < BarkStageFractions.Length
                              && GetSawInput(heldLog) == SawInput.Log;

    /// <summary>
    /// The authored log state (1..<see cref="LogStateCount"/>) the cut sits at for <paramref name="progress"/>
    /// seconds: <c>clamp(floor(progress / SawSeconds * 20) + 1, 1, 20)</c> — 20 even steps across a full rip, so 0
    /// progress = whole log (state 1) and a completed cut = the last sliver (state 20). <see cref="SawSeconds"/> is
    /// read live from config (matching the hold timing). The SAME scalar drives the 4 yield/wear stages (a subset of
    /// these boundaries: 5→6, 14→15, 19→20, 20→∅), so visual and yield never drift apart (T06.07).
    ///
    /// On a short rip the raw 20-step walk would flicker faster than the eye (or the network) wants, so the step is
    /// snapped down onto a coarser grid (<see cref="SwapStride"/>) that keeps successive swaps ≥ <see cref="MinSwapInterval"/>
    /// apart — at the SawSeconds floor the log just stays whole and vanishes on completion (no shrink animation). This
    /// decimation is PURELY VISUAL: the yield stages fire off <see cref="sawProgress"/> via <see cref="StagesPassed"/>,
    /// never off this, so collapsing models can never change how many planks/beams a log drops.
    /// </summary>
    int VisualModelFromProgress(float progress)
    {
        float sawSeconds = SawSeconds;
        if (sawSeconds <= 0f) return 1; // degenerate config — just show the whole log
        int step = (int)(progress / sawSeconds * LogStateCount); // 0..LogStateCount; progress ≥ 0 → int-cast is floor
        int stride = SwapStride(sawSeconds);
        if (stride > 1) step -= step % stride; // snap down onto the decimated grid so swaps stay ≥ MinSwapInterval apart
        return GameMath.Clamp(step + 1, 1, LogStateCount);
    }

    // The shown log model never swaps faster than this (seconds). At the default SawSeconds = 6 over 20 models a swap
    // lands every 0.3s; a short rip (SawSeconds down to its 0.1 floor) would otherwise flicker through all 20 in a
    // blink, so SwapStride collapses adjacent models to hold the swaps at least this far apart.
    const float MinSwapInterval = 0.1f;

    /// <summary>
    /// How many adjacent authored models to collapse into one visible swap so a fast rip doesn't flicker. Un-decimated
    /// swaps land <c>SawSeconds / LogStateCount</c> apart; this returns <c>ceil(MinSwapInterval / that)</c> — 1 (show
    /// every model) once <see cref="SawSeconds"/> ≥ 2s, 2 (skip every other) below 2s, 3 below 1s, and so on up to
    /// <see cref="LogStateCount"/> at the 0.1s floor, where the log just stays whole until it's consumed. PURELY
    /// VISUAL — the 4 yield stages fire off <see cref="sawProgress"/> (<see cref="StagesPassed"/>) independently of
    /// this, so decimation can never change the per-log plank/beam total (the one hard invariant).
    /// </summary>
    static int SwapStride(float sawSeconds)
    {
        float rawInterval = sawSeconds / LogStateCount; // > 0 (caller guards sawSeconds > 0)
        int stride = (int)System.Math.Ceiling(MinSwapInterval / rawInterval);
        return GameMath.Clamp(stride, 1, LogStateCount);
    }

    /// <summary>
    /// The authored bark-strip state (1..<see cref="DebarkStateCount"/>) the strip sits at for
    /// <paramref name="progress"/> seconds of debarking: <c>clamp(floor(progress / DebarkSeconds * bands) + 1, 1,
    /// DebarkStateCount)</c> where <c>bands</c> is the number of bark bands (<see cref="DebarkHideSequence"/>.Length =
    /// 9), the debark counterpart to <see cref="VisualModelFromProgress"/>. State 1 = full bark (no band hidden), state
    /// <see cref="DebarkStateCount"/> = the bare core (all nine <c>part_*</c> bands hidden). We divide by the BAND count
    /// (9), NOT the state count (10), so the nine bands come off evenly across the WHOLE strip and the LAST band
    /// disappears only at completion (fraction 1.0 → CompleteDebark swaps the log out the same tick), not at 9/10 = 90%:
    /// dividing by DebarkStateCount left the bare core showing from 90% on, so the player kept stripping a visibly-bare
    /// log for the final tenth (very noticeable with the slow knife). Now the strip shows bark right up to the drop.
    /// <see cref="DebarkSeconds"/> is the reference (spud) duration read live from config; a slower tier reaches the same
    /// states more slowly because its per-tick advance is scaled down (<see cref="BarkSpeedFor"/>), but
    /// <see cref="barkProgress"/> still spans [0, DebarkSeconds] for every tier, so the mapping is tier-independent.
    /// Snapped onto the <see cref="DebarkSwapStride"/> grid so a fast strip doesn't flicker (swaps stay ≥
    /// <see cref="MinSwapInterval"/> apart), exactly like the rip's decimation. PURELY VISUAL — the strip transform
    /// fires off <see cref="BarkStagesPassed"/> (one stage, at completion), never off this, so the visual can never
    /// change whether/when the log debarks. Pure function of progress + config, so it is also called server-side by
    /// <see cref="AdvanceDebark"/> to detect a bucket change for syncing.
    /// </summary>
    int BarkVisualFromProgress(float progress)
    {
        float barkSeconds = DebarkSeconds;
        if (barkSeconds <= 0f) return 1; // degenerate config — just show the fully-barked log
        int bands = DebarkHideSequence.Length; // 9 band removals span [0, DebarkSeconds]; the 10th state (bare) is fraction 1.0 only
        int step = (int)(progress / barkSeconds * bands); // 0..bands; progress ≥ 0 → int-cast is floor
        int stride = DebarkSwapStride(barkSeconds);
        if (stride > 1) step -= step % stride; // snap down onto the decimated grid so swaps stay ≥ MinSwapInterval apart
        return GameMath.Clamp(step + 1, 1, DebarkStateCount);
    }

    /// <summary>How many adjacent bark states to collapse into one visible swap so a fast strip doesn't flicker — the
    /// debark counterpart to <see cref="SwapStride"/>, over the nine band swaps (<see cref="DebarkHideSequence"/>.Length,
    /// matching <see cref="BarkVisualFromProgress"/>'s divisor). At the default DebarkSeconds = 1 over 9 swaps a swap lands
    /// every ≈ 0.11s (stride 1); a shorter strip collapses adjacent swaps to hold them ≥ <see cref="MinSwapInterval"/>
    /// apart. PURELY VISUAL.</summary>
    static int DebarkSwapStride(float barkSeconds)
    {
        int bands = DebarkHideSequence.Length; // 9 swaps span the strip (one per band), matching BarkVisualFromProgress
        float rawInterval = barkSeconds / bands; // > 0 (caller guards barkSeconds > 0)
        int stride = (int)System.Math.Ceiling(MinSwapInterval / rawInterval);
        return GameMath.Clamp(stride, 1, bands);
    }

    /// <summary>
    /// Position one tesselated log-state mesh onto the pair: orient the authored (Z-long) billet onto the pair
    /// axis and slide it one cell along that axis so it centres on the master/partner boundary and spans both
    /// trestles. The partner always sits at +axis from the lex-smaller master, so the push is always toward +axis.
    /// The X-axis branch uses the SAME +90° turn as the stage mesh (<see cref="UpdateMesh"/>), so log and trestles
    /// stay consistent — if the stage mesh ever needs its turn flipped, flip this in lockstep. The 45° roll, the
    /// cross-section and the resting height are baked into the model (T05.04), not here. Mutates in place.
    /// </summary>
    void PlaceLogMesh(MeshData mesh)
    {
        if (pairAxis == EnumAxis.X) // east-west: turn the Z-long billet onto X, partner at +X
        {
            mesh.Rotate(0f, GameMath.PIHALF, 0f); // about the block centre (0.5, 0.5, 0.5) — matches the stage mesh
            mesh.Translate(1f, 0f, 0f);           // authored centre x 0 → 1 (master/partner boundary)
        }
        else // Z (north-south, default): the authored long axis already lies along Z, partner at +Z
        {
            mesh.Translate(0f, 0f, 1f);           // authored centre z 0 → 1 (master/partner boundary)
        }
    }

    // ───────────────────────── Interaction-help live refresh ─────────────────────────
    //
    // The vanilla interaction-help HUD (HudElementInteractionHelp) does NOT poll: it recomposes the bottom-screen
    // hints only when the looked-at cell / selection changes, or when a client BlockChanged fires for the SELECTED
    // cell (vs-interaction-help-no-poll; the chain — MarkBlockDirty → BlockChanged → HudElementInteractionHelp.On-
    // BlockChanged — is documented at BlockEntitySawmill.Render.cs). So a state change WITHOUT a re-hover — a log
    // laid, a rip/strip begun, the bark fully off — leaves the hints stale until the player looks away and back.
    // (The block-INFO box has no such issue: it recomposes every 500ms.) We force that BlockChanged ourselves.
    //
    // Unlike the single-cell chopping block — which gets this for free because its content-change MarkBlockDirty(Pos)
    // lands on the very cell being looked at — the sawhorse is a 2-cell pair whose state + MarkDirty(true) live only
    // on the MASTER (vs-sawhorse-multiblock-pairing). A master-side change re-syncs only the master's tree, so only
    // the master's UpdateMesh marks the master cell; when the player looks at the PARTNER cell no BlockChanged is
    // ever raised for it and the hint stays stale (the "only refreshes when you look away and back" report). So we
    // mirror the sawmill: on a client sync, if the hint-relevant state actually changed, mark whichever of THIS
    // pair's two cells the player is currently looking at — that raises BlockChanged there and recomposes the hint.

    // Last help-relevant state signature (client). Seeded in Initialize; compared on every client tree sync so we
    // only re-fire when a hint would actually change (a running rip/strip's per-bucket progress syncs otherwise don't).
    string? lastHelpSig;

    /// <summary>The discrete state the master's interaction help (<see cref="BlockSawhorse.SelectInteractions"/>)
    /// branches on, read through <see cref="GetMaster"/> so either cell yields the same signature: the build stage,
    /// whether paired, whether a log is laid, whether it is still barked, and — as BOOLEANS, so a running rip/strip's
    /// per-tick progress sync doesn't refire — whether a rip / a strip has begun (those flip the saw/strip hints via the
    /// mode lock) plus whether the log is still recoverable (<see cref="LogRecoverable"/>, the recovery grace — flips the
    /// Ctrl+RMB take↔discard hint at that boundary).</summary>
    string HelpSignature()
    {
        BlockEntitySawhorse m = GetMaster();
        return $"{(int)m.stage}|{m.inPair}|{m.heldLog != null}|{IsSawableLog(m.heldLog)}|{m.sawProgress > 0f}|{m.barkProgress > 0f}|{LogRecoverable}";
    }

    /// <summary>Whether <paramref name="pos"/> is one of this pair's two cells (this trestle or its partner) — the
    /// sawhorse counterpart to the sawmill's <c>IsFootprintCell</c>. A single (unpaired) trestle has no partner, so
    /// only its own cell matches.</summary>
    bool IsPairCell(BlockPos pos) => pos.Equals(Pos) || (partnerPos != null && pos.Equals(partnerPos));

    /// <summary>
    /// Recompose the interaction-help hints if <see cref="HelpSignature"/> changed since the last client sync, by
    /// firing a <c>BlockChanged</c> on whichever of the pair's cells the player is looking at (client
    /// <c>MarkBlockDirty(pos)</c> raises <c>BlockChanged(pos)</c>, and the help HUD recomposes when that pos equals
    /// the selected cell — see <see cref="BlockEntitySawmill"/>). Fired only when this client is actually looking at
    /// the pair, so an unobserved auto-fed pair isn't re-tesselated on every sync; the signature is still updated
    /// when not looking, so the first change after looking away doesn't spuriously fire. Client-only (guarded by
    /// <see cref="capi"/>).
    /// </summary>
    void RefreshInteractionHelpOnChange()
    {
        if (capi == null || Block == null || Block.BlockId == 0) return;
        string sig = HelpSignature();
        if (sig == lastHelpSig) return;
        lastHelpSig = sig;
        BlockSelection? sel = capi.World.Player?.CurrentBlockSelection;
        if (sel?.Position != null && IsPairCell(sel.Position)) capi.World.BlockAccessor.MarkBlockDirty(sel.Position);
    }
}
