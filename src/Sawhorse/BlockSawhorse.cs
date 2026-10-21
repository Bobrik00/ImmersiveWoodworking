using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

/// <summary>
/// The sawhorse (one trestle) block — a generic wood block with NO wood variants (it's built from sticks and
/// rope, not cut from a specific log). Two placed orthogonally form the log-ripping multiblock pair (E04); each
/// is an independent block carrying a <see cref="BlockEntitySawhorse"/> that owns the build stage, pair state
/// and (master-side) the held log.
///
/// <see cref="OnBlockInteractStart"/> routes every right-click on a trestle by modifier (T05.02):
///  • plain RMB → lay a held log / debarked log on a finished EMPTY pair (<see cref="BlockEntitySawhorse.TryPutLog"/>),
///    saw when the pair holds a log and an EFFECTIVE saw is in hand (the pit saw always, an ordinary saw only when
///    AllowVanillaSaw is on — otherwise the attempt is refused with a "need a pit saw" nudge and nothing happens)
///    AND the player stands at an axis end
///    (<see cref="BlockEntitySawhorse.TryStartSawing"/>, E06/T06.01), or otherwise drive the stick→stick→rope
///    build (<see cref="BlockEntitySawhorse.TryBuildStep"/>);
///  • Ctrl+RMB → remove the held log: an untouched one goes back to the inventory
///    (<see cref="BlockEntitySawhorse.TryTakeLog"/>), a begun one is scrapped with no drop
///    (<see cref="BlockEntitySawhorse.DiscardLog"/>);
///  • Shift+RMB → vanilla, so a held log places ADJACENT (the natural block-place gesture) and the stick/rope
///    GroundStorable piles keep working.
/// All log gestures address the master (the BE proxies via GetMaster), so either cell behaves identically.
///
/// Build / log gestures are PLAIN or Ctrl right-clicks; Shift stays vanilla. Laying the FIRST trestle from bare
/// ground lives in <see cref="BehaviorSawhorseBuild"/> on the stick; everything on an ALREADY placed trestle is
/// handled here, because the engine consults the block before the held item (HeldPriorityInteract defaults to
/// false). Gestures we don't claim fall through to vanilla so the stick's "stick pile" (Shift, GroundStorable)
/// and the rope's store (Ctrl+Shift) keep working.
/// </summary>
public class BlockSawhorse : Block
{
    // FK rip-saw animation played on the player while a genuine saw hold is in progress (T06.08). Registered as
    // metadata on game:entities/humanoid/player + keyframes on seraph-faceless by patches/sawing_animations.json,
    // and backfilled onto reskin shapes by EntityBehaviorChopAnim. Unlike the chopping block's woodchop (a single
    // impact-timed swing), this LOOPS (onAnimationEnd=Repeat) until the hold ends, then EaseOut's. We drive it
    // manually here — gated on TryStartSawing — rather than via the item's GetHeldTpUseAnimation, because the
    // engine plays a held-use anim on ANY RMB-hold with the item (EntityPlayer.HandleSeraphHandAnimations), which
    // would saw in mid-air; manual start fires only when actually ripping a log at the pair's end.
    const string SawAnim = "sawing";

    // ───────────────────────── Manually-authored collider boxes (pair) ─────────────────────────
    //
    // Per-part collision + selection boxes for the ASSEMBLED PAIR, hand-authored in sawhorse.json
    // (attributes.collisionBoxes / attributes.selectionBoxes, each with a "trestle" and a "log" Cuboidf) and parsed
    // ONCE here at load — on BOTH sides, because collision runs server-side too. One AABB per part; collision and
    // selection are SEPARATE sets so the white outline can differ from the collider. The BE
    // (BlockEntitySawhorse.GetPairBoxes) orients them onto the pair axis, mirrors the trestle onto the partner cell,
    // adds the log box only while a log is laid, and clamps each cell to its own [0,1] footprint. Immutable after
    // load → safe for the off-thread collision/selection reads. A missing/malformed box falls back to the BE's
    // built-in default. A SINGLE / half-built trestle is NOT covered by these — it uses the top-level JSON
    // collisionbox/selectionbox cube (GetPairBoxes returns null for it).
    internal Cuboidf TrestleCollBox { get; private set; } = BlockEntitySawhorse.DefaultTrestleBox;
    internal Cuboidf LogCollBox     { get; private set; } = BlockEntitySawhorse.DefaultLogBox;
    internal Cuboidf TrestleSelBox  { get; private set; } = BlockEntitySawhorse.DefaultTrestleBox;
    internal Cuboidf LogSelBox      { get; private set; } = BlockEntitySawhorse.DefaultLogBox;

    // ───────────────────────────── Interaction help (T07.01) ─────────────────────────────
    //
    // The hint shown when looking at a trestle depends on its BE state, mirroring BlockChoppingBlock: the heavy
    // stack lists (every sawable log, every saw) are resolved ONCE client-side in OnLoaded and cached process-wide
    // via ObjectCacheUtil, then GetPlacedBlockInteractionHelp PICKS the matching set per state. The saw-tool icons
    // are kept split (pit saws vs every other saw — by EnumTool.Saw, so modded saws too) so the saw hint can include
    // the non-pit saws only when the server-synced AllowVanillaSaw flag is on — assembled live in SelectInteractions,
    // since the flag isn't known at load. All gestures follow the live layout — build and saw on plain RMB, remove on Ctrl+RMB; Shift is never
    // used here (it stays vanilla, see OnBlockInteractStart). Lang keys are blockhelp-sawhorse-* (T07.02 fills the
    // handbook / remaining strings).
    //
    // Sets, by state (selected in SelectInteractions):
    //  • Stage1   → add a stick;          Stage2 → add rope                         (half-built trestle)
    //  • Complete, single → (no hint; the player builds a second trestle alongside)
    //  • Complete, empty pair → lay a sawable log / debarked log
    //  • Complete, pair holding a log → saw (a pit saw, or an ordinary saw when allowed, from an END) AND/OR, while the
    //    log still has bark, strip it from a SIDE (the bark spud / a knife; or an axe + offhand hammer on its own
    //    line) — the per-log mode lock (T05.03) hides whichever the started action refuses — + remove: take an
    //    UNTOUCHED log back, or discard a BEGUN cut / strip with no drop.

    WorldInteraction[] addStickInteractions = System.Array.Empty<WorldInteraction>();
    WorldInteraction[] addRopeInteractions  = System.Array.Empty<WorldInteraction>();
    WorldInteraction[] putLogInteractions = System.Array.Empty<WorldInteraction>();

    // The saw hint's tool icons are split into the pit saws and every OTHER saw, so the latter set can be shown or
    // hidden by the AllowVanillaSaw config — which is server-synced and arrives AFTER OnLoaded, so it can't be baked
    // into a fixed array here (a client's local default would mislead). "Other saws" is mod-independent: every
    // collectible the game classes as a saw (EnumTool.Saw) that isn't a pit saw, so a modded saw shows up too. Both
    // lists are resolved once (process-wide cache); the saw WorldInteraction is assembled LIVE in SelectInteractions
    // from pit saws + (config-gated) other saws. take/discard never depend on the tool, so they stay pre-built.
    ItemStack[] pitSawStacks   = System.Array.Empty<ItemStack>();
    ItemStack[] otherSawStacks = System.Array.Empty<ItemStack>();
    WorldInteraction? takeInteraction;
    WorldInteraction? discardInteraction;

    // Debark-tool icon sets for the side-strip hint (T05.01), split by HAND so the hint reads honestly. The
    // SINGLE-hand tools — the bark spud (always) and a knife (only when AllowKnifeDebark) — are assembled live in
    // SelectInteractions like the saws (the flag is server-synced, unknown at load); the tier-2 axe+offhand-hammer
    // path is its own pre-built line (icons = axes, the required hammer stated in the text — a cycling icon list reads
    // as "any one of these", so the offhand hammer can't be an alternative icon). All mod-independent: keyed on
    // EnumTool (axe/knife) or the opt-in barkspud-* code, exactly like the saw sets above.
    ItemStack[] barkSpudStacks = System.Array.Empty<ItemStack>();
    ItemStack[] axeStacks      = System.Array.Empty<ItemStack>();
    ItemStack[] knifeStacks    = System.Array.Empty<ItemStack>();
    WorldInteraction? debarkAxeHammerInteraction;

    // Resolved in OnLoaded (both sides) so SelectInteractions can read the live, server-synced AllowVanillaSaw /
    // AllowKnifeDebark flags.
    ImmersiveWoodworkingModSystem? modSystem;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        // Live config source for the interaction help (AllowVanillaSaw, server-synced after this runs). Resolved on
        // both sides — cheap and harmless — though only the client-side hint reads it.
        modSystem = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();

        // Parse the hand-authored pair collider boxes from attributes. MUST run on BOTH sides (collision is
        // server-side too), so it sits ABOVE the client-only interaction-help block below. Each falls back to the
        // BE default when its attribute is absent/malformed.
        TrestleCollBox = ParseBox("collisionBoxes", "trestle", BlockEntitySawhorse.DefaultTrestleBox);
        LogCollBox     = ParseBox("collisionBoxes", "log",     BlockEntitySawhorse.DefaultLogBox);
        TrestleSelBox  = ParseBox("selectionBoxes", "trestle", BlockEntitySawhorse.DefaultTrestleBox);
        LogSelBox      = ParseBox("selectionBoxes", "log",     BlockEntitySawhorse.DefaultLogBox);

        // Interaction help is a purely client-side hint; nothing on the server needs these lists.
        if (api is not ICoreClientAPI capi) return;

        // The sawable inputs the empty pair accepts (log-placed-* → planks, debarkedlog-* → beams), resolved once and
        // cached process-wide. The shared builder (also used by BlockSawmill) documents the gate (F-S12-02).
        ItemStack[] sawableStacks = BlockEntitySawhorse.GetOrCreateSawableStacks(api, capi);

        // The saw-tool icon sets, split so the hint can show/hide the non-pit saws by config (selected live in
        // SelectInteractions). Pit saws (code pitsaw-*) always rip; every OTHER saw — anything the game classes as a
        // saw (EnumTool.Saw), so vanilla AND modded — only when AllowVanillaSaw is on. Mirrors what the gate treats
        // as effective (IsPitSaw always vs SawIsEffective's flag). Each is resolved once and cached process-wide.
        pitSawStacks   = ObjectCacheUtil.GetOrCreate(api, "iwSawhorsePitSawStacks",   () => ToolStacks(api, capi, o => o.FirstCodePart() == "pitsaw"));
        otherSawStacks = ObjectCacheUtil.GetOrCreate(api, "iwSawhorseOtherSawStacks", () => ToolStacks(api, capi, o => o.Tool == EnumTool.Saw && o.FirstCodePart() != "pitsaw"));

        // The debark-tool icon sets (T05.01), resolved once + cached process-wide like the saws. Bark spud by its
        // opt-in code (barkspud-*, NOT barkspudhead-* — FirstCodePart splits at '-'); axes and knives by EnumTool, so
        // a modded axe/knife qualifies too. The spud+knife line is config-gated LIVE (AllowKnifeDebark) in
        // SelectInteractions; the axes feed the always-on tier-2 axe+hammer line built just below.
        barkSpudStacks = ObjectCacheUtil.GetOrCreate(api, "iwSawhorseBarkSpudStacks", () => ToolStacks(api, capi, o => o.FirstCodePart() == "barkspud"));
        axeStacks      = ObjectCacheUtil.GetOrCreate(api, "iwSawhorseAxeStacks",      () => ToolStacks(api, capi, o => o.Tool == EnumTool.Axe));
        knifeStacks    = ObjectCacheUtil.GetOrCreate(api, "iwSawhorseKnifeStacks",    () => ToolStacks(api, capi, o => o.Tool == EnumTool.Knife));

        // Build hints: a stick (stage 1) / rope (stage 2) in hand. Both are game-domain items that always resolve;
        // the null guard keeps the hint as plain text rather than crashing if a stack ever fails to resolve.
        ItemStack? stick = StackOf(api, "stick");
        ItemStack? rope  = StackOf(api, "rope");
        addStickInteractions = new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-sawhorse-addstick",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = stick != null ? new[] { stick } : null
            }
        };
        addRopeInteractions = new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-sawhorse-addrope",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = rope != null ? new[] { rope } : null
            }
        };

        // Empty pair: lay a sawable log / debarked log.
        putLogInteractions = new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-sawhorse-putlog",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = sawableStacks
            }
        };

        // Pair holding a log: the Ctrl+RMB remove that differs by progress. The SAW hint itself (with its tool
        // icons) is built live in SelectInteractions because its icon set depends on the server-synced config.
        takeInteraction = new WorldInteraction
        {
            ActionLangCode = "immersivewoodworking:blockhelp-sawhorse-take",
            HotKeyCode = "ctrl",
            MouseButton = EnumMouseButton.Right
        };
        discardInteraction = new WorldInteraction
        {
            ActionLangCode = "immersivewoodworking:blockhelp-sawhorse-discard",
            HotKeyCode = "ctrl",
            MouseButton = EnumMouseButton.Right
        };

        // The tier-2 axe+hammer strip — its own side-strip hint line (shown live when the held log still has bark and
        // isn't locked to sawing). Icons are the axes; the required offhand hammer is stated in the action text, since
        // a cycling icon list reads as alternatives (a hammer icon would falsely imply a hammer alone strips). Always
        // available — no config gate (only the tier-1 knife is toggle-gated, in the live single-hand set).
        debarkAxeHammerInteraction = new WorldInteraction
        {
            ActionLangCode = "immersivewoodworking:blockhelp-sawhorse-debark-axehammer",
            MouseButton = EnumMouseButton.Right,
            Itemstacks = axeStacks
        };
    }

    /// <summary>Every collectible matching <paramref name="match"/>, flattened to its handbook stacks for the
    /// saw-tool hint icons. Cheap predicate gate first (so the heavy resolve only runs for candidates), then
    /// <c>GetHandBookStacks</c> wrapped in try/catch so a (modded) tool that throws during the block-load phase can't
    /// crash mod load — the same vanilla idiom <c>BlockChoppingBlock</c> uses for its choppable list.</summary>
    static ItemStack[] ToolStacks(ICoreAPI api, ICoreClientAPI capi, System.Func<CollectibleObject, bool> match)
    {
        List<ItemStack> list = new List<ItemStack>();
        foreach (CollectibleObject obj in api.World.Collectibles)
        {
            if (obj?.Code == null || !match(obj)) continue;
            List<ItemStack>? stacks;
            try { stacks = obj.GetHandBookStacks(capi); }
            catch { continue; } // a tool override from another mod shouldn't be able to crash load
            if (stacks != null) list.AddRange(stacks);
        }
        return list.ToArray();
    }

    /// <summary>One representative stack of a game-domain item (e.g. <c>game:stick</c>), or null if it can't be
    /// resolved. Used for the build-hint icons.</summary>
    static ItemStack? StackOf(ICoreAPI api, string code)
    {
        Item? item = api.World.GetItem(new AssetLocation("game", code));
        return item != null ? new ItemStack(item) : null;
    }

    /// <summary>Read one hand-authored collider box from <c>attributes.<paramref name="group"/>.<paramref name="key"/></c>
    /// (e.g. collisionBoxes.trestle) as a <see cref="Cuboidf"/>, returning <paramref name="fallback"/> when the
    /// attribute is absent or doesn't deserialize. Navigates one level at a time so a missing group/key can't NRE on a
    /// null token.</summary>
    Cuboidf ParseBox(string group, string key, Cuboidf fallback)
    {
        if (Attributes == null) return fallback;
        JsonObject groupNode = Attributes[group];
        if (groupNode == null || !groupNode.Exists) return fallback;
        JsonObject node = groupNode[key];
        if (node == null || !node.Exists) return fallback;
        return node.AsObject<Cuboidf>(null) ?? fallback;
    }

    /// <summary>Pick the interaction-help set by the looked-at trestle's state and append the block's own (base)
    /// help, mirroring <c>BlockChoppingBlock.GetPlacedBlockInteractionHelp</c>. Client-side; reads the
    /// server-synced BE state.</summary>
    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        WorldInteraction[] own = SelectInteractions(world, selection);
        return own.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
    }

    /// <summary>
    /// The interaction-help set matching the trestle's current state:
    ///  • half-built (<see cref="SawhorseStage.Stage1"/> / <see cref="SawhorseStage.Stage2"/>) → add stick / rope;
    ///  • finished but single (not yet paired) → "build a second trestle alongside" (informational);
    ///  • finished empty pair → lay a sawable log;
    ///  • finished pair holding a log → saw (end) and/or strip bark (side, while still barked) + take (still within the
    ///    recovery grace) / discard (past the grace); the per-log mode lock hides whichever action the started one refuses.
    /// Reads the BE state through the master (<see cref="BlockEntitySawhorse.HasLog"/> /
    /// <see cref="BlockEntitySawhorse.LogRecoverable"/>) so either cell of a pair answers the same. Empty array when
    /// there's no sawhorse BE.
    /// </summary>
    WorldInteraction[] SelectInteractions(IWorldAccessor world, BlockSelection selection)
    {
        if (selection == null || world.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntitySawhorse be)
        {
            return System.Array.Empty<WorldInteraction>();
        }

        switch (be.Stage)
        {
            case SawhorseStage.Stage1: return addStickInteractions; // one stick laid → add the second
            case SawhorseStage.Stage2: return addRopeInteractions;  // two sticks → finish with rope
        }

        // Complete:
        if (!be.InPair) return System.Array.Empty<WorldInteraction>(); // single → no hint; player builds an orthogonal partner
        if (!be.HasLog) return putLogInteractions;               // empty pair → lay a log

        // Pair holding a log: the rip (an end, a saw) and/or the side strip (a side, a debark tool), plus the
        // progress-dependent Ctrl+RMB remove. WHICH of the two work-hints show follows the per-log MODE LOCK
        // (T05.03, already enforced in CanSawHere/CanDebarkHere): a log is sawn OR debarked, decided by whichever
        // BEGAN first, so the hint mirrors the gate and never points at a refused action —
        //  • a begun strip (BarkStarted) refuses + hides the rip;
        //  • a begun rip (SawStarted) refuses + hides the strip;
        //  • a still-barked log begun by NEITHER offers both; an already-debarked log (no bark) offers only the rip.
        // (Hints don't poll — vs-interaction-help-no-poll — so this is the state seen on (re)hover, not live mid-hold.)
        List<WorldInteraction> hints = new List<WorldInteraction>(4);

        // A begun strip refuses the saw → don't point at it; nor at a wood whose plank AND beam both do not exist, which
        // the rip gate now refuses too (T03.03). Mode-agnostic here (CanProduceAnySaw): the help OFFERS the rip when
        // EITHER product is possible, and the per-mode refusal is the nudge's job (SawModePlan). The strip hint below
        // has carried its own such clause from the start — this is the rip reading the same way.
        if (!be.BarkStarted && be.CanProduceAnySaw())
        {
            hints.Add(new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-sawhorse-saw",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = SawToolStacks() // pit saws always + other saws when AllowVanillaSaw, chosen live by config
            });
        }

        if (be.HasBark && !be.SawStarted && be.DebarkProductResolves()) // strip only a still-barked log that isn't locked to sawing AND can actually become a debarkedlog (never offer a strip that can't finish)
        {
            hints.Add(new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-sawhorse-debark",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = DebarkSingleHandStacks() // bark spud always + knife when AllowKnifeDebark, chosen live
            });
            if (debarkAxeHammerInteraction != null) hints.Add(debarkAxeHammerInteraction); // tier-2 axe + offhand hammer
        }

        hints.Add((be.LogRecoverable ? takeInteraction : discardInteraction)!); // take a still-recoverable log (within the grace) / discard one past the grace
        return hints.ToArray();
    }

    /// <summary>The tool icons for the saw hint — the saws that actually RIP here: pit saws always, plus every
    /// other saw when <see cref="ImmersiveWoodworkingConfig.AllowVanillaSaw"/> is on. Read LIVE (not baked at load)
    /// because the flags are server-synced and only authoritative after the join packet — so the hint matches what
    /// actually advances the cut (<see cref="BlockEntitySawhorse.SawIsEffective"/>), even on a server that disabled
    /// non-pit saws. (When disabled an ordinary saw can't rip here at all — refused with a nudge, no motion — so it
    /// is correctly absent from the hint, which points only to the tool that works.) When
    /// <see cref="ImmersiveWoodworkingConfig.RemovePitSaw"/> is on the pit saw is removed, so the hint shows ONLY
    /// the ordinary saws (mirroring the gate's inverted branch). Falls back to just the pit saws if the mod system
    /// somehow didn't resolve.</summary>
    ItemStack[] SawToolStacks()
    {
        // RemovePitSaw removes the pit saw: only ordinary saws rip, so the hint shows just those (never the pit saw,
        // which no longer works). Mirrors SawIsEffective's RemovePitSaw branch.
        if (modSystem != null && modSystem.Config.RemovePitSaw) return otherSawStacks;

        if (modSystem == null || !modSystem.Config.AllowVanillaSaw || otherSawStacks.Length == 0)
        {
            return pitSawStacks;
        }
        if (pitSawStacks.Length == 0) return otherSawStacks;

        ItemStack[] both = new ItemStack[pitSawStacks.Length + otherSawStacks.Length];
        pitSawStacks.CopyTo(both, 0);
        otherSawStacks.CopyTo(both, pitSawStacks.Length);
        return both;
    }

    /// <summary>The icons for the single-hand side-strip hint — the debark tools used ALONE in the main hand: the bark
    /// spud always, plus every knife when <see cref="ImmersiveWoodworkingConfig.AllowKnifeDebark"/> is on. Read LIVE
    /// (not baked at load) because the flag is server-synced and only authoritative after the join packet — so the
    /// hint matches what actually strips here (<see cref="BlockEntitySawhorse.BarkToolIsEffective"/>), hiding the knife
    /// on a server that disabled it. The tier-2 axe+hammer path is a SEPARATE line
    /// (<see cref="debarkAxeHammerInteraction"/>): it needs two hands, and a cycling icon list can't say "both". Mirrors
    /// <see cref="SawToolStacks"/> — conservative fallback to just the spud when the flag is off / unresolved or the
    /// knife set is empty.</summary>
    ItemStack[] DebarkSingleHandStacks()
    {
        if (modSystem == null || !modSystem.Config.AllowKnifeDebark || knifeStacks.Length == 0) return barkSpudStacks;
        if (barkSpudStacks.Length == 0) return knifeStacks;

        ItemStack[] both = new ItemStack[barkSpudStacks.Length + knifeStacks.Length];
        barkSpudStacks.CopyTo(both, 0);
        knifeStacks.CopyTo(both, barkSpudStacks.Length);
        return both;
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntitySawhorse be)
        {
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        // A NEW press clears any standing refusal (AuditFixPlan T06.04): a "that end is taken" packet silences exactly
        // the hold it was sent for, and every gesture on this pair ends up here, so no stale gag can outlive one press.
        // Client-side state, so the server neither sets nor clears it.
        if (world.Side == EnumAppSide.Client) be.ClearHoldRefusal();

        var controls = byPlayer.Entity.Controls;
        ItemSlot activeSlot = byPlayer.InventoryManager.ActiveHotbarSlot;

        // Shift+RMB stays ENTIRELY vanilla — a log / debarked block in hand places adjacent (the natural block-
        // place gesture, which is the expected behaviour), and the stick's "stick pile" / rope's store
        // (GroundStorable on Shift / Ctrl+Shift) keep working. Laying a log ONTO the pair is plain RMB (below),
        // so Shift never has to fight vanilla placement.
        if (controls.ShiftKey)
        {
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        // Ctrl+RMB (no Shift) is the "remove the log" gesture when the pair holds one, swallowed either way: an
        // UNTOUCHED log goes back to the player's inventory (TryTakeLog), a BEGUN one is scrapped with no drop
        // (DiscardLog — the player abandons the partial cut). With NO log it falls through to vanilla, so a ctrl
        // gesture on a plain trestle behaves normally and the pit saw's own ctrl-ground-store isn't clobbered.
        // (Ctrl+RMB takes an UNTOUCHED log back and scraps a BEGUN one — once a rip has raised saw progress
        // above 0, that log is begun and the scrap branch is what fires.)
        if (controls.CtrlKey)
        {
            if (be.HasLog)
            {
                if (!be.TryTakeLog(byPlayer)) be.DiscardLog(byPlayer); // untouched → take; begun → scrap
                return true;
            }
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        // Plain RMB, by precedence on a trestle:
        //  • pair already holds a log → an EFFECTIVE saw in hand at an axis end begins the rip (E06; the pit saw
        //    always, an ordinary saw only when AllowVanillaSaw is on). An ordinary saw with the flag off — like any
        //    non-saw — is refused: TryStartSawing returns false, the animation never starts, and a "need a pit saw"
        //    nudge shows. Swallowed either way (taking the log back is Ctrl+RMB);
        //  • finished EMPTY pair with a log / debarked log in hand → lay it on the pair (TryPutLog rejects a
        //    single/half-built trestle and a non-sawable hand → false, falling through);
        //  • otherwise → drive the stick → stick → rope build (TryBuildStep, a no-op for a gesture that doesn't
        //    fit the stage).
        // Every plain RMB on a trestle is swallowed so nothing is placed on top or consumed by default.
        if (be.HasLog)
        {
            // T01.06: stripping bark is the SIDE counterpart to the rip — a player at a long SIDE with a debark tool
            // (the bark spud, an axe + offhand hammer, or a knife) begins a strip hold. The side and end zones are
            // geometrically DISJOINT (a player is at a side OR an end, never both), so this never competes with the
            // rip below: TryStartDebarking returns false for an end-standing sawyer and we fall straight through.
            // T03.02/T03.03: on a GENUINE start, play the held tool's tier FK strip animation (DebarkAnimFor) — the
            // bark spud's two-handed pull (debarking-barkspud), the axe+hammer's own gesture (debarking-axe), or the
            // knife's one-handed scrape (debarking-knife). Started manually here, gated on the real start (not the item's
            // GetHeldTpUseAnimation, which fires on ANY RMB-hold — vs-held-use-anim-trigger — and would strip in
            // mid-air), on both sides like the saw start; ended in Stop/Cancel. Swallow on success. (A side-standing
            // player with the WRONG tool falls through to the rip below, which also fails — both misses then land on
            // the single NotifyInteractBlocked nudge, which picks the SIDE/strip message for a side-standing player.)
            if (be.TryStartDebarking(byPlayer, activeSlot, blockSel.FullPosition))
            {
                string? debarkAnim = BlockEntitySawhorse.DebarkAnimFor(activeSlot?.Itemstack, byPlayer.Entity.LeftHandItemSlot?.Itemstack);
                // Tier 2 holds a vanilla AXE in the main hand, whose held-"ready" pose (axe-*: "beginholditem") the
                // engine only EASES OUT on the click (HandleSeraphHandAnimations re-arms the ready stack only while no
                // mouse is down) — so for the easeOut window it overlaps debarking-axe's ease-IN and the arms blend
                // through a wrong pose ("начало анимации багается", intermittent on how far the ready pose had eased
                // in). StartHeldWorkAnim hard-stops that lingering ready pose so it can't overlap, exactly as
                // BlockChoppingBlock does for the axe swing. (The rip never showed this because the vanilla saw carries
                // no ready pose; the bark spud's own gesture is started the same way for consistency.)
                if (debarkAnim != null) StartHeldWorkAnim(byPlayer, debarkAnim);
                return true;
            }

            // E06: an EFFECTIVE saw (pit saw always; ordinary only when AllowVanillaSaw is on) + standing at an axis
            // END → begin the rip (returns true). Start the FK saw animation ONLY on a genuine start, on both sides
            // (the holder sees it locally; the server replicates it to other players in 3rd person), mirroring
            // BlockChoppingBlock's chop start. The hold continues in OnBlockInteractStep (progress + sawdust) and ends
            // in Stop/Cancel (StopAnimation). When NEITHER the strip (above) nor the rip began, raise ONE refusal
            // nudge that reads the player's position + tool to name the right action — the rip's "need a saw / stand
            // at an end" at an end, or the strip's "need a debark tool / stand at a side" at a long side.
            if (be.TryStartSawing(byPlayer, activeSlot, blockSel.FullPosition)) StartHeldWorkAnim(byPlayer, SawAnim);
            else be.NotifyInteractBlocked(byPlayer, activeSlot);
            return true; // swallow regardless — plain RMB on a held-log pair never places or builds (take = Ctrl)
        }

        if (be.TryPutLog(activeSlot, byPlayer)) return true;

        // The put can now refuse an accepted-SHAPE input the pair could make nothing from (T03.03). That is the one
        // refusal a player cannot read off the world, so it says so; every other one falls through silently to the
        // build step below, exactly as before.
        be.NotifyPutBlocked(activeSlot);
        be.TryBuildStep(byPlayer);
        return true;
    }

    // ── Sawing hold (E06/T06.02). OnBlockInteractStart returned true (above), so the engine drives this loop while
    // RMB is held. The BE owns the timing/progress; here we just relay each tick. Mirrors BlockChoppingBlock's
    // chop hold, minus the swing animation (that's T06.08). Non-saw interactions (put/build/take) return true from
    // Start too, so Step also fires for them — ContinueSawing's gate re-check returns false at once (no log to saw
    // / not a pit saw / not at an end), ending those instantly, exactly as the chopping block's Step aborts a put.

    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntitySawhorse be) return false;

        // REFUSED (AuditFixPlan T06.04, F-L4-06): the server owns the two co-op maps, so this client's press was
        // optimistic — it started the FK animation, whose frame-locked clips carry, and the local half of the hold
        // began throwing dust or chips, all for an end/side that belongs to somebody else. The refusal packet has
        // already cut the animation and named the reason; the rest of the press is served here, by doing NOTHING and
        // saying the hold is still running. TRUE, not false, and the "still running" is the point: end the interaction
        // while the button is down and the engine re-arms the whole gesture 0.25 s later (BuildRepeatDelay), so a held
        // button would restart the animation, the dust and the round trip four times a second. Alive-and-silent costs
        // one refusal per press instead. The chopping block met the same re-arm from the other side and answered it the
        // same way — its Step keeps a started swing alive until release precisely because a false return re-fired Start
        // and replayed the wind-up in game (BlockChoppingBlock.OnBlockInteractStep, "bug 2"). Clears on the next press
        // (OnBlockInteractStart).
        if (world.Side == EnumAppSide.Client && be.HoldRefusedLocally) return true;

        // The ONE place the rip/strip clock is made honest (AuditFixPlan T02.03). The engine's `secondsUsed` runs
        // ~0.14 s ahead of real time on the server, because it REPLAYS the frames a 0.15 s packet covered and adds
        // 0.02 s per replayed step — see ImmersiveWoodworkingModSystem.HonestHoldSeconds. Both holds accumulate this
        // number as DELTAS, so unclamped the over-credit lands in sawProgress/barkProgress every burst: a 6 s rip
        // finished in ~5.86 and a 1.5 s strip in ~1.36 (F-L4-04). Clamped here, once, at the outermost seam — every
        // consumer downstream (ClaimOrVerifyEnd's baseline, the ContinueSawing/ContinueDebarking deltas, the restock
        // pause's LastSecondsUsed) inherits a monotone real-time clock and needs no arithmetic of its own.
        secondsUsed = ImmersiveWoodworkingModSystem.HonestHoldSeconds(world, byPlayer, secondsUsed);

        // T01.06: a side-strip hold takes precedence over the rip relay, but the two are disjoint by player position
        // (side vs end), so only ever ONE advances. ContinueDebarking re-checks the debark gate and returns false the
        // instant it breaks (or for a rip hold, which is at an end, not a side), so a sawyer falls straight through to
        // the rip relay below. T03.04: while a genuine strip runs, throw cosmetic bark chips + the generic strip loop
        // off that same "is stripping" signal, so the gate's no-log / wrong-side / wrong-tool rejection suppresses
        // both for free, exactly like the rip's sawdust below.
        //
        // BOTH sides emit, and they emit DIFFERENT halves of the same effect (AuditFixPlan T06.01, F-L4-13). The
        // client half is the debarker's own, at frame rate. The server half is everyone else's: the engine calls the
        // CLIENT half of this hook for the local player alone, so a bystander used to get no chips and no loop at all
        // (only the frame-locked hit clips, which ride the replicated animation). BroadcastDebarkFx throttles itself
        // and addresses everyone except byPlayer, so nobody hears or sees it twice.
        if (be.ContinueDebarking(byPlayer, byPlayer.InventoryManager.ActiveHotbarSlot, secondsUsed))
        {
            if (world.Side == EnumAppSide.Client) { be.SpawnBarkChips(byPlayer, secondsUsed); be.UpdateDebarkSound(byPlayer); }
            else be.BroadcastDebarkFx(byPlayer);
            return true;
        }
        // The strip just ENDED while RMB is still held (the gate broke or the bark fully came off → ContinueDebarking
        // false): fade the generic strip loop out. A no-op for a non-debarker (a rip / put / build / take never started
        // one), so this is safe to run before the rip relay below.
        if (world.Side == EnumAppSide.Client) be.StopDebarkSound();

        // ContinueSawing returns true only while a GENUINE rip is in progress (log present, pit saw in hand, still
        // at an end); it returns false at once for a non-saw hold (put/build/take) or when the gate breaks. Sawdust
        // is purely cosmetic, so it hangs off that same "is cutting" signal — only while actually cutting, so the DoD
        // gate (no log / not at an end / no pit saw → no particles) is satisfied for free.
        //
        // Split across the two sides exactly as the strip above (AuditFixPlan T06.01, F-L4-13): the sawyer's own client
        // draws at frame rate, and the server sends a throttled copy to everyone ELSE, who otherwise watched the saw
        // cut air. The old single client-side line bought its free gate at the price of the effect not existing for
        // anyone but the holder of the mouse button, which the comment here did not say.
        bool cutting = be.ContinueSawing(byPlayer, byPlayer.InventoryManager.ActiveHotbarSlot, secondsUsed);
        if (cutting)
        {
            if (world.Side == EnumAppSide.Client) be.SpawnSawDust(byPlayer, secondsUsed);
            else be.BroadcastSawDust(byPlayer);
        }
        return cutting;
    }

    public override void OnBlockInteractStop(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        // End the saw loop AND any strip loop (onActivityStopped=EaseOut fades the pose out). Unconditional +
        // idempotent: a hold that never started one — a non-saw / non-debark gesture (put/build/take) — is a
        // harmless no-op. Both sides, like start. The strip codes cover all three debark tiers (spud + axe + knife),
        // so any path stops here.
        byPlayer.Entity.StopAnimation(SawAnim);
        foreach (string code in BlockEntitySawhorse.DebarkAnimCodes) byPlayer.Entity.StopAnimation(code);

        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntitySawhorse be)
        {
            // Honest clock, same as Step (T02.03) — and it matters MOST here: the release burst the engine replays
            // before calling Stop is exactly where the inflation is largest, and the final partial tick charged below
            // would otherwise bill up to 0.16 s that never elapsed.
            float rawSecondsUsed = secondsUsed;
            secondsUsed = ImmersiveWoodworkingModSystem.HonestHoldSeconds(world, byPlayer, secondsUsed);
#if IW_DEBUG
            // T02.03 measurement, one line per release — the rip/strip counterpart to BlockChoppingBlock's `chophold`.
            // `raw` minus `held` on the SERVER is the engine's replay inflation, measured. This station is the one the
            // inflation costs most in RELATIVE terms: a constant ~0.14 s against DebarkSeconds = 1.5 is a tenth of the
            // whole strip. Fires for a put/build/take release too (they also reach Stop); those simply own no hold.
            world.Logger.Notification("[IW-DBG] sawhold side={0} pos={1} raw={2:0.000} held={3:0.000} inflation={4:0.000}",
                world.Side, blockSel.Position, rawSecondsUsed, secondsUsed, rawSecondsUsed - secondsUsed);
#endif
            be.StopSawing(byPlayer, secondsUsed);
            be.StopDebarking(byPlayer, secondsUsed); // idempotent no-op for a non-debarker (rip / put / build / take)
            if (world.Side == EnumAppSide.Client) be.StopDebarkSound(); // fade the generic strip loop out on release
        }
    }

    public override bool OnBlockInteractCancel(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, EnumItemUseCancelReason cancelReason)
    {
        // Looked away / switched slot mid-hold: stop the saw loop AND any strip loop, then finalise the progress
        // reached so far (same as a release). The strip stop is idempotent (a non-debarker never started one).
        byPlayer.Entity.StopAnimation(SawAnim);
        foreach (string code in BlockEntitySawhorse.DebarkAnimCodes) byPlayer.Entity.StopAnimation(code);

        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntitySawhorse be)
        {
            // Honest clock, same as Step/Stop (T02.03). Cancel gets the same replay treatment as Stop
            // (ServerSystemBlockSimulation runs the identical catch-up loop on CancelBlockUse), so it needs the
            // identical clamp. Held in a LOCAL, not written back over the parameter: the base call below hands the
            // argument back to the engine, which owns its own units.
            float held = ImmersiveWoodworkingModSystem.HonestHoldSeconds(world, byPlayer, secondsUsed);
            be.StopSawing(byPlayer, held);
            be.StopDebarking(byPlayer, held); // idempotent no-op for a non-debarker (rip / put / build / take)
            if (world.Side == EnumAppSide.Client) be.StopDebarkSound(); // fade the generic strip loop out on look-away
        }
        return base.OnBlockInteractCancel(secondsUsed, world, byPlayer, blockSel, cancelReason);
    }

    // ───────────────────────── Held work-anim start (clean ease-in) ─────────────────────────
    //
    // Start the FK work loop (the saw rip or a debark strip) WITHOUT the muddy frame-1 blend the vanilla held-"ready"
    // pose otherwise causes. While a tool is raised the engine plays its GetHeldReadyAnimation (an axe-* returns
    // "beginholditem"); on the RMB-down it only EASES that pose OUT (HandleSeraphHandAnimations re-arms the ready
    // stack solely while no mouse is down — EntityPlayer.cs), so for the whole easeOut window it overlaps our anim's
    // ease-IN and the arms read a wrong intermediate pose. The vanilla saw carries NO ready pose (default null), which
    // is why only the tier-2 axe (+offhand hammer) start glitched, intermittently — it depends on how far the ready
    // pose had eased in when you clicked. The chopping block (also axe-held) hit this exact conflict and kills it the
    // same way (BlockChoppingBlock.CancelHeldReadyAnim/ResetChopAnim); the rip + strip starts never did. Holder's own
    // view only (the reach-in pokes the currently-rendered animator); other observers fall back to the server-synced
    // anim. Called on BOTH sides like the bare StartAnimation it replaces — GetAnimationState is null-safe on a shape
    // lacking the anim. NOTE: unlike woodchop this is a LOOP, so there is no frame-0 impact timing to preserve and no
    // back-to-back relaunch (a held strip never re-fires Start mid-hold); only the raised-tool→work transition needs
    // help, which is the single case below.

    // Each work-anim base code plus its first-person "-fp" sibling (the engine routes the holder's own view to -fp,
    // so the carry must pin BOTH). Mirrors BlockChoppingBlock.ChopAnimVariants.
    static string[] WorkAnimVariants(string baseCode) => new[] { baseCode, baseCode + "-fp" };

    /// <summary>Start the held work loop <paramref name="baseCode"/> and clear the lingering vanilla held-"ready"
    /// pose so its fade can't muddy the ease-in (see the section note). If the ready pose was actually on screen,
    /// carry the work anim in at FULL weight the same frame so the arms blend ready→work directly with no one-frame
    /// idle snap (the same trick as <c>BlockChoppingBlock.ResetChopAnim</c>'s carry); otherwise let it ease in from
    /// idle as normal. A no-op beyond the plain start for a tool with no ready pose (the vanilla saw).</summary>
    static void StartHeldWorkAnim(IPlayer byPlayer, string baseCode)
    {
        byPlayer.Entity.StartAnimation(baseCode); // engine cascades the "-fp" sibling for the holder's 1st-person view

        bool readyPoseWasVisible = CancelHeldReadyAnim(byPlayer);

        // RESET the frame-locked hit sounds on EVERY (re)start. The engine plays each animationSound once per loop
        // iteration, gated on SoundPlayedAtIteration[i] != Iterations (ClientAnimator.OnFrame), and that dedup array
        // is cleared ONLY by a full RunningAnimation.Stop() — which a normal release does NOT do until the ease-out
        // fully finishes (it just eases the weight out, AnimatorBase.ProgressRunningAnimation). So a quick
        // release→re-press REUSES the still-easing-out instance: AnimNowActive re-activates it and rewinds the frame
        // but leaves SoundPlayedAtIteration intact, so it still reads "already played this iteration" and the first
        // loop's hits are SILENT until the next wrap. For a short tool like the bark spud — whose whole strip
        // (DebarkSeconds ≈ 1.5s) finishes inside ONE loop (~1.8s) — that wrap never comes, so the hits stay silent for the
        // entire re-pressed strip. Nulling it forces a fresh dedup array next frame (re-filled with -1), so the hits
        // fire from frame 1 every time.
        //
        // CRITICAL — reset it on the TpAnimManager, NOT Entity.AnimManager: the animationSounds live ONLY on the base
        // (non-fp) TP-variant code, which the engine always owns and ticks on the player's TpAnimManager in BOTH camera
        // modes (PlayerAnimationManager routes the base code onto TpAnimManager, and TpAnimManager is ticked even in
        // 1st person). In 1st person Entity.AnimManager returns the selfFp animator instead, whose only saw/debark
        // instance is the SOUNDLESS "-fp" variant — nulling that does nothing for the audio while the stale TP dedup
        // survives. That mis-targeting was the "no hit sounds for the first ~loop after a re-press, first person only"
        // bug (bark spud silent outright, axe losing its first hit). TpAnimManager.Animator.GetAnimationState(baseCode)
        // returns the real sound-bearing instance regardless of camera. Null-safe (no-op on the server, or a non-seraph
        // shape lacking the anim).
        RunningAnimation? soundAnim = byPlayer.Entity.TpAnimManager?.Animator?.GetAnimationState(baseCode);
        if (soundAnim != null) soundAnim.SoundPlayedAtIteration = null;

        // Mask the ready→work transition only when the vanilla held-"ready" pose was actually on screen: pin the work
        // anim to full weight this frame so the arms blend ready→work directly with no one-frame idle snap (otherwise
        // let it ease in from idle — a cold start already looks right). This acts on the RENDERED animator
        // (Entity.AnimManager — selfFp in 1st person, Tp in 3rd) across BOTH the base + "-fp" variants, since either can
        // be the one on screen. Weight ONLY: do NOT rewind CurrentFrame / zero Iterations the way woodchop's
        // ResetChopAnim does for its frame-0 impact timing — this is a Repeat LOOP that eases in on Active alone
        // (RunningAnimation.Progress), and rewinding the frame would itself re-suppress the just-reset sounds
        // (re-crossing a frame already marked played this iteration).
        if (!readyPoseWasVisible) return;

        IAnimator? animator = byPlayer.Entity.AnimManager?.Animator;
        if (animator == null) return;
        foreach (string code in WorkAnimVariants(baseCode))
        {
            RunningAnimation? ras = animator.GetAnimationState(code);
            if (ras != null) ras.EasingFactor = 1f; // shape may lack this variant (non-seraph) → null, skip
        }
    }

    /// <summary>
    /// Hard-cancel every work loop this block starts — the rip and all three strip tiers — with NO ease-out tail, and
    /// their first-person siblings with them. <c>StopAnimation</c> alone keeps advancing the frame while the weight
    /// fades, so the gesture plays on as a ghost and its frame-locked clips keep firing; <c>Stop()</c> on each running
    /// variant zeroes the weight next frame, so the arms read "nothing happened". Called when the server refuses a
    /// crowded end or side (AuditFixPlan T06.04) to cut the hold that client had already started optimistically — the
    /// counterpart to <see cref="BlockChoppingBlock.CancelChopAnim"/>, which does the same for the swing. All codes
    /// unconditionally: a player runs at most one of them, and a stop for a loop that never started is a no-op.
    /// </summary>
    internal static void CancelWorkAnim(IPlayer byPlayer)
    {
        byPlayer.Entity.StopAnimation(SawAnim);
        foreach (string code in BlockEntitySawhorse.DebarkAnimCodes) byPlayer.Entity.StopAnimation(code);

        // The holder's currently-visible animator (fp hands in 1st person, the 3rd-person body otherwise).
        IAnimator? animator = byPlayer.Entity.AnimManager?.Animator;
        if (animator == null) return;

        foreach (string code in AllWorkAnimVariants) animator.GetAnimationState(code)?.Stop();
    }

    /// <summary>Every work loop this block can start — the rip plus the three strip tiers — each with its
    /// first-person sibling: the flat list <see cref="CancelWorkAnim"/> walks. Derived from the same two sources the
    /// starts use (<see cref="SawAnim"/> + <see cref="BlockEntitySawhorse.DebarkAnimCodes"/>), so a future strip tier
    /// cannot be added there and left un-cancelled here.</summary>
    static readonly string[] AllWorkAnimVariants = BuildAllWorkAnimVariants();

    static string[] BuildAllWorkAnimVariants()
    {
        string[] baseCodes = new string[1 + BlockEntitySawhorse.DebarkAnimCodes.Length];
        baseCodes[0] = SawAnim;
        BlockEntitySawhorse.DebarkAnimCodes.CopyTo(baseCodes, 1);

        string[] all = new string[baseCodes.Length * 2];
        for (int i = 0; i < baseCodes.Length; i++)
        {
            all[i * 2] = baseCodes[i];
            all[i * 2 + 1] = baseCodes[i] + "-fp";
        }
        return all;
    }

    /// <summary>Hard-clear the vanilla held-"ready" pose of the MAIN-hand tool the instant a work anim starts, and
    /// report whether it was actually visible (Running and eased past a sliver). The engine only EASES the ready pose
    /// out, which overlaps and muddies our ease-in; <c>Stop()</c> removes it cleanly. The offhand (a tier-2 hammer)
    /// carries no ready pose, so the main hand is enough. Mirrors <c>BlockChoppingBlock.CancelHeldReadyAnim</c>.</summary>
    static bool CancelHeldReadyAnim(IPlayer byPlayer)
    {
        ItemSlot slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        string? readyAnim = slot?.Itemstack?.Collectible?.GetHeldReadyAnimation(slot, byPlayer.Entity, EnumHand.Right);
        if (readyAnim == null) return false;

        RunningAnimation? ras = byPlayer.Entity.AnimManager?.Animator?.GetAnimationState(readyAnim);
        if (ras == null) return false;

        bool wasVisible = ras.Running && ras.EasingFactor > 0.1f; // Running alone isn't enough — it can be fading at ~0
        ras.Stop(); // hard-stop now so its fade can't overlap the work anim's ease-in
        return wasVisible;
    }

    // A trestle placed FROM AN ITEM STACK is the hand-crafted one (recipes/grid/sawhorse.json, live only when
    // Config.CraftableSawhorse is on): it arrives fully assembled, so jump its BE straight to Complete and pair
    // it, skipping the in-world stick→stick→rope stages. The in-world stick build (BehaviorSawhorseBuild) instead
    // uses BlockAccessor.SetBlock(blockId, pos) with NO itemstack, so byItemStack is null there and this is a no-op
    // for that path (OnBuilt sets Stage1). base.OnBlockPlaced runs first so the BE exists (it SpawnBlockEntity's it),
    // mirroring vanilla BlockAnimalTrap. Server-authoritative: the completion + pairing are written on the server and
    // synced to clients, exactly as TryBuildStep's completion is.
    public override void OnBlockPlaced(IWorldAccessor world, BlockPos blockPos, ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(world, blockPos, byItemStack);

        if (world.Side != EnumAppSide.Server || byItemStack == null) return;
        if (world.BlockAccessor.GetBlockEntity(blockPos) is BlockEntitySawhorse be) be.OnPlacedAsCompleteFromItem();
    }

    // No "sawhorse" item exists in the DEFAULT flow (T03.05) — the trestle is built, never held as an item — so a
    // creative middle-click "pick block" can't grab one (no creative-inventory entry, no default drop; breaking it
    // returns its sticks/rope via BlockEntitySawhorse.OnBlockBroken). The optional hand-craft (CraftableSawhorse)
    // produces a placeable stack via the recipe, but OnPickBlock stays null so middle-click still doesn't.
    public override ItemStack? OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        return null;
    }

    /// <summary>
    /// The hover NAME, and this override is LOAD-BEARING: it exists purely because <see cref="OnPickBlock"/> right
    /// above hands back null on purpose. The base builds the placed name as
    /// <c>OnPickBlock(world, pos)?.GetName()</c> plus each behaviour's contribution, then TrimEnd's (vsapi
    /// <c>Block.GetPlacedBlockName</c>, read by decompiling 1.22 — no line number on purpose, the one this repo
    /// already quotes for the same method disagrees with a second decompile of the SAME dll, so the offsets are the
    /// decompiler's and not the source's) — appending a null to a StringBuilder appends NOTHING, so the sawhorse
    /// resolved to an
    /// EMPTY string rather than null, and the block info HUD (which substitutes "Unknown" only for null) drew a blank
    /// title line over every placed trestle. Rebuild the name from the block's own stack, which is exactly what the
    /// base would have resolved through the pick stack (<c>Lang.GetMatching("immersivewoodworking:block-sawhorse")</c>,
    /// via CollectibleObject.GetHeldItemName) had one existed — so the placed name stays tied to the same lang key as
    /// the handbook page and needs no second key to drift out of sync. The behaviour loop is mirrored for the reason
    /// spelled out on <see cref="BlockHalfLogSlab.GetPlacedBlockName"/>: vanilla "Unstable" (this block's only
    /// declared behaviour) contributes nothing, but BlockBehaviors is a public array a third-party patch can append
    /// to, and silently swallowing a suffix would be a real bug with no symptom here to point at.
    ///
    /// <para>The name is deliberately CONSTANT across build stage and pairing, and this block is NOT the place to
    /// copy <see cref="BlockChopper.GetPlacedBlockName"/>. That machine earns its rename because its code is
    /// <c>chopper-frame-*</c>: the placed thing really is a frame, the assembled thing is a different object, and its
    /// nine cells are one machine that must read as one name. A trestle is a frame of nothing — one block IS one
    /// sawhorse, and a finished pair is two of them (the mod's own text says "a pair of sawhorses" throughout), so
    /// both cells naming themselves "Sawhorse" is the correct reading and there is no assembled-only name to switch
    /// to. Half-built stages keep the name too: the stage is already carried by the interaction help ("Add a stick" /
    /// "Add rope"), which is the surface the player acts on, and an unfinished-trestle name would cost a key in five
    /// lang files to describe a state the player is holding the fix for.</para>
    /// </summary>
    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        StringBuilder sb = new StringBuilder(new ItemStack(this).GetName());
        foreach (BlockBehavior bh in BlockBehaviors) bh.GetPlacedBlockName(sb, world, pos);
        return sb.ToString().TrimEnd();
    }

    // Collision fits the multiblock to its meshes: a finished PAIR collides as ONE combined box — the two
    // trestles' bounding box, grown to include a laid log while one is present — instead of two full cubes. A
    // single / half-built trestle keeps the JSON full-cube (GetPairCollisionBoxes returns null); the SELECTION box is
    // a separate authored set (GetSelectionBoxes, below). The BE returns this cell's CLAMPED SLICE of the combined
    // box (the partner cell carries the other half along the axis), so the broadphase resolves the multiblock from
    // either end (see BlockEntitySawhorse.GetPairCollisionBoxes). Box values are hand-authored in sawhorse.json.
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BlockEntitySawhorse be && be.GetPairCollisionBoxes() is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetCollisionBoxes(blockAccessor, pos);
    }

    // Fit the SELECTION (white outline) box of a finished pair to its OWN authored box set (attributes.selectionBoxes),
    // SEPARATE from the collider — so the highlight can be tuned independently of what you physically bump into. Built
    // the same way (BE GetPairSelectionBoxes → per-cell clamped slice). A single / half-built trestle keeps the JSON
    // cube. (It's still a handy live probe: the outline is exactly what GetPairSelectionBoxes returns on the client,
    // so a box that floats off the model means the authored selection box doesn't match the re-authored mesh.)
    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BlockEntitySawhorse be && be.GetPairSelectionBoxes() is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetSelectionBoxes(blockAccessor, pos);
    }

    // Particles collide against GetParticleCollisionBoxes(), which by DEFAULT returns the static JSON full-cube
    // collisionbox field — NOT our GetCollisionBoxes override above. That made cosmetic sawdust settle on the cube's
    // invisible top face (y≈1) in the open middle of the A-frame, so flakes looked stuck in mid-air. Route particles
    // to the BE's laid-LOG box instead (BlockEntitySawhorse.GetParticleCollisionBoxes): dust rests on the VISIBLE log,
    // and where there's no box (open frame / no log) it falls through to the ground below — never onto the full cube.
    public override Cuboidf[] GetParticleCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BlockEntitySawhorse be)
        {
            return be.GetParticleCollisionBoxes();
        }
        return base.GetParticleCollisionBoxes(blockAccessor, pos);
    }
}
