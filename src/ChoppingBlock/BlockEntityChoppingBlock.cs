using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ImmersiveWoodworking;

/// <summary>
/// Holds a single choppable stack as a nested itemstack (not a separate block) and renders it on top
/// of the stand. Chopping is driven by <see cref="BlockChoppingBlock"/>, which calls <see cref="Chop"/>.
///
/// Accepts, by hand: a full log (barked/debarked) → 2 half-logs (E04/T04.02); a half-log → firewood; a log section
/// or a laid chopping block → firewood; firewood → sticks. The automated chopper skips the half-log step
/// (log → firewood in one pass); the split is the ONE hand-only divergence (see <see cref="GetHandChopResult"/> vs the
/// shared <see cref="GetChopResult"/>). That split is opt-out via <see cref="ImmersiveWoodworkingConfig.HalfLogChopStage"/>
/// (default on) — turned off, a hand-laid log chops straight to firewood too, so the stand matches the machine.
/// </summary>
public class BlockEntityChoppingBlock : BlockEntity
{
    InventoryGeneric inventory;

    // A single axe stuck into the TOP of the stand (Shift+RMB), stored as a nested stack — deliberately NOT in
    // `inventory`, so pre-existing single-slot saves keep loading without an inventory-size migration (which
    // InventoryGeneric.FromTreeAttributes would otherwise force via the saved qslots). Only ever set on an EMPTY stand
    // and mutually exclusive with the laid content, so "an axe is parked here" cleanly disables chopping (the gate lives
    // in BlockChoppingBlock). Persisted + synced via To/FromTreeAttributes (sawhorse heldLog idiom); rendered by
    // UpdateToolMesh; dropped on break. null = nothing stuck.
    ItemStack? stuckTool;

    // Horizontal yaw (radians) from the stand centre to the player who stuck the axe, captured once at stick time and
    // persisted alongside stuckTool. PoseToolOnStand swings the axe about Y by this so its handle leans toward that
    // player. Meaningless when nothing is stuck; 0 leaves the mesh's native lean (toward -Z) unrotated.
    float stuckToolYaw;

    // The wood species this block was made from, stored as DATA (not a block variant) so a single block code
    // `choppingblock` serves every species — including ones from other wood mods (wildcrafttree, …) without a
    // per-mod patch. Captured from the source log at craft / place (T01.03 / T01.04 / E04) and re-textured onto
    // the stand at render time (E02). Both parts are kept on purpose, mirroring the sawhorse's proven cross-domain
    // pipeline (WoodTextures + LogTexSource): the species string drives the texture path / per-species name / recipe
    // output attribute, and the domain lets a MODDED species resolve its textures in the mod's own domain — e.g.
    // WoodTextures.Get(capi, woodDomain, wood, WoodTexFamily.Bark), which tries woodDomain first and falls back to
    // game per texture. woodDomain defaults to "game" so a block with no recorded domain (a legacy/migration edge, or the
    // temporary vanilla JSON recipe that can only template the bare {wood}) still resolves against the base game.
    // null wood = species not yet known (bare/legacy); downstream falls back to the default oak look + generic name.
    string? wood;
    string woodDomain = "game";

    /// <summary>The stored wood species (e.g. "oak", "azobe"), or null if none has been recorded yet. Read by the
    /// block for the held/placed name (T03.03) and the runtime re-texture (E02).</summary>
    public string? Wood => wood;

    /// <summary>The domain the stored species' textures live in (defaults to "game"). Feeds the cross-domain
    /// texture resolve (E02), so a modded species finds its own textures.</summary>
    public string WoodDomain => woodDomain;

    /// <summary>Adopt a wood species (+ its domain) onto this stand, keeping the domain-default rule in ONE place
    /// (null/empty domain → "game", matching FromTreeAttributes). Pure setter: the caller persists+syncs
    /// (MarkDirty) on the server — used by the place-read (<see cref="OnBlockPlaced"/>, T01.03) and the legacy
    /// self-heal (T05.02).</summary>
    internal void SetWood(string? newWood, string? newDomain)
    {
        // An EMPTY species is "none", exactly like a missing one (AuditFixPlan T04.06 / F-L6-28): a stack stamped with a
        // null species comes back from the first save/sync as a present-but-BLANK key (StringAttribute.ToBytes writes
        // "" for null), and treating that as a known wood would send every downstream reader — texture paths, the
        // per-species name — off with a hole where the species should be. Normalising here covers both callers that
        // adopt from a stack; the loader (FromTreeAttributes) applies the same rule.
        wood = string.IsNullOrEmpty(newWood) ? null : newWood;
        woodDomain = string.IsNullOrEmpty(newDomain) ? "game" : newDomain;
    }

    // Client-only display. The stored stack is tesselated into a mesh and baked into the chunk's terrain mesh
    // via OnTesselation — no per-frame renderer, so the engine frustum/occlusion/distance-culls it for free.
    // contentMesh is built on the MAIN thread (UpdateContentMesh) and only READ on the tesselation thread; the
    // reference is swapped atomically and the mesh is never mutated after assignment, so the read is safe.
    ICoreClientAPI? capi;
    MeshData? contentMesh;
    // The identity contentMesh was last built for (F-S06-09), so UpdateContentMesh can skip the whole re-tesselation when
    // nothing that affects the mesh changed — mirroring the stand's standMeshForWood gate. Captures everything the build
    // reads: the workpiece kind + code (+ a half-log's stored species) and, for the staged full-log / half-log paths only,
    // the visual bucket (chop stage) + the facing yaw. A per-strike sync that stays inside one bucket, or a MarkDirty for
    // an unrelated reason, then leaves the laid mesh untouched instead of re-tesselating the staged shape to the identical
    // result. null = no content (a bare stand), so re-laid content always rebuilds.
    string? contentMeshKey;

    // Client-only display of an axe stuck into the top (stuckTool). Same main-thread-build / tess-thread-read contract
    // as contentMesh: built by UpdateToolMesh (Initialize / FromTreeAttributes), the reference swapped atomically and
    // never mutated after assignment, so OnTesselation can read it on the tesselation thread safely. Null when nothing
    // is stuck. The axe item is re-tesselated into the BLOCK atlas through LaidContentItemTexSource (same path as a
    // stored firewood item), then posed upright into the stand by PoseToolOnStand.
    MeshData? toolMesh;

    // The identity toolMesh was last built for (AuditFixPlan T05.04 / F-L10-10) — the tool half of the contentMeshKey /
    // standMeshForWood gate, and the reason UpdateToolMesh is no longer the block's per-strike re-tesselation source.
    // FromTreeAttributes runs on EVERY tree sync, so before this gate every synced strike re-tesselated the axe item and
    // — in the far commoner no-axe case — still called MarkBlockDirty on a stand whose tool mesh was already null.
    // Captures everything PoseToolOnStand + the tesselation read: the axe item's CODE (LaidContentItemTexSource resolves
    // textures from the Item alone, never from the stack's attributes) and the stuck yaw. null = nothing stuck, so the
    // gate also skips the null-to-null case and a re-stuck axe always rebuilds.
    string? toolMeshKey;

    // Stand mesh, re-textured per species (E02). Same client-thread contract as contentMesh: built on the MAIN
    // thread (RebuildStandMesh, from Initialize / FromTreeAttributes / the placer's OnBlockPlaced) and only READ on
    // the tesselation thread (OnTesselation); the reference is swapped atomically and the mesh is never mutated
    // after assignment, so the read is safe. Null when the species is unknown (a bare / legacy / pre-migration
    // stand) OR before the first build — OnTesselation then falls back to the block's own default mesh, which the
    // block JSON's default oak M1/M2 textures give, so the stand is never invisible. Rebuilt only when the species
    // key changes (standMeshForWood gate), mirroring the sawhorse's stageMesh. CPU-side MeshData like contentMesh,
    // so the GC reclaims it — no GPU resource to dispose (the held/GUI MultiTextureMeshRefs come later in E03).
    MeshData? standMesh;
    string? standMeshForWood; // "{domain}:{wood}" the current standMesh was built for, or null for a bare stand

    // Sound played when a choppable is laid onto the stand. File: assets/immersivewoodworking/sounds/place.ogg
    // PlaySoundAt does NOT auto-prepend "sounds/", so the path must include it (extension is auto-added).
    static readonly AssetLocation PlaceSound = new AssetLocation("immersivewoodworking", "sounds/place");

    // Chop sounds shipped under assets/immersivewoodworking/sounds/chop/. The chop resolves SERVER-side
    // (Chop -> PlayChopSound), which picks one of these at random and broadcasts its PATH; nearby clients
    // resolve and play the .ogg from their own assets (the server never decodes it). This list is the single
    // source of truth — to add or rename a chop sound, drop the .ogg in that folder and edit this list.
    //
    // We deliberately do NOT auto-scan the folder. "sounds" is a client-only asset category, so on a
    // dedicated server — the very side that picks the sound — Assets.GetLocations returns nothing, which is
    // why the old auto-discovery still needed a hand-maintained fallback. Having both a discovered set and a
    // hardcoded fallback meant they could silently drift apart (add choppingwood5.ogg → the client finds it,
    // the dedicated server's fallback never plays it). One explicit list that every side uses can't drift.
    static readonly AssetLocation[] ChopSounds =
    {
        new AssetLocation("immersivewoodworking", "sounds/chop/choppingwood1"),
        new AssetLocation("immersivewoodworking", "sounds/chop/choppingwood2"),
        new AssetLocation("immersivewoodworking", "sounds/chop/choppingwood3"),
        new AssetLocation("immersivewoodworking", "sounds/chop/choppingwood4"),
    };

    // Ordinary axe-STRIKE sounds — one per NON-completing hit while a log is chopped over several strikes (E03). The
    // COMPLETING strike (the log splitting into firewood) plays the heavier ChopSounds set instead (in Chop), so the
    // ear hears "thunk, thunk, … crack". Same single-source-of-truth + no-auto-scan contract as ChopSounds: drop a new
    // .ogg here and edit this list. Files: assets/immersivewoodworking/sounds/chop/axechop{1..3}.ogg (mono, so OpenAL
    // spatializes them — see the sawhorse's mono-only note).
    static readonly AssetLocation[] StrikeSounds =
    {
        new AssetLocation("immersivewoodworking", "sounds/chop/axechop1"),
        new AssetLocation("immersivewoodworking", "sounds/chop/axechop2"),
        new AssetLocation("immersivewoodworking", "sounds/chop/axechop3"),
    };

    // Per-player capture of the axe (slot + exact stack) that STARTED the current chop hold, so the wear
    // lands on that axe even if the player swaps their active hotbar slot mid-swing (EDGE-02). Server-only
    // and transient (never serialized): set in OnBlockInteractStart -> CapturePendingAxe, consumed by Chop,
    // cleared on stop/cancel. Keyed by PlayerUID so two players chopping one stand can't alias each other.
    readonly Dictionary<string, (ItemSlot slot, ItemStack stack)> pendingAxes = new();

    // ONE right-click chop hold, kept in ONE type (AuditFixPlan T02.02). These values used to sit loose on the BE, and
    // they were BE-GLOBAL: a second player's click re-captured the whole set mid-hold, so the FIRST chopper's product
    // kind, pacing, wear share and XP silently became the second player's, and that player's release disarmed the hold
    // (F-L4-03). The comment that stood here used to claim the damage was bounded ("it never loses or dupes content,
    // wear is targeted by PlayerUID") — only the wear half was ever true, and only because pendingAxes is keyed by
    // PlayerUID; everything else was shared. The set now travels together under ONE owner: BeginChopHold REFUSES a
    // second chopper instead of overwriting it, ChopTick ignores ticks from anyone else, and EndChopHold listens only
    // to the owner. One stand serves one chopper — per-player keying of the bundle was rejected deliberately (author,
    // AuditFixPlan T02.02): a chopping block is a one-man tool, and two men swinging axes into the same billet is not a
    // mode worth modelling.
    //
    // The whole bundle is transient interaction state — never serialized, meaningless across a save / reload — and it is
    // PER-SIDE: the client keeps its own instance to pace and predict its own swing, the server keeps the authoritative
    // one, and the two are never synced to each other. They agree because every captured input is deterministic and put
    // on the wire's 1/64 grid at its source (F-S06-01, AuditFixPlan T02.01), NOT because they exchange anything.
    sealed class ChopHold
    {
        /// <summary>The one chopper this hold belongs to. While the claim is fresh (<see cref="LastTickMs"/>) a click by
        /// anyone else is refused, so nobody can re-capture the set under the running chopper.</summary>
        internal readonly string OwnerUid;

        /// <summary>Engine time (ms) of the last tick this hold received, refreshed by <see cref="ChopTick"/>. A live
        /// hold is refreshed many times a second, so this only ever goes stale on a chopper who logged out, crashed or
        /// was teleported away mid-swing — <see cref="ChopHoldOwnerTimeoutMs"/> later the stand frees itself instead of
        /// staying locked to a player who is gone (the same disease F-L5-04 describes on the sawhorse).</summary>
        internal long LastTickMs;

        /// <summary>The per-swing duration this hold paces off, captured ONCE at <see cref="BeginChopHold"/> (swiftsaw,
        /// T04.02): <see cref="ImmersiveWoodworkingConfig.ChopSeconds"/> shortened by the holder's swiftsaw bonus
        /// (<see cref="ImmersiveWoodworkingModSystem.EffectiveChopSeconds"/>), else exactly Config.ChopSeconds. Captured
        /// once rather than re-read per tick so a mid-hold tier change can't drift the pacing, and read identically by
        /// <see cref="ChopTick"/> (relaunch interval) and <c>StartChopAnimation</c> (swing speed) — the first swing
        /// impacts at THIS value, not the raw ChopSeconds. Exposed to the Block side via <see cref="HeldChopSeconds"/>
        /// so the swing animation reads the SAME frozen number the strike schedule is built from; the release guard
        /// reads that schedule directly instead (<see cref="SwingHasLanded"/>).</summary>
        internal readonly float Seconds;

        /// <summary>The stack kind being chopped this hold, captured ONCE at <see cref="BeginChopHold"/> (where the
        /// stand is known non-empty) and held for the whole hold. <see cref="ChopTick"/> paces off THIS, never re-reading
        /// inventory[0] mid-loop: the synced stand content lags the server by a tick or two every cycle (chop empties it,
        /// the restock refills it, both round-trip to the client), so pacing the cosmetic swing off it would stall the
        /// loop whenever a sync is late — the swing would freeze on its impact frame until the content caught up,
        /// knocking the rhythm off. The kind is stable for a hold (restock is category-locked), so one snapshot is
        /// correct, and it can't go null on a momentarily-empty synced stand. The authoritative chop/refill still read
        /// the real inventory (<see cref="Chop"/> / <see cref="TryAutoRestock"/>, server-side).</summary>
        internal readonly ItemStack? Ref;

        /// <summary>Whether THIS hold is being worked with a MAUL (E06/T06.05), captured ONCE at
        /// <see cref="BeginChopHold"/> off the holder's active tool and held for the whole hold. Three consumers read it:
        /// <see cref="ChopStrike"/> (the maul chop-speed penalty, both sides), <see cref="Chop"/> (skip the half-log
        /// split + XP parity, server) and <see cref="ChopWearCost"/> (wear off the firewood yield — the WHOLE-log
        /// target — instead of the split's share of it, server; the one part of the cost that can change under a
        /// workpiece, which is why <see cref="WearThisStrike"/> tallies what it charged rather than re-deriving it).
        /// Captured rather than re-read at completion so it stays deterministic between client and server — the
        /// active hotbar slot is synced, so both compute the same flag at hold start — AND so a player can't start a log
        /// with a maul and "finish" it with a plain axe (or vice-versa) to mix the maul's one-pass firewood with an axe's
        /// cheaper pacing: the whole hold commits to the tool it BEGAN with.</summary>
        internal readonly bool IsMaul;

        /// <summary>Per-strike step factors captured ONCE at <see cref="BeginChopHold"/> off the holder's STARTING axe
        /// (E05 desync fix, 2026-07-24) — the metal-tier factor (<see cref="ToolTierSpeedFactor"/>) and the
        /// Toolsmith-sharpness factor (<see cref="ChopSharpnessFactor"/>). <see cref="ChopStrike"/> reads THESE, not a
        /// live per-strike ActiveAxe read: the axe is worn SERVER-SIDE only (WearThisStrike) and Toolsmith degrades its
        /// sharpness there too, while the client never wears it locally (its stack sync lags) — so a live per-strike read
        /// diverged `step` across sides, and E05's avalanche jitter (<see cref="ChopJitterFactor"/>) amplified that hair
        /// into a diverging completion strike and a cascading <see cref="WorkpieceIndex"/> desync over a long AutoRestock
        /// hold. Freezing alone does NOT make `step` identical, though — that was the false claim the old comment
        /// carried, and it cost a whole second audit to overturn: the two sides compute the metal factor from registries
        /// that DIFFER, because the client's arrived over the wire truncated onto a 1/64 grid (AuditFixPlan T02.01).
        /// Identity comes from ToolTierSpeedFactor quantizing its input the same way the wire does, so both sides divide
        /// the same numbers; freezing then keeps that shared value stable for the whole hold. A blunting axe therefore no
        /// longer changes speed mid-hold — the same "commit to the starting tool" semantics as the maul flag, and a hold
        /// is short relative to sharpness decay.</summary>
        internal readonly float MetalMult;
        internal readonly float SharpMult;

        /// <summary>Per-hold copy of <see cref="chopProgress"/> that <see cref="ChopTick"/> actually advances
        /// (<see cref="ChopStrike"/>): seeded from the serialized value at <see cref="BeginChopHold"/> and incremented
        /// locally on BOTH sides from deterministic inputs (config + the holder's own axe), so client and server reach
        /// 100% on the SAME strike WITHOUT the client reading the laggy synced chopProgress — the same reason the loop
        /// paces off <see cref="Ref"/>, not the synced stand.</summary>
        internal float Progress;

        /// <summary>Per-hold index of the workpiece being chopped (0 = the first, ++ on each completion), folded into the
        /// chop-jitter seed (E05, <see cref="ChopJitterFactor"/>) so successive workpieces in ONE hold get a DIFFERENT
        /// strike count — otherwise every fresh workpiece seeds off `before == 0` and an AutoRestock stack (a whole stack
        /// chopped through one block in one hold) would rob the jitter of any log-to-log variation. Advanced on BOTH
        /// sides at the same `completed` strike, so it stays in lockstep by the SAME construction (F-S06-01) with NO new
        /// synced/persisted state and NO networking.</summary>
        internal int WorkpieceIndex;

        /// <summary>Terminal latch for this hold: set true once the FINAL strike of the final workpiece has landed and
        /// nothing remains to chop, so <see cref="ChopTick"/> short-circuits to None (the swing holds its impact pose
        /// until release) and PHASE A can NOT re-fire <see cref="ChopStrike"/> on the now-empty stand. Load-bearing:
        /// unlike the old binary <see cref="Chop"/> (HasContent-guarded, so a re-fire on an empty stand was a harmless
        /// no-op), ChopStrike advances <see cref="Progress"/> unconditionally — without this latch a held RMB would keep
        /// phantom-striking the empty stand and, via PHASE B's HasContent-gated <see cref="TryAutoRestock"/>, drain the
        /// whole pack even with AutoRestock off.</summary>
        internal bool Done;

        // The two-phase swing clock (AutoRestock, E02): a swing runs to its impact (where the chop resolves), then a
        // short configurable pause, then the next swing relaunches. Both thresholds are in the HONEST hold seconds
        // (BlockChoppingBlock clamps the engine's replayed argument to real time — AuditFixPlan T02.03) and are laid
        // out as an ABSOLUTE schedule: each transition advances the PREVIOUS threshold by its interval rather than
        // restarting from the "now" that happened to observe it. That distinction is what keeps the two sides
        // together over a long AutoRestock hold. A threshold restarted from "now" inherits the observing side's
        // sampling error every cycle — one frame on the client, but up to one 0.15 s hold packet on the server — so
        // the server's cycles ran systematically longer and the gap grew log after log. Anchored absolutely, both
        // sides run the same multiples of (Seconds + restock delay) from t=0 and the sampling error stays a
        // non-cumulative lateness on WHEN each threshold is noticed. A hold pacing off a stale config can't drift
        // either: Seconds is captured once at BeginChopHold and never re-read.
        internal float NextImpactAt;   // hold seconds at which the next chop resolves (a swing's impact frame)
        internal bool InDelay;         // true while pausing after a chop, before the next log is laid + swing relaunched
        internal float RelaunchAt;     // hold seconds at which to lay the next log + relaunch the swing (only while InDelay)

        internal ChopHold(string ownerUid, long nowMs, float seconds, ItemStack? refStack, float progress,
                          bool isMaul, float metalMult, float sharpMult)
        {
            OwnerUid = ownerUid;
            LastTickMs = nowMs;
            Seconds = seconds;
            Ref = refStack;
            Progress = progress;
            IsMaul = isMaul;
            MetalMult = metalMult;
            SharpMult = sharpMult;
            NextImpactAt = seconds; // the first swing is launched by the caller at t=0, so its impact lands one `seconds` in
        }
    }

    /// <summary>The chop hold running on this stand, or null when nobody is chopping. Armed by
    /// <see cref="BeginChopHold"/>, cleared by <see cref="EndChopHold"/>. Non-null IS "a genuine chop hold is running" -
    /// the guard that keeps a NON-chop interaction (Shift+RMB stick-tool, Ctrl+RMB take/pull, each of which also returns
    /// true from Start with an axe in hand and therefore also drives Step) from running <see cref="ChopTick"/> off a
    /// previous hold's stale pacing and phantom-striking the stand. A HasContent re-check can NOT stand in for it: a real
    /// chop is legitimately empty-standed during the post-strike breather and the final pose-hold (F-S06-01).</summary>
    ChopHold? chopHold;

    /// <summary>How long (ms) a chop hold keeps this stand for its owner without a tick. A live hold is refreshed on
    /// every interaction step, so this only expires on a chopper who logged out, crashed or was teleported away
    /// mid-swing — long enough to ride out a lag spike, short enough that the next chopper never waits on a ghost.</summary>
    const long ChopHoldOwnerTimeoutMs = 2000;

    /// <summary>Block-entity packet: "this stand is busy, your chop was refused" — server to the ONE refused player
    /// (<see cref="RefuseChopHold"/>), handled in <see cref="OnReceivedServerPacket"/>. A private id: this BE has no
    /// dialog and so no <c>EnumBlockEntityPacketId</c> traffic to collide with.</summary>
    const int ChopRefusedPacketId = 2201;

    // DISCRETE chop progress (E03): the fraction [0,1] of the current laid workpiece that has been chopped, advanced one
    // notch per axe strike (ChopStrike). SERIALIZED + synced (To/FromTreeAttributes) so a half-chopped log resumes after
    // release / reload and drives the future visual bucket (ChopVisualBucket, T03.05); reset to 0 whenever the content
    // changes (TryPut / TryTake / TryAutoRestock) or a workpiece completes. Server-authoritative — only the server writes it.
    float chopProgress;

    // How much axe durability the CURRENT workpiece has already been charged (AuditFixPlan T03.01, F-L4-05). Lives
    // exactly as long as chopProgress — same four resets — because it is the other half of the same accounting: the bar
    // says how far the workpiece got, this says what was paid for getting it there. Kept as a tally instead of being
    // re-derived from the bar because the PRICE can change under a workpiece (a maul taking over from an axe costs more),
    // and a re-derived milestone reads the cheap stretch as already paid at the expensive rate — see WearThisStrike.
    // SERIALIZED so a half-chopped log can't be re-priced by releasing and re-gripping; -1 from FromTreeAttributes means
    // "saved before this tally existed", which WearThisStrike seeds the old way, once. Server-authoritative.
    int chopWearCharged;

    // Yaw (radians) the laid LOG faces, so a chopped log turns its front toward the chopper instead of sitting fixed to
    // world axes. Set to face the player on PLACE (TryPut) and re-set on each chop-START (FaceContentTo, from
    // OnBlockInteractStart) — NOT on release, and NOT continuously; walking round and re-pressing re-orients it. Same
    // atan2(-dx,-dz) convention + Y-rotation as the stuck axe (stuckToolYaw), so yaw 0 = the model's authored front,
    // unrotated (the model itself carries the correct base orientation — no code offset). Applied to the staged log mesh
    // (BuildStagedLogMesh) AND the placed half-log (BuildPlacedHalfLogMesh); other content ignores it. SERIALIZED + synced (To/FromTreeAttributes) so the facing
    // survives reload and reaches every client. Server-authoritative — only the server writes it. An auto-restocked log
    // keeps the current yaw (the chopper is still standing in the same spot), so it isn't reset on restock.
    float contentYaw;

    // Lazily-resolved mod system for the live, server-synced config (mirrors BlockEntitySawhorse.Config). Used by the
    // auto-restock gate (CanRestock); Chop resolves the same config inline for its other knobs.
    ImmersiveWoodworkingModSystem? modSystem;
    ImmersiveWoodworkingConfig Config => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).Config;

    /// <summary>The per-swing chop seconds <paramref name="byPlayer"/>'s hold is pacing off (swiftsaw, T04.02) — the
    /// value captured at <see cref="BeginChopHold"/> while THAT player's chop hold is running, else the plain
    /// <see cref="ImmersiveWoodworkingConfig.ChopSeconds"/>. The Block-side swing animation (<c>StartChopAnimation</c>)
    /// reads THIS so it uses the exact same frozen number <see cref="ChopTick"/> paces off — the first swing impacts
    /// here, not at the raw ChopSeconds. Owner-scoped (AuditFixPlan T02.02): a player refused this stand, or one
    /// running a following NON-chop interaction (put/take/build, which also drives Step/Stop), reads the plain config
    /// instead of somebody else's captured value. The release guard does NOT use this — it asks
    /// <see cref="SwingHasLanded"/>, which knows WHICH swing is on screen (AuditFixPlan T02.04).</summary>
    public float HeldChopSeconds(IPlayer byPlayer)
        => chopHold is { } hold && hold.OwnerUid == byPlayer.PlayerUID ? hold.Seconds : Config.ChopSeconds;

    /// <summary>Whether <paramref name="byPlayer"/>'s own chop hold is currently running on this stand — i.e. whether
    /// there is a swing of ours on screen for them at all. The Block side asks this before deciding what to do with the
    /// swing on release (AuditFixPlan T02.04): the client finishes ONE release through two engine callbacks
    /// (<c>OnBlockInteractCancel(ReleasedMouse)</c> and then, the same frame, <c>OnBlockInteractStop</c>), and whichever
    /// runs first ends the hold, so the second must find this false and leave the (possibly easing-out) swing alone.
    /// Owner-scoped for the same reason as <see cref="HeldChopSeconds"/>.</summary>
    public bool HasChopHold(IPlayer byPlayer)
        => chopHold is { } hold && hold.OwnerUid == byPlayer.PlayerUID;

    /// <summary>Whether the swing CURRENTLY on screen for <paramref name="byPlayer"/> has already landed its impact, so
    /// a release now should let it ease out of the impact pose instead of being cut dead (AuditFixPlan T02.04).
    /// <para>Asks the hold's own strike schedule rather than doing arithmetic on the total hold time. The obvious test —
    /// <c>secondsUsed &gt;= <see cref="HeldChopSeconds"/></c> — is only right for the FIRST swing: over an AutoRestock
    /// hold every later release is past that threshold too, so a release mid-wind-up of swing #3 read as "landed" and
    /// eased out, which is exactly the ghost wind-up (bug 2) the hard cut exists to prevent. The schedule is absolute
    /// since T02.03, so the answer is simply where the hold stands: the terminal pose-hold (<see cref="ChopHold.Done"/>)
    /// and the post-strike breather (<see cref="ChopHold.InDelay"/>) are both after an impact, and otherwise we are in
    /// PHASE A winding up toward <see cref="ChopHold.NextImpactAt"/>.</para>
    /// <para>Deterministic on both sides — the schedule is the same one <see cref="ChopTick"/> runs off the honest hold
    /// clock, so the chopper's own client and the server (whose animation every observer sees) reach the same verdict.
    /// The caller must pass the HONEST seconds (<see cref="ImmersiveWoodworkingModSystem.HonestHoldSeconds"/>).
    /// Falls back to the plain per-swing config when this isn't the caller's hold, mirroring
    /// <see cref="HeldChopSeconds"/>.</para></summary>
    public bool SwingHasLanded(IPlayer byPlayer, float secondsUsed)
    {
        if (chopHold is not { } hold || hold.OwnerUid != byPlayer.PlayerUID) return secondsUsed >= Config.ChopSeconds;
        if (hold.Done || hold.InDelay) return true;   // terminal impact pose / post-strike breather — both post-impact
        return secondsUsed >= hold.NextImpactAt;      // PHASE A: winding up, unless the release lands ON the impact
    }

    /// <summary>Whether the hold running on this stand committed to a MAUL (<see cref="ChopHold.IsMaul"/>); false when
    /// no hold is running. Read by the server-side <see cref="Chop"/> and <see cref="ChopWearCost"/>, which only ever
    /// run from inside a strike, so the false fallback is unreachable in practice — it keeps them honest for any future
    /// caller that reaches them outside a hold.</summary>
    bool HoldIsMaul => chopHold?.IsMaul ?? false;

    /// <summary>Metal-tier speed factor (E02) for the chopping axe, in [floor, 1] — a copper axe returns a fraction, a
    /// steel one 1.0; a flat 1.0 when <see cref="ImmersiveWoodworkingConfig.ScaleWorkSpeedByToolTier"/> is off. An axe
    /// with NO wood mining speed no longer lands on 1.0: the four vanilla <c>axe-*-ruined</c> stubs pass this block's
    /// <see cref="EnumTool.Axe"/> gate, and they now drop to the axe family's weakest tier instead of felling a log in
    /// the single strike of steel (AuditFixPlan T03.02). Thin instance wrapper over the mod system's shared family index
    /// (built once on load, read on both sides), the same lazy <see cref="modSystem"/> resolve as <see cref="Config"/> —
    /// mirroring the sawhorse. Used by <see cref="ChopStrike"/> to scale the per-strike progress so a lesser metal needs
    /// more strikes (T02.06).</summary>
    float ToolTierSpeedFactor(ItemStack? stack) => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).ToolTierSpeedFactor(stack);

    /// <summary>Toolsmith sharpness→speed factor for the swung axe, using the CHOP PLATEAU curve
    /// (<see cref="ImmersiveWoodworkingModSystem.ChopSharpnessFactor"/>): 1.0 across the 35–65% sharpness band (vanilla) or
    /// WITHOUT Toolsmith, rising to <see cref="ImmersiveWoodworkingConfig.ChopSharpnessCeiling"/> when razor-sharp and
    /// falling to <see cref="ImmersiveWoodworkingConfig.SharpnessSpeedFloor"/> when very blunt. Distinct from the pit saw /
    /// bark spud's linear curve. Same lazy <see cref="modSystem"/> resolve as <see cref="ToolTierSpeedFactor"/>; read on
    /// both sides off the synced axe stack so the per-strike bar advance stays deterministic (F-S06-01).</summary>
    float ChopSharpnessFactor(ItemStack? stack) => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).ChopSharpnessFactor(stack);

    public bool HasContent => !inventory[0].Empty;

    /// <summary>True when the laid workpiece is a valid chop input UNDER THE LIVE CONFIG — i.e. <see cref="IsChoppable"/>
    /// including the <see cref="ImmersiveWoodworkingConfig.AllowFirewoodToSticks"/> gate. The block's chop-start branch
    /// (<see cref="BlockChoppingBlock.OnBlockInteractStart"/>) requires this so a FIREWOOD workpiece left on the stand
    /// from before <c>AllowFirewoodToSticks</c> was turned off does NOT begin a hold: the resolver would yield null (no
    /// drop, no empty), and without this gate the discrete-strike loop would just spin the bar with nothing happening.
    /// The player takes such a workpiece back with Ctrl+RMB instead. A bare stand returns false.</summary>
    public bool ContentIsChoppable() => HasContent && IsChoppable(inventory[0].Itemstack, Config);

    /// <summary>True when an axe is stuck into the top of the stand (Shift+RMB). While set, the stand can't be chopped
    /// and can't take a log — the axe must be pulled out first (Ctrl+RMB). Mutually exclusive with <see cref="HasContent"/>:
    /// an axe only goes into an EMPTY stand, and a log can't be laid over a stuck axe.</summary>
    public bool HasTool => stuckTool != null;

    public BlockEntityChoppingBlock()
    {
        inventory = new InventoryGeneric(1, "choppingblock", "0", null);
    }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        inventory.LateInitialize("choppingblock-" + Pos, api);

        capi = api as ICoreClientAPI;
        if (capi != null)
        {
            RebuildStandMesh();  // E02: build the species-textured stand (the species was already read by the prior FromTreeAttributes)
            UpdateContentMesh(); // build the initial content mesh; the first chunk tesselation picks both up
            UpdateToolMesh();    // ...and a stuck axe, if one was persisted
        }

        // E05/T05.02: a stand placed before the data-block redesign loads as the hidden legacy husk
        // (BlockChoppingBlockLegacy, the re-registered choppingblock-{wood} code — T05.01). Migrate it to the live
        // variant-less block on load. Server-side and DEFERRED: the world can't be mutated during chunk load / inside
        // Initialize, so schedule the swap for a moment later (the sawhorse's RegisterDelayedCallback-in-Initialize
        // idiom). Idempotent — an already-migrated stand isn't a legacy block, so SelfHealFromLegacy re-checks and
        // no-ops. This also covers a legacy HELD item placed from an old inventory: it sets down the legacy block,
        // whose BE runs this same path.
        if (api.Side == EnumAppSide.Server && Block is BlockChoppingBlockLegacy)
        {
            RegisterDelayedCallback(_ => SelfHealFromLegacy(), 100);
        }
    }

    // Live data-block a pre-redesign stand (legacy per-species code choppingblock-{wood}) migrates into on load.
    static readonly AssetLocation LiveChoppingBlockCode = new AssetLocation("immersivewoodworking", "choppingblock");

    /// <summary>
    /// One-shot, server-side migration of a pre-redesign stand (E05/T05.02). Old worlds saved the chopping block as a
    /// per-species VARIANT code <c>choppingblock-{wood}</c>; this BE now loads on the hidden legacy husk
    /// (<see cref="BlockChoppingBlockLegacy"/>) that re-registers those codes (T05.01). Here it reads the species off
    /// the legacy block's code, <c>ExchangeBlock</c>s the position to the live variant-less <c>choppingblock</c>, and
    /// stamps the species onto this BE — after which the stand is indistinguishable from one placed by the new system
    /// (correct runtime re-texture + per-species name) and no husk remains.
    ///
    /// Why it is safe and lossless:
    ///  • <c>ExchangeBlock</c> swaps the block id WITHOUT OnBlockRemoved/OnBlockPlaced, so THIS BlockEntity instance and
    ///    its 1-slot inventory (any laid log/firewood) survive untouched — the engine repoints <c>BlockEntity.Block</c>
    ///    and calls <c>OnExchanged</c> (vsapi BlockEntity.cs:232). It is a synced op (vanilla doors swap the same way and
    ///    every client sees it), so the migrated block reaches clients live, and the chunk is marked for save.
    ///  • The species comes from the legacy block's <c>wood</c> variant (<see cref="BlockChoppingBlockLegacy.LegacyWood"/>),
    ///    the only place it was ever stored (old BEs persisted just the inventory). Pre-redesign stands are all vanilla
    ///    woods (the old block only registered game-domain species), so the domain is "game".
    ///  • Deferred from Initialize and server-only; <c>MarkDirty(true)</c> persists + syncs the species, which drives the
    ///    client re-texture through <see cref="FromTreeAttributes"/>.
    ///  • Idempotent: re-checks the block is still legacy before acting, so a second load (already migrated) is a no-op.
    /// </summary>
    void SelfHealFromLegacy()
    {
        if (Api?.Side != EnumAppSide.Server) return;
        if (Block is not BlockChoppingBlockLegacy legacy) return; // already migrated (or never legacy) → nothing to do

        Block? live = Api.World.GetBlock(LiveChoppingBlockCode);
        if (live == null)
        {
            Api.World.Logger.Warning("[immersivewoodworking] Legacy chopping block at {0} could not migrate: live 'choppingblock' not found.", Pos);
            return;
        }

        // Swap the block id in place; the engine keeps this BE + its inventory and repoints BlockEntity.Block.
        // (legacy is captured before the swap, so its `wood` variant is still readable below.)
        Api.World.BlockAccessor.ExchangeBlock(live.BlockId, Pos);

        // Carry the old code's species onto the migrated BE (domain "game": pre-redesign stands were vanilla-only).
        SetWood(legacy.LegacyWood, "game");
        MarkDirty(true); // persist + sync the species (drives the client re-texture via FromTreeAttributes)

#if IW_DEBUG
        Api.World.Logger.Notification("[immersivewoodworking] Migrated legacy chopping block at {0} -> choppingblock (wood={1}).", Pos, legacy.LegacyWood ?? "<none>");
#endif
    }

    /// <summary>
    /// Bakes the species-textured stand mesh (E02) and the stored stack's content mesh into the chunk's terrain
    /// mesh, so the engine culls them for free and there is no per-frame draw. Returns true to SUPPRESS the
    /// engine's default cube mesh once our own re-textured stand is built (same idiom as the sawhorse stage mesh);
    /// returns false (drawing the block's own default oak mesh) only for a bare / legacy stand or in the brief
    /// window before the first build, so the stand is never invisible. Runs on the tesselation thread: it only
    /// READS the standMesh / contentMesh references (both built on the main thread) — each reference is swapped
    /// atomically and the mesh is never mutated after assignment, so the reads are safe.
    /// </summary>
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        // Content (laid log/firewood) always draws on top, whichever stand mesh is in effect.
        MeshData? content = contentMesh;
        if (content != null) mesher.AddMeshData(content);

        // A stuck axe draws on top too. Mutually exclusive with content in practice (an axe only goes into an empty
        // stand), but drawing both would be harmless.
        MeshData? tool = toolMesh;
        if (tool != null) mesher.AddMeshData(tool);

        // Null stand (unknown species, or pre-first-build) → let base draw the block's own default (oak) mesh, so
        // the stand is never invisible. Otherwise draw our re-textured stand and skip the default cube.
        MeshData? stand = standMesh;
        if (stand == null) return base.OnTesselation(mesher, tessThreadTesselator);
        mesher.AddMeshData(stand);
        return true;
    }

    // Process-wide cache of the parsed stand Shape. The geometry is species-INDEPENDENT (textures are bound per BE
    // at tesselation via ChoppingBlockTexSource), so one parsed Shape is safely shared across every chopping block
    // and wood type — exactly how the sawhorse shares its log shapes, and how the engine reuses one cached Shape
    // per block across all chunk tesselations. Shape.TryGet re-parses the JSON on every call, so without this each
    // species' build would re-deserialize the file; the unsynchronized check-then-set is harmless (a rare double
    // parse on first use yields identical data, like EntityBehaviorChopAnim's source-anim cache).
    static Shape? sharedStandShape;
    static readonly AssetLocation StandShapePath = new AssetLocation("immersivewoodworking", "shapes/block/choppingblock.json");

    static Shape? GetStandShape(ICoreClientAPI capi) => sharedStandShape ??= Shape.TryGet(capi, StandShapePath);

    // E05: the three staged laid-LOG shapes — whole (bucket 0), part-hewn (1), deeply-hewn (2) — picked by
    // ChopVisualBucket so a log being chopped over several strikes visibly splits apart on the stand. Same
    // process-wide parsed-Shape cache + species-INDEPENDENT idiom as the stand shape: the geometry carries only the
    // wood/wood_corner/debarked_wood keys (bark sides / end-grain caps / freshly-hewn inner face), bound to the log's
    // real species at tesselation via LogTexSource, so one parsed shape per stage serves every wood. Loaded lazily and
    // shared across every chopping block; the unsynchronized check-then-set is harmless (a rare first-use double parse
    // yields identical data), exactly like sharedStandShape.
    static readonly Shape?[] sharedStageLogShapes = new Shape?[3];
    static readonly AssetLocation[] StageLogShapePaths =
    {
        new AssetLocation("immersivewoodworking", "shapes/block/choppingblock-log1.json"),
        new AssetLocation("immersivewoodworking", "shapes/block/choppingblock-log2.json"),
        new AssetLocation("immersivewoodworking", "shapes/block/choppingblock-log3.json"),
    };

    static Shape? GetStageLogShape(ICoreClientAPI capi, int bucket)
    {
        bucket = GameMath.Clamp(bucket, 0, 2);
        return sharedStageLogShapes[bucket] ??= Shape.TryGet(capi, StageLogShapePaths[bucket]);
    }

    // E05/T05.05: the two staged laid-HALF-LOG shapes — fresh (bucket 0) and part-hewn (bucket 1) — picked by
    // HalfLogVisualBucket (threshold 50%) so a half-log being chopped into firewood visibly splits before it drops at
    // 100%, mirroring the full-log stages. Dedicated "half-log resting on the block" models, authored in block space
    // already sitting on the stand top (like the stage LOGs), so they show at the right size instead of the tiny item
    // mesh the generic laid-content path scaled down. Same process-wide parsed-Shape cache + species-INDEPENDENT idiom
    // as the stand / stage-log shapes: the geometry carries only the wood/wood_corner/debarked_wood keys (bark round
    // sides / tree-ring end caps / debarked inner split face), bound to the half-log's real species per BE at
    // tesselation via LogTexSource. Loaded lazily; the unsynchronized check-then-set is harmless (a rare first-use
    // double parse yields identical data), exactly like sharedStageLogShapes. Fresh (halflog-placed1) is byte-identical
    // to the earlier single halflog-placed model it supersedes (T04.03).
    static readonly Shape?[] sharedStageHalfLogShapes = new Shape?[2];
    static readonly AssetLocation[] StageHalfLogShapePaths =
    {
        new AssetLocation("immersivewoodworking", "shapes/block/halflog-placed1.json"),
        new AssetLocation("immersivewoodworking", "shapes/block/halflog-placed2.json"),
    };

    static Shape? GetStageHalfLogShape(ICoreClientAPI capi, int bucket)
    {
        bucket = GameMath.Clamp(bucket, 0, 1);
        return sharedStageHalfLogShapes[bucket] ??= Shape.TryGet(capi, StageHalfLogShapePaths[bucket]);
    }

    /// <summary>
    /// (Re)build the stand mesh with the stored species' textures and flag the block for re-tesselation, so the
    /// stand in the world shows its own wood (oak ≠ pine visually). Change-gated on the species key
    /// (standMeshForWood): it runs on every refresh (Initialize / sync / place) but only re-tesselates when the
    /// species actually changes. Client-side, MAIN thread. A bare / legacy stand (null species) drops the mesh so
    /// OnTesselation draws the block's own default (oak) mesh. The species textures resolve cross-domain
    /// (<see cref="WoodTextures"/>), so a modded wood finds its own bark/treetrunk; the parsed shape is shared
    /// process-wide (<see cref="GetStandShape"/>). On a missing shape the key is left unset so a later call retries
    /// and the default mesh draws meanwhile (never invisible).
    /// </summary>
    void RebuildStandMesh()
    {
        if (capi == null) return; // client-only

        string? key = wood == null ? null : woodDomain + ":" + wood;
        if (key == standMeshForWood) return; // species unchanged → keep the current mesh (or stay bare)

        if (key == null)
        {
            // Bare / legacy stand: no species to paint → fall back to the block's own default mesh.
            standMesh = null;
            standMeshForWood = null;
            capi.World.BlockAccessor.MarkBlockDirty(Pos);
            return;
        }

        MeshData? mesh = BuildStandMesh(capi, wood!, woodDomain);
        if (mesh == null) return; // shape missing → leave key unset to retry; the default mesh draws meanwhile

        standMesh = mesh;
        standMeshForWood = key;
        capi.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    /// <summary>
    /// Build the species-textured stand mesh — the SINGLE source of truth for the chopping-block "geometry + wood",
    /// shared by the in-world stand (<see cref="RebuildStandMesh"/>, baked into the chunk terrain mesh) and the
    /// held/GUI/dropped item (<see cref="BlockChoppingBlock"/>.OnBeforeRender, uploaded as a GPU mesh). Tesselates the
    /// process-cached shape with a <see cref="ChoppingBlockTexSource"/> bound to the species' bark (M1) and tree-ring
    /// end-grain (M2): the block is only ever crafted from a BARKED placed log, so bark sides + ring top always, no
    /// debarked branch (unlike the sawhorse). Each texture resolves cross-domain (<see cref="WoodTextures"/>: the
    /// species' own mod domain first, then game, then its log block), so a modded wood finds its own bark/treetrunk. Returns null only if the shape
    /// can't be loaded (caller falls back to the block's own default-oak mesh, so it's never invisible). Client-side,
    /// MAIN thread (capi.Tesselator). The species is always known here — callers gate on a non-null wood first.
    /// </summary>
    internal static MeshData? BuildStandMesh(ICoreClientAPI capi, string wood, string woodDomain)
    {
        Shape? shape = GetStandShape(capi);
        if (shape == null) return null;

        AssetLocation barkTex = BarkTex(capi, wood, woodDomain);
        AssetLocation endTex = WoodTextures.Get(capi, woodDomain, wood, WoodTexFamily.Treetrunk);
        capi.Tesselator.TesselateShape("immersivewoodworking:choppingblock", shape, out MeshData mesh,
            new ChoppingBlockTexSource(capi, barkTex, endTex, woodDomain + ":" + wood));
        return mesh;
    }

    /// <summary>The species' BARK texture — the stand's round sides (<c>M1</c>), and the ONE texture the block's
    /// particle colour samples (<see cref="BlockChoppingBlock"/>.GetRandomColor, AuditFixPlan T04.06). Named on its own
    /// so the "bark/" path lives in a single place shared by the mesh and the chips: paint and particles must show the
    /// same wood. Bark rather than the tree-ring end grain because that is what the block's own default already
    /// resolves to — <c>choppingblock.json</c> declares no per-FACE texture keys, so the engine's facing lookup always
    /// misses and lands on the first entry, <c>M1</c>. Overriding the colour therefore changes the SPECIES and nothing
    /// else.</summary>
    internal static AssetLocation BarkTex(ICoreClientAPI capi, string wood, string woodDomain)
        => WoodTextures.Get(capi, woodDomain, wood, WoodTexFamily.Bark);

    /// <summary>Read a chopping-block STACK's species pair, or null when it carries none — the one place that decides
    /// "does this stack know its wood?", shared by the laid/fed mesh (<see cref="BuildLaidContentMesh"/>) and the
    /// particle colour (<see cref="BlockChoppingBlock"/>.GetRandomColor). An EMPTY species counts as none, not as a
    /// species: <c>StringAttribute.ToBytes</c> writes <c>""</c> for a null value, so a bad stamp survives the first
    /// save/sync as a present-but-blank key (AuditFixPlan T04.06 / F-L6-28) and would otherwise build texture paths
    /// with a hole where the wood should be. The write side is guarded too
    /// (<see cref="BlockChoppingBlock"/>.OnCreatedByCrafting) — this is the read-side half of the same invariant.</summary>
    internal static (string wood, string domain)? StackSpecies(ItemStack stack)
    {
        string? wood = stack.Attributes.GetString("wood", null);
        if (string.IsNullOrEmpty(wood)) return null;
        string domain = stack.Attributes.GetString("woodDomain", "game");
        return (wood!, string.IsNullOrEmpty(domain) ? "game" : domain);
    }

    /// <summary>Plays one random chop (log-splitting) sound at the stand on the COMPLETING strike. Dual-call (F-S06-11):
    /// called on BOTH sides with <paramref name="byPlayer"/> as dualCallByPlayer, so the chopper's OWN client plays it
    /// locally — right at the swing's visual impact, on the same clock the animation runs on — while the server broadcasts
    /// to every OTHER nearby player and skips the holder's copy. That removes the server→client broadcast latency and the
    /// accumulating server/client tick drift that made the split sound trail the swing over a long auto-restock run.</summary>
    void PlayChopSound(IPlayer byPlayer)
    {
        AssetLocation sound = ChopSounds[Api.World.Rand.Next(ChopSounds.Length)];
        Api.World.PlaySoundAt(sound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, byPlayer, true, 24f);
    }

    /// <summary>Plays one random ordinary axe-STRIKE sound at the stand on each NON-completing strike, so the intermediate
    /// hits aren't silent. Dual-call (F-S06-11): see <see cref="PlayChopSound"/> — the holder plays it locally at its own
    /// impact, the server broadcasts to everyone else (dualCallByPlayer = <paramref name="byPlayer"/>).</summary>
    void PlayStrikeSound(IPlayer byPlayer)
    {
        AssetLocation sound = StrikeSounds[Api.World.Rand.Next(StrikeSounds.Length)];
        Api.World.PlaySoundAt(sound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, byPlayer, true, 24f);
    }

    /// <summary>Right-click with a choppable in hand: move one into the stand. Returns true if accepted.
    /// Server-authoritative (mirrors TryTake/Chop); the client just swallows the click and reflects the
    /// synced state — its content mesh updates from FromTreeAttributes when the server pushes the change.</summary>
    public bool TryPut(ItemSlot fromSlot, IPlayer byPlayer)
    {
        if (HasContent || HasTool || !IsChoppable(fromSlot.Itemstack, Config)) return false; // HasTool: no log over a stuck axe; Config gates firewood (AllowFirewoodToSticks)

        // Only the server moves the item out of the held slot and into the stand. Running TryPutInto on the
        // client too would locally decrement the held slot before the authoritative move syncs back — a
        // redundant double-mutation the engine then has to correct, and asymmetric with TryTake/Chop.
        if (Api.Side != EnumAppSide.Server) return true;

        int moved = fromSlot.TryPutInto(Api.World, inventory[0], 1);
        if (moved <= 0) return false;

        chopProgress = 0f; // a freshly-laid workpiece starts unchopped (E03)
        chopWearCharged = 0; // ...and owes its full wear from scratch (T03.01)
        contentYaw = YawToPlayer(byPlayer); // a freshly-laid log faces the player who set it down
        fromSlot.MarkDirty();
        UpdateContentMesh();
        MarkDirty(true);

        // Server plays + broadcasts so it's heard once by everyone nearby (no client double-play).
        Api.World.PlaySoundAt(PlaceSound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
        return true;
    }

    /// <summary>Ctrl+RMB: pop the stored stack back out — into the player's inventory, or the world if full.
    /// Server-authoritative; the client just swallows the click and reflects the synced state.</summary>
    public bool TryTake(IPlayer byPlayer)
    {
        if (!HasContent) return false;

        if (Api.Side == EnumAppSide.Server)
        {
            ItemStack stack = inventory[0].TakeOutWhole();
            chopProgress = 0f; // taking the workpiece out discards any partial chop progress (E03)
            chopWearCharged = 0; // ...and the tally of what was paid for it (T03.01). The durability itself stays spent on the axe: a log laid back down starts unchopped and pays again, so this is never a way to chop cheaply
            UpdateContentMesh();
            MarkDirty(true);

            // Test the REMAINDER, never the return value. TryGiveItemstack returns "at least ONE item moved", not "it
            // all fit", so on a nearly-full pack a `!(...)` check skips the drop and the leftover ceases to exist.
            // TryPutInto decrements the caller's stack in place, so StackSize afterwards IS what's left (0 = all in).
            // The stand holds exactly one item today — TryPut and TryAutoRestock both TryPutInto(…, 1) into a slot they
            // guard empty — but unlike InventoryChopper/InventorySawmill this InventoryGeneric sets no MaxSlotStackSize,
            // so nothing in the TYPE enforces that, and TakeOutWhole hands back whatever is actually there.
            // Verified against Vintagestory.Common.PlayerInventoryManager, VintagestoryLib 1.22.3.
            byPlayer.InventoryManager.TryGiveItemstack(stack);
            if (stack.StackSize > 0)
            {
                Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 1.0, 0.5));
            }

            Api.World.PlaySoundAt(PlaceSound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
        }
        return true;
    }

    /// <summary>Shift+RMB with an axe in hand on an EMPTY stand: stick the axe into the top (<see cref="stuckTool"/>).
    /// Only an axe (<see cref="EnumTool.Axe"/>), and only when the stand holds neither content nor another axe — so a
    /// stuck axe and a laid log can never coexist, which is what makes "an axe is parked here" cleanly disable chopping.
    /// Server-authoritative, mirroring <see cref="TryPut"/>: the client returns true to swallow the click and reflects
    /// the synced state; the server moves the exact axe stack (with its durability / attributes) out of the held slot.</summary>
    public bool TryStickTool(ItemSlot fromSlot, IPlayer byPlayer)
    {
        if (HasContent || HasTool) return false;
        if (fromSlot.Empty || fromSlot.Itemstack.Collectible?.Tool != EnumTool.Axe) return false;

        // Only the server moves the item; running it client-side too would double-decrement the held slot before the
        // authoritative move syncs back (same reasoning as TryPut).
        if (Api.Side != EnumAppSide.Server) return true;

        stuckTool = fromSlot.TakeOut(1);
        // Capture the horizontal direction to the player so PoseToolOnStand can lean the handle toward them.
        // atan2(-dx, -dz) pairs with that Y-rotation: yaw 0 = the mesh's native lean (toward -Z), unrotated.
        double dx = byPlayer.Entity.Pos.X - (Pos.X + 0.5);
        double dz = byPlayer.Entity.Pos.Z - (Pos.Z + 0.5);
        stuckToolYaw = (float)System.Math.Atan2(-dx, -dz);
        fromSlot.MarkDirty();
        UpdateToolMesh();
        MarkDirty(true);

        Api.World.PlaySoundAt(PlaceSound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
        return true;
    }

    /// <summary>Ctrl+RMB when an axe is stuck: pull it back out — into the player's inventory, or the world if full.
    /// The caller gives this priority over taking the content, though in practice the two never coexist.
    /// Server-authoritative, mirroring <see cref="TryTake"/>.</summary>
    public bool TryPullTool(IPlayer byPlayer)
    {
        if (!HasTool) return false;

        if (Api.Side == EnumAppSide.Server)
        {
            ItemStack stack = stuckTool!;
            stuckTool = null;
            UpdateToolMesh();
            MarkDirty(true);

            if (!byPlayer.InventoryManager.TryGiveItemstack(stack))
            {
                Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 1.0, 0.5));
            }

            Api.World.PlaySoundAt(PlaceSound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
        }
        return true;
    }

    /// <summary>Consume the stored stack and drop the chopped result into the world. Server-side only.</summary>
    public void Chop(IPlayer byPlayer)
    {
        if (Api.Side != EnumAppSide.Server || !HasContent) return;

        ImmersiveWoodworkingModSystem mod = Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        ImmersiveWoodworkingConfig config = mod.Config;

        ItemStack? input = inventory[0].Itemstack;
        // GetHandChopResult (not GetChopResult): the HAND path splits a full log into half-logs (E04/T04.02); the
        // automated chopper keeps the shared GetChopResult (log→firewood). Everything else resolves the same on both.
        // The hand block passes its MANUAL drop amounts (E04/T04.05) — the chopper's -1-inherit overrides never touch
        // this path, so the block always yields its own numbers.
        (ItemStack template, int dropCount)? result = GetHandChopResult(Api.World, input, config, DropAmounts.Manual(config), HoldIsMaul);
        if (result == null) return;

        inventory[0].TakeOutWhole();
        UpdateContentMesh();
        // KEEPS redrawOnClient, unlike the per-strike sync T05.04 made data-only — and for a reason that has nothing to
        // do with the mesh (the client's own UpdateContentMesh would repaint the emptied stand either way). Emptying
        // flips HasContent, and HasContent is what BlockChoppingBlock.GetPlacedBlockInteractionHelp branches its whole
        // hint set on. The help HUD does NOT poll (vs-interaction-help-no-poll), this stand has no
        // RefreshInteractionHelpOnChange of its own (the sawhorse / sawmill / chopper each grew one), and the player is
        // by definition still looking at the block they just finished chopping — so the dirty-block packet is the only
        // thing that recomposes "place wood to chop" over the stale "chop this log". Same for every other
        // content/tool/species boundary in this file; only the mid-log strike, the hold's settling sync and the facing
        // turn went data-only.
        MarkDirty(true);

        // splitter (xSkills, T03.02): boost FIREWOOD output only — never sticks or half-logs (sticks are a low-value
        // byproduct, half-logs are an intermediate) — and only on THIS hand path. The automated chopper resolves through
        // GetChopResult directly (no Chop, no IPlayer), so it never reaches here and stays unbuffed (out of scope). A whole
        // log makes firewood over its TWO half-log chops, so the bonus rolls per half-log — expected +extra × FirewoodPerLog
        // across the log. Chance-based, one roll per completed workpiece; a clean no-op without xSkills / a Tier-0 splitter.
        ItemStack outStack = result.Value.template;
        if (IsFirewood(outStack) && mod.TryGetSplitterYield(byPlayer, out float splitterExtra))
            ApplySplitterYield(outStack, result.Value.dropCount, splitterExtra);

        SpawnDistributed(outStack, result.Value.dropCount, byPlayer);

        // Forestry XP for one completed log-PROCESSING workpiece (xSkills, E02/T02.03) — a clean no-op without xSkills
        // (GrantForestryXp hard-gates to the server and to an installed, enabled integration). Granted for the
        // log-processing chain and SCALED BY WORKPIECE (user 2026-07-16): a whole log → two half-logs is the BASE
        // (XSkillsChopXpPerWorkpiece); each half-log → firewood is HALF the base (it's the second, smaller step of the
        // same log). So a fully processed log yields base (log→halves) + 2 × ½·base (each half→firewood) = 2 × base.
        // The SECOND-ORDER firewood → sticks chop (ChopKind.Firewood) used to be EXCLUDED outright (0 XP) as an
        // anti-farm rule; since 2026-07-31 (user) it pays its OWN absolute rate, XSkillsFirewoodChopXp — default 0.05,
        // a quarter of the log rate. It stays a separate knob rather than another fraction of the base precisely
        // BECAUSE it is the farmable one: firewood is a stockpileable byproduct whose input can be acquired in bulk
        // outside the chopping block, so a server must be able to zero it (0 = the original rule) without touching the
        // log/half-log grants, and raising the log rate must not drag the farm rate up with it. The splitter ability
        // still never boosts sticks — that principle is unchanged. Per-WORKPIECE, not per-strike. The AUTOMATED chopper
        // never reaches here (GetChopResult / OnChopperTick, no IPlayer), so station chopping grants no personal XP
        // either. `input` still references the just-chopped stack (TakeOutWhole cleared the slot, not the captured
        // reference), so its kind resolves after the drop.
        ChopKind choppedKind = ChopKindOf(input);
        float chopXp = choppedKind == ChopKind.Log      ? config.XSkillsChopXpPerWorkpiece
                     : choppedKind == ChopKind.HalfLog  ? config.XSkillsChopXpPerWorkpiece * 0.5f
                     : choppedKind == ChopKind.Firewood ? config.XSkillsFirewoodChopXp
                     : 0f; // ChopKind.None — unrecognised workpiece, nothing to credit
        // E06/T06.06 (variant A — parity, 2026-07-24): a MAUL turns a full log straight to firewood in ONE workpiece
        // (T06.05), skipping the log→2 half-logs→firewood chain that grants base + 2 × ½·base = 2 × base above. Left as the
        // plain ChopKind.Log branch it would credit only 1 × base — HALF the forestry XP for the same wood, a covert
        // penalty that reads as a bug ("why doesn't the maul level forestry?"). So credit the full 2 × base, but ONLY when
        // the maul actually replaced the chain: captured maul (ChopHold.IsMaul) on a FULL log (IsFullLog) with the split
        // stage ON. HalfLogChopStage off = every tool one-passes the log for 1 × base, so no bonus (parity with the axe,
        // not a maul-only buff). Scoped to ChopKind.Log alone, so it can never double the half-log (0.5 × base) or the
        // firewood (XSkillsFirewoodChopXp) rate — the maul does not chop either of those into being. splitter yield
        // (above) needs no change — it rolls once on the maul's single firewood drop instead of twice per half-log, and
        // the expectation matches (bigger stack, one roll; verify by run).
        if (HoldIsMaul && config.HalfLogChopStage && IsFullLog(input))
            chopXp = config.XSkillsChopXpPerWorkpiece * 2f;
        if (chopXp > 0f)
            mod.GrantForestryXp(byPlayer, chopXp, "chop:" + choppedKind);
        else
            // Reachable two ways now, and a log must say WHICH: an unrecognised workpiece (ChopKind.None), or a rate
            // the server deliberately zeroed (XSkillsFirewoodChopXp = 0 restores the old no-XP-for-sticks rule). Both
            // are silent no-ops otherwise, i.e. indistinguishable from a broken grant — which is the exact confusion
            // that made "no xp for firewood" get reported as a bug in the first place.
            mod.DbgXp("chop:" + choppedKind, byPlayer, "no xp: rate for this kind is 0 (config) or kind unrecognised");

        PlayChopSound(byPlayer); // dual-call (F-S06-11): broadcast the log-split sound to OTHER nearby clients, skip the
                                 // holder — the holder's own client already plays it locally at its impact (ChopStrike)
        // NOTE 1: axe WEAR is no longer charged here. It is spent PER STRIKE in ChopStrike (WearThisStrike), spread
        // across the workpiece's strikes as progress milestones so it can't be dodged by doing the work with a good axe
        // and "finishing" a nearly-chopped log with a throwaway flint one. The per-log TOTAL is unchanged; Chop just
        // drops + empties + plays the split sound.
        // NOTE 2: auto-restock is NOT done here either. The continuous-chop loop (ChopTick) delays laying the next log
        // until AFTER the configured pause, so the stand sits visibly empty during the breather; ChopTick owns the refill.
    }

    /// <summary>
    /// One axe-strike impact on the laid workpiece (E03): advance the DISCRETE chop progress by one notch and, only when
    /// it reaches 100%, resolve the chop exactly once (<see cref="Chop"/>: drop + empty + wear the axe). Each strike adds
    /// <see cref="ImmersiveWoodworkingConfig.ChopSpeedMultiplier"/> scaled by the axe's metal-tier factor
    /// (<see cref="ToolTierSpeedFactor"/>, T02.06), the axe's Toolsmith sharpness (<see cref="ChopSharpnessFactor"/>) and
    /// the per-kind billet boost — a copper axe advances LESS per strike than a steel one, and a BLUNT axe less than a
    /// sharp one, so either needs more strikes; with tier-scaling off and no Toolsmith both factors are 1.0 and every axe
    /// advances by exactly <c>ChopSpeedMultiplier × the billet boost</c> per strike.
    /// The increment is computed from LOCAL, deterministic inputs (config + the holder's own active axe), so
    /// <see cref="ChopTick"/> reaches completion on the SAME strike on client and server WITHOUT reading the laggy synced
    /// stand — mirroring how the loop paces off <see cref="ChopHold.Ref"/>. Server-side it also persists the advanced
    /// progress (or the reset on completion) and syncs it; the client only advances <see cref="ChopHold.Progress"/> to
    /// pace its own swing. Returns true on the strike that COMPLETED the workpiece. Called at each swing impact inside
    /// ChopTick's PHASE A.
    /// </summary>
    bool ChopStrike(IPlayer byPlayer)
    {
        // The hold owns every captured factor below. Null is unreachable from ChopTick (which bails on it first);
        // the guard is here so a future caller can't strike a stand nobody is holding.
        ChopHold? hold = chopHold;
        if (hold == null) return false;

        float speedMult = Config.ChopSpeedMultiplier;                 // Sanitize keeps this in [0.05, 10] (never 0 — the bar must fill)
        // metalMult + sharpMult are captured ONCE at BeginChopHold (E05 desync fix, 2026-07-24), NOT read live per strike:
        // the server wears the axe (WearThisStrike) and Toolsmith degrades its sharpness every strike, but the client
        // never wears it locally, so a live read diverged `step` across sides and E05's avalanche jitter cascaded it into a
        // desync over a long hold. Frozen here (like ChopHold.Seconds / ChopHold.IsMaul), so both sides pace off ONE captured
        // number. What makes that number the SAME number is not the freeze but the 1/64 quantization inside
        // ToolTierSpeedFactor — the client's tool registry crossed the wire truncated, so the raw factors never agreed
        // (AuditFixPlan T02.01). Both mechanisms are needed: quantization to agree, freezing to stay agreed.
        float metalMult = hold.MetalMult;                             // 1.0 when tier-scaling is off / the tool is non-axe / better-than-steel
        float sharpMult = hold.SharpMult;                             // chop PLATEAU: 1.0 across 35-65% / no Toolsmith, up to ChopSharpnessCeiling razor-sharp, down to SharpnessSpeedFloor very blunt

        // FIREWOOD → sticks and a HALF-LOG → firewood both chop FASTER than a whole log: they're already-split billet
        // wood. Each kind carries its OWN boost now (user 2026-07-12) so they tune independently — FirewoodChopSpeedMultiplier
        // (2.25 by default, so a SHARP copper axe one-shots firewood: ~0.47×1.0×2.25≈1.06) vs HalfLogChopSpeedMultiplier
        // (2, so copper still needs two strikes on a half-log). Keyed off the captured hold kind (ChopHold.Ref) — the same
        // on client and server — so the boosted increment stays deterministic. A whole log (ChopKind.Log) stays at LOG speed.
        ChopKind holdKind = ChopKindOf(hold.Ref);
        float kindSpeed = holdKind == ChopKind.Firewood ? Config.FirewoodChopSpeedMultiplier
                        : holdKind == ChopKind.HalfLog  ? Config.HalfLogChopSpeedMultiplier
                        : 1f;
        // E06/T06.07: a MAUL swallows the log→2 half-logs→firewood chain in ONE workpiece (GetHandChopResult skips the
        // split for it), so it clears a whole log in the strikes an axe spends merely SPLITTING it — strictly better than
        // an axe for firewood, which is the point. MaulChopSpeedMultiplier (default 1.5 — enough that a steel maul
        // one-shots a log even at the bottom of the E05 jitter spread) can NERF that below 1; it never speeds a workpiece below the 1-strike floor.
        // Gated on the FROZEN hold kind (holdKind == ChopKind.Log), NOT a per-strike IsFullLog: ChopStrike runs on both
        // sides and MUST stay bit-identical, but the live workpiece (inventory[0]) lags the client every restock cycle —
        // the same reason kindSpeed above reads ChopHold.Ref, not the stand. ChopKind is stable across a hold (AutoRestock
        // is category-locked), so both sides agree. The catch: ChopKind.Log is BROADER than the skip's live IsFullLog (it
        // also covers log sections and a laid choppingblock, which chop straight to firewood for every tool). Gating on
        // IsFullLog(hold.Ref) — the FIRST workpiece — instead lets an AutoRestock hold that STARTED on a non-full-log
        // (section / choppingblock) but tail-restocks full logs freeze the gate to false and clear those logs UNPENALISED,
        // bypassing the knob entirely (a real exploit caught in review 2026-07-24). Gating on ChopKind.Log penalises EVERY
        // log-family workpiece in the hold, so a restocked full log is always slowed — exploit closed. The only cost is
        // the SAFE direction: a maul on a bare section/choppingblock (rare misuse — it saves no work there) chops at ~0.5×,
        // slower than an axe but never faster. Skip / XP / wear stay per-workpiece EXACT (server-side, off the live input);
        // only this both-sides speed gate uses the frozen approximation. HalfLogChopStage off = every tool one-passes the
        // log, so no penalty. Deterministic (F-S06-01); the E05 jitter still multiplies AFTER this, below.
        float maulMult = hold.IsMaul && Config.HalfLogChopStage && holdKind == ChopKind.Log
                       ? Config.MaulChopSpeedMultiplier : 1f;
        float before = hold.Progress;                        // pre-strike progress: wear/bucket baseline AND part of the jitter seed
        // The FLOATING chop-speed knob (E11): each strike advances the bar by ChopSpeedMultiplier × the metal-tier factor
        // × the axe's Toolsmith sharpness × the billet boost. 1.0 (steel, sharp, whole log) one-shots a log; >1 fills
        // faster (weaker metals one-shot sooner); a BLUNT axe (sharpMult < 1) spreads it over more strikes. Unlike the
        // tool factor, ChopSpeedMultiplier is NOT capped at the steel reference, so chopping can be tuned past the
        // one-strike-steel floor. Sharpness only PENALISES a blunt edge (tops out at 1.0), so it slows blunt tools rather
        // than speeding sharp ones past their metal baseline.
        float step = metalMult * sharpMult * kindSpeed * maulMult * speedMult;
        // E05: scatter the notch by a DETERMINISTIC per-strike jitter in [1-J, 1+J] (J = ChopProgressJitter) so the
        // number of strikes a workpiece takes stops being perfectly predictable. ChopJitterFactor is seeded ONLY from
        // values both sides share at this instant (Pos, PlayerUID, the per-hold workpiece index, quantized `before`) —
        // never World.Rand or the synced stand — so client and server compute the same factor and finish on the SAME
        // strike (F-S06-01). At J=0 it
        // returns exactly 1f without hashing, so a disabled jitter reproduces the old increment bit-for-bit. The spread
        // is symmetric, so it does not change the average strike count or the total axe wear (completion still forces the
        // final wear milestone, see WearThisStrike) — it only shifts WHICH strike crosses a milestone / visual bucket.
        float jitter = ChopJitterFactor(byPlayer, hold.WorkpieceIndex, before);
        hold.Progress += step * jitter;

        bool completed = hold.Progress >= 1f;
#if IW_DEBUG
        // E05/T05.03: paired determinism trace. Client and server run ChopStrike INDEPENDENTLY; these lines (in
        // client-main.log vs server-main.log, or the two processes under test_multiplayer.bat) must match element for
        // element across a single hold — that is the proof the jitter did not fork the two sides. Stripped from Release.
        // `at` is the hold second this strike was SCHEDULED for (AuditFixPlan T02.03): equal across the sides proves
        // the impact schedule did not fork, and a per-side strike COUNT that matches the swings drawn is what shows
        // the release burst no longer sneaks an extra strike past the button (F-L9-02).
        Api.World.Logger.Notification("[IW-DBG] chopstrike side={0} pos={1} wp={2} at={3:0.000} before={4:0.00000} step={5:0.00000} jitter={6:0.00000} after={7:0.00000} completed={8}",
            Api.Side, Pos, hold.WorkpieceIndex, hold.NextImpactAt, before, step, jitter, hold.Progress, completed);
#endif

        if (Api.Side == EnumAppSide.Server)
        {
            // Charge THIS strike's share of the axe wear to the axe that landed it (anti-abuse). The full-workpiece cost
            // is unchanged — it's just spread across the strikes as progress milestones — so a player can't do the work
            // with a good axe and "finish" a nearly-chopped log with a throwaway flint one to dump all the wear on it.
            WearThisStrike(byPlayer, before, hold.Progress, completed);

            // Set the persisted bar BEFORE the completion Chop: Chop's own MarkDirty then syncs both the emptied stand
            // AND the reset bar in one round-trip; the mid-log branch persists + syncs the advanced fraction itself.
            chopProgress = completed ? 0f : hold.Progress;
            if (completed)
            {
                chopWearCharged = 0; // the finished workpiece is fully paid (WearThisStrike just forced the final milestone); the next one starts its own tally (T03.01)

                // drop + empty + BROADCAST the log-split sound to OTHER players (dual-call, skips the holder), once per
                // completed workpiece; the holder plays it on its own client below.
                Chop(byPlayer);
            }
            else
            {
                if (HasContent) PlayStrikeSound(byPlayer); // broadcast the axe hit to OTHERS; silent on an empty stand (the rare restock race)

                // Persist + sync the intermediate progress (drives the bar / visual bucket). DATA-ONLY, redrawOnClient:
                // false (T05.04 / F-L10-10): redrawOnClient sends every observing client a separate dirty-block packet,
                // and that re-tesselates the whole 32³ section — work this block's OWN key already proves unnecessary.
                // Everything downstream of the sync is covered without it: FromTreeAttributes runs on a data-only resend
                // just the same, UpdateContentMesh dirties the block ITSELF on the strikes that cross a visual bucket
                // (and only those), and the "% chopped" readout rides the block-info HUD's unconditional 500 ms
                // recompose off the client's freshly-synced BE copy. The interaction hints don't enter into it: this
                // stand's help set branches on HasTool / HasContent (BlockChoppingBlock.GetPlacedBlockInteractionHelp),
                // neither of which a mid-log strike touches. Same economy the machine got four times over in F-S12-03.
                MarkDirty();
            }
        }
        else
        {
            // CLIENT (the chopper's own view) — F-S06-11: play the impact sound HERE, locally, on the same clock as the
            // swing's visual impact. ChopStrike runs from ChopTick's PHASE A exactly when the hold clock reaches the swing's
            // impact (ChopHold.NextImpactAt == swing-start + ChopSeconds, and the swing's speed is set so its impact frame lands
            // on ChopSeconds), so this fires right at the visible hit — no server→client broadcast latency. That the SERVER
            // resolves the chop at the same moment is a T02.03 property, not a free one: until then the server's copy of the
            // hold clock ran a constant ~0.14 s ahead (the engine replays a packet's frames at +0.02 s each), so the drop and
            // everyone else's strike sound arrived BEFORE this local hit (F-L9-02). Two things hold them together now — the
            // clamp to real seconds at the Block seam, and the absolute impact schedule (see the ChopHold note), which stops
            // the sides' per-cycle sampling error from accumulating over a long auto-restock run.
            // Never gated on the synced stand (HasContent lags): the
            // holder keeps its own rhythm, which is the whole point of pacing off its own clock (mirrors ChopTick / F-S06-01).
            // The dualCallByPlayer arg is a no-op on the client — the call just plays locally; the server side above owns the
            // broadcast to everyone else, skipping this holder so no double play.
            if (completed)
            {
                PlayChopSound(byPlayer);
            }
            else
            {
                PlayStrikeSound(byPlayer);
                // ...and advance the laid log's chop STAGE visual locally, in lockstep with THIS impact, so the model
                // swaps to "part-hewn" / "deeply-hewn" exactly on the strike the player sees + hears — not a network
                // round-trip later when the server's chopProgress sync arrives (F-S06-11 follow-up). The completing
                // strike is excluded on purpose: its drop + restock is server-authoritative content, so that swap
                // stays sync-driven (predicting it would flash a wrong stage on the outgoing log).
                PredictChopStageVisual();
            }
        }

        if (completed) { hold.Progress = 0f; hold.WorkpieceIndex++; } // both sides: reset the bar AND advance the jitter-seed workpiece index for the next one
        return completed;
    }

    /// <summary>
    /// Deterministic per-strike jitter multiplier for the chop-progress notch (E05): a value in [1 - J, 1 + J]
    /// (J = <see cref="ImmersiveWoodworkingConfig.ChopProgressJitter"/>) so the number of strikes a workpiece takes
    /// varies slightly, WITHOUT ever desyncing client and server. The seed is built ONLY from values that are
    /// bitwise-identical on both sides at the instant of the strike (the reason the whole increment is kept local,
    /// F-S06-01): the block coordinates, the holder's <see cref="IPlayer.PlayerUID"/>, the per-hold workpiece index
    /// (<paramref name="workpieceIndex"/>), and <paramref name="before"/> — the pre-strike progress, accumulated locally on each
    /// side from the same inputs rather than read off the laggy synced stand. So both sides compute the SAME factor and
    /// complete on the SAME strike; no phantom swing, no out-of-time split sound.
    ///
    /// <para>"The same inputs" is the load-bearing half, and it was once merely ASSUMED — the seed forked here for a
    /// month behind exactly that word (AuditFixPlan T02.01). One of those inputs is the tool's metal-tier factor, which
    /// each side derives from its own copy of the collectible registry, and those copies are NOT the same: the client's
    /// crossed the wire truncated onto a 1/64 grid. A copper axe therefore accumulated <c>before</c> ~0.0011 apart per
    /// strike (4.65 of this hash's own quanta), the murmur finalizer below turned that into an unrelated factor, and the
    /// two sides finished up to 40% of logs on different strikes. A live re-press MASKS it — a client that waited for
    /// the sync adopts the server's progress and agrees perfectly — which is why the bug reads as absent unless you
    /// re-press within the sync window. It is closed at the source: the factor is quantized to the wire's grid on both
    /// sides (<see cref="ImmersiveWoodworkingModSystem.QuantizeLikeWire"/>). Keep every future input to this hash held
    /// to the same standard.</para>
    ///
    /// <para>The workpiece index is what gives log-to-log variation: a FRESH workpiece always seeds off
    /// <paramref name="before"/> == 0, so without it every log in an AutoRestock stack (a whole stack chopped through one
    /// block in one hold) would take the identical strike count — the jitter would vanish in the common workflow. It is
    /// a transient per-side accumulator like ChopHold.Progress (init in BeginChopHold, ++ on completion on BOTH sides), so
    /// it stays in lockstep by the same construction with no synced state and no networking.</para>
    ///
    /// Determinism rules (do NOT relax any of these):
    /// <list type="bullet">
    /// <item>No World.Rand / <c>System.Random</c> and no networking: their state differs per side.</item>
    /// <item>The string hash is our OWN FNV-1a (<see cref="StableStringHash"/>), NOT <c>string.GetHashCode()</c>: .NET
    /// randomizes string hash codes PER PROCESS, so <c>PlayerUID.GetHashCode()</c> would differ between the client and
    /// server processes and fork the two sides — the single most likely hidden bug here. (Same reasoning as the config
    /// digest logged at client join.)</item>
    /// <item><paramref name="before"/> is QUANTIZED to an int before hashing, which absorbs a LAST-BIT float difference
    /// between the sides — and nothing more. Do not read it as a safety net: it is 1/4096 wide, and the real divergence
    /// this hash was once fed (a copper axe's metal factor, before AuditFixPlan T02.01 quantized it) measured 4.65 of
    /// these quanta. Any input that crossed the wire has to be put on the wire's own 1/64 grid AT ITS SOURCE
    /// (<see cref="ImmersiveWoodworkingModSystem.QuantizeLikeWire"/>); by the time it reaches this hash it is too late,
    /// because the murmur finalizer below deliberately scatters neighbouring seeds into unrelated outputs.</item>
    /// <item>Every input must be a value both sides can compute WITHOUT the collectible registry, or one they derived
    /// from a quantized read. The registry itself is not shared: the client never parses <c>itemtypes</c>, it receives
    /// a truncated copy over the wire.</item>
    /// </list>
    ///
    /// At J &lt;= 0 it returns exactly 1f with no hashing, so a disabled jitter is bit-for-bit identical to the old
    /// strictly-deterministic increment. The spread is symmetric about 1.0, so it does not shift the expected notch —
    /// it only reshuffles which strike crosses a wear milestone / visual bucket (both derived from progress, so they
    /// stay correct; completion still forces the final wear milestone, keeping per-workpiece wear unchanged).
    /// </summary>
    float ChopJitterFactor(IPlayer byPlayer, int workpieceIndex, float before)
    {
        float j = Config.ChopProgressJitter;
        if (j <= 0f) return 1f; // disabled: skip the hash entirely for a bit-exact match with the pre-E05 increment

        // FNV-1a over the synchronized seed values (same 32-bit constants as ImmersiveWoodworkingModSystem.ConfigDigest,
        // and stable across processes for the same reason). Fold the block coords, the holder's stable UID hash, the
        // per-hold workpiece index, and the quantized pre-strike progress into one 32-bit accumulator.
        uint h = 2166136261u;                                        // FNV offset basis
        h = (h ^ (uint)Pos.X) * 16777619u;                           // FNV prime
        h = (h ^ (uint)Pos.Y) * 16777619u;
        h = (h ^ (uint)Pos.Z) * 16777619u;
        h = (h ^ (uint)workpieceIndex) * 16777619u;                  // per-hold workpiece index: varies successive logs in one AutoRestock hold
        h = (h ^ StableStringHash(byPlayer.PlayerUID)) * 16777619u;
        h = (h ^ (uint)(int)(GameMath.Clamp(before, 0f, 1f) * 4096f)) * 16777619u;
        // Final avalanche (murmur3 32-bit finalizer) so seeds that differ only in the low bits of `before` don't map to
        // near-adjacent outputs — without it consecutive strikes would drift the factor smoothly instead of scattering.
        h ^= h >> 16; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;

        float h01 = (h & 0xFFFFFFu) / (float)0x1000000; // top-cleared 24 bits -> [0, 1); 24 bits stays in float's exact-int range
        return 1f + (h01 * 2f - 1f) * j;                // uniform on [1 - j, 1 + j]
    }

    /// <summary>A process-STABLE 32-bit hash of a string (FNV-1a), for seeding <see cref="ChopJitterFactor"/>. Unlike
    /// <c>string.GetHashCode()</c>, which .NET randomizes per process (so it would differ between the client and server
    /// processes and break cross-side determinism), this depends only on the characters. Same FNV-1a idiom as the config
    /// digest in the mod system.</summary>
    static uint StableStringHash(string s)
    {
        uint h = 2166136261u;
        for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
        return h;
    }

    /// <summary>The holder's active axe (the one being swung), or null if the active slot isn't an axe — read only for
    /// its metal-tier speed factor (<see cref="ChopStrike"/>). Reads the same synced active slot on both sides, so the
    /// per-strike increment matches. Wear still targets the axe captured at swing START (<see cref="WearSwingAxe"/>), so a
    /// mid-swing slot swap can't move the wear; the speed uses whatever axe is active at the impact.</summary>
    static ItemStack? ActiveAxe(IPlayer byPlayer)
    {
        ItemSlot slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        return !slot.Empty && slot.Itemstack.Collectible?.Tool == EnumTool.Axe ? slot.Itemstack : null;
    }

    /// <summary>How <see cref="ChopTick"/> tells the caller to pace the cosmetic swing this tick. The progress + chop are
    /// resolved internally (server-side); these only drive the animation so it reads as a rhythm, not a freeze.</summary>
    public enum ChopTickAction
    {
        /// <summary>Nothing to do this tick — keep the current swing as-is (mid-swing, or holding its impact pose
        /// after the FINAL strike of the final workpiece until release).</summary>
        None,
        /// <summary>A strike just landed and another swing is coming (more strikes on this same log, or the next
        /// auto-fed one): ease the swing OUT from its impact pose for the breather (so it relaxes instead of hanging
        /// frozen).</summary>
        EnterPause,
        /// <summary>The breather elapsed: start a FRESH swing — on the SAME log (mid-chop) or the next one just auto-laid
        /// (after a completed workpiece). It eases in from the relaxed pose; its impact lands one <c>chopSeconds</c>
        /// later, in lockstep with the strike timer.</summary>
        Relaunch
    }

    /// <summary>
    /// One tick of a chop hold for <see cref="BlockChoppingBlock.OnBlockInteractStep"/>, driving the discrete-strike chop
    /// as a two-phase clock. A workpiece is chopped over several strikes (E03); each cycle reads strike → breather →
    /// (relaunch | place-next) → strike:
    ///  • PHASE A (waiting for impact) — once <paramref name="secondsUsed"/> reaches the next swing's impact, land a
    ///    strike (<see cref="ChopStrike"/>: advance the bar; on 100% drop + EMPTY the stand + wear the axe, else just
    ///    chip the same log). Then keep swinging UNLESS this was the final strike of the final log — i.e. it COMPLETED
    ///    the workpiece AND the player has no more acceptable log to auto-lay (<see cref="CanRestock"/>, a no-take peek):
    ///    that returns <see cref="ChopTickAction.None"/> and the swing holds its end pose until release (exactly like a
    ///    single chop). Any other strike returns <see cref="ChopTickAction.EnterPause"/> so the caller eases the swing
    ///    out for the breather.
    ///  • PHASE B (pausing) — for <paramref name="restockDelay"/> seconds the swing eases out; once it elapses,
    ///    <see cref="TryAutoRestock"/> lays the next log only if the stand is now EMPTY (a completed workpiece) — it
    ///    self-guards to a no-op on a still-full stand (a mid-log strike), so the same log keeps going — then return
    ///    <see cref="ChopTickAction.Relaunch"/> so the caller starts a fresh swing, impacting one
    ///    <see cref="HeldChopSeconds"/> later where the next strike lands.
    /// The bar advances on LOCAL deterministic inputs (config + the holder's own axe), so client and server complete on
    /// the SAME strike without the client reading the lagging synced stand. <see cref="ChopStrike"/> /
    /// <see cref="Chop"/> / <see cref="TryAutoRestock"/> are server-guarded, so the client runs this purely to pace its
    /// own swing; the continue/stop decision uses <see cref="CanRestock"/> (own-pack peek) + the local bar, both of which
    /// answer the same on the client. With <paramref name="restockDelay"/> = 0 the breather collapses to one frame.
    /// Pacing is primed by <see cref="BeginChopHold"/>.
    /// <para><paramref name="secondsUsed"/> must be the HONEST hold clock — the caller
    /// (<see cref="BlockChoppingBlock.OnBlockInteractStep"/>) runs the engine's argument through
    /// <see cref="ImmersiveWoodworkingModSystem.HonestHoldSeconds"/> first (AuditFixPlan T02.03). Passing the raw one
    /// re-opens F-L9-02: the server's copy is inflated by the frames the engine replays per hold packet, which put it
    /// permanently ~0.14 s ahead of the client drawing the swing and let the release burst land one extra strike.</para>
    /// </summary>
    public ChopTickAction ChopTick(IPlayer byPlayer, float secondsUsed, float restockDelay)
    {
        // Only a hold primed by BeginChopHold is a genuine chop. A non-chop interaction (Shift+RMB stick-tool, Ctrl+RMB
        // take/pull) also returns true from Start with an axe still in hand, so the engine drives this tick for it too; bail
        // before the loop runs off an earlier chop's STALE pacing and phantom-strikes (chopProgress on an empty stand) or
        // auto-restocks a REAL log from the pack. Deliberately NOT a HasContent check — a real chop is legitimately
        // empty-standed mid-breather and at the final pose-hold. F-S06-01.
        ChopHold? hold = chopHold;
        if (hold == null) return ChopTickAction.None;

        // ...and only the OWNER's ticks drive it (AuditFixPlan T02.02). A second player holding RMB on this stand is
        // Stepped by the engine exactly the same, and before the ownership rule their tick advanced the FIRST chopper's
        // clock: their secondsUsed paced the swing, their axe took the wear, their name took the XP (F-L4-03). They were
        // already refused at BeginChopHold, so here we simply give them nothing to do.
        if (hold.OwnerUid != byPlayer.PlayerUID) return ChopTickAction.None;

        // Freshness stamp for the ownership claim (see ChopHoldOwnerTimeoutMs): a live hold is refreshed every step, so
        // only an owner who vanished mid-swing lets the claim lapse and frees the stand for the next chopper.
        hold.LastTickMs = Api.World.ElapsedMilliseconds;

        // The per-swing duration was captured once at BeginChopHold (swiftsaw-effective, T04.02) — pace off THAT, not a
        // fresh Config read, so client and server stay in lockstep across the whole hold even if the tier changed mid-swing.
        float chopSeconds = hold.Seconds;
        if (chopSeconds <= 0f) return ChopTickAction.None;

        // Terminal state: the final strike of the final workpiece already landed and nothing's left to chop — hold the
        // impact pose until release. Latched (not re-derived) so PHASE A can't re-fire ChopStrike on the now-empty
        // stand (ChopStrike, unlike the old Chop, isn't HasContent-guarded — see ChopHold.Done).
        if (hold.Done) return ChopTickAction.None;

        // A workpiece that is ON the stand but NOT choppable under the live config can never resolve — GetHandChopResult
        // returns null, so a completing Chop no-ops WITHOUT emptying the stand and the strike loop would otherwise spin
        // its swing forever re-completing a workpiece it can't consume. The only way to reach this is the content turning
        // non-choppable MID-HOLD: AllowFirewoodToSticks flipped off (server config) while a firewood hold is running
        // (the chop-start branch's ContentIsChoppable gate already blocks STARTING such a hold). Latch done so the hold
        // ends gracefully, exactly like the last-workpiece case — the firewood stays Ctrl+RMB-recoverable. An EMPTY stand
        // (the normal mid-breather / pre-restock window) is NOT this case (HasContent false) and falls through untouched.
        // Deterministic on both sides (synced inventory + synced config), so client and server stop together.
        if (HasContent && !ContentIsChoppable()) { hold.Done = true; return ChopTickAction.None; }

        // An axe stuck into the stand ends the loop the same way, for the same reason: the feed now refuses to lay a log
        // over one (TryAutoRestock / CanRestock, AuditFixPlan T03.04 / F-L1-07), so without this latch the swing would go
        // on striking an empty stand until the phantom workpiece "completed" and CanRestock finally said no. The ONLY
        // window is somebody else sticking their axe in during the breather — a stand must be empty to take one, and
        // HasContent excludes it the rest of the time. The flag is serialized/synced, so both sides latch together.
        if (HasTool) { hold.Done = true; return ChopTickAction.None; }

        // PHASE B — pausing after a strike: once the breather elapses, relaunch the swing. On a COMPLETED workpiece the
        // stand is empty and TryAutoRestock lays the next log (server); on a mid-log strike the stand is still full, so
        // it self-guards to a no-op and the same log keeps going.
        if (hold.InDelay)
        {
            if (secondsUsed < hold.RelaunchAt) return ChopTickAction.None;   // still pausing → swing easing out
            hold.InDelay = false;
            TryAutoRestock(byPlayer, hold.Ref);              // server: lay the next log IF the stand is empty and axe-free; else no-op / client no-op
            // The relaunched swing impacts one ChopSeconds after the breather WAS DUE to end, not after the tick that
            // happened to notice it had (T02.03) — see the ChopHold schedule note. On the client, which is the side
            // actually drawing the swing, the two are one frame apart, so the impact frame still lands on the strike.
            hold.NextImpactAt = hold.RelaunchAt + chopSeconds;
            return ChopTickAction.Relaunch;                  // tell the caller to start a fresh swing now
        }

        // PHASE A — waiting for the next swing's impact. Pace off our OWN clock + pack + local bar only, NOT the synced
        // stand: HasContent / chopProgress both lag the server every cycle (see ChopHold.Ref / ChopHold.Progress), so
        // gating the cosmetic swing on them would freeze the loop whenever a sync is late. The authoritative progress +
        // chop are server-side and HasContent-guarded inside ChopStrike/Chop, so they never drop or double-resolve.
        if (secondsUsed < hold.NextImpactAt) return ChopTickAction.None;

        // A strike lands: advance the discrete bar. Only the strike that reaches 100% resolves the chop (drop + empty +
        // wear) server-side inside ChopStrike; earlier strikes just chip away at the same log.
        bool completed = ChopStrike(byPlayer);

        // Keep swinging unless this was the FINAL strike of the FINAL log:
        //  • mid-log strike (not completed) → always continue; the same log is still on the stand, more strikes to go.
        //  • completed log → continue only while the player still has another acceptable log of the started kind to
        //    auto-lay next (CanRestock, a no-take own-pack peek — answers the same on client and server). On the last
        //    log this latches ChopHold.Done and returns None so the swing holds its impact pose until release, exactly
        //    like a single chop — and no further strike fires (the latch is what makes None mean "done", not "retry").
        if (completed && !CanRestock(byPlayer, hold.Ref))
        {
            hold.Done = true;
            return ChopTickAction.None;
        }

        // Breather before the next swing — between EVERY strike (intra-log and log-handover alike). PHASE B lays the
        // next log after the pause only when this strike emptied the stand. Timed from the impact that WAS DUE, not
        // from the tick that observed it (T02.03) — see the ChopHold schedule note.
        hold.InDelay = true;
        hold.RelaunchAt = hold.NextImpactAt + (restockDelay > 0f ? restockDelay : 0f);
        return ChopTickAction.EnterPause;
    }

    /// <summary>Prime the per-hold chop pacing — call once at the start of each right-click hold
    /// (<see cref="BlockChoppingBlock.OnBlockInteractStart"/>), where the stand is known non-empty. The first swing is
    /// launched there at t=0, so its impact lands at <paramref name="chopSeconds"/>. Snapshots the content as
    /// <see cref="ChopHold.Ref"/> so the rest of the hold paces off a stable kind, not the laggy synced stand stack, and
    /// seeds <see cref="ChopHold.Progress"/> from the persisted <see cref="chopProgress"/> so a half-chopped log RESUMES
    /// where it stood rather than restarting (the field is accurate here on both sides — the stand was idle before this
    /// hold, so the last sync reflects the true value).
    ///
    /// <para>Returns FALSE when another player's hold owns this stand (AuditFixPlan T02.02): one stand serves one
    /// chopper, so a second interactor is refused — and told why (<see cref="RefuseChopHold"/>) — instead of silently
    /// re-capturing the running hold's product kind, pacing, wear share and XP out from under its owner (F-L4-03). The
    /// caller must then start no swing. The claim goes stale after <see cref="ChopHoldOwnerTimeoutMs"/> without a tick,
    /// so a chopper who logs out mid-swing can't hold the stand hostage. The owner re-pressing is NOT a second chopper:
    /// it re-primes the hold exactly as before.</para></summary>
    /// <returns>True when the hold was primed for <paramref name="byPlayer"/>; false when the stand is busy.</returns>
    public bool BeginChopHold(float chopSeconds, IPlayer byPlayer)
    {
        long nowMs = Api.World.ElapsedMilliseconds;
        ChopHold? running = chopHold;
        if (running != null && running.OwnerUid != byPlayer.PlayerUID && nowMs - running.LastTickMs < ChopHoldOwnerTimeoutMs)
        {
            RefuseChopHold(byPlayer);
            return false;
        }

        // Capture the STARTING axe ONCE and derive every per-strike step factor from it here — the maul flag AND the
        // metal-tier / Toolsmith-sharpness speed factors (E06/T06.05 + E05 desync fix, 2026-07-24). All three are frozen
        // for the hold, so ChopStrike's `step` doesn't DRIFT apart mid-hold even though the server wears the axe every
        // strike (and Toolsmith degrades its sharpness) while the client's stack sync lags. Whether the two sides START
        // from the same number is a separate question with a separate answer: the metal factor is derived from a tool
        // registry the client only has in 1/64-truncated form, so ToolTierSpeedFactor quantizes to that grid on both
        // sides (AuditFixPlan T02.01). The active hotbar slot is synced and the stand was idle before this hold, so both
        // sides read the SAME starting axe here (F-S06-01). Committing to the start tool also closes "begin with a maul,
        // finish with an axe" (and the sharpness twin).
        ItemStack? startAxe = ActiveAxe(byPlayer);
        chopHold = new ChopHold(
            byPlayer.PlayerUID,
            nowMs,
            chopSeconds,                     // swiftsaw (T04.02): the effective per-swing duration for THIS hold, captured once
            inventory[0].Itemstack?.Clone(), // the kind being chopped this hold (Clone: the slot is emptied each chop)
            chopProgress,                    // resume the discrete bar where it stood (E03), don't restart it
            IsMaul(startAxe),
            ToolTierSpeedFactor(startAxe),
            ChopSharpnessFactor(startAxe));
        return true;
    }

    /// <summary>Tell <paramref name="byPlayer"/> that somebody else is already chopping this stand. The refusal is
    /// decided on the SERVER because only the server sees every player's hold — the bundle is per-side and never synced,
    /// so a client knows only its own and cannot judge this. It reaches the refused player as a TARGETED block-entity
    /// packet rather than as synced state: nobody else needs to know, and a stand nobody is fighting over pays nothing.
    /// Their client turns it into the ingame error and cancels the swing it had already started optimistically
    /// (<see cref="OnReceivedServerPacket"/>).</summary>
    void RefuseChopHold(IPlayer byPlayer)
    {
        if (Api.Side == EnumAppSide.Server && byPlayer is IServerPlayer serverPlayer)
        {
            ((ICoreServerAPI)Api).Network.SendBlockEntityPacket(serverPlayer, Pos, ChopRefusedPacketId);
        }
    }

    /// <summary>Client end of the refusal (<see cref="RefuseChopHold"/>). The click was optimistic by design: the button
    /// went down, this client armed its own hold and started swinging, because it cannot see whose hold the stand is
    /// already running (the bundle is per-side, never synced). This packet is the correction — drop the local hold, cut
    /// the swing, and say why in the ingameerror channel (a VTML channel: the message must carry no bare
    /// <c>&lt;</c>). It lands well before the first strike at any sane latency — the first impact is a whole
    /// <c>ChopSeconds</c> away — so no strike sound, progress or stage visual is ever predicted on a stand this player
    /// was refused.</summary>
    public override void OnReceivedServerPacket(int packetid, byte[] data)
    {
        if (packetid == ChopRefusedPacketId)
        {
            IPlayer? me = capi?.World.Player;
            if (me == null) return;

            if (chopHold?.OwnerUid == me.PlayerUID) chopHold = null;
            BlockChoppingBlock.CancelChopAnim(me);
            capi!.TriggerIngameError(this, "chopbusy", Lang.Get("immersivewoodworking:ingameerror-choppingblock-busy"));
            return;
        }

        base.OnReceivedServerPacket(packetid, data);
    }

    /// <summary>End <paramref name="byPlayer"/>'s chop hold — called from
    /// <see cref="BlockChoppingBlock.OnBlockInteractStop"/> and <see cref="BlockChoppingBlock.OnBlockInteractCancel"/>
    /// when the right-click is released or aborted. Clears <see cref="chopHold"/> so a FOLLOWING non-chop interaction
    /// (stick-tool / take / pull — each also drives Step with an axe in hand) can't run <see cref="ChopTick"/> off this
    /// hold's stale pacing and phantom-strike (F-S06-01). Owner-scoped (AuditFixPlan T02.02): a second player's release
    /// used to disarm the RUNNING hold and stall the real chopper until they re-pressed (F-L4-03), so a release by
    /// anyone but the owner is a no-op. Transient interaction state; nothing to persist.</summary>
    public void EndChopHold(IPlayer byPlayer)
    {
        if (chopHold == null || chopHold.OwnerUid != byPlayer.PlayerUID) return;
        chopHold = null;
        // Post-release reconciliation (progress-flicker fix, 2026-07-24): push ONE authoritative sync so the client's
        // now-frozen prediction settles EXACTLY to the server's chopProgress the instant its own hold clears — during
        // the hold the client owned the display and ignored the per-strike echoes (see FromTreeAttributes), so without
        // this a coalesced or over-predicted last strike could leave the resting % up to one strike off until the next
        // unrelated sync. Server-only; a no-op-valued MarkDirty is harmless. Api?.Side: EndChopHold also runs on the
        // client (from Stop/Cancel), where MarkDirty is not wanted. DATA-ONLY for the same reason as the per-strike
        // sync above (T05.04): this settles a NUMBER, and the settling value is by construction the one the client
        // already predicted — the mesh it implies is the mesh already drawn, so a forced section re-tesselation would
        // repaint the stand to itself. Should the last strike genuinely have crossed a bucket, UpdateContentMesh sees
        // the key change on this very sync and dirties the block itself.
        if (Api?.Side == EnumAppSide.Server) MarkDirty();
    }

    /// <summary>
    /// Turn the laid LOG to face <paramref name="byPlayer"/> — called at each chop-START
    /// (<see cref="BlockChoppingBlock.OnBlockInteractStart"/>) so a log presents its front to whoever is chopping it, and
    /// re-orients when a player walks round and starts again. Server-authoritative: only the server writes
    /// <see cref="contentYaw"/> and syncs it (MarkDirty), which drives the client re-tesselation via FromTreeAttributes.
    /// A full LOG and a placed HALF-LOG re-orient (see <see cref="BuildStagedLogMesh"/> / <see cref="BuildPlacedHalfLogMesh"/>);
    /// other content (firewood / a laid block) is left as-is. A no-op when the facing is unchanged, so a
    /// re-press from the same spot doesn't re-sync. NOT called on release, so letting go mid-chop leaves the log as it is.
    /// </summary>
    public void FaceContentTo(IPlayer byPlayer)
    {
        if (Api.Side != EnumAppSide.Server) return;
        ItemStack? laid = inventory[0].Itemstack;
        if (!HasContent || (!IsFullLog(laid) && !IsHalfLog(laid))) return; // only the staged log / placed half-log carry a facing

        float yaw = YawToPlayer(byPlayer);
        if (yaw == contentYaw) return; // already facing them → nothing to re-sync
        contentYaw = yaw;
        UpdateContentMesh();
        // Persist + sync the new facing. DATA-ONLY (T05.04): the mesh really does change here, and the client rebuilds
        // and dirties it ITSELF — LaidWorkpieceIdentity carries the yaw on exactly the two branches this method can
        // turn (staged full log, placed half-log), so the FromTreeAttributes → UpdateContentMesh this sync triggers
        // sees a changed key and re-tesselates. redrawOnClient would add a second, earlier dirty for the same repaint.
        MarkDirty();
    }

    /// <summary>Horizontal yaw (radians) from the stand centre toward <paramref name="byPlayer"/>, in the same
    /// atan2(-dx,-dz) convention the stuck axe uses (<see cref="TryStickTool"/>): 0 = the mesh's authored front,
    /// unrotated. Shared by <see cref="TryPut"/> (face the placer) and <see cref="FaceContentTo"/> (face the chopper).</summary>
    float YawToPlayer(IPlayer byPlayer)
    {
        double dx = byPlayer.Entity.Pos.X - (Pos.X + 0.5);
        double dz = byPlayer.Entity.Pos.Z - (Pos.Z + 0.5);
        return (float)System.Math.Atan2(-dx, -dz);
    }

    /// <summary>Whether <paramref name="byPlayer"/> still has a choppable to auto-lay next (a no-take peek behind the
    /// <see cref="ImmersiveWoodworkingConfig.AutoRestock"/> gate), preferring the SAME collectible AND wood as
    /// <paramref name="justChopped"/>. Used by <see cref="ChopTick"/> to decide whether to continue the loop; because
    /// it reads the player's OWN pack it answers identically on client and server, so the holder's swing stops in
    /// step with the server on the last log. Deliberately still asks only about the PACK: the other reason a feed can
    /// refuse — an axe stuck into the stand (<see cref="TryAutoRestock"/>, AuditFixPlan T03.04 / F-L1-07) — is latched
    /// one step earlier, by <see cref="ChopTick"/>'s own <see cref="HasTool"/> check, so that rule keeps a single
    /// home instead of being restated here.</summary>
    bool CanRestock(IPlayer byPlayer, ItemStack? justChopped)
        => Config.AutoRestock && AutoRestockUtil.FindRestockSlot(byPlayer, RestockAccept(justChopped), RestockPreferred(justChopped)) != null;

    /// <summary>The auto-restock ACCEPT predicate: a stack of the SAME chop category (<see cref="ChopKindOf"/>) as the
    /// one just chopped — logs restock only logs, firewood only firewood, never crossing
    /// categories. Returns an always-false predicate when the chopped kind is unrecognised (nothing restocks). Paired
    /// with <see cref="RestockPreferred"/> (same exact code) so the feed prefers the same species WITHIN the category.</summary>
    static System.Func<ItemStack, bool> RestockAccept(ItemStack? justChopped)
    {
        ChopKind kind = ChopKindOf(justChopped);
        return s => kind != ChopKind.None && ChopKindOf(s) == kind;
    }

    /// <summary>The "prefer the same species" predicate for the auto-restock scan: a stack that is the same thing, and
    /// the same WOOD, as the one just chopped (<paramref name="justChopped"/>) — or null when there's nothing to match
    /// (the caller then falls back to <see cref="RestockAccept"/> — any stack of the same category). Shared by
    /// <see cref="CanRestock"/> (peek) and <see cref="TryAutoRestock"/> (take) so the two can't drift.</summary>
    static System.Func<ItemStack, bool>? RestockPreferred(ItemStack? justChopped)
    {
        if (justChopped?.Collectible?.Code == null) return null;
        ItemStack sample = justChopped; // non-null past the check; captured so the lambda needs no re-test
        return s => SameChoppableSpecies(sample, s);
    }

    /// <summary>Whether two accepted inputs are the same thing of the same WOOD — the identity behind "prefer the same
    /// species" (<see cref="RestockPreferred"/>).
    /// <para>A log and a firewood carry their species IN THE CODE (<c>log-placed-birch-ud</c>), so for them the code
    /// comparison alone was, and remains, the whole answer. The half-log does not: it is ONE item for every wood, with
    /// the species held in the stack (<c>wood</c> / <c>woodDomain</c>, stamped at split time — see
    /// <see cref="HalfLogSpecies"/>). Comparing codes therefore called every half-log the same species, and the
    /// preference silently collapsed to the accept predicate: a run of oak halves fed itself birch as readily as oak,
    /// while the log and firewood chains it sits beside kept their wood (AuditFixPlan T03.04, F-L2-06). The sawhorse's
    /// counterpart has always compared species proper (GetWoodSpecies), which is the behaviour this restores here.</para>
    /// </summary>
    static bool SameChoppableSpecies(ItemStack a, ItemStack b)
    {
        if (a.Collectible?.Code == null || !a.Collectible.Code.Equals(b.Collectible?.Code)) return false;
        if (!IsHalfLog(a)) return true; // species rides the code — the check above already settled it

        // Same defaults the rest of the file reads these two with (a bare half-log renders as default-oak/game), so
        // "no attribute" and "the default written out" compare equal rather than splitting one species in two.
        //
        // The bark state joins them (AuditFixPlan T04.01) for the same reason the species did: since the split carries
        // it, a barked and a debarked half of the same wood are two visibly different workpieces that do not stack,
        // and "prefer the same thing" would otherwise pull a barked half onto a stand that had been working through a
        // stripped stack — the exact drift F-L2-06 named, one axis over.
        return a.Attributes.GetString("wood", "") == b.Attributes.GetString("wood", "")
            && a.Attributes.GetString("woodDomain", "game") == b.Attributes.GetString("woodDomain", "game")
            && ItemHalfLog.IsDebarked(a) == ItemHalfLog.IsDebarked(b);
    }

    /// <summary>
    /// AutoRestock (E02), server-side: lay the next choppable from <paramref name="byPlayer"/>'s own pack onto the
    /// (empty) stand — never the active slot (that holds the axe) — restricted to the SAME category as
    /// <paramref name="justChopped"/> (<see cref="RestockAccept"/>: logs↔logs, firewood↔firewood)
    /// and preferring the SAME collectible within it (<see cref="RestockPreferred"/>). Called by <see cref="ChopTick"/>
    /// at the END of the post-chop pause, so the log appears after the breather, not at the chop. No-op when nothing
    /// qualifies (the stand stays empty). The AutoRestock gate is already applied by the caller's
    /// <see cref="CanRestock"/> check.
    /// </summary>
    void TryAutoRestock(IPlayer byPlayer, ItemStack? justChopped)
    {
        // HasTool repeats the manual door's gate (TryPut): a stand with an axe stuck in it takes no workpiece, and the
        // auto-feed was the ONE put path that didn't say so (AuditFixPlan T03.04, F-L1-07). The two states are meant to
        // be mutually exclusive — the axe must be pulled out (Ctrl+RMB) before anything is laid — so a log dropped on
        // top of one produced a stand holding both, which no manual gesture can create and the Ctrl+RMB take path reads
        // as "content first". A stuck axe is somebody's, quite possibly not the chopper's, which is why the feed must
        // stop at it rather than push it aside.
        if (Api.Side != EnumAppSide.Server || HasContent || HasTool) return;

        ItemSlot? src = AutoRestockUtil.FindRestockSlot(byPlayer, RestockAccept(justChopped), RestockPreferred(justChopped));
        if (src == null) return;

        if (src.TryPutInto(Api.World, inventory[0], 1) > 0)
        {
            chopProgress = 0f; // the auto-laid next log starts unchopped (E03)
            chopWearCharged = 0; // ...and owes its own wear, not the finished log's (T03.01)
            src.MarkDirty();
            UpdateContentMesh();
            MarkDirty(true); // redrawOnClient on purpose: the auto-laid log flips HasContent back, and with it the hint set (see Chop)
            Api.World.PlaySoundAt(PlaceSound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
        }
    }

    /// <summary>
    /// Charge THIS strike's share of the axe wear (E03 anti-abuse). The full cost of chopping the current workpiece is
    /// unchanged — the same amount the old per-completion wear used (<see cref="ChopWearCost"/>) — but it is spread
    /// across the strikes as progress MILESTONES: crossing progress k/cost charges 1 durability, to the axe that landed
    /// the strike which crossed it. So the total per workpiece is identical, yet each point falls on the tool actually
    /// doing that stretch of the work — a player can't do the hard strikes with a good axe and "finish" a nearly-chopped
    /// log with a throwaway flint one to dump the whole log's wear on the flint. Completion forces the final milestone so
    /// the last remainder is always charged.
    ///
    /// What has ALREADY been paid is TALLIED on the workpiece (<see cref="chopWearCharged"/>), not re-derived from the
    /// progress bar (AuditFixPlan T03.01, F-L4-05). Deriving it worked only while the cost stayed put for the life of the
    /// workpiece, and it does not: <see cref="ChopWearCost"/> reads <see cref="ChopHold.IsMaul"/>, which lives ONE HOLD,
    /// while the progress lives the WORKPIECE. So an axe could prime a log cheaply, and the maul that took over on the
    /// next hold re-read the same bar against ITS higher cost and found the primed stretch "already paid" — the maul
    /// cleared the log for about half its price. A tally can't be re-interpreted: switching tools mid-workpiece now pays
    /// the DIFFERENCE up to whatever the finishing tool costs. The reverse order (maul first, axe finishes) can leave the
    /// tally above the cheaper cost — that just charges nothing more, an overcharge of what was genuinely spent, never a
    /// dodge. Server-side.
    /// </summary>
    void WearThisStrike(IPlayer byPlayer, float progressBefore, float progressAfter, bool completed)
    {
        int cost = ChopWearCost();
        if (cost <= 0) return; // durability disabled (RPDP ≤ 0) or the product isn't resolvable

        // LEGACY workpiece (saved before the tally existed, chopWearCharged < 0): seed the tally the way the old code
        // derived it, from the progress standing before this strike. For a workpiece whose cost never changed the two
        // agree exactly, so this reproduces the old accounting for the one log that was mid-chop across the upgrade
        // instead of re-charging it from zero.
        if (chopWearCharged < 0) chopWearCharged = (int)(GameMath.Clamp(progressBefore, 0f, 1f) * cost);

        int reachedAfter = completed ? cost : (int)(GameMath.Clamp(progressAfter, 0f, 1f) * cost);
        int due = reachedAfter - chopWearCharged;

#if IW_DEBUG
        // AuditFixPlan T03.01: the DoD is "the whole log→halves→firewood chain costs exactly AxeDurabilityPerLog, at
        // every setting" — a claim about a SUM across three workpieces, which nothing else in the game surfaces (the
        // durability readout moves too little to read, and the chopstrike trace carries progress, not price). One line
        // per charge makes the run readable: add up `due` over the three workpieces and compare to the target. `cost`
        // and `maul` say which of the three prices was applied and why. Server-side only; stripped from Release.
        Api.World.Logger.Notification("[IW-DBG] chopwear pos={0} dpl={1} cost={2} maul={3} tally={4} due={5} completed={6}",
            Pos, Config.AxeDurabilityPerLog, cost, HoldIsMaul, chopWearCharged, due < 0 ? 0 : due, completed);
#endif

        if (due <= 0) return;

        chopWearCharged += due; // persisted by the caller's MarkDirty (or reset with the bar on completion)
        WearSwingAxe(byPlayer, due);
    }

    /// <summary>The total axe durability a full chop of the CURRENT workpiece costs, derived from the direct whole-log
    /// target <see cref="ImmersiveWoodworkingConfig.AxeDurabilityPerLog"/> (default 3). A whole log costs exactly that,
    /// whether an axe SPLITS it (the split remainder <see cref="SplitWearCost"/> plus each half-log at
    /// <see cref="PerHalfLogWear"/>) or a maul clears it in ONE pass — the split chain sums back to the target by
    /// construction. A half-log is its <see cref="PerHalfLogWear"/> share; firewood → sticks (a separate step, not part of
    /// a log) is a flat 1 at the shipped numbers; a bare log section is anchored to its firewood-equivalent output. 0 when
    /// durability is disabled (target ≤ 0) or the product can't be resolved. Read from the workpiece on the stand, which
    /// is present on every strike (Chop empties it only after the final one). Server-side.</summary>
    int ChopWearCost()
    {
        int dpl = Config.AxeDurabilityPerLog;
        if (dpl <= 0) return 0;
        // GetHandChopResult so a full log resolves to its half-log split (the machine's GetChopResult would read it as
        // firewood and over-charge the split); half-log/firewood resolve the same on both. MANUAL amounts (E04/T04.05):
        // the wear anchors on the HAND block's own firewood output, never a chopper override. ChopHold.IsMaul (E06/T06.05):
        // a maul skips the split here TOO, so its full log resolves to firewood and is charged the WHOLE-log target
        // (below) instead of the split's SHARE of it — the maul does the whole log's work in one pass and pays for the
        // whole log. It MUST match the flag Chop uses: the cost has to follow the product actually made. This flag is
        // also the one thing about the cost that can change WITHIN a workpiece, which is why what has been paid is
        // tallied rather than re-derived — see WearThisStrike.
        ItemStack? input = inventory[0].Itemstack;
        (ItemStack template, int dropCount)? result = GetHandChopResult(Api.World, input, Config, DropAmounts.Manual(Config), HoldIsMaul);
        if (result == null) return 0;

        // Splitting a full log into half-logs produces no firewood — charge only the REMAINDER of the whole-log target
        // after the eventual half-log→firewood chops (SplitWearCost). Never zero at any reachable target (T03.01).
        if (IsHalfLog(result.Value.template)) return SplitWearCost();

        // The workpiece produces firewood (a half-log, or a full log a MAUL clears in one pass, or a laid section) or
        // sticks (firewood → sticks). AxeDurabilityPerLog is the DIRECT whole-log target:
        //  • a half-log is its 1/ChopChainSteps share (PerHalfLogWear), matching the SplitWearCost remainder so the chain
        //    sums back to the target exactly;
        //  • a full log cleared straight to firewood (maul, or HalfLogChopStage off) is the whole target;
        //  • anything else (a bare log section, and firewood → sticks) is anchored to its firewood-equivalent output, so a
        //    section costs a log's share and firewood → sticks lands on the flat min of 1 (its stick yield is well under a log's firewood).
        if (IsHalfLog(input)) return PerHalfLogWear();
        if (IsFullLog(input)) return dpl;
        int cost = (int)System.Math.Round((double)result.Value.template.StackSize / System.Math.Max(1, Config.FirewoodPerLog) * dpl);
        return cost < 1 ? 1 : cost;
    }

    /// <summary>The axe wear charged for one HALF-LOG → firewood chop: the whole-log target
    /// (<see cref="ImmersiveWoodworkingConfig.AxeDurabilityPerLog"/>) divided by the number of CHARGEABLE STEPS in the
    /// axe chain (<see cref="ChopChainSteps"/> — the split plus each half), integer floor, min 1: a half-log never costs
    /// less than 1 (user 2026-07-08). Paired with <see cref="SplitWearCost"/> (which charges the remainder) so the whole
    /// log→half-logs→firewood chain sums back to the target exactly. Dividing by the STEPS rather than by the halves is
    /// what leaves the split a share of its own (AuditFixPlan T03.01) — see SplitWearCost. Shared by both callers so the
    /// two never drift. Server-side.</summary>
    int PerHalfLogWear()
    {
        int dpl = Config.AxeDurabilityPerLog;
        if (dpl <= 0) return 0;
        int perHalf = dpl / ChopChainSteps;
        return perHalf < 1 ? 1 : perHalf;
    }

    /// <summary>
    /// The axe wear charged for SPLITTING a full log into <see cref="HalfLogsPerLog"/> half-logs (E04/T04.02): the
    /// whole-log target <see cref="ImmersiveWoodworkingConfig.AxeDurabilityPerLog"/> MINUS what the half-log→firewood
    /// chops each take (<see cref="PerHalfLogWear"/> × <see cref="HalfLogsPerLog"/>). So the whole
    /// log→half-logs→firewood chain costs exactly the target.
    ///
    /// It used to come out ZERO on the shipped default — the target was split across the HALVES only, so at target 2 the
    /// two half-log chops already spent it and the split itself was free. That "tiny abuse" (a free split into a
    /// slightly-better-burning intermediate) is closed (AuditFixPlan T03.01): the target is divided across
    /// <see cref="ChopChainSteps"/> — the split counts as a step of the work, because it IS one — and the split takes
    /// what the halves leave. That remainder is never zero and never below one half's share for any reachable target
    /// (floored at ChopChainSteps in <see cref="ImmersiveWoodworkingConfig.Sanitize"/>, or exactly 0 = "no wear at all"),
    /// so no clamp is needed here and none is wanted: a clamp would silently hide a target that stopped adding up.
    /// Server-side.
    /// </summary>
    int SplitWearCost()
    {
        int dpl = Config.AxeDurabilityPerLog;
        if (dpl <= 0) return 0;
        return dpl - HalfLogsPerLog * PerHalfLogWear();
    }

    /// <summary>Spend <paramref name="cost"/> durability on the axe that STARTED the current swing (captured by
    /// <see cref="CapturePendingAxe"/>) as long as that exact stack is still in its original slot, so swapping the active
    /// hotbar slot mid-swing can't move the wear onto a different axe; if the started axe has since left its slot, falls
    /// back to the currently active axe so a strike still always costs its due. Server-side.</summary>
    void WearSwingAxe(IPlayer byPlayer, int cost)
    {
        if (cost <= 0) return;

        // The captured (swing-start) axe is still the right target only if its exact stack hasn't left its slot.
        (ItemSlot? startedSlot, ItemStack? startedStack) = PeekPendingAxe(byPlayer);
        bool startedIntact = startedSlot != null
            && startedSlot.Itemstack == startedStack
            && startedStack?.Collectible.Tool == EnumTool.Axe;

        ItemSlot axeSlot = startedIntact ? startedSlot! : byPlayer.InventoryManager.ActiveHotbarSlot;
        if (axeSlot.Empty || axeSlot.Itemstack.Collectible.Tool != EnumTool.Axe) return;

        axeSlot.Itemstack.Collectible.DamageItem(Api.World, byPlayer.Entity, axeSlot, cost);
        axeSlot.MarkDirty();
    }

    /// <summary>Remember the axe (slot + exact stack) the player is starting a chop with, so the wear lands
    /// on that axe even if they swap their active slot before impact (see <see cref="WearSwingAxe"/>). Server-only
    /// and transient; keyed by player so two players chopping the same stand can't alias each other's axe.</summary>
    public void CapturePendingAxe(IPlayer byPlayer)
    {
        if (Api.Side != EnumAppSide.Server) return;

        ItemSlot slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (!slot.Empty && slot.Itemstack.Collectible.Tool == EnumTool.Axe)
        {
            pendingAxes[byPlayer.PlayerUID] = (slot, slot.Itemstack);
        }
    }

    /// <summary>Forget a player's captured chop axe (call on stop/cancel). No-op if Chop already consumed it.</summary>
    public void ClearPendingAxe(IPlayer byPlayer)
    {
        if (Api.Side == EnumAppSide.Server) pendingAxes.Remove(byPlayer.PlayerUID);
    }

    /// <summary>Peek the axe captured for this player at the CURRENT swing's start (see <see cref="CapturePendingAxe"/>),
    /// or (null, null) if none. Does NOT remove it — the capture is refreshed at each swing (start + relaunch) and cleared
    /// on stop/cancel (<see cref="ClearPendingAxe"/>), so per-strike wear reads it without consuming it.</summary>
    (ItemSlot? slot, ItemStack? stack) PeekPendingAxe(IPlayer byPlayer)
        => pendingAxes.TryGetValue(byPlayer.PlayerUID, out var hold) ? (hold.slot, hold.stack) : (null, null);

    // Drop scatter for a chopped pile, blocks/tick (same units as the sawhorse's SawDropEjectVelocity = 0.12).
    // These were briefly config-tunable (DropHorizontalVelocity/DropVerticalVelocity) while dialling in the feel;
    // the knobs are gone now, so the tuned values live here as named type-level constants — mirroring how the
    // sawhorse keeps its own drop-physics tuning in named consts (SawDropEjectVelocity, …).
    const double DropHorizontalVelocity = 0.075; // sideways spread (±)
    const double DropVerticalVelocity = 0.1;     // upward pop

    /// <summary>
    /// Splits the template's total stack size into separate item entities — as evenly as possible, object count
    /// capped to the total so each carries at least one item (via the shared
    /// <see cref="DropDistribution.SplitEvenly"/>, the same split the sawhorse uses in DropStageBatch) — and
    /// scatters each pile out of the stand with a small random pop. With
    /// <see cref="ImmersiveWoodworkingConfig.DropsAtPlayer"/> on, each pile drops at <paramref name="byPlayer"/>'s feet
    /// instead (<see cref="DropDistribution.TrySpawnAtPlayer"/>): the pile split is unchanged, only the spawn point moves.
    /// </summary>
    void SpawnDistributed(ItemStack template, int dropCount, IPlayer byPlayer)
    {
        System.Random rnd = Api.World.Rand;
        foreach (int size in DropDistribution.SplitEvenly(template.StackSize, dropCount))
        {
            ItemStack single = template.Clone();
            single.StackSize = size;

            if (DropDistribution.TrySpawnAtPlayer(Config, Api.World, byPlayer, single)) continue;

            Vec3d dropPos = Pos.ToVec3d().Add(0.25 + rnd.NextDouble() * 0.5, 0.9, 0.25 + rnd.NextDouble() * 0.5);
            Vec3d velocity = new Vec3d(
                (rnd.NextDouble() * 2 - 1) * DropHorizontalVelocity,
                rnd.NextDouble() * DropVerticalVelocity,
                (rnd.NextDouble() * 2 - 1) * DropHorizontalVelocity);
            Api.World.SpawnItemEntity(single, dropPos, velocity);
        }
    }

    // Single source of truth for what the chopping block accepts, so the "can I place this?" check
    // (IsChoppable) and the "what does it turn into?" logic (GetChopResult) can never drift apart and
    // leave an item that can be placed but not chopped. FirstCodePart() of "log-placed-oak-ud" is "log",
    // of "choppingblock-oak" is "choppingblock", of "flotsam-driftwood-1" is "flotsam", etc.
    // Note: FirstCodePart()=="log" matches ALL log kinds by design — log-placed, log-grown and log-resin are all
    // choppable. By hand a full log (IsFullLog: "log"/"debarkedlog") splits into half-logs first (E04/T04.02); a
    // section keeps chopping straight to firewood, and the automated chopper chops every log straight to firewood.
    // (The sawhorse deliberately narrows to log-placed only, because it mirrors a recipe; the chopping block has no
    // such constraint and accepts any log.)
    // NOTE: beach driftwood ("flotsam-driftwood-*") is deliberately NOT here. It used to be chopped on the block into
    // sticks, but E07 moved it to an in-world HAND break (BlockBehaviorFlotsamBreak): hold right-click on the driftwood
    // block to snap it into firewood + sticks by size. Its vanilla flotsam→firewood grid recipe stays disabled via
    // ImmersiveWoodworkingModSystem.IsFlotsamToFirewood, so driftwood is only ever processed by that hand break.
    // internal so ImmersiveWoodworkingModSystem can reuse the exact accept-list when deciding which vanilla/modded
    // "axe + X → firewood" grid recipes to strip — tying "what the stand can chop" to "what we remove the hand
    // recipe for", so the two can't drift (a narrow-trunk log the stand can't take, e.g. wildcrafttree's lognarrow,
    // is absent here and so keeps its hand recipe instead of being orphaned).
    internal static readonly string[] LogCodeParts = { "log", "logsection", "debarkedlog", "choppingblock" };

    /// <summary>The firewood item's code part, and the TAIL every species-specific firewood shares with it (E04/T04.05):
    /// vanilla's <c>agedfirewood</c> is the one that ships, and it is what a full aged-wood line has to be able to lay
    /// back on the stand. Recognised by SHAPE (ends with "firewood"), never by a list of species — the mod-independence
    /// rule (source-conventions.md §2), so a wood mod that ships its own <c>{wood}firewood</c> is accepted with no
    /// patch from us, exactly as its logs already are.</summary>
    const string FirewoodCode = "firewood";

    // The per-stack predicates below read code parts through CodeParts rather than FirstCodePart (AuditFixPlan
    // T05.03 / F-L10-01): the engine accessor splits the whole code on every call, and IsChoppable — which fans out
    // to three of them — is what the powered chopper's start gate (CanChop) asks on every 25 ms server tick and
    // every client frame. The collectible-level CouldBeChoppable below keeps the engine idiom: it runs once per
    // collectible at block load, where the split costs nothing and reads better.
    static bool IsLog(ItemStack? stack) => CodeParts.IsAny(stack?.Block?.Code, 0, LogCodeParts);

    static bool IsFirewood(ItemStack? stack) => CodeParts.EndsWith(stack?.Item?.Code, 0, FirewoodCode);

    /// <summary>Whether a code part names firewood — plain <c>firewood</c> or a species-specific <c>{wood}firewood</c>
    /// (E04/T04.05). Shared by the per-stack <see cref="IsFirewood"/>, the collectible-level
    /// <see cref="CouldBeChoppable"/> pre-filter and the mod system's vanilla-recipe strip
    /// (<c>ImmersiveWoodworkingModSystem.IsLogToFirewood</c>), so "what the stand produces and takes back" and "which
    /// hand recipe we remove for it" can never disagree about what firewood is. The per-stack <see cref="IsFirewood"/>
    /// asks the identical question of the code directly (<see cref="CodeParts.EndsWith"/>) so it needs no code part
    /// materialised; this string form stays for the callers that already hold a bare code from JSON.</summary>
    internal static bool IsFirewoodCodePart(string? part)
        => part != null && part.EndsWith(FirewoodCode, System.StringComparison.Ordinal);

    // The half-log item's code / FirstCodePart (immersivewoodworking:halflog) — one source of truth for the split-log
    // intermediate (E04), shared by the per-stack IsHalfLog and the collectible-level CouldBeChoppable pre-filter.
    const string HalfLogCode = "halflog";

    // A HALF-LOG item (E04/T04.02): the split-log intermediate on the way to firewood. Its own choppable kind — laid on
    // the stand and chopped into firewood, but auto-restocked only from other half-logs (ChopKind.HalfLog). An ITEM, so
    // (like firewood) it's matched off stack.Item, never stack.Block.
    static bool IsHalfLog(ItemStack? stack) => CodeParts.Is(stack?.Item?.Code, 0, HalfLogCode);

    // A FULL log — a barked or debarked log (FirstCodePart "log" or "debarkedlog"), the ONLY inputs the HAND stand
    // splits into half-logs (E04/T04.02). Deliberately NARROWER than IsLog: it excludes log SECTIONS (E08's domain) and
    // a laid chopping block, which keep chopping straight to firewood, while still covering grown/resin logs (they share
    // the "log" first part). The automated chopper never calls this, so every log there goes straight to firewood.
    static bool IsFullLog(ItemStack? stack) => IsFullLogBlock(stack?.Block);

    /// <summary>The BLOCK-level form of <see cref="IsFullLog"/> — a barked or debarked log, i.e. exactly the set of
    /// blocks that can become half-logs. <c>internal</c> so the mod system's non-combustible-species scan
    /// (<see cref="ImmersiveWoodworkingModSystem.IsNonCombustibleWood"/>) walks the SAME block set the split accepts,
    /// instead of restating "log or debarkedlog" a second time.</summary>
    internal static bool IsFullLogBlock(Block? block)
    {
        AssetLocation? code = block?.Code;
        return CodeParts.Is(code, 0, "log") || CodeParts.Is(code, 0, "debarkedlog");
    }

    /// <summary>Whether this block is the ALREADY-STRIPPED kind of full log — the half of <see cref="IsFullLogBlock"/>
    /// whose bark is gone. The one place that reading of the code shape lives (AuditFixPlan T04.01), so the split's
    /// stamp and the laid-log texture pick cannot disagree about what a debarked log is. Vanilla's own strip recipe
    /// and this mod's sawhorse both produce exactly this code, so it needs no per-mod knowledge: a wood mod that ships
    /// logs at all ships them under the same two first parts (source-conventions.md §2, accept by code SHAPE).</summary>
    internal static bool IsDebarkedLogBlock(Block? block) => CodeParts.Is(block?.Code, 0, "debarkedlog");

    // A MAUL (колун, E06): the splitting tool that turns a full log STRAIGHT to firewood on the stand, skipping the
    // half-log stage (GetHandChopResult). Recognised by FirstCodePart "maul", DOMAIN-INDEPENDENTLY and NOT by a concrete
    // code — so a maul assembled through Toolsmith (which re-issues the same first part) and any clone another mod bases on
    // "maul" both count. The maul carries tool:"axe", so it already passes the axe gate (BlockChoppingBlock.OnBlockInteractStart)
    // and the interaction-help filter for free; this predicate only adds the skip-the-split behaviour on top.
    static bool IsMaul(ItemStack? stack) => CodeParts.Is(stack?.Collectible?.Code, 0, "maul");

    // internal so BlockChoppingBlock can reuse the exact accept-list when building its interaction help,
    // keeping the "what can I place?" check and the advertised stacks from drifting apart. Config-parametrised so the
    // FIREWOOD input can be gated off (ImmersiveWoodworkingConfig.AllowFirewoodToSticks, default on): with it off,
    // firewood is NOT choppable — refused at every input gate (hand put, chopper put, hopper push, chopper start) and
    // dropped in GetChopResult — so it can neither be laid nor yield sticks. Logs and half-logs are never gated (a log
    // still chops into firewood regardless — that step is the primary output, not the firewood→sticks conversion this
    // flag removes). Shared by both chopping stations, so the one gate can't drift between them.
    internal static bool IsChoppable(ItemStack? stack, ImmersiveWoodworkingConfig config)
        => IsLog(stack) || (config.AllowFirewoodToSticks && IsFirewood(stack)) || IsHalfLog(stack);

    // Cheap collectible-level gate (code part only): true if obj's code could be a choppable input, WITHOUT
    // resolving handbook stacks. Lets interaction-help building filter FIRST (the vanilla idiom — see
    // BlockForge/BlockBloomery), so GetHandBookStacks is only ever called on plausible choppables. Crucial
    // because some unrelated vanilla items throw inside their own GetHandBookStacks during the early
    // block-load phase (ItemPressedMash NREs on api.World), which would otherwise crash mod load. The precise
    // per-stack check stays IsChoppable.
    internal static bool CouldBeChoppable(CollectibleObject obj)
    {
        if (obj?.Code == null) return false;
        string part = obj.FirstCodePart();
        return System.Array.IndexOf(LogCodeParts, part) >= 0
            || IsFirewoodCodePart(part)
            || part == HalfLogCode;
    }

    // The auto-restock category of a chopped input: a held swing only ever re-stocks WITHIN this category, so chopping
    // logs never pulls in your firewood and vice versa (mirrors the sawhorse's same-kind rule). Three kinds: a Log →
    // firewood (or → half-logs by hand); a HalfLog → firewood; Firewood → sticks. HalfLog is an item so it can't
    // collide with the block-based Log. Only the AUTO-FEED is category-locked — the manual place via TryPut still
    // accepts ANY choppable. (Driftwood used to be a fourth kind here; E07 moved it to the in-world hand break.)
    enum ChopKind { None, Log, Firewood, HalfLog }

    static ChopKind ChopKindOf(ItemStack? stack)
    {
        if (IsHalfLog(stack)) return ChopKind.HalfLog;   // its own category — half-logs auto-restock only from half-logs
        if (IsLog(stack)) return ChopKind.Log;
        if (IsFirewood(stack)) return ChopKind.Firewood;
        return ChopKind.None;
    }

    /// <summary>How many half-logs a full log splits into on the hand stand (E04/T04.02) — fixed at 2 because the item
    /// is literally a HALF-log (the model is a half-cylinder), so this is a structural constant, not a config knob. It
    /// also divides the whole-log firewood (a half-log finishes into <see cref="ImmersiveWoodworkingConfig.FirewoodPerLog"/>
    /// / this), so two half-logs reconstitute exactly a whole log's firewood. The axe WEAR divides by
    /// <see cref="ChopChainSteps"/> instead, one step more.</summary>
    const int HalfLogsPerLog = 2;

    /// <summary>How many CHARGEABLE steps a whole log takes on the axe chain: the lengthwise split, plus one chop for
    /// each of the <see cref="HalfLogsPerLog"/> halves. The whole-log durability target
    /// (<see cref="ImmersiveWoodworkingConfig.AxeDurabilityPerLog"/>) is divided by THIS to price one half
    /// (<see cref="PerHalfLogWear"/>), leaving the split the remainder (<see cref="SplitWearCost"/>) — which is then
    /// never free and never cheaper than a half, at any target the config allows (AuditFixPlan T03.01). Dividing by the
    /// halves alone is what used to make the split free on the shipped default. Also the floor
    /// <see cref="ImmersiveWoodworkingConfig.Sanitize"/> clamps a non-zero target up to, for the same reason.</summary>
    internal const int ChopChainSteps = HalfLogsPerLog + 1;

    /// <summary>
    /// Resolve the chop product for one input: firewood → sticks, half-log → firewood, any log → firewood,
    /// with each total clamped to <c>dropCount × the product's max stack</c> (a runtime ceiling — the product item isn't
    /// loaded at config time). The <c>game:</c> product codes are hardcoded on purpose — the mod-independence rule
    /// "the chop product is vanilla on purpose" (<c>source-conventions.md</c>): the species drives only the stand
    /// paint, never the product. Its ONE documented exception is <see cref="ResolveFirewood"/>, which prefers a
    /// species' own <c>{wood}firewood</c> where the game ships one (vanilla's <c>agedfirewood</c>) and falls back to
    /// the hardcoded <c>game:firewood</c> everywhere else.
    ///
    /// This is the AUTOMATED-CHOPPER / shared resolver: it never produces half-logs. The manual stand's log→half-log
    /// split is the ONE divergence and lives in <see cref="GetHandChopResult"/> (E04/T04.02, user decision 2026-07-07 —
    /// the machine stays a single-pass log→firewood). Everything else — half-log→firewood, firewood→sticks,
    /// and the non-splitting log kinds (sections, a laid chopping block) → firewood — is identical on both paths, so it
    /// lives here and both call it.
    ///
    /// <c>internal static</c> + world-parametrised (E06/T06.02): the automated chopper has no <see cref="BlockEntityChoppingBlock"/>
    /// instance, so it reuses THIS resolver through its single output seam <c>GetChopBatch</c> — one source of truth for
    /// "what a chop yields", shared by the hand stand (<see cref="Chop"/>) and the machine. The <paramref name="world"/> is
    /// passed instead of the old <c>Api.World</c> so the static method carries no instance state; the log/firewood
    /// classifiers it calls are pure static (no world), so they stay private to this class.
    /// </summary>
    internal static (ItemStack template, int dropCount)? GetChopResult(IWorldAccessor world, ItemStack? input, ImmersiveWoodworkingConfig config, DropAmounts a)
    {
        if (input == null) return null;

        // Half-log -> firewood (E04/T04.02): the split billet finishes into its share of a whole log's firewood
        // (FirewoodPerLog / HalfLogsPerLog, default 4) over a matching share of the piles, so two half-logs yield
        // exactly what a whole log would. SHARED — this is both what a hand-laid half-log finishes into AND what the
        // automated chopper does if a half-log is fed to it; only the log->half-log split itself is manual-only.
        // ACCEPTED EDGE: the /HalfLogsPerLog is integer division, so an ODD FirewoodPerLog (a non-default config —
        // Sanitize only floors it to ≥1, the shipped 8 is even) drops the remainder: two half-logs then total
        // FirewoodPerLog-1, i.e. the hand chain yields 1 less than the machine's whole-log pass. Left as-is on purpose
        // (matches the plan's own "FirewoodPerLog/2" floor math): a per-item "extra firewood" attribute or forcing the
        // config even would be more surprising than a ≤1-firewood loss that only a hand-picked odd config ever sees.
        if (IsHalfLog(input))
            return MakeFirewood(world, ChopInputSpecies(input), a.FirewoodPerLog / HalfLogsPerLog,
                                System.Math.Max(1, a.FirewoodDropCount / HalfLogsPerLog));

        // Logs (incl. sections, debarked, chopping blocks) -> firewood. This is the machine's whole-log output AND the
        // hand stand's output for the log kinds that DON'T split into half-logs (sections, the mod's own block); a
        // hand-laid FULL log instead splits into half-logs first (GetHandChopResult).
        if (IsLog(input)) return MakeFirewood(world, ChopInputSpecies(input), a.FirewoodPerLog, a.FirewoodDropCount);

        // Firewood -> sticks. Gated by AllowFirewoodToSticks (default on): with it off, firewood yields NO product here
        // (falls through to null), so a completed strike drops + empties nothing — the belt-and-braces to the input gate
        // (IsChoppable), covering the edge where a firewood was laid BEFORE the flag was turned off. Both chopping
        // stations share this resolver, so the machine's GetChopBatch returns null for firewood too (its CanChop then
        // fails via IsValidInput, so it never strikes a firewood it can't process).
        if (config.AllowFirewoodToSticks && IsFirewood(input)) return MakeSticks(world, a);

        return null;
    }

    /// <summary>Builds a firewood chop result of <paramref name="total"/> items across <paramref name="dropCount"/>
    /// piles, clamped to <c>dropCount × the item's runtime max stack</c> — the natural ceiling (no pile can exceed its
    /// max stack across its drops), the same formula the sawhorse uses in GetSawProduct. Done here, not in
    /// Config.Sanitize, because the firewood item isn't loaded yet at config time. The log→firewood body lifted out so
    /// the whole-log (<see cref="ImmersiveWoodworkingConfig.FirewoodPerLog"/>) and half-log (its
    /// <see cref="HalfLogsPerLog"/>'th share) branches share ONE clamp.</summary>
    static (ItemStack template, int dropCount)? MakeFirewood(IWorldAccessor world, (string? wood, string domain) species, int total, int dropCount)
    {
        Item? firewood = ResolveFirewood(world, species);
        if (firewood == null) return null;
        int amount = GameMath.Clamp(total, 1, dropCount * firewood.MaxStackSize);
        return (new ItemStack(firewood, amount), dropCount);
    }

    /// <summary>
    /// The firewood item this species chops into: its OWN <c>{wood}firewood</c> where such an item exists (the log's
    /// domain first, then <c>game:</c>), else plain <c>game:firewood</c>.
    ///
    /// <para>This is the ONE documented exception to "the chop product is vanilla on purpose"
    /// (<c>source-conventions.md</c> §2), and it exists because vanilla itself makes it: its grid recipe turns
    /// <c>log-placed-aged-ud</c> into <c>agedfirewood</c> and every other log into <c>firewood</c>. With the mod's
    /// stations replacing that recipe, chopping an aged log gave ORDINARY firewood while the grid gave the aged kind —
    /// one wood with two doors and two products (AuditFixPlan T04.05, F-PT-02). The rule is otherwise intact: the
    /// product is still vanilla and still not looked up per species — it is looked up per species only where the game
    /// (or a wood mod, by the same code shape and with no patch from us) actually ships a separate one, and falls back
    /// to the single hardcoded <c>game:firewood</c> everywhere else. That fallback is what keeps this off the
    /// "a per-species product must resolve, or the input is refused at the door" rule: this product can never fail to
    /// resolve, so no gate is owed one.
    ///
    /// <para>A null/blank species (a log whose code shape <see cref="LogBlockSpecies"/> cannot parse, a bare half-log,
    /// a laid chopping block) goes straight to plain firewood.
    /// </summary>
    static Item? ResolveFirewood(IWorldAccessor world, (string? wood, string domain) species)
    {
        if (!string.IsNullOrEmpty(species.wood))
        {
            string path = species.wood + FirewoodCode;
            Item? own = world.GetItem(new AssetLocation(species.domain, path));
            if (own != null) return own;
            if (species.domain != "game")
            {
                Item? vanilla = world.GetItem(new AssetLocation("game", path));
                if (vanilla != null) return vanilla;
            }
        }

        return world.GetItem(new AssetLocation("game", FirewoodCode));
    }

    /// <summary>The (species, domain) pair that decides <see cref="ResolveFirewood"/> for one chop input. A LOG carries
    /// both in its code (<see cref="LogBlockSpecies"/>); a HALF-LOG carries them in the stack attributes the split
    /// stamped, which is what keeps the whole log→halves→firewood chain on ONE wood — an aged log makes aged halves,
    /// and those make aged firewood, rather than the chain changing product halfway (E04/T04.05). Read with the same
    /// defaults the rest of the file uses for those two attributes.</summary>
    static (string? wood, string domain) ChopInputSpecies(ItemStack input)
        => IsHalfLog(input)
            ? (input.Attributes?.GetString("wood", null), input.Attributes?.GetString("woodDomain", "game") ?? "game")
            : LogBlockSpecies(input.Block);

    /// <summary>
    /// Add the splitter (xSkills, T03.02) bonus to a firewood <paramref name="stack"/> in place — the chance-based whole
    /// units of <c>base × extra</c> (<see cref="XSkillsYield.ChanceScaled"/>, the same rounding the sawyer yield uses),
    /// re-clamped to the SAME pile-safety ceiling <see cref="MakeFirewood"/> applied (<c>dropCount × maxStack</c>, so no
    /// pile can exceed a max stack). The ceiling is NOT relaxed by the bonus (it is a hard invariant) — but the default 8
    /// firewood across its piles sits far below it, so the bonus is never actually cut. Server-side (called from
    /// <see cref="Chop"/>); a Tier-0 splitter / no xSkills never reaches here (the caller gates on TryGetSplitterYield).
    /// </summary>
    void ApplySplitterYield(ItemStack stack, int dropCount, float extra)
    {
        int bonus = XSkillsYield.ChanceScaled(stack.StackSize, extra, Api.World.Rand);
        if (bonus <= 0) return;
        int ceiling = dropCount * stack.Collectible.MaxStackSize;
        stack.StackSize = GameMath.Clamp(stack.StackSize + bonus, 1, ceiling);
    }

    /// <summary>
    /// The HAND stand's chop product (E04/T04.02) — identical to the automated chopper's <see cref="GetChopResult"/>
    /// EXCEPT that a FULL log (<see cref="IsFullLog"/>: a barked/debarked log, not a section or the mod's own block) is
    /// split LENGTHWISE into <see cref="HalfLogsPerLog"/> half-logs instead of going straight to firewood. That split is
    /// the ONE manual-only divergence the plan calls for (user decision 2026-07-07): the machine stays single-pass
    /// log→firewood, while by hand a log becomes 2 half-logs that are each then chopped into firewood (so a whole log
    /// still yields the same 8 firewood, just over two carryable intermediates). Everything else — half-log→firewood,
    /// firewood→sticks, and the non-splitting log kinds → firewood — resolves through the shared
    /// <see cref="GetChopResult"/>, so the two paths can only ever differ on that first split. Used by <see cref="Chop"/>
    /// (the drop) and <see cref="ChopWearCost"/> (the per-workpiece wear).
    ///
    /// The split is OPT-OUT via <see cref="ImmersiveWoodworkingConfig.HalfLogChopStage"/> (default on). Turned OFF, the
    /// full-log branch is skipped and a hand-laid log resolves through <see cref="GetChopResult"/> too — chopping
    /// straight to firewood in one pass, making the hand stand match the machine (no half-log intermediate at all).
    /// </summary>
    internal static (ItemStack template, int dropCount)? GetHandChopResult(IWorldAccessor world, ItemStack? input, ImmersiveWoodworkingConfig config, DropAmounts a, bool skipHalfLogStage = false)
    {
        // skipHalfLogStage (E06/T06.05): a MAUL forces this ONE chop past the split, so a full log resolves straight to
        // firewood exactly as if HalfLogChopStage were off — the maul's whole point. It's the SAME fall-through the
        // HalfLogChopStage-off case takes, so wear (ChopWearCost, which also routes through here with the flag) is charged
        // off the firewood yield: the whole-log target, not the split's share of it. A plain axe passes false and keeps
        // the split.
        // The half-log split is opt-out (ImmersiveWoodworkingConfig.HalfLogChopStage, default on). With it OFF a
        // hand-laid whole log falls through to GetChopResult and chops STRAIGHT to firewood in one pass, exactly like
        // the automated chopper — the intermediate is gone. Because ChopWearCost also resolves through here, the direct
        // log→firewood chop is charged the whole-log target — which is what the split chain sums to as well, at EVERY
        // reachable target (AuditFixPlan T03.01 made the chain exact), so the two paths never differ on WEAR. The
        // FIREWOOD total still can, by at most 1 and always in the player's favour: the direct chop yields the full
        // FirewoodPerLog while the chain drops the FirewoodPerLog/2 integer-division remainder on an odd config — the
        // accepted edge in GetChopResult. Not equalised on purpose: the direct chop's clean full yield is the better
        // result, not something to degrade to match the chain's rounding.
        if (config.HalfLogChopStage && !skipHalfLogStage && IsFullLog(input)) return MakeHalfLogs(world, input!, config);
        return GetChopResult(world, input, config, a);
    }

    /// <summary>
    /// Builds the log-split result: <see cref="HalfLogsPerLog"/> half-logs (<c>immersivewoodworking:halflog</c>), each
    /// STAMPED with the source log's wood species + domain (<see cref="HalfLogSpecies"/>) so the carried / laid /
    /// rendered half-log shows and finishes as the right wood — the cross-domain species pipeline that lets a modded log
    /// (wildcrafttree, …) produce a correctly-textured half-log with no per-mod patch. A log whose species can't be read
    /// (an odd code shape) still splits, just as a bare (default-oak) half-log. Null only if the half-log item isn't
    /// loaded. Manual path only (via <see cref="GetHandChopResult"/>).
    /// </summary>
    static (ItemStack template, int dropCount)? MakeHalfLogs(IWorldAccessor world, ItemStack log, ImmersiveWoodworkingConfig config)
    {
        Item? halflog = world.GetItem(new AssetLocation("immersivewoodworking", "halflog"));
        if (halflog == null) return null;

        ItemStack template = new ItemStack(halflog, HalfLogsPerLog);
        (string? wood, string domain) = HalfLogSpecies(log);
        if (wood != null)
        {
            template.Attributes.SetString("wood", wood);
            template.Attributes.SetString("woodDomain", domain);
        }

        // The SURFACE state travels with the species (AuditFixPlan T04.01). Splitting a log does not put bark back on
        // it, so a half off a `debarkedlog` is stamped debarked and stays that way through the hand, the stand, the
        // chopper and the wall it is eventually built into. Read off the input BLOCK, not off a config or a tool: the
        // engine's own code shape is the only thing that knows whether this log had been stripped.
        //
        // Stamped INDEPENDENTLY of the species branch above, deliberately. The two facts have different sources, and a
        // log whose code shape LogBlockSpecies cannot parse still tells us plainly which kind of log it is — losing
        // the bark state as collateral damage of an unreadable species would be a second bug hiding behind the first.
        ItemHalfLog.StampDebarked(template, IsDebarkedLogBlock(log.Block));

        return (template, HalfLogsPerLog);
    }

    /// <summary>Read the wood species + its domain off a full-log stack for stamping onto the half-logs it splits into.
    /// A <c>debarkedlog-{wood}-{rot}</c> carries the species at code part 1; every <c>log-{placed|grown|resin}-{wood}-{rot}</c>
    /// carries it at part 2 (the extra kind part shifts it one slot right) — the same offset the sawhorse's
    /// <see cref="BlockEntitySawhorse.GetWoodSpecies"/> uses, widened here to the grown/resin logs the stand also
    /// accepts. The domain is the log block's OWN domain, so a modded species resolves its textures in its mod's domain
    /// (T04.04). Returns null wood on an unexpected code shape — the half-log then falls back to the default look, never
    /// crashing. Only ever called for an <see cref="IsFullLog"/> stack (so the block is non-null).</summary>
    static (string? wood, string domain) HalfLogSpecies(ItemStack log) => LogBlockSpecies(log.Block);

    /// <summary>The species + domain a log BLOCK carries, in the exact form a half-log stack stores them
    /// (<c>wood</c> / <c>woodDomain</c>). Split out of <see cref="HalfLogSpecies"/> and made <c>internal</c> so the mod
    /// system's non-combustible-species scan (<see cref="ImmersiveWoodworkingModSystem.IsNonCombustibleWood"/>) keys its
    /// index with the IDENTICAL rule that stamps the stack. That identity is the point: if a wood mod ever ships a log
    /// code shaped differently (so the part offsets read the wrong slot), the index and the stack still land on the same
    /// key and simply agree — they can never drift into a half-match. Returns a null wood on an unexpected code shape;
    /// the caller then falls back to the default look / default combustion, never crashing.</summary>
    internal static (string? wood, string domain) LogBlockSpecies(Block? block)
    {
        if (block?.Code == null) return (null, "game");
        string domain = block.Code.Domain;
        string? wood = block.FirstCodePart() == "debarkedlog" ? block.FirstCodePart(1) : block.FirstCodePart(2);
        return (wood, domain);
    }

    /// <summary>Builds the "sticks/branches" chop result from a firewood input —
    /// clamped to dropCount * the stick item's max stack (only known at runtime), the same natural-ceiling
    /// clamp the sawhorse uses. <c>internal static</c> + world-parametrised alongside <see cref="GetChopResult"/>
    /// (E06/T06.02) so the automated chopper reuses it without a stand instance.</summary>
    internal static (ItemStack template, int dropCount)? MakeSticks(IWorldAccessor world, DropAmounts a)
    {
        Item? stick = world.GetItem(new AssetLocation("game", "stick"));
        if (stick == null) return null;
        int amount = GameMath.Clamp(a.SticksPerFirewood, 1, a.SticksDropCount * stick.MaxStackSize);
        return (new ItemStack(stick, amount), a.SticksDropCount);
    }

    // Top surface of the choppingblock shape (11/16 = 0.6875): where placed content rests. Used by the log/firewood
    // transform below (PoseLaidContent) to re-seat scaled content onto the stand. Mirrors the
    // COLLISION box y2 in blocktypes/choppingblock.json — the physical top the character stands on (kept in sync by
    // hand there). NOTE the SELECTION box is deliberately taller than this (so the raised content is clickable), so
    // it is NOT mirrored. Lowered 13/16→11/16 with the shorter re-authored stand model (art
    // shapes/choppingblock.json), so content re-seats onto the new, lower top surface.
    const float StandTopY = 0.6875f;

    // Pose for an axe stuck into the top of the stand (UpdateToolMesh / PoseToolOnStand). The vanilla axe item shape
    // lies flat with the handle along +X (grip at low X, head at the +X end) and the blade edge pointing -Z, so a
    // -90° turn about Z stands it upright with the head DOWN into the stand and the grip UP; a tilt about X then leans
    // it over AND decides which side of the head bites in — NEGATIVE tips the head toward -Z so the BLADE edge drives
    // down into the wood (positive buries the blunt poll instead), and ~45° reads as "planted at an angle". The mesh
    // is then seated by its post-rotation bounds (centred on X/Z, lowest point sunk ToolEmbedDepth below the top
    // surface), so it lands correctly regardless of the exact axe model. VISUAL-ONLY tuning constants — no gameplay
    // depends on them; flip ToolStandUpRad's sign if the head ends up pointing up, ToolLeanRad's if the poll bites in
    // instead of the blade. The whole leaning axe then swings about Y toward the player who stuck it (stuckToolYaw); if
    // it ends up facing AWAY, negate the atan2 in TryStickTool.
    static readonly float ToolScale = 0.8f;                      // 0.8 = 20% smaller than native; seating below is by ABSOLUTE bounds, so the embed depth + centring stay put regardless of scale
    static readonly float ToolStandUpRad = -GameMath.PIHALF;     // -90° about Z: head end down, grip up
    static readonly float ToolLeanRad = -45f * GameMath.DEG2RAD; // ~45° tilt about X; NEGATIVE so the BLADE (not the poll) bites down into the stand
    const float ToolEmbedDepth = 0.08f;                          // how far the lowest point sinks below the stand's top surface
    const float ToolHeadBandFraction = 0.25f;                    // seat by the centroid of the lowest 25% (the head), so the driven-in point is centred — not the whole-axe bbox

    /// <summary>
    /// (Re)builds the content mesh for the stored stack and flags the block for re-tesselation, so the stand's
    /// terrain mesh picks it up through OnTesselation. Client-side, main thread (Initialize / FromTreeAttributes).
    /// Delegates the block/item tesselation + pose to the shared <see cref="BuildLaidContentMesh"/> (a stored ITEM is
    /// re-tesselated against the block atlas there, because the chunk terrain mesh cannot sample the item atlas).
    /// </summary>
    void UpdateContentMesh()
    {
        if (capi == null) return; // client-only

        if (!HasContent)
        {
            contentMeshKey = null; // F-S06-09: forget the built identity so re-laid content always rebuilds
            contentMesh = null;
            capi.World.BlockAccessor.MarkBlockDirty(Pos);
            return;
        }

        ItemStack stack = inventory[0].Itemstack!; // guarded by HasContent above

        // F-S06-09: skip the whole re-tesselation when the laid mesh would be identical. The identity is a SUPERSET of
        // every input the builder reads, so an unchanged one guarantees an unchanged mesh, never a stale one — which is
        // what stops a per-strike sync that stays inside one stage from re-tesselating the shape to itself. Identity AND
        // builder are the shared seam (T04.07), so this stand and the automated chopper cannot drift apart.
        string key = LaidWorkpieceIdentity(stack, chopProgress, contentYaw);
        if (key == contentMeshKey) return; // unchanged → keep the current mesh, skip the chunk re-tesselation
        contentMeshKey = key;

        // The one dispatcher for "what does a laid workpiece look like" — staged log / placed half-log / plain laid
        // content — shared with the automated chopper's bed. contentMesh is the single source of truth read by
        // OnTesselation.
        contentMesh = BuildLaidWorkpieceMesh(capi, stack, chopProgress, contentYaw);
        capi.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    /// <summary>
    /// Render-cache identity for a laid workpiece — a SUPERSET of every input <see cref="BuildLaidWorkpieceMesh"/>
    /// reads, so an unchanged identity guarantees an unchanged mesh, never a stale one (F-S06-09). Shared by the hand
    /// stand and the automated chopper for the same reason the builder is (T04.07): a key that drifted from the builder
    /// would hand one of them the wrong wood or the wrong stage — and the chopper's half of it keys an
    /// <c>ObjectCacheUtil</c> entry that lives for the whole PROCESS, where a wrong hit sticks for the session.
    ///
    /// Per branch: a full log's species rides in its block CODE, so the code + the stage bucket + the yaw are its whole
    /// identity. A half-log adds its stored species AND its bark state (T04.01) — the two skins share a code and a
    /// species and differ only in what they are painted with. The plain laid path reads neither bucket nor yaw, so both
    /// are omitted there, and adds the one other attribute-borne species there is, a laid chopping block (T04.06);
    /// every other input on that path carries its species in the code (or has none), for which the two attribute reads
    /// come back empty and the identity is the old code-only string. Over-keying would only cost a harmless extra
    /// rebuild, never a wrong mesh; UNDER-keying is the bug this guards.
    ///
    /// <paramref name="yaw"/> is the caller's frame: the hand stand passes its <see cref="contentYaw"/> (the workpiece
    /// turns to the player who laid / last chopped it), the automated chopper passes 0 and bakes its machine facing in
    /// afterwards.
    /// </summary>
    internal static string LaidWorkpieceIdentity(ItemStack stack, float progress, float yaw) =>
        IsFullLog(stack) ? "full|" + stack.Collectible?.Code + "|" + LogStageBucket(progress) + "|" + yaw
        : IsHalfLog(stack) ? "half|" + stack.Collectible?.Code + "|" + stack.Attributes.GetString("wood", "") + "|" + stack.Attributes.GetString("woodDomain", "") + "|" + (ItemHalfLog.IsDebarked(stack) ? "bare" : "bark") + "|" + HalfLogStageBucket(progress) + "|" + yaw
        : "laid|" + stack.Collectible?.Code + "|" + stack.Attributes.GetString("wood", "") + "|" + stack.Attributes.GetString("woodDomain", "");

    /// <summary>
    /// The SINGLE source of truth for "what mesh does a workpiece laid on a chopping block get" (T04.07): the hand
    /// stand (<see cref="UpdateContentMesh"/>) and the automated chopper's bed
    /// (<c>BlockEntityChopper.BuildWorkpieceMesh</c>) both come through here, so ONE item can no longer have two looks.
    /// Before this seam the chopper always took the plain laid path below, which scales an ITEM mesh to 63.75 % and
    /// seats it by a rule that assumes the mesh fills [0..1]: the half-log shape starts 2.43 px up, so its base landed
    /// 1.55 px ABOVE the stand top and the billet floated there at two thirds of the size the hand stand drew (F-L9-05).
    ///
    /// E05: a laid FULL log (barked or debarked) shows its chop STAGE through a custom per-bucket shape (whole →
    /// part-hewn → deeply-hewn, <see cref="LogStageBucket"/>), and a laid HALF-LOG a 2-stage split (fresh → part-hewn,
    /// <see cref="HalfLogStageBucket"/>), each textured for its own species — so chopping either over several strikes
    /// visibly splits it apart before it drops at 100 %. Everything else (log sections, a laid chopping block,
    /// firewood) takes the plain laid path. A null staged mesh (missing shape or unreadable species) falls back to that
    /// same path, so a workpiece is never invisible.
    ///
    /// The stage shapes are authored in block space ALREADY sitting on the stand top, so they are not posed — and they
    /// seat on the chopper's bed exactly as on the hand stand, because the chopper draws that bed from the stand's own
    /// geometry at its own block origin, unshifted. <paramref name="yaw"/> turns the staged shapes about the block
    /// centre; the plain laid path ignores it, so a caller that needs that one turned rotates the result itself.
    /// Client-side, MAIN thread (capi.Tesselator); the caller owns caching.
    /// </summary>
    internal static MeshData? BuildLaidWorkpieceMesh(ICoreClientAPI capi, ItemStack stack, float progress, float yaw) =>
        IsFullLog(stack) ? BuildStagedLogMesh(capi, stack, LogStageBucket(progress), yaw) ?? BuildLaidContentMesh(capi, stack)
        : IsHalfLog(stack) ? BuildPlacedHalfLogMesh(capi, stack, HalfLogStageBucket(progress), yaw) ?? BuildLaidContentMesh(capi, stack)
        : BuildLaidContentMesh(capi, stack);

    /// <summary>
    /// CLIENT-only prediction (F-S06-11 follow-up): at a non-completing strike, advance the laid workpiece's chop-STAGE
    /// visual to the local <see cref="ChopHold.Progress"/> so the staged model (full log: whole → part-hewn → deeply-hewn
    /// by <see cref="ChopVisualBucket"/>; half-log: fresh → part-hewn by <see cref="HalfLogVisualBucket"/>) swaps in
    /// lockstep with the swing + sound, instead of trailing the server's <see cref="chopProgress"/> round-trip. The
    /// value is client-predicted; the next <see cref="FromTreeAttributes"/> sync reconciles it — deterministic, so it's
    /// the SAME value (server and client advance the bar identically, F-S06-01), and re-tesselating to the same bucket is
    /// visually a no-op. Re-tesselates ONLY when the bucket actually crosses a threshold, so mid-bucket strikes don't churn
    /// the mesh. This note used to excuse the sibling cost as "the per-strike sync re-tesselation is pre-existing,
    /// F-S06-09", and that named the wrong mechanism (AuditFixPlan T05.04 / F-L10-10, conflict К3): F-S06-09 gated the
    /// MESH REBUILD behind <see cref="contentMeshKey"/> and never touched the engine's SECTION re-tesselation, which the
    /// per-strike <c>MarkDirty(true)</c> kept ordering on every observing client regardless of that key. Both halves of
    /// that are gone now — the strike sync is data-only and <see cref="UpdateToolMesh"/> has a key of its own — so a
    /// mid-bucket strike orders no re-tesselation at all, from here or from the sync. Advancing <see cref="chopProgress"/> here
    /// also keeps the <c>GetBlockInfo</c> "% chopped" readout in step with the impact.
    /// </summary>
    void PredictChopStageVisual()
    {
        if (capi == null) return; // client-only (this runs from ChopStrike's client branch)
        ChopHold? hold = chopHold;
        if (hold == null) return; // no hold, nothing predicted (ChopStrike only reaches here from inside one)
        ItemStack? stack = inventory[0].Itemstack;
        if (stack == null) return;

        // Only advance FORWARD on the SAME displayed log. Right after a completed strike the hold's progress resets to 0 for
        // the next log, but the emptied/restocked content is still sync-driven — the rendered stack may momentarily be the
        // OUTGOING log until FromTreeAttributes arrives. Predicting then would flash a whole-log stage on stale content, so
        // bail; the sync repaints the fresh log and prediction resumes from there.
        if (hold.Progress < chopProgress) return;

        int beforeBucket = IsFullLog(stack) ? ChopVisualBucket : IsHalfLog(stack) ? HalfLogVisualBucket : -1;
        chopProgress = hold.Progress; // client-predicted; reconciled by the next FromTreeAttributes to the same value
        int afterBucket = IsFullLog(stack) ? ChopVisualBucket : IsHalfLog(stack) ? HalfLogVisualBucket : -1;

        if (afterBucket != beforeBucket) UpdateContentMesh(); // a staged workpiece crossed a threshold — repaint at the impact
    }

    /// <summary>
    /// Pose a freshly-tesselated laid-content mesh onto the stand's top surface — the SINGLE source of truth for "how a
    /// laid workpiece sits on the chopping block", so the hand stand (<see cref="UpdateContentMesh"/>) AND the automated
    /// chopper's bed (<see cref="BlockEntityChopper"/> render) show an IDENTICAL workpiece (user-reported 2026-07-05: the
    /// chopper must render the fed log like the hand stand, not with the sawmill's standalone log shape). Logs/firewood
    /// are scaled, centred and re-seated onto <see cref="StandTopY"/> (firewood stood upright first). The SELECTION box
    /// is intentionally taller than the stand (see blocktypes/choppingblock.json) so the raised content stays clickable;
    /// the COLLISION box still ends at <see cref="StandTopY"/>. Mutates <paramref name="mesh"/> in place.
    /// </summary>
    internal static void PoseLaidContent(MeshData mesh, ItemStack stack)
    {
        // Logs/firewood: scaled, centred, resting ON the stand's top surface. Firewood lies flat by default, so stand it
        // upright first. Firewood is enlarged ~+44% (two +20% bumps) and logs +50% over the former half-block size; the Y
        // translate then re-seats whatever scale's base onto StandTopY (0.6875, 11/16) so the (now larger) content rests
        // on the stand instead of floating above it or sinking in.
        float scale;
        if (IsFirewood(stack))
        {
            mesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), GameMath.PIHALF, 0f, 0f);
            scale = 0.72f; // firewood: two +20% bumps over the former 0.5 (0.5→0.6→0.72; ×1.44)
        }
        else
        {
            scale = 0.6375f; // logs: +50% then −15% over the former 0.5 (0.5→0.75→0.6375; ×1.275)
        }
        mesh.Scale(new Vec3f(0.5f, 0.5f, 0.5f), scale, scale, scale);
        // A [0..1] mesh scaled about its centre (0.5) lands its base at 0.5·(1−scale); lift that to the top.
        float scaledBaseY = 0.5f * (1f - scale);
        mesh.Translate(0f, StandTopY - scaledBaseY, 0f); // base rests at StandTopY
    }

    /// <summary>
    /// Tesselate a laid CHOPPABLE stack (a log / section / debarked BLOCK, or a firewood ITEM) into a mesh
    /// posed onto the stand top — the SINGLE source of truth for "turn a laid workpiece into a mesh", so the hand stand
    /// (<see cref="UpdateContentMesh"/>) AND the automated chopper's bed render an IDENTICAL workpiece for EVERY accepted
    /// input, not just blocks (user-reported 2026-07-05: firewood laid on the chopper showed nothing — the chopper only
    /// tesselated blocks). A BLOCK tesselates straight into the block atlas; an ITEM is re-tesselated into the BLOCK atlas
    /// via <see cref="LaidContentItemTexSource"/> (the chunk terrain mesh — and, for consistency, the chopper's custom
    /// renderer — sample the block atlas, never the item atlas) with its render passes flattened to <c>BlendNoCull</c>
    /// like vanilla display blocks. The mesh is then seated by <see cref="PoseLaidContent"/> (firewood stood upright).
    /// Returns null for an empty / neither-block-nor-item stack. Client-only (needs the
    /// tesselator); the caller owns caching and any facing rotation.
    /// </summary>
    internal static MeshData? BuildLaidContentMesh(ICoreClientAPI capi, ItemStack stack)
    {
        MeshData mesh;
        if (stack.Block is BlockChoppingBlock && StackSpecies(stack) is { } cb
            && BuildStandMesh(capi, cb.wood, cb.domain) is MeshData cbMesh)
        {
            // T04.06: a laid / machine-fed CHOPPING BLOCK shows its OWN species. Like the half-log below (and unlike
            // every other choppable), its species rides as a stack ATTRIBUTE, not in the block code — so the plain
            // TesselateBlock path underneath would paint every wood with the block's JSON default (oak), on the hand
            // stand AND on the chopper's bed, while the very same stand in the world and in your hand is repainted.
            // Reuses BuildStandMesh, the single source of truth for "choppingblock + species", so all four surfaces
            // agree by construction. A bare (species-less) stand or a missing shape falls through to the default mesh.
            mesh = cbMesh;
        }
        else if (stack.Block != null)
        {
            capi.Tesselator.TesselateBlock(stack.Block, out mesh);
        }
        else if (stack.Item is ItemHalfLog && stack.Attributes.GetString("wood", null) is string hlWood
                 && ItemHalfLog.BuildHalfLogMesh(capi, hlWood, stack.Attributes.GetString("woodDomain", "game"),
                        ItemHalfLog.IsDebarked(stack)) is MeshData hlMesh)
        {
            // T04.03: a laid HALF-LOG shows its STORED species — the same LogTexSource path as the held/GUI render —
            // instead of the item's default-oak textures a plain TesselateItem would read (LaidContentItemTexSource
            // only sees item.Textures, not the stack's `wood` attribute). Solid wood, so it keeps LogTexSource's opaque
            // pass (like the stand / staged-log meshes), not the BlendNoCull the generic item path below uses. A bare
            // (species-less) half-log or a missing shape falls through to that generic path.
            mesh = hlMesh;
        }
        else if (stack.Item != null)
        {
            // Re-tesselate the item against the BLOCK atlas via a self-contained tex source; BlendNoCull matches how
            // vanilla display blocks add item meshes to the terrain pool.
            capi.Tesselator.TesselateItem(stack.Item, out mesh, new LaidContentItemTexSource(capi, stack.Item));
            for (int i = 0; i < mesh.RenderPassCount; i++) mesh.RenderPassesAndExtraBits[i] = (short)EnumChunkRenderPass.BlendNoCull;
        }
        else
        {
            return null;
        }

        PoseLaidContent(mesh, stack); // scale/centre/seat onto the stand
        return mesh;
    }

    /// <summary>
    /// E05: build the laid-LOG mesh for the current chop STAGE — the custom "log being hewn on the stand" shape for the
    /// visual <paramref name="bucket"/> (0 whole / 1 part-hewn / 2 deeply-hewn), textured for the log's OWN species via
    /// <see cref="LogTexSource"/> (bark round sides / tree-ring end caps / freshly-hewn inner face), the same
    /// species-bound source the sawhorse and half-log use — so one geometry set per stage serves every wood, vanilla or
    /// modded. Barked vs debarked (<see cref="IsFullLog"/> covers both) picks the side/end textures via
    /// <see cref="LogTexSource.ForLog"/>. Unlike <see cref="BuildLaidContentMesh"/>'s scaled/centred content, the stage
    /// shapes are authored in block space ALREADY sitting on the stand's top surface, so they are NOT run through
    /// <see cref="PoseLaidContent"/>. Returns null if the shape can't be loaded or the species can't be read off the
    /// code (the caller then falls back to the plain laid-log mesh, so a log is never invisible). Client-side, MAIN
    /// thread (capi.Tesselator). Reached only through <see cref="BuildLaidWorkpieceMesh"/>, so BOTH stations stage
    /// their log through it since T04.07 (it used to be the hand stand's alone).
    /// <paramref name="yaw"/> (radians, <see cref="contentYaw"/>) turns the log about the block centre to face the player
    /// who placed / last chopped it (0 = the model's authored front, no code offset — the model carries the base orient).
    /// </summary>
    internal static MeshData? BuildStagedLogMesh(ICoreClientAPI capi, ItemStack logStack, int bucket, float yaw)
    {
        Block? logBlock = logStack.Block;
        if (logBlock == null) return null; // defensive — callers gate on IsFullLog (a block stack)

        Shape? shape = GetStageLogShape(capi, bucket);
        if (shape == null) return null; // shape missing → fall back to the plain laid log

        (string? wood, _) = HalfLogSpecies(logStack); // species off the code; domain resolves inside ForLog
        if (wood == null) return null; // odd code shape → fall back to the plain laid log

        SawInput kind = IsDebarkedLogBlock(logBlock) ? SawInput.DebarkedLog : SawInput.Log;
        capi.Tesselator.TesselateShape("immersivewoodworking:choppingblock-log", shape, out MeshData mesh,
            LogTexSource.ForLog(capi, logBlock, kind, wood));
        // Face the player (contentYaw), about the block centre — the stage shapes are authored in-place, so this is a
        // straight Y-rotation with no re-seat; yaw 0 leaves the model's authored front (no code offset).
        if (yaw != 0f) mesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0f, yaw, 0f);
        return mesh;
    }

    /// <summary>
    /// Build the laid HALF-LOG mesh from the dedicated per-STAGE "half-log resting on the block" shapes (E05/T05.05:
    /// halflog-placed1 fresh / halflog-placed2 part-hewn, picked by <paramref name="bucket"/> = <see cref="HalfLogVisualBucket"/>
    /// at the 50% threshold) — authored in block space already sitting on the stand top, so it shows at full size
    /// instead of the tiny item mesh the generic laid-content path scaled down (user-reported 2026-07-08: the
    /// item-scaled half-log was much smaller than a log cube). Textured for the half-log's OWN stored species (the
    /// <c>wood</c>/<c>woodDomain</c> stack attributes, stamped when the log was split) via the same <see cref="LogTexSource"/>
    /// mapping the held/GUI render uses — bark round sides / tree-ring end caps / debarked inner split face — each
    /// resolved cross-domain by <see cref="WoodTextures"/>. Authored in-place, so NOT run through <see cref="PoseLaidContent"/>;
    /// turned to face the player (<paramref name="yaw"/> = <see cref="contentYaw"/>) about the block centre, exactly like
    /// the full log. Returns null for a bare (species-less) half-log or a missing shape, so the caller falls back to the
    /// item-based laid path. Client-side, MAIN thread (capi.Tesselator). Reached only through
    /// <see cref="BuildLaidWorkpieceMesh"/>, so BOTH stations draw their half-log through it since T04.07 (it used to
    /// be the hand stand's alone, and the chopper shrank the ITEM mesh instead — F-L9-05).
    /// </summary>
    internal static MeshData? BuildPlacedHalfLogMesh(ICoreClientAPI capi, ItemStack stack, int bucket, float yaw)
    {
        string? wood = stack.Attributes.GetString("wood", null);
        if (wood == null) return null; // bare half-log → fall back to the item-based laid mesh
        string woodDomain = stack.Attributes.GetString("woodDomain", "game");

        Shape? shape = GetStageHalfLogShape(capi, bucket); // fresh (0) / part-hewn (1) by HalfLogVisualBucket
        if (shape == null) return null; // shape missing → fall back to the item-based laid mesh

        // Same species→texture mapping AND the same skin branch (T04.01) as the held/GUI half-log render
        // (ItemHalfLog.BuildHalfLogMesh), through the same call: this is the second of the two surfaces a half-log is
        // looked at on, and a stripped half must not grow bark back the moment it is laid on the stand.
        bool debarked = ItemHalfLog.IsDebarked(stack);
        capi.Tesselator.TesselateShape("immersivewoodworking:halflog-placed", shape, out MeshData mesh,
            LogTexSource.ForSpecies(capi, woodDomain, wood, debarked, woodDomain + ":" + wood + (debarked ? " (debarked)" : "")));
        // Face the player (contentYaw), about the block centre — authored in-place, so a straight Y-rotation, same as the
        // full log; yaw 0 leaves the model's authored front.
        if (yaw != 0f) mesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0f, yaw, 0f);
        return mesh;
    }

    /// <summary>
    /// (Re)build the mesh for a stuck axe (<see cref="stuckTool"/>) and flag the block for re-tesselation, so the
    /// stand's terrain mesh picks it up through <see cref="OnTesselation"/>. Client-side, main thread (Initialize /
    /// FromTreeAttributes). The axe is an item, so — exactly like a stored firewood item (<see cref="UpdateContentMesh"/>)
    /// — it is re-tesselated against the BLOCK atlas via <see cref="LaidContentItemTexSource"/>, then posed upright into
    /// the stand by <see cref="PoseToolOnStand"/>. No axe (or, defensively, a non-item stack) → no tool mesh.
    /// Gated by <see cref="toolMeshKey"/> (T05.04), so an unrelated tree sync — a per-strike one above all — neither
    /// re-tesselates the axe nor dirties the block.
    /// </summary>
    void UpdateToolMesh()
    {
        if (capi == null) return; // client-only

        Item? toolItem = stuckTool?.Item;

        // T05.04: skip the whole re-tesselation when the stuck-tool mesh would be identical, exactly as
        // UpdateContentMesh and RebuildStandMesh do for their meshes. The key is compared FIRST — before the no-tool
        // branch — so the common "nothing stuck, and nothing was stuck last sync either" case costs a string compare
        // instead of a MarkBlockDirty, which is what made every per-strike sync re-tesselate the section.
        string? key = toolItem == null ? null : toolItem.Code + "|" + stuckToolYaw;
        if (key == toolMeshKey) return; // unchanged → keep the current mesh (or stay bare), skip the chunk re-tesselation
        toolMeshKey = key;

        if (toolItem == null)
        {
            toolMesh = null;
            capi.World.BlockAccessor.MarkBlockDirty(Pos);
            return;
        }

        // Re-tesselate the axe item against the BLOCK atlas via LaidContentItemTexSource (the chunk terrain mesh only
        // samples the block atlas); BlendNoCull matches how vanilla display blocks add item meshes to the terrain pool.
        capi.Tesselator.TesselateItem(toolItem, out MeshData mesh, new LaidContentItemTexSource(capi, toolItem));
        for (int i = 0; i < mesh.RenderPassCount; i++) mesh.RenderPassesAndExtraBits[i] = (short)EnumChunkRenderPass.BlendNoCull;

        PoseToolOnStand(mesh, stuckToolYaw);

        toolMesh = mesh;
        capi.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    /// <summary>
    /// Pose a freshly-tesselated axe mesh as "stuck into the top of the stand": stand it upright (head down, grip up),
    /// give it a slight lean, then seat it by its post-rotation bounds — centred on X/Z with the head sunk
    /// <see cref="ToolEmbedDepth"/> below the stand's top surface. Bounds-based seating keeps it planted regardless of
    /// the exact axe model. See the pose-constant comment for the axe shape's native orientation and how to tune this.
    /// </summary>
    static void PoseToolOnStand(MeshData mesh, float yaw)
    {
        Vec3f centre = new Vec3f(0.5f, 0.5f, 0.5f);
        if (ToolScale != 1f) mesh.Scale(centre, ToolScale, ToolScale, ToolScale);

        // Explicit rotations (order matters): stand upright, lean ~45° (blade biting down), then swing the whole
        // leaning axe about Y so its handle faces the player who stuck it (yaw 0 = the native -Z lean, unrotated).
        mesh.Rotate(centre, 0f, 0f, ToolStandUpRad); // -90° about Z → head end down, grip up
        mesh.Rotate(centre, ToolLeanRad, 0f, 0f);    // ~45° about X → blade edge biting down
        if (yaw != 0f) mesh.Rotate(centre, 0f, yaw, 0f); // face the player (BlockEntityChoppingBlock.stuckToolYaw)

        int count = mesh.VerticesCount;
        if (count == 0) return;

        // Vertical extent first, so we can isolate the HEAD (the part driven into the wood = the lowest band).
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            float y = mesh.xyz[i * 3 + 1];
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        // Centre on the HEAD's centroid (lowest band), NOT the whole-axe bounding box: a long axe leaning at 45° would
        // drag the bbox centre out toward the handle tip, landing the head off to the side. Averaging just the lowest
        // band puts the driven-in point on the stand centre with the handle rising out of it.
        float band = minY + ToolHeadBandFraction * System.Math.Max(0f, maxY - minY);
        double sumX = 0, sumZ = 0; int n = 0;
        for (int i = 0; i < count; i++)
        {
            if (mesh.xyz[i * 3 + 1] > band) continue;
            sumX += mesh.xyz[i * 3];
            sumZ += mesh.xyz[i * 3 + 2];
            n++;
        }
        if (n == 0) return; // degenerate — the minY vertex always qualifies, so this is defensive only

        float headCx = (float)(sumX / n);
        float headCz = (float)(sumZ / n);
        mesh.Translate(0.5f - headCx, (StandTopY - ToolEmbedDepth) - minY, 0.5f - headCz);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);

        // Wood species (T01.02). Plain strings — no ResolveBlockOrItem needed (unlike the sawhorse's heldLog,
        // which is a real stack): the species is just an identity tag. woodDomain defaults to "game" so a block
        // saved before a domain was recorded still resolves its textures against the base game.
        // Same empty-is-none rule as SetWood (T04.06 / F-L6-28), so a stand saved from a blank-stamped stack loads as
        // bare (default look + generic name) instead of as a species with no name.
        string? loadedWood = tree.GetString("wood", null);
        wood = string.IsNullOrEmpty(loadedWood) ? null : loadedWood;
        string loadedDomain = tree.GetString("woodDomain", "game");
        woodDomain = string.IsNullOrEmpty(loadedDomain) ? "game" : loadedDomain;

        ITreeAttribute? invtree = tree.GetTreeAttribute("inventory");
        if (invtree != null) inventory.FromTreeAttributes(invtree);

        // The stuck axe (Shift+RMB) — a nested stack; tree itemstacks load unresolved, so resolve it (sawhorse heldLog idiom).
        stuckTool = tree.GetItemstack("stuckTool");
        stuckTool?.ResolveBlockOrItem(worldForResolving);
        stuckToolYaw = tree.GetFloat("stuckToolYaw", 0f);

        // Discrete chop progress (E03). Default 0 (unchopped / bare); server-authoritative, so a client that is NOT
        // actively chopping this stand (idle, or watching another player) just reflects the synced value — and so does
        // a half-chopped workpiece resuming its bar after reload.
        //
        // PROGRESS-FLICKER FIX (2026-07-24): while THIS client is actively chopping this stand (a chopHold armed by
        // its OWN BeginChopHold), the displayed bar is owned by the LOCAL deterministic prediction (ChopHold.Progress),
        // which advances in lockstep with the swing the player sees + hears. The per-strike server sync (a data-only
        // MarkDirty inside ChopStrike) echoes that SAME value, but arrives ~1 strike OUT OF PHASE — the two sides tick independently
        // (F-S06-01) — so blindly assigning it here stomps the prediction BACKWARD and makes the % readout (GetBlockInfo)
        // and the split-stage visual (ChopVisualBucket / UpdateContentMesh) flicker (91 → 52 → 91…, the log un-splitting).
        // Reconcile FORWARD-ONLY, mirroring PredictChopStageVisual's own rule: keep the local bar only when it is a genuine
        // forward prediction — the hold's progress > 0 AND not behind the sync — else defer to the sync. The `> 0` deferral is
        // load-bearing for the POST-COMPLETION window: right after a completing strike the hold's progress is reset to 0 while
        // the hold is still armed and the client's inventory still holds the OUTGOING log, so a stale high-progress
        // echo must still paint that outgoing log at its true stage instead of flashing it back to a whole 0% log (the very
        // "whole-log stage on the outgoing log" that PredictChopStageVisual bails to avoid). The `>= synced` clause takes a
        // genuinely-forward sync (server ahead) on the rare occasion it happens. Once the hold ends (chopHold cleared
        // by EndChopHold) the sync resumes as the authority; EndChopHold forces one final server MarkDirty so the resting
        // value settles exactly to the server's. Api?.Side (not bare Api.Side): FromTreeAttributes runs at initial chunk
        // load before Initialize with Api == null, and chopHold is null there anyway → the synced value is used.
        float syncedChopProgress = tree.GetFloat("chopProgress", 0f);
        chopProgress = (Api?.Side == EnumAppSide.Client && chopHold is { } drawnHold && drawnHold.Progress > 0f && drawnHold.Progress >= syncedChopProgress)
            ? drawnHold.Progress
            : syncedChopProgress;

        // Wear already paid for the workpiece on the stand (T03.01). No flicker rule to mirror: the client never reads
        // this, only the server charges wear. The default is the LEGACY SENTINEL -1, not 0, and the difference matters
        // exactly once per world — for a workpiece that was mid-chop when the save predates the tally. 0 would say
        // "nothing paid yet" and re-charge it from scratch; -1 says "unknown", and WearThisStrike then seeds the tally
        // the way the old code derived it, from the progress bar. A workpiece saved WITH the tally at 0 (genuinely
        // nothing paid) also arrives as -1 — ToTreeAttributes writes no key for it, exactly as it writes none for a
        // zero bar — and that is harmless: the seed reads the same 0 back off the bar.
        chopWearCharged = tree.GetInt("chopWearCharged", -1);

        // Which way the laid log faces the chopper (E05). Default 0 = the model's authored front; server-authoritative,
        // so the client just reflects the synced value and re-tesselates the staged log below with it.
        contentYaw = tree.GetFloat("contentYaw", 0f);

        if (Api?.Side == EnumAppSide.Client)
        {
            RebuildStandMesh(); // E02: re-texture the stand for the synced species (the key gate skips if unchanged)
            UpdateContentMesh();
            UpdateToolMesh();
        }
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);

        // Persist the wood species (T01.02). Only write it when known, so a bare block doesn't pin an empty
        // string; woodDomain rides along only with a species (a domain without a species is meaningless).
        if (wood != null)
        {
            tree.SetString("wood", wood);
            tree.SetString("woodDomain", woodDomain);
        }

        // The stuck axe (Shift+RMB), only when present — a stand with nothing stuck writes no key.
        if (stuckTool != null)
        {
            tree.SetItemstack("stuckTool", stuckTool);
            tree.SetFloat("stuckToolYaw", stuckToolYaw);
        }

        // Persist the discrete chop progress only while a workpiece is partway chopped — an unchopped / empty stand
        // writes no key (matches wood / stuckTool); read back with default 0 (E03).
        if (chopProgress > 0f) tree.SetFloat("chopProgress", chopProgress);

        // The wear already paid for that workpiece (T03.01), on the same terms: only a positive tally writes a key. A
        // 0 tally is the reading FromTreeAttributes arrives at on its own (see the sentinel there), so writing it would
        // add a key per stand for nothing.
        if (chopWearCharged > 0) tree.SetInt("chopWearCharged", chopWearCharged);

        // The laid log's facing (E05), only while something is laid and it isn't the default front — an empty / straight
        // stand writes no key (read back with default 0).
        if (HasContent && contentYaw != 0f) tree.SetFloat("contentYaw", contentYaw);

        TreeAttribute invtree = new TreeAttribute();
        inventory.ToTreeAttributes(invtree);
        tree["inventory"] = invtree;
    }

    // ───────────────────────── Collectible mappings (survive an id remap) ──────────────────────────────────────────
    //
    // Two persistent stacks carry a world-specific numeric block/item id: the workpiece log in the 1-slot inventory and
    // the stuck axe (stuckTool, a raw nested stack). Without remapping them, an imported schematic or a world whose id
    // table differs would resolve either to the wrong collectible / null. The inventory slot is iterated like BEQuern;
    // the raw axe mirrors BEHelveHammer.HammerStack (the sawhorse heldLog / sawmill blade-kit idiom). wood/woodDomain
    // are plain identity tags (not stacks), so they need no remap.

    public override void OnStoreCollectibleMappings(Dictionary<int, AssetLocation> blockIdMapping, Dictionary<int, AssetLocation> itemIdMapping)
    {
        base.OnStoreCollectibleMappings(blockIdMapping, itemIdMapping);
        foreach (ItemSlot slot in inventory)
            slot.Itemstack?.Collectible.OnStoreCollectibleMappings(Api.World, slot, blockIdMapping, itemIdMapping);
        stuckTool?.Collectible.OnStoreCollectibleMappings(Api.World, new DummySlot(stuckTool), blockIdMapping, itemIdMapping);
    }

    public override void OnLoadCollectibleMappings(IWorldAccessor worldForResolve, Dictionary<int, AssetLocation> oldBlockIdMapping, Dictionary<int, AssetLocation> oldItemIdMapping, int schematicSeed, bool resolveImports)
    {
        base.OnLoadCollectibleMappings(worldForResolve, oldBlockIdMapping, oldItemIdMapping, schematicSeed, resolveImports);
        foreach (ItemSlot slot in inventory)
            slot.Itemstack?.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve);
        stuckTool?.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve);
    }

    /// <summary>
    /// Read the wood species off the placing stack (T01.03) so a picked / crafted stand keeps its species when set
    /// back down. Mirrors the vanilla "identity in attributes" seam (BEBehaviorShapeFromAttributes.OnBlockPlaced,
    /// which reads its `type` the same way): byItemStack is null on worldgen and on remote (non-placer) clients, so
    /// it's guarded; the species is adopted only when actually present (a bare stack stays the default look); and
    /// MarkDirty(true) on the server pushes the value to clients through the To/FromTreeAttributes sync wired in
    /// T01.02 (which is where the mesh rebuilds for E02). The pick/drop write-back lives on the block
    /// (BlockChoppingBlock.OnPickBlock / GetDrops), so the round-trip closes here.
    ///
    /// E02: SetWood runs on the placer client too, and now that the stand re-textures per species the placer
    /// rebuilds its mesh locally (the `else` branch) so it never flashes the default-oak texture for the one frame
    /// before the server's MarkDirty round-trips back through FromTreeAttributes. Remote clients are always correct
    /// via that sync; RebuildStandMesh is null-safe (no-ops until capi is set) and its key gate makes the later
    /// sync rebuild a no-op when the species is unchanged. Mirrors vanilla BEBehaviorShapeFromAttributes.OnBlockPlaced,
    /// which calls loadMesh() after adopting its type.
    /// </summary>
    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);

        string? placedWood = byItemStack?.Attributes.GetString("wood", null);
        if (placedWood != null)
        {
            SetWood(placedWood, byItemStack!.Attributes.GetString("woodDomain", "game"));
            if (Api?.Side == EnumAppSide.Server) MarkDirty(true); // persist + sync the adopted species to clients
            else RebuildStandMesh();                              // placer client: re-texture now, no one-frame default-oak flicker
        }
    }

    // Spill the laid content + a stuck axe on ANY removal so they aren't lost — in ONE place (E06). This fires for
    // a manual break (Block.SpawnDropsAndRemoveBlock → SetBlock(0)) AND for a FIRE consuming the flammable stand
    // (BEBehaviorBurning.OnFireDeath → SetBlock(0, FuelPos)), which removes the block WITHOUT routing through
    // OnBlockBroken — the old spill lived in OnBlockBroken and so was skipped on a burn, losing the stuck axe and
    // the laid log/half-log (the caveat that used to be documented here). Moving it to OnBlockRemoved covers both
    // with NO double-drop: a manual break calls OnBlockBroken THEN SetBlock(0)→OnBlockRemoved, but only THIS spills
    // now (the OnBlockBroken override is gone; the base still runs behaviors). Chunk unload uses the SEPARATE
    // OnBlockUnloaded callback (content persists via To/FromTreeAttributes; the engine re-tesselates the terrain
    // mesh for free — no GPU resource of ours to dispose, contentMesh is CPU-side MeshData the GC reclaims), and
    // the wood-species retexture swaps the block id via ExchangeBlock (→ OnExchanged, not this) — so neither
    // spuriously drops.
    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved(); // base cleanup (unregister tick listeners / callbacks) — must not be skipped
        if (Api?.Side == EnumAppSide.Server)
        {
            inventory.DropAll(Pos.ToVec3d().Add(0.5, 0.5, 0.5));
            if (stuckTool != null)
            {
                Api.World.SpawnItemEntity(stuckTool, Pos.ToVec3d().Add(0.5, 0.5, 0.5));
                stuckTool = null;
            }
        }
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder sb)
    {
        base.GetBlockInfo(forPlayer, sb);
        if (HasContent) sb.AppendLine(inventory[0].Itemstack?.GetName());

        // Player-facing chop progress as a percent (E03), shown only once a strike has landed so an untouched log reads
        // as just its name (mirrors the sawhorse's "Sawing: {0}%" and the automated chopper's line). Read from the
        // synced chopProgress field: ChopStrike MarkDirty's it on every strike, so this client-side hover read sees it
        // climb. A one-strike (steel) chop completes before any partial value shows — the bar is meaningful for the
        // lesser metals that take several strikes.
        if (HasContent && chopProgress > 0f)
        {
            int percent = GameMath.Clamp((int)(chopProgress * 100f), 0, 100);
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-choppingblock-progress", percent));
        }
#if IW_DEBUG
        // Dev-only: surface the stored species so T01.02's round-trip (place → reload → same value) is verifiable
        // in-world, plus the discrete chop bar + its visual bucket so E03's progress (place → strike → reload → resume)
        // is verifiable too. Stripped from Release; T06.01 audits all redesign diagnostics are gated like this.
        sb.AppendLine($"[IW-DBG] wood={wood ?? "<none>"} domain={woodDomain} chop={chopProgress:0.00} bucket={ChopVisualBucket} halfbucket={HalfLogVisualBucket}");
        // T04.07: the SHARED workpiece render identity — the exact string the mesh cache is keyed on. The automated
        // chopper prints the same field, so "one item, one look" is checkable by hovering both stations: every term but
        // the trailing yaw (this stand turns content to the player, the machine to its facing) must read identical.
        sb.AppendLine($"[IW-DBG] workpiece={(inventory[0].Itemstack is { } dbgStack ? LaidWorkpieceIdentity(dbgStack, chopProgress, contentYaw) : "none")}");
#endif
    }

    /// <summary>
    /// Visual progress bucket (E03/T03.05 → wired in E05): maps a continuous chop progress onto the three staged
    /// laid-LOG shapes (0 = whole, 1 = part-hewn, 2 = deeply-hewn) — the chopping-block counterpart of the sawhorse's
    /// staged log model. Read by <see cref="BuildLaidWorkpieceMesh"/> / <see cref="LaidWorkpieceIdentity"/> to pick the
    /// stage shape (<see cref="BuildStagedLogMesh"/>) for a laid full log, and by the IW_DEBUG block info. STATIC and
    /// progress-parametrised since T04.07, because the automated chopper stages its fed log off its OWN progress
    /// through the same seam — one threshold pair for both stations, not a second copy on the machine.
    /// A re-tesselation follows naturally on every synced strike (<c>FromTreeAttributes</c> → the station's mesh
    /// update), so the stage advances live as the bar climbs. Thresholds 33% / 66% split the bar into the three stages
    /// (E05/T05.02); the log drops at 100% (<see cref="Chop"/>), so bucket 2 covers [66%, 100%). With a one-strike
    /// (steel) hand chop the bar jumps 0→100% and no intermediate stage shows — the stages are meaningful for the
    /// lesser metals that take several strikes, and on the machine for its multi-strike (<c>ChopperStrikes</c>) pass.
    /// </summary>
    internal static int LogStageBucket(float progress) => progress < 0.33f ? 0 : progress < 0.66f ? 1 : 2;

    /// <summary>This stand's own <see cref="LogStageBucket"/> (its <see cref="chopProgress"/>) — for
    /// <see cref="PredictChopStageVisual"/> and the IW_DEBUG readout.</summary>
    int ChopVisualBucket => LogStageBucket(chopProgress);

    /// <summary>
    /// Visual progress bucket for a laid HALF-LOG (E05/T05.05): maps a continuous chop progress onto the two staged
    /// half-log shapes — fresh (0) below 50%, part-hewn (1) at or above — the half-log counterpart of
    /// <see cref="LogStageBucket"/>, and STATIC for the same T04.07 reason. Read by
    /// <see cref="BuildLaidWorkpieceMesh"/> / <see cref="LaidWorkpieceIdentity"/> to pick the
    /// <see cref="BuildPlacedHalfLogMesh"/> stage; a re-tesselation follows naturally on every synced strike
    /// (<c>FromTreeAttributes</c> → the station's mesh update), so the stage advances live as the bar climbs — on the
    /// hand stand and, since T04.07, on the automated chopper too. A single 50% threshold splits the bar into the
    /// two stages; the half-log drops (→ firewood) at 100% (<see cref="Chop"/>), so bucket 1 covers [50%, 100%). With a
    /// one-strike chop the bar jumps 0→100% and the fresh stage never yields to the part-hewn one — the stage is
    /// meaningful for the lesser metals (and lower <see cref="ImmersiveWoodworkingConfig.ChopSpeedMultiplier"/>) that
    /// take several strikes. A half-log chops at <see cref="ImmersiveWoodworkingConfig.HalfLogChopSpeedMultiplier"/>
    /// (×2 by default), so even a SHARP copper axe still needs two strikes (~0.47 × 2 ≈ 0.95) and the part-hewn stage
    /// shows; steel one-shots it, skipping the stage.
    /// </summary>
    internal static int HalfLogStageBucket(float progress) => progress < 0.5f ? 0 : 1;

    /// <summary>This stand's own <see cref="HalfLogStageBucket"/> (its <see cref="chopProgress"/>) — for
    /// <see cref="PredictChopStageVisual"/> and the IW_DEBUG readout.</summary>
    int HalfLogVisualBucket => HalfLogStageBucket(chopProgress);
}
