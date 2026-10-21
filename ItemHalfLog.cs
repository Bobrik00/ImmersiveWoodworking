using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
// Item defines an instance `Shape` property (a CompositeShape), which shadows the Vintagestory.API.Common.Shape TYPE
// inside this class — so alias the type to reference the parsed-shape API unambiguously (esp. from static members).
using VSShape = Vintagestory.API.Common.Shape;

namespace ImmersiveWoodworking;

/// <summary>
/// Half-log (E04/T04.01): a log split lengthwise on the chopping block, the carryable intermediate on the way to
/// firewood (a log → 2 half-logs → firewood, T04.02). A SINGLE data-item, not per-species variants — the wood species
/// rides as a stack attribute (<c>wood</c> + <c>woodDomain</c>, stamped from the source log at chop time) and the model
/// is re-textured per species at runtime, so one code serves every wood (including other wood mods') with no per-mod
/// patch. This is the item counterpart of the chopping block's data-block: same OnBeforeRender → per-species
/// MultiTextureMeshRef idiom (vanilla BlockShapeFromAttributes), but tesselating the "split log" shape through
/// <see cref="LogTexSource"/> (bark round sides / end-grain caps / bare split face), the same species-bound source the
/// sawhorse and sawmill use for their laid logs.
/// </summary>
public class ItemHalfLog : Item
{
    // Per-species GPU meshes for the held / GUI / dropped item, keyed "{domain}:{wood}" plus the debarked flag.
    // Process-wide ObjectCacheUtil entry, lazily built+uploaded in OnBeforeRender and freed in OnUnloaded — the exact
    // BlockChoppingBlock idiom. The flag is part of the KEY and not an afterthought: a barked and a debarked half-log
    // of one species share every other identifier, so a species-only key would hand whichever was tesselated first to
    // both for the rest of the process (AuditFixPlan T04.01).
    const string HeldMeshCacheKey = "iwHalfLogHeldMeshes";

    // The wood species offered in creative/handbook come from here (the all-domain set the mod system discovered once
    // from every worldproperties/block/wood — vanilla + wildcrafttree + any wood mod), so every mod's species gets a
    // creative entry with no per-mod patch. Same source the chopping block uses.
    ImmersiveWoodworkingModSystem modSystem = null!;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        modSystem = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();

        // One creative stack per wood species (each carrying the wood + domain attribute), so the creative menu and —
        // via the default GetHandBookStacks path — every species is discoverable. Both sides: api.World is ready here.
        BuildCreativeStacks(api);

        // Which species a SPECIES-LESS stack lays down (HalfLogBlockPlan T04.02). Resolved here, once, because the
        // species index it reads is filled in AssetsLoaded — before any OnLoaded — and because a per-click resolve
        // would have to sweep the block registry.
        ResolveDefaultWood();
    }

    // ───────────────────────────── the debarked flag (AuditFixPlan T04.01) ─────────────────────────────
    //
    // A half-log split off a `debarkedlog` has no bark left on its round side, and until T04.01 that fact died at the
    // split: MakeHalfLogs stamped species and domain only, so a player who had stripped a log got the bark back the
    // moment it was chopped. The flag rides the STACK, exactly like the species, because a half-log is ONE item for
    // every wood and every surface state — and it rides ONE key that everything reads through the two helpers below,
    // so the three producers (the split, the slab drop, the slab's picked stack) and the five readers (held mesh, laid
    // mesh on the stand, the chopper's workpiece, the name, the placement) cannot drift into a half-match.
    //
    // WRITTEN ONLY WHEN TRUE, and that is load-bearing rather than tidy. TreeAttribute compares the whole KEY SET, so
    // an absent key and an explicit `false` are DIFFERENT stacks that refuse to merge in a backpack — the same trap
    // BlockHalfLogSlab.HalfLogStack documents for the wood/woodDomain pair. "Absent means barked" therefore has to be
    // the single spelling of a barked half-log everywhere, which is also why the read below takes a default of false
    // rather than asking whether the key exists.

    /// <summary>Stack-attribute key for "this half came off a log whose bark was already stripped".</summary>
    internal const string DebarkedAttr = "debarked";

    /// <summary>Whether this half-log is a debarked one. Null-safe and absent-safe: everything that is not an
    /// explicit true is barked, which is the one reading of the key (see the note above).</summary>
    internal static bool IsDebarked(ItemStack? stack) => stack?.Attributes?.GetBool(DebarkedAttr, false) == true;

    /// <summary>Stamp the flag onto a freshly built half-log stack. A no-op for a barked one BY DESIGN — writing an
    /// explicit <c>false</c> would split barked half-logs into two non-merging kinds — so this is safe to call
    /// unconditionally with whatever the source was, and every producer does exactly that.</summary>
    internal static void StampDebarked(ItemStack stack, bool debarked)
    {
        if (debarked) stack.Attributes.SetBool(DebarkedAttr, true);
    }

    // ───────────────────────────── per-species combustion ─────────────────────────────

    /// <summary>
    /// Suppress this half-log's burn when its stored species is one the source wood mod marked NON-FUEL — the item-side
    /// half of the exception <c>blocktypes/halflogslab.json</c> makes with <c>halflogslab-petrified-*: null</c>.
    ///
    /// <para>Why it has to be code: the species lives in the STACK (<c>wood</c> / <c>woodDomain</c>), not in the item
    /// code, so <c>combustiblePropsByType</c> has nothing to glob against and the flat <c>combustibleProps</c> in
    /// <c>itemtypes/halflog.json</c> would apply to every species alike. Wildcraft Trees ships <c>petrified</c> — stone,
    /// not wood — strips combustion from it in seven of its own assets and keeps it out of its firewood recipe; our
    /// chopping block accepts a petrified log all the same, so without this a stone half-log burned for 42 s.
    ///
    /// <para>The rule is general (<see cref="ImmersiveWoodworkingModSystem.IsNonCombustibleWood"/>: a species whose OWN
    /// logs all carry no burn), so any wood mod's non-fuel species is honoured with no per-mod patch and no hardcoded
    /// species name here. A bare stack, a species we can't judge, or a call before <c>AssetsFinalize</c> all fall
    /// through to the JSON value, so the ordinary half-log is untouched.
    ///
    /// <para>This is THE seam for it: firepit and ground-storage fuel, pit-kiln fuel, an item burning inside a fire
    /// (<c>EntityItem</c>), the held tooltip and the handbook all read burn data through
    /// <c>Collectible.GetCombustibleProperties</c>, never the field — so one override covers every one of them, on both
    /// sides, for stacks that already exist in a save as well as new ones.
    /// </summary>
    public override CombustibleProperties GetCombustibleProperties(IWorldAccessor world, ItemStack itemstack, BlockPos pos)
    {
        string? wood = itemstack?.Attributes?.GetString("wood", null);
        if (wood != null && modSystem != null &&
            modSystem.IsNonCombustibleWood(wood, itemstack!.Attributes.GetString("woodDomain", "game")))
        {
            return null!; // not fuel: no burn, no ignition, no burn line in the tooltip or handbook
        }

        return base.GetCombustibleProperties(world, itemstack, pos);
    }

    // ───────────────────────────── placing it as a building block (T04.02) ─────────────────────────────
    //
    // What the engine does for a BLOCK stack, done by hand, because a half-log is an ITEM. The whole vanilla build
    // chain — SystemMouseInWorldInteractions.OnBlockBuild -> ClientMain.OnPlayerTryPlace -> the place packet -> the
    // server's ServerSystemBlockSimulation.TryModifyBlockInWorld -> Block.TryPlaceBlock — hangs off one term of the
    // right-click dispatch, `activeHotbarSlot.Itemstack.Class == EnumItemClass.Block` (decompiled
    // SystemMouseInWorldInteractions, the `flag2` of the second `if`). An Item never satisfies it, so NOTHING of that
    // chain runs for us: not the neighbour-cell offset, not the world-edge test, not the claim check, not the lava
    // test, not CanPlaceBlock, not the sound, not the stack decrement, not the red "placefailure-*" toast. Every one
    // of those is reproduced below, in vanilla's order — no vanilla ITEM in the game reproduces the full set (the
    // closest, ItemDryGrass, skips the world edge, lava and the creative exemption), so the checklist here is
    // assembled from the engine's own build path rather than copied off one item.
    //
    // Two things from that path are deliberately NOT reproduced. `IServerEventAPI.DidPlaceBlock` cannot be raised
    // from mod code at all, so third-party logging and rollback mods will not see a half-log placement. And the
    // ceiling case is only half-closed: see the guard below.
    //
    // The mirror of that fact is that there is no fall-through to protect: an Item returning NotHandled cannot reach
    // vanilla placement, so claiming the click costs only the block's own Shift+RMB interaction — which is exactly
    // what a block held in hand costs too, since OnBlockBuild sits ahead of it in the same dispatch.
    //
    // WHERE THE GESTURE SITS (T04.03, verified against the decompiled dispatch rather than assumed). The right-click
    // handler is two chained `if`s, and the two halves order the block and the held item OPPOSITE ways:
    //
    //   plain RMB   `!Shift && TryBeginUseBlock(block)`  ||  `!Shift && TryBeginUseActiveSlotItem()`
    //   Shift+RMB   `Shift && block.PlacedPriorityInteract && TryBeginUseBlock(block)`
    //             || `OnBlockBuild()`                    // a BLOCK stack only; an Item never reaches it
    //             || `Shift && !HeldPriorityInteract && TryBeginUseActiveSlotItem()`   <-- us
    //             || `Shift && !block.PlacedPriorityInteract && TryBeginUseBlock(block)`
    //
    // So on plain RMB the block is asked FIRST and we only ever get what it declined: laying a half-log on a chopping
    // block, opening a chest, advancing a sawhorse, and the mod's own Ctrl+RMB "take it back" convention (Ctrl is not
    // read by the dispatch at all) are safe by engine ordering, with no guard here. On Shift+RMB the order INVERTS —
    // the held item is asked before the plain block term — so claiming a Shift click takes that block's interaction
    // away. That is the same toll a block held in hand pays (OnBlockBuild sits ahead of the block term too), which is
    // why placement is not gated on Shift; but it is a real toll, and the two gestures below are bought back.
    //
    // Handing a click back is a bare `return` BEFORE handHandling is set: the dispatch then falls through to its last
    // term and the block gets it. Anything added here must therefore be side-independent, per the gate rule below.

    /// <summary>The species a stack with no <c>wood</c> attribute places. Prefers <c>oak</c> because that is what a
    /// bare half-log already LOOKS like — <c>itemtypes/halflog.json</c> pins its three textures to
    /// <c>game:block/wood/{bark,treetrunk,debarked}/oak</c> and <see cref="OnBeforeRender"/> leaves that default mesh
    /// alone for a species-less stack — so the block that appears matches the item that was held. Null only where the
    /// species index is empty, i.e. where no half-log slab exists at all.</summary>
    const string PreferredDefaultWood = "oak";

    string? defaultWood;

    /// <summary>
    /// Pick <see cref="defaultWood"/> once: <c>oak</c> when the species index knows it, else the ordinally smallest
    /// species it does know, else null.
    ///
    /// <para>Why not just the literal <c>"oak"</c>: two shipped-code paths can leave a world without it. The species
    /// list is not vanilla's — <c>DiscoverWoodSpecies</c> reads every domain's <c>worldproperties/block/wood</c>, and
    /// a wood mod could patch oak out of game's (or drop the oak variants out of <c>log.json</c> with a
    /// <c>skipVariants</c> patch, which lands before variant expansion). And <c>InjectWoodVariants</c> has four early
    /// returns, each of which leaves the on-disk <c>placeholder</c> as the only registered species. Neither is
    /// hypothetical enough to ship an unguarded literal for, and neither is worth a hardcoded second name.
    ///
    /// <para>Why ORDINALLY smallest rather than "the first one": <c>Assets.GetMany</c> is unordered, so the index's
    /// enumeration order is not stable across launches, and this value has to agree between the client and the
    /// server on every start — the same reason <c>InjectWoodVariants</c> sorts and
    /// <see cref="HalfLogPlacement.SelfCheck"/> picks its sample species this way.
    ///
    /// <para>This is NOT a species whitelist and does not breach the mod-independence rule (source-conventions.md,
    /// "accept by code shape, never by species"): nothing is gated on the name, it only names the FALLBACK for a
    /// stack that carries no species at all, and the fallback still resolves through the ordinary registry lookup.
    /// </summary>
    void ResolveDefaultWood()
    {
        IReadOnlyDictionary<string, string> known = modSystem.WoodDomains;
        if (known.ContainsKey(PreferredDefaultWood)) { defaultWood = PreferredDefaultWood; return; }

        string? smallest = null;
        foreach (string wood in known.Keys)
            if (smallest == null || string.CompareOrdinal(wood, smallest) < 0) smallest = wood;
        defaultWood = smallest;
    }

    /// <summary>The species this stack should place: its own, or <see cref="defaultWood"/> when it carries none.
    /// Bare stacks are ordinary, not exotic — the handbook page is deliberately one (<see cref="GetHandBookStacks"/>),
    /// and a log whose code shape <c>BlockEntityChoppingBlock.LogBlockSpecies</c> cannot parse splits into bare
    /// half-logs. Read with the same defaults every other reader here uses, so the block that appears is the species
    /// the item was named and painted for.</summary>
    string? SpeciesToPlace(ItemStack? stack)
    {
        string? wood = stack?.Attributes?.GetString("wood", null);
        return string.IsNullOrEmpty(wood) ? defaultWood : wood;
    }

    /// <summary>
    /// Right-click with a half-log in hand: lay it down as <c>halflogslab-{wood}-{skin}-{rot}-{run}-{face}</c>, one item per
    /// press (T04.02). The orientation is <see cref="HalfLogPlacement"/>'s (T04.01) and the species is the stack's.
    ///
    /// <para><b>Server-authoritative, with the client only claiming the gesture</b> — the split every other in-world
    /// mutation in this mod uses (<c>BehaviorSawhorseBuild</c>, <c>BlockTarPot</c>, <c>BlockPerforatedPot</c>), and
    /// here it also buys correctness the vanilla predict-then-correct model does not: the run axis is derived from the
    /// player's yaw, and the server's copy of that yaw trails the client's by up to a UDP position tick, so a
    /// client-side prediction would visibly flip a log 90 degrees on placement whenever the click lands near a
    /// quadrant boundary. Nothing is drawn until the server's block arrives, so nothing can flicker. The cost is one
    /// round trip of latency, which is a tick in single-player.
    ///
    /// <para><b>The client is the gate.</b> The engine sends the interaction to the server only if this method sets
    /// <paramref name="handHandling"/> to something other than <c>NotHandled</c> (decompiled
    /// SystemMouseInWorldInteractions.TryBeginUseActiveSlotItem), so every bail-out ABOVE the assignment must be one
    /// the server would take too, or the server would never be asked. That is why the claim test and
    /// <c>TryPlaceBlock</c> — the two tests whose answers are side-dependent or expensive — sit BELOW it, and only the
    /// side-independent ones (is there such a block, is the target position valid at all) sit above.
    ///
    /// <para><b>One placement per press</b>, gated on <paramref name="firstEvent"/> — true exactly once per physical
    /// button-down. The dispatch does re-enter this method roughly every 0.25 s while the button stays down
    /// (BuildRepeatDelay), and it re-enters with a FRESHLY ray-traced selection (UpdatePicking calls
    /// UpdateCurrentSelection before the dispatch on every frame), so a repeat would in principle walk a run of slabs
    /// exactly as holding the button with a vanilla block does: the slab just placed becomes the aimed block, is not
    /// replaceable, and the offset below steps to the next cell. It is gated off anyway, for two reasons. One per
    /// press is the intended feel for a block you aim individually — the orientation is a function of yaw, so a
    /// dragged run would change axis mid-drag. And the repeat does not compose with this method being
    /// server-authoritative: for a round trip longer than the 0.25 s repeat the client's next ray still hits the OLD
    /// cell, because the slab it should step off has not arrived, so the repeat would place nothing and toast
    /// "notreplaceable" instead. Revisit at T04.05 if building feels slow — the gate is the single term to drop, but
    /// the latency case has to be answered with it.
    /// </summary>
    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling)
    {
        // Behaviours first, and yield to any that claims the click. Ours carries none today, but a third-party patch
        // can attach one to this item (the mod does exactly that to game:stick), and a GroundStorable-style behaviour
        // claiming Shift+RMB must win over placement — the same reason BlockHalfLogSlab.GetHeldItemName keeps its
        // own behaviour loop instead of just returning the name.
        base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handHandling);
        if (handHandling != EnumHandHandling.NotHandled) return;

        if (!firstEvent || blockSel == null || entitySel != null || slot?.Itemstack == null) return;

        IWorldAccessor? world = byEntity?.World;
        // Resolve the player BEFORE anything else can need it: Claims.TryAccess dereferences it (WorldData and
        // Entity.Alive) before its own null guard, so a non-player entity holding a half-log would throw there.
        IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;
        if (world == null || byPlayer == null) return;

        // NO SHIFT+RMB CARVE-OUTS HERE, deliberately (T04.03 / F-PT-04): Shift+RMB with a half-log in hand lays a
        // slab EVERYWHERE, with no block the gesture is quietly handed back to. Two used to be carved out, both gone.
        //
        // A FINISHED FIREPIT was one, so the half-log — FUEL by design (750 C / 42 s, itemtypes/halflog.json) — could
        // still be fed to one by the standard gesture, since vanilla's fuel branch is Shift-ONLY (the whole branch
        // sits inside `if (ShiftKey)` in BlockFirepit.OnBlockInteractStart and takes anything with BurnTemperature >
        // 0). Dropped on the author's preference, not on a defect: predictability of the placement gesture beats the
        // convenience of loading fuel. Nothing became impossible, only slower — a finished firepit carries the
        // `Container` behaviour (survival/blocktypes/wood/firepit.json), so plain RMB still opens its dialog and the
        // half-log goes into the fuel slot by hand, the way everything that is not firewood is loaded. The construct
        // stages 1-4 were never carved out, so placement is now uniform across all five stages of the block.
        //
        // CTRL+SHIFT AT THE CHOPPER / SAWMILL was the other, from when a bare `CtrlKey` on those two block entities
        // made Ctrl+Shift a second, undocumented "take it back" chord. T03.05 narrowed both to `CtrlKey && !ShiftKey`
        // (BlockEntityChopper.OnInteract, BlockEntitySawmill.OnInteract), so that chord removes nothing any more: the
        // click reached the machine, matched neither a build part nor a log, and was swallowed doing nothing at all.
        // Plain Shift+RMB there already laid a slab, so keeping the carve-out left ONE block behaving two ways under
        // a modifier that no longer means anything to it.
        //
        // THE RULE, should a carve-out ever be proposed again — both halves have to hold. It must be a
        // SIDE-INDEPENDENT read, because every bail-out above the handHandling assignment must be one the server
        // would take too, or the server is never asked (the gate paragraph in the docstring). And the block must
        // actually DO something with the click: a `return` for a block that would decline anyway is not neutral, it
        // spends the click on nothing at all, which is worse than laying a slab. Plain RMB needs no carve-out either
        // way — there the block is asked first and has already said no.

        // WHICH BLOCK. A species with no variant resolves to null: nothing placed, nothing spent — and now the
        // player is TOLD, rather than left clicking at a block that never appears (AuditFixPlan T04.05, F-PT-02).
        //
        // Vanilla's `aged` (and `veryaged` / `rotten` / `veryrotten`) used to be the shipped case here — literal
        // states in log.json / log-debarked.json that appear in no worldproperties, so they yielded a half-log and no
        // slab. They now HAVE slabs: the axis is the union of the worldproperties species and the species only a log
        // blocktype declares (ImmersiveWoodworkingModSystem.DiscoverLogOnlySpecies), which is by construction every
        // species a half-log can be stamped with. So this branch is no longer a shipped-content gap but a genuine
        // "should not happen", which is exactly the kind of silence worth breaking.
        //
        // A species whose WOOD MOD WAS UNINSTALLED is NOT that case, and the difference is worth stating because the
        // guard below looks like it covers both. The savegame remembers the code, so the engine hands back an
        // IsMissing placeholder rather than null (T07.02, measured): the placement goes through and lays that frozen
        // placeholder, spending the half-log. Left as is by decision — the cell turns back into the real slab the
        // moment the wood mod is reinstalled, which is how every other mod's blocks behave, and refusing here would
        // be this mod inventing a rule the rest of the game does not have.
        //
        // The SKIN comes off the stack and not off the click (T04.01): it is a property of the wood in hand, not of
        // where it is being put. So it joins the species on the "read from the stack" side of this call, while rot,
        // run and face stay on the "derived from the aim" side — and a stripped half-log lays a stripped slab without
        // any new gesture. A bare stack has no flag, which reads as barked, which is what a bare half-log looks like.
        HalfLogState state = HalfLogPlacement.Select(blockSel, byPlayer);
        Block? slab = BlockHalfLogSlab.Get(world, SpeciesToPlace(slot.Itemstack),
            BlockHalfLogSlab.SkinFor(IsDebarked(slot.Itemstack)), state.Rot, state.Run, state.Face);
        if (slab == null)
        {
            // Client-only side effect, and the `return` below is taken on BOTH sides — so this does not break the
            // gate rule stated in the docstring (every bail-out above the handHandling assignment must be one the
            // server takes too). TriggerIngameError is the mod's own idiom for a refused interaction; the stack's own
            // name carries the species, so one lang key covers a named and a bare half-log alike.
            (world.Api as ICoreClientAPI)?.TriggerIngameError(this, "halflognoslab",
                Lang.Get("immersivewoodworking:ingameerror-halflog-noslab", slot.Itemstack.GetName()));
            return;
        }

        // WHICH CELL — vanilla's rule verbatim (decompiled OnBlockBuild): the aimed-at cell if what is there yields
        // to us, else one step along the aimed face. On a CLONE, never on blockSel: BlockPos.Offset/Add mutate in
        // place, and on the client this selection is the player's live one, which the engine serializes to the server
        // AFTER this method returns — offsetting it here would ship the server a doubly-offset position.
        // blockSel.Face and blockSel.HitPosition are left alone, so HalfLogPlacement above (which reads exactly those
        // two, both relative to the LOOKED-AT block) is unaffected either way.
        BlockSelection placeSel = blockSel.Clone();
        if (!world.BlockAccessor.GetBlock(blockSel.Position).IsReplacableBy(slab))
        {
            placeSel.Position.Add(blockSel.Face);
            placeSel.DidOffset = true;
        }

        // Off the edge of the world. TryPlaceBlock does NOT test this (vanilla tests it one level up, in
        // OnPlayerTryPlace, and reports "outsideworld"), and without it the failure is silent and expensive: an
        // out-of-range position hands back the air block, whose Replaceable is 9999, so CanPlaceBlock PASSES, while
        // the SetBlock underneath returns early on the null chunk — a half-log spent on nothing.
        //
        // IsValidPos does NOT cover the world CEILING, whatever its name suggests: it tests the four horizontal edges
        // and the world floor, and above MapSizeY it answers true (decompiled WorldMap.IsValidPos — the only Y term
        // is `posY >= 32768`, which is the escape hatch that lets MINI-DIMENSION positions through, since those live
        // above 32768 by construction). Vanilla has the identical hole and loses a block stack at the ceiling the
        // same way; the second term closes it here because it costs one comparison, and it is scoped to the main
        // dimension so it cannot re-close the escape hatch the engine deliberately left open.
        if (!world.BlockAccessor.IsValidPos(placeSel.Position)) return;
        if (placeSel.Position.dimension == 0 && placeSel.Position.Y >= world.BlockAccessor.MapSizeY) return;

        // The gesture is ours from here, on both sides: this is what makes the engine run the method server-side.
        // PreventDefaultAction rather than PreventDefault so the build motion still plays — this is a placement, and
        // it is the value the mod's other build gesture (BehaviorSawhorseBuild) and vanilla's closest item-places-a-
        // block case (ItemDryGrass) both use.
        handHandling = EnumHandHandling.PreventDefaultAction;
        if (world.Side != EnumAppSide.Server) return;

        // Land claims, at the PLACEMENT cell rather than the clicked one — building just outside someone's claim
        // while aiming at a block inside it is legal, and vanilla tests the same cell. This is not belt-and-braces
        // even though Block.CanPlaceBlock repeats it below: it is the seam that reports the SPECIFIC refusal to the
        // player (landclaimed / playerdead / in spectator or guest mode, each with its own message, where
        // CanPlaceBlock collapses all of them to "claimed"), it is what the server's CanPlaceOrBreakBlock event hangs
        // off — so third-party protection mods can veto our placement at all — and it refuses BEFORE anything is
        // touched. TryAccess messages the player and re-syncs the slot and block itself, so a bare return is the
        // complete refusal; adding our own message here would double it.
        if (!world.Claims.TryAccess(byPlayer, placeSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return;

        IServerPlayer? serverPlayer = byPlayer as IServerPlayer;

        // Lava. Vanilla refuses to put ANY block into a lava cell outside creative — but that test lives in
        // ClientMain.OnPlayerTryPlace INSIDE the block-stack branch, so no item in the game ever reaches it and every
        // vanilla item that lays a block down (ItemDryGrass included) skips it. Reproduced here because this block is
        // FUEL (750 C / 42 s, halflogslab.json) and would otherwise be the one wooden slab a player can lay inside
        // lava. Read from the FLUID layer EXPLICITLY: the default layer answers with the solid block whenever there
        // is one, and lava carries no selection box, so the aim passes through it and the cell it fills is exactly
        // the kind the default read would call empty.
        if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative
            && world.BlockAccessor.GetBlock(placeSel.Position, BlockLayersAccess.Fluid).LiquidCode == "lava")
        {
            serverPlayer?.SendIngameError("toohottoplacehere", Lang.GetL(serverPlayer.LanguageCode, "placefailure-toohottoplacehere"));
            return;
        }

        // TryPlaceBlock rather than CanPlaceBlock + DoPlaceBlock by hand: it IS those two in that order, plus the
        // BlockBehavior hooks around them. The block declares no behaviours today, but T05.01 adds WrenchOrientable,
        // and a third-party patch can append one at any time — going through the funnel keeps both working for free.
        // The stack is passed through so it reaches Block.OnBlockPlaced; nothing reads it today (the slab has no
        // block entity, the species is its variant), but a future one could not be given it retroactively.
        string failureCode = "";
        if (!slab.TryPlaceBlock(world, byPlayer, slot.Itemstack, placeSel, ref failureCode))
        {
            // Vanilla turns this code into the red overlay itself, but only on the block path (the client composes
            // Lang.Get("placefailure-" + code) after OnBlockBuild fails). Nothing does it for an item, so a silent
            // refusal would read as a broken block. Localised in the RECIPIENT's language, not the server's.
            if (failureCode.Length > 0 && failureCode != "__ignore__" && serverPlayer != null)
                serverPlayer.SendIngameError(failureCode, Lang.GetL(serverPlayer.LanguageCode, "placefailure-" + failureCode));
            return;
        }

        // SetBlock does not do this; vanilla's place path calls it explicitly on both sides for the same reason.
        world.BlockAccessor.TriggerNeighbourBlockUpdate(placeSel.Position);

        // Read off the PLACED block, so retuning the asset retunes this, and taken as the whole SoundAttributes
        // rather than its bare path — that struct carries the sound's own range, pitch and volume, which the raw
        // AssetLocation overload (used elsewhere in this mod for its own one-off clips) would replace with
        // hardcoded ones. dualCallByPlayer is null ON PURPOSE: only the server ran this placement, so the placer has
        // no local copy to pair with and must be included in the broadcast. -0.5 puts it at the block's floor, as
        // vanilla's own item-places-a-block cases do.
        if (slab.Sounds != null) world.PlaySoundAt(slab.Sounds.Place, placeSel.Position, -0.5, null);

        // Exactly one, only now that the block is really in the world, and never in creative. TakeOut rather than
        // StackSize-- because it nulls the stack on the last one instead of leaving a zero-size stack behind that
        // ItemSlot.Empty would still call non-empty; MarkDirty because TakeOut only notifies the inventory on that
        // last-one path.
        if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            slot.TakeOut(1);
            slot.MarkDirty();
        }
    }

#if IW_DEBUG
    /// <summary>
    /// Prove, in one startup line, the ONE thing about placement that a machine can decide without a mouse: that a
    /// SPECIES-LESS half-log has a block to become (T04.02). Called from the mod system beside
    /// <see cref="HalfLogPlacement.SelfCheck"/> — server at <c>GameReady</c>, client at <c>LevelFinalize</c>, both
    /// because <see cref="defaultWood"/> is resolved per side from that side's own species index and the two must
    /// agree. Stripped from a shipped build.
    ///
    /// <para>Everything else in the Definition of Done (a slab appears, the species survives, a claim refuses, the
    /// stack does not shrink in creative) is a click, and belongs to T04.05. This one is not: the default species is
    /// the only placement input that is CHOSEN rather than read off the click, it is chosen from an index that a
    /// wood mod changes, and getting it wrong fails silently — every bare stack would simply do nothing, which reads
    /// as "the gesture is broken" rather than as "that species has no block".
    ///
    /// <para>Runs the real path (<see cref="SpeciesToPlace"/> on an actual bare stack), not a restatement of it, and
    /// resolves through the block REGISTRY, so it also catches the default drifting away from a registered species.
    /// <c>down-we-split</c> is the probe state because it exists for every species — it is the one the asset exposes
    /// to creative and the handbook.
    /// </summary>
    internal static void SelfCheck(ICoreAPI api)
    {
        ItemHalfLog? halflog = null;
        foreach (Item item in api.World.Items) if (item is ItemHalfLog found) { halflog = found; break; }
        if (halflog == null)
        {
            api.Logger.Warning("[immersivewoodworking] Half-log placement default-species check skipped: the half-log item is not registered.");
            return;
        }

        // A bare stack is exactly what the handbook page and an unparseable log's chop produce.
        string? resolved = halflog.SpeciesToPlace(new ItemStack(halflog));
        Block? slab = resolved == null ? null : BlockHalfLogSlab.Get(api.World, resolved, BlockHalfLogSlab.SkinBarked, "down", "we", "split");

        if (slab == null)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log placement default species FAILED: a species-less half-log resolves to '{0}', which has no registered slab, so a bare half-log places nothing. Preferred default is '{1}'; the species index holds {2} species.",
                resolved ?? "(none)", PreferredDefaultWood, api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()?.WoodDomains.Count ?? 0);
        }
        else
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log placement: a species-less half-log places '{0}'{1}.",
                slab.Code, resolved == PreferredDefaultWood ? "" : " (the preferred default '" + PreferredDefaultWood + "' is not in this world's species index)");
        }
    }
#endif

    // ───────────────────────────── per-species held / GUI / drop render ─────────────────────────────

    /// <summary>
    /// Render the half-log with its stored species' textures in every item surface — in hand (1st/3rd person), the
    /// GUI/inventory/creative icon and as a dropped item — by swapping <paramref name="renderinfo"/>.ModelRef to a
    /// per-species GPU mesh (<see cref="BuildHalfLogMesh"/>). Same idiom as vanilla BlockShapeFromAttributes /
    /// <see cref="BlockChoppingBlock.OnBeforeRender"/>: lazily build + upload one MultiTextureMeshRef per species into a
    /// process-wide cache (freed in <see cref="OnUnloaded"/>). A bare stack (no recorded species) keeps the item's own
    /// default-oak mesh + JSON transforms from base. Runs on the main render thread, where capi.Tesselator and the GPU
    /// upload are both legal (the engine drives item OnBeforeRender there).
    /// </summary>
    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);

        string? wood = itemstack.Attributes.GetString("wood", null);
        // Bare stack → keep base's default (oak) mesh + the default transforms. That covers the bare-but-DEBARKED
        // stack too (only reachable from a log whose code shape LogBlockSpecies cannot parse): with no species there
        // is no texture family to pick from in the first place, so the flag has nothing to steer.
        if (wood == null) return;

        string woodDomain = itemstack.Attributes.GetString("woodDomain", "game");
        bool debarked = IsDebarked(itemstack);
        string key = woodDomain + ":" + wood + (debarked ? ":debarked" : "");

        Dictionary<string, MultiTextureMeshRef> cache =
            ObjectCacheUtil.GetOrCreate(capi, HeldMeshCacheKey, () => new Dictionary<string, MultiTextureMeshRef>());

        if (!cache.TryGetValue(key, out MultiTextureMeshRef? meshref))
        {
            MeshData? mesh = BuildHalfLogMesh(capi, wood, woodDomain, debarked);
            if (mesh == null) return; // shape missing → leave base's default mesh; retry on the next render
            meshref = capi.Render.UploadMultiTextureMesh(mesh);
            cache[key] = meshref;
        }

        // Same geometry as the default item mesh, so base's per-target transform (gui/ground/hand) already fits;
        // we only swap which textured mesh is drawn.
        renderinfo.ModelRef = meshref;
    }

    /// <summary>Free the per-species GPU meshes built by <see cref="OnBeforeRender"/> on world unload/reload so the
    /// MultiTextureMeshRefs don't leak (TryGet → Dispose each → Delete). Client-only GPU resource.</summary>
    public override void OnUnloaded(ICoreAPI api)
    {
        base.OnUnloaded(api);
        if (api is not ICoreClientAPI capi) return;

        Dictionary<string, MultiTextureMeshRef>? cache = ObjectCacheUtil.TryGet<Dictionary<string, MultiTextureMeshRef>>(capi, HeldMeshCacheKey);
        if (cache != null)
        {
            foreach (MultiTextureMeshRef meshref in cache.Values) meshref.Dispose();
            ObjectCacheUtil.Delete(capi, HeldMeshCacheKey);
        }
    }

    // Process-wide cache of the parsed half-log Shape. Species-INDEPENDENT geometry (textures are bound per stack via
    // LogTexSource), so one parsed Shape is shared across every species — exactly how the chopping block shares its
    // stand shape. Shape.TryGet re-parses the JSON each call, so this avoids re-deserializing per species; the
    // unsynchronized check-then-set is harmless (a rare first-use double parse yields identical data).
    static VSShape? sharedShape;
    static readonly AssetLocation ShapePath = new AssetLocation("immersivewoodworking", "shapes/item/halflog.json");

    /// <summary>
    /// Build the species-textured half-log mesh — the split-log shape tesselated with a <see cref="LogTexSource"/>
    /// bound to the species' round sides, tree-ring end-grain (caps) and debarked inner (the bare split face).
    /// Each texture resolves cross-domain (<see cref="WoodTextures"/>: the species' own mod domain first, then game,
    /// then the species' log block), so a modded wood finds its own textures. Returns null only if the shape can't be
    /// loaded (caller falls back to the item's own default-oak mesh, so it's never invisible). Client-side, MAIN thread.
    ///
    /// <para><paramref name="debarked"/> (AuditFixPlan T04.01) swaps TWO of the three families, not one. The round
    /// side obviously loses its bark. The END CAPS move too, onto vanilla's separate <c>treetrunk/debarked</c> — the
    /// tree-ring texture WITHOUT the ring of bark around its rim — because leaving them on plain <c>treetrunk</c>
    /// would draw bark back onto the cut face and undo the strip on a different surface. This is not a new mapping:
    /// it is exactly the pair <see cref="LogTexSource.ForLog"/> already uses for a whole debarked log on the sawhorse
    /// and the sawmill, so a half of a stripped log now looks like half of the log it came off. The split face is the
    /// bare inner wood either way — that is what splitting reveals, bark or no bark.
    /// </summary>
    internal static MeshData? BuildHalfLogMesh(ICoreClientAPI capi, string wood, string woodDomain, bool debarked)
    {
        VSShape? shape = sharedShape ??= VSShape.TryGet(capi, ShapePath);
        if (shape == null) return null;

        capi.Tesselator.TesselateShape("immersivewoodworking:halflog", shape, out MeshData mesh,
            LogTexSource.ForSpecies(capi, woodDomain, wood, debarked, woodDomain + ":" + wood + (debarked ? " (debarked)" : "")));
        return mesh;
    }

    // ───────────────────────────── per-species name + single handbook page ─────────────────────────────

    /// <summary>Per-species held name; covers hand / GUI / creative / handbook / dropped. A bare stack (no species)
    /// shows the generic name.</summary>
    public override string GetHeldItemName(ItemStack itemStack)
    {
        string? wood = itemStack.Attributes.GetString("wood", null);
        // No species → the generic name, and that stays ONE string for both surface states. A bare stack only exists
        // where the source log's code shape could not be parsed at all; naming that case "debarked half-log" would
        // spend a lang key per language on a stack the player is not supposed to be able to hold.
        if (wood == null) return Lang.Get("immersivewoodworking:item-halflog");
        return HalfLogName(wood, itemStack.Attributes.GetString("woodDomain", "game"), IsDebarked(itemStack));
    }

    /// <summary>"&lt;Species&gt; half-log" from the per-language template (item-halflog-name, {0}=species), or its
    /// debarked twin (item-halflog-debarked-name) when the bark was already off (AuditFixPlan T04.01). Two names for
    /// two things, exactly as vanilla separates <c>log</c> from <c>debarkedlog</c>: they no longer look alike, they no
    /// longer place the same block, and a shared name would leave a player with two visibly different stacks that
    /// refuse to merge and cannot be told apart in a tooltip. The resolver behind both lives in
    /// <see cref="WoodName"/> — the chopping block and the half-log slab name themselves through the same one, each
    /// with its own template key.</summary>
    static string HalfLogName(string wood, string woodDomain, bool debarked)
        => WoodName.InTemplate(
            debarked ? "immersivewoodworking:item-halflog-debarked-name" : "immersivewoodworking:item-halflog-name",
            wood, woodDomain);

    /// <summary>Collapse the handbook to ONE generic "Half-log" page (a single bare stack) instead of one per species,
    /// so the woodworking overview can deep-link to a stable item-immersivewoodworking:halflog page — exactly like the
    /// chopping block. The creative menu still lists every species (<see cref="CollectibleObject.CreativeInventoryStacks"/> set in
    /// <see cref="BuildCreativeStacks"/>); the per-species look/name still resolves everywhere else (a bare handbook
    /// stack simply lacks the wood attribute → default oak + generic name).
    ///
    /// <para>UNCONDITIONAL, and that is what keeps this page alive now that <see cref="BuildCreativeStacks"/> clears
    /// <see cref="CollectibleObject.CreativeInventoryTabs"/>: the base implementation returns null for anything in no
    /// creative tab (vsapi Collectible.cs), and this override never consults either field. Since AuditFixPlan T04.04
    /// this is also the ONLY handbook page the half-log has anywhere — the placed <see cref="BlockHalfLogSlab"/> is
    /// out of both the menu and the book — so the prose in <c>halflog-handbook-text</c> is where "it is also a
    /// building block" gets said.</summary>
    public override List<ItemStack> GetHandBookStacks(ICoreClientAPI capi)
    {
        return new List<ItemStack> { new ItemStack(this) };
    }

    /// <summary>
    /// Generate one creative-inventory stack per wood species (each carrying the wood + domain attribute) and assign
    /// them to <see cref="CollectibleObject.CreativeInventoryStacks"/>, so the creative menu shows every species.
    /// Mirrors <see cref="BlockChoppingBlock"/>.BuildCreativeStacks (Type=Item here). If species discovery turns up
    /// nothing, the JSON <c>creativeinventory</c> (the bare item) is left as the fallback.
    ///
    /// <para><b>Two tabs, general and items</b> (AuditFixPlan T04.04). Twenty of the mod's twenty-one itemtypes are in
    /// <c>items</c>; the half-log was the only one a player could not find there. The pair is duplicated in the JSON
    /// fallback and has to stay in step with it.
    ///
    /// <para><b>And the JSON field is CLEARED on success</b>, the other half of that task. The two
    /// sources are ADDED, not overridden: InventoryPlayerCreative.GatherTabStacks walks <c>CreativeInventoryTabs</c>
    /// and <c>CreativeInventoryStacks</c> in two independent branches with no <c>else</c> and no dedupe. So the JSON
    /// <c>creativeinventory: ["*"]</c> kept minting one extra entry off the BARE item — a thirteenth "Half-log" with
    /// no species next to the twelve named ones on vanilla, a fifty-eighth on a full modpack — and a player who took
    /// it got a species-less half-log that neither middle-click nor breaking would ever hand back. Clearing happens
    /// only INSIDE the success branch, and that ordering is load-bearing: the field is filled at InitItem, before any
    /// <c>OnLoaded</c>, so clearing it unconditionally would erase the half-log from creative entirely on a world
    /// where discovery found nothing. An empty array rather than null, so nothing downstream has to be null-safe.
    /// The handbook page does not ride on this field at all — see <see cref="GetHandBookStacks"/>.
    /// </summary>
    void BuildCreativeStacks(ICoreAPI api)
    {
        IReadOnlyList<(string wood, string domain)> woods = modSystem.WoodSpecies;
        if (woods.Count == 0) return; // keep the JSON creativeinventory fallback if discovery failed

        List<JsonItemStack> stacks = new List<JsonItemStack>(woods.Count);
        foreach ((string wood, string domain) in woods)
        {
            JsonItemStack jstack = new JsonItemStack
            {
                Code = Code,
                Type = EnumItemClass.Item,
                Attributes = new JsonObject(new JObject { ["wood"] = wood, ["woodDomain"] = domain })
            };
            if (jstack.Resolve(api.World, "immersivewoodworking half-log species")) stacks.Add(jstack);
        }

        if (stacks.Count > 0)
        {
            CreativeInventoryStacks = new CreativeTabAndStackList[]
            {
                new CreativeTabAndStackList { Stacks = stacks.ToArray(), Tabs = new string[] { "general", "items" } }
            };
            CreativeInventoryTabs = System.Array.Empty<string>();
        }
    }
}
