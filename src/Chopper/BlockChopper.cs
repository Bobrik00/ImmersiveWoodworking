using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

/// <summary>
/// Master block of the automated CHOPPER — the single placed <c>chopper-frame</c>, the mech-powered upgrade tier
/// over the hand chopping block. It carries the <see cref="BlockEntityChopper"/> that owns all machine state; the
/// other cells of its footprint are invisible <see cref="BlockChopperGhost"/> fillers (placed in T03.03) that route
/// every click / break back here, so the multiblock behaves as one object — the exact shape of the neighbouring
/// automated sawmill (<see cref="BlockSawmill"/>) and vanilla's pulverizer.
///
/// The footprint gate (<see cref="CanPlaceBlock"/>) + ghost placement (<see cref="OnBlockPlaced"/>, T03.03), the
/// per-cell collision routing (T03.02) and the full teardown (T03.05, <see cref="OnBlockBroken"/>/<see cref="GetDrops"/>
/// → <see cref="BlockEntityChopper.RemoveFootprintGhosts"/>/<see cref="BlockEntityChopper.AppendPartDrops"/>) are
/// wired. The state-dependent interaction-help (T08.01) is wired here (<see cref="OnLoaded"/> +
/// <see cref="SelectInteractions"/>), reading the master BE's assembly / I/O state. Orientation (the <c>side</c>
/// variantgroup + the vanilla HorizontalOrientable behavior) lives in <c>frame.json</c>, and it runs this
/// <see cref="CanPlaceBlock"/>/<see cref="OnBlockPlaced"/> on the oriented variant (the look-derived facing), so no C#
/// is needed for it.
/// </summary>
public class BlockChopper : Block
{
    // ───────────────────────── Interaction help (T08.01) ─────────────────────────
    //
    // The hint shown when looking at the machine depends on the master BE's assembly + I/O state (mirrors the sawmill /
    // the hand chopping block). The heavy stack lists (every head metal, every choppable input) are resolved ONCE
    // client-side in OnLoaded and cached process-wide, then GetPlacedBlockInteractionHelp PICKS the matching set per
    // state. A look at a ghost-filler cell routes here too (BlockChopperGhost forwards to this master block with the
    // selection at the master cell), so any cell shows the same help. Direct mirror of BlockSawmill. Lang keys
    // blockhelp-chopper-*.

    // Item icons for the part-install hint — the mandatory mechanism parts, by code. Drive + arm are mod ITEMS; the
    // clamp-bed is the hand `choppingblock` BLOCK (2026-07-04 — reuse the block, not a bespoke part), resolved as a
    // block. MissingMandatoryParts() returns these three codes (chopperdrive / chopperarm / choppingblock); StacksFor
    // maps each to its cached icon. Resolved once in OnLoaded (client).
    static readonly string[] PartHintItemCodes = { "chopperdrive", "chopperarm" };
    readonly Dictionary<string, ItemStack> partStacks = new Dictionary<string, ItemStack>();

    // The head metal variants (chopperhead-*) for the "install head" hint, and the choppable inputs for the "place
    // log" hint (the same accept-list the hand chopping block advertises). Resolved once, cached process-wide.
    // headStacks is config-free and resolved in OnLoaded; the choppable list reads the config and is resolved on first
    // use instead — see ChoppableStacks() (AuditFixPlan T02.05 / F-L3-10). null there = not built yet.
    ItemStack[] headStacks = System.Array.Empty<ItemStack>();
    ItemStack[]? choppableStacks;

    // State-independent interactions, pre-built in OnLoaded (their icons don't vary): the Ctrl+RMB log removal (take an
    // untouched log back / discard a begun chop) and the Ctrl+RMB head removal (shown only when no log is loaded — a
    // loaded log takes precedence on that gesture, matching OnInteract). (No "take product" hint — the product is
    // ejected forward out of the machine on completion, pulverizer-style, never stored in a slot to take.)
    WorldInteraction? takeLogInteraction;
    WorldInteraction? discardLogInteraction;
    WorldInteraction? removeInteraction;

    /// <summary>Resolve the cached interaction-help stacks (client-only). Mirrors <see cref="BlockSawmill.OnLoaded"/>:
    /// the part / head / choppable-input icon lists are resolved once here and reused per state in
    /// <see cref="GetPlacedBlockInteractionHelp"/>.</summary>
    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        if (api is not ICoreClientAPI capi) return; // interaction help is a purely client-side hint

        // Part icons: drive + arm are mod ITEMS; the clamp-bed is the hand `choppingblock` BLOCK (2026-07-04).
        foreach (string code in PartHintItemCodes)
        {
            Item? it = api.World.GetItem(new AssetLocation("immersivewoodworking", code));
            if (it != null) partStacks[code] = new ItemStack(it);
        }
        Block? bed = api.World.GetBlock(new AssetLocation("immersivewoodworking", "choppingblock"));
        if (bed != null) partStacks["choppingblock"] = new ItemStack(bed);

        // Head icons: every chopperhead-{metal} handbook stack, so the "install head" hint shows the metals.
        // GetHandBookStacks wrapped in try/catch (a modded override could throw during block load) — the sawmill idiom.
        headStacks = ObjectCacheUtil.GetOrCreate(api, "iwChopperHeadStacks", () =>
        {
            List<ItemStack> list = new List<ItemStack>();
            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                if (obj?.Code == null || obj.FirstCodePart() != "chopperhead") continue;
                List<ItemStack>? stacks;
                try { stacks = obj.GetHandBookStacks(capi); } catch { continue; }
                if (stacks != null) list.AddRange(stacks);
            }
            return list.ToArray();
        });

        // The choppable-input icons are NOT resolved here — they read the config, and on the client the server's
        // config has not arrived yet at OnLoaded. See ChoppableStacks() (AuditFixPlan T02.05 / F-L3-10).

        // Ctrl+RMB on a loaded log: take it back whole (within the grace) / discard it (past the grace) — the
        // progress-dependent split mirrors the sawmill / sawhorse. Picked in SelectInteractions by the BE's
        // LogRecoverable state. The DISCARD half is registered but never shown on the shipped default, where the split
        // has no reachable second side (BlockEntityChopper.LogRecoverable carries the arithmetic); it earns its keep on
        // a server running ChopperStrikes > 1, so both interactions are built here unconditionally.
        takeLogInteraction = new WorldInteraction
        {
            ActionLangCode = "immersivewoodworking:blockhelp-chopper-takelog",
            HotKeyCode = "ctrl",
            MouseButton = EnumMouseButton.Right
        };
        discardLogInteraction = new WorldInteraction
        {
            ActionLangCode = "immersivewoodworking:blockhelp-chopper-discardlog",
            HotKeyCode = "ctrl",
            MouseButton = EnumMouseButton.Right
        };
        removeInteraction = new WorldInteraction
        {
            ActionLangCode = "immersivewoodworking:blockhelp-chopper-removehead",
            HotKeyCode = "ctrl",
            MouseButton = EnumMouseButton.Right
        };
    }

    /// <summary>Pick the interaction-help set by the master BE's state and append the block's own (base) help, mirroring
    /// <see cref="BlockSawmill.GetPlacedBlockInteractionHelp"/>. Client-side; reads the server-synced BE state. A ghost
    /// cell forwards here with the selection at the master cell (see <see cref="BlockChopperGhost"/>).</summary>
    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        return SelectInteractions(world, selection).Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
    }

    /// <summary>
    /// The interaction-help set matching the chopper's current state:
    ///  • not yet assembled → install the next mandatory part (drive / arm / bed-choppingblock), then the head (after
    ///    the arm);
    ///  • assembled, empty input → place a choppable log (the product is ejected forward on completion, not taken);
    ///  • Ctrl+RMB: with a log loaded → take it back (untouched) / discard it (begun); with no log → remove the head
    ///    when one is installed (mechanism parts are committed).
    /// Empty array when there's no chopper BE at the selection. Direct mirror of <see cref="BlockSawmill.SelectInteractions"/>.
    /// </summary>
    WorldInteraction[] SelectInteractions(IWorldAccessor world, BlockSelection selection)
    {
        if (selection == null || world.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityChopper be)
        {
            return System.Array.Empty<WorldInteraction>();
        }

        var list = new List<WorldInteraction>();

        if (!be.IsComplete)
        {
            // Assembly: install the next mandatory part, or — once all three are in — the head (it sits in the arm).
            string[] missing = be.MissingMandatoryParts();
            if (missing.Length > 0)
            {
                list.Add(new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-chopper-installpart",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = StacksFor(missing)
                });
            }
            else if (!be.HasHead)
            {
                list.Add(new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-chopper-installhead",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = headStacks
                });
            }
        }
        else if (be.InputEmpty)
        {
            // Assembled and empty: feed a log. (No "take product" hint — the product is ejected forward out of the
            // machine on completion, pulverizer-style, not stored in a slot to take.)
            list.Add(new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-chopper-putlog",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = ChoppableStacks()
            });
        }

        // The Ctrl+RMB "remove" gesture, state-dependent to match OnInteract: a LOADED LOG comes off first — taken back
        // WHOLE while under the recovery grace, scrapped once past the grace into the chop (be.LogRecoverable) — and
        // only with NO log does the head come off (whenever one is installed; the mechanism parts are committed,
        // recoverable only by breaking the frame). On the shipped default this ternary always picks takeLogInteraction:
        // LogRecoverable cannot go false at ChopperStrikes = 1 (BlockEntityChopper.LogRecoverable has the arithmetic).
        // Left branching so a non-default server gets the right hint.
        if (!be.InputEmpty && takeLogInteraction != null && discardLogInteraction != null)
        {
            list.Add(be.LogRecoverable ? takeLogInteraction : discardLogInteraction);
        }
        else if (be.InputEmpty && be.HasHead && removeInteraction != null)
        {
            list.Add(removeInteraction);
        }

        return list.ToArray();
    }

    /// <summary>The choppable-input icons for the "feed a log" hint — the SAME accept-list the hand chopping block
    /// advertises (<see cref="BlockEntityChoppingBlock.IsChoppable"/>) — resolved on FIRST USE rather than in
    /// <see cref="OnLoaded"/>, then cached process-wide and memoized here.
    ///
    /// The lateness IS the fix (AuditFixPlan T02.05 / F-L3-10), the same one the hand block's
    /// <c>BlockChoppingBlock.EmptyInteractions</c> makes: the list depends on
    /// <see cref="ImmersiveWoodworkingConfig.AllowFirewoodToSticks"/>, and on the client the server's config lands
    /// AFTER Block.OnLoaded — so a list built there advertised the client's own default forever, offering firewood on a
    /// server that forbids it. By the first help query the config is in.
    ///
    /// The hand block caches its copy only WRAPPED in a WorldInteraction (iwChoppingBlockEmptyInteractions), so this is
    /// a separate raw-stack cache under a chopper key using the SAME filter (unlike the sawmill, which does reuse the
    /// sawhorse's iwSawhorseSawableStacks). Cheap CouldBeChoppable code-gate FIRST, then GetHandBookStacks in
    /// try/catch, then the precise IsChoppable — the hand block's exact builder (memory
    /// vs-handbook-desc-and-interaction-help: gate BEFORE GetHandBookStacks or a modded item's throw crashes mod load).
    /// Our own chopping-block variants are skipped so the hint isn't cluttered by 12 of them (feeding one in is allowed
    /// but pointless to suggest — the same exclusion the hand block makes). Client-only.</summary>
    ItemStack[] ChoppableStacks()
    {
        if (choppableStacks != null) return choppableStacks;
        if (api is not ICoreClientAPI capi) return System.Array.Empty<ItemStack>();

        return choppableStacks = ObjectCacheUtil.GetOrCreate(api, "iwChopperChoppableStacks", () =>
        {
            // The server-synced config, read HERE (first help query) rather than at load — see the method doc.
            ImmersiveWoodworkingConfig config = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().Config;
            List<ItemStack> list = new List<ItemStack>();
            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                if (obj is BlockChoppingBlock) continue;
                if (!BlockEntityChoppingBlock.CouldBeChoppable(obj)) continue;
                List<ItemStack>? stacks;
                try { stacks = obj.GetHandBookStacks(capi); } catch { continue; }
                if (stacks == null) continue;
                foreach (ItemStack stack in stacks) if (BlockEntityChoppingBlock.IsChoppable(stack, config)) list.Add(stack);
            }
            return list.ToArray();
        });
    }

    /// <summary>The cached part icon stacks for the given codes (skipping any that didn't resolve), for the
    /// "install part" hint.</summary>
    ItemStack[] StacksFor(string[] codes)
    {
        var list = new List<ItemStack>();
        foreach (string c in codes) if (partStacks.TryGetValue(c, out ItemStack? s)) list.Add(s);
        return list.ToArray();
    }

    // ───────────────────────── Hover name + info (E08) ─────────────────────────

    /// <summary>
    /// The hover NAME. Once the machine is assembled it presents as the finished "Дровокол" (<c>block-chopper</c>)
    /// rather than the "Рама дровокола" frame it was placed as — the block <see cref="Block.Code"/> stays
    /// <c>chopper-frame-*</c> even when built, so the base name (from <see cref="OnPickBlock"/> → the frame stack)
    /// would read "frame" forever. The ghost-filler cells route their name here too
    /// (<see cref="BlockChopperGhost.GetPlacedBlockName"/>), so every cell of the multiblock shows the same name.
    /// Falls back to the base frame name before the BE exists or while still unassembled.
    /// </summary>
    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityChopper be && be.IsComplete)
        {
            return Lang.Get("immersivewoodworking:block-chopper");
        }
        return base.GetPlacedBlockName(world, pos);
    }

    /// <summary>
    /// The hover INFO. The base <see cref="Block.GetPlacedBlockInfo"/> appends the static block description
    /// (<c>blockdesc-chopper-frame-*</c> = "…Установите в неё детали, чтобы собрать станок.") after the BE's live
    /// <see cref="BlockEntityChopper.GetBlockInfo"/>. On the placed machine that line is redundant when unassembled
    /// (GetBlockInfo already lists the missing parts) and plain WRONG once assembled — and ONLY the master cell carries
    /// it (the ghost fillers resolve <c>blockdesc-chopper-ghost</c>, which doesn't exist), so the bed cell (the master,
    /// at the footprint origin) read differently from the arms/gears. Strip that one auto-appended line so every
    /// footprint cell reads the same live state. The handbook and the held-frame tooltip keep the full description —
    /// they resolve it via <c>Collectible.GetItemDescText</c>, not this method, so the assemble how-to stays where it
    /// belongs.
    /// </summary>
    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        string info = base.GetPlacedBlockInfo(world, pos, forPlayer);
        if (Code == null) return info;

        // Recompute the EXACT resolved description string the base appended (the same lang code it uses — see
        // Block.GetPlacedBlockInfo), then remove that substring. Recomputing the resolved value keeps this robust
        // across languages, and the guards make it a safe no-op when the desc isn't present (a missing key, or an
        // off-master query).
        string descCode = Code.Domain + ":" + ItemClass.ToString().ToLowerInvariant() + "desc-" + Code.Path;
        string desc = Lang.GetMatching(descCode);
        if (desc != descCode && desc.Length > 0)
        {
            int idx = info.LastIndexOf(desc, System.StringComparison.Ordinal);
            if (idx >= 0) info = info.Remove(idx, desc.Length).TrimEnd();
        }
        return info;
    }

    /// <summary>
    /// Footprint gate (T03.03): the frame may only be placed where ALL footprint cells are free. Check the master
    /// cell with the base logic, then each non-master cell from the shared footprint table
    /// (<see cref="BlockEntityChopper.GetCells"/>) via a cloned selection — exactly how vanilla
    /// <c>BlockPulverizer.CanPlaceBlock</c> checks its up-cell. The footprint has NO -Y pit cell (every cell is
    /// at/above the master), so the frame places STANDARD on flat ground — it only refuses if the cells above/around
    /// aren't free. On a blocked cell <c>base.CanPlaceBlock</c> has already set a localized
    /// <paramref name="failureCode"/> (e.g. "notreplaceable" / "entityintersecting"), so the player gets a sensible
    /// message. Facing comes from the to-be-placed block's "side" variant (T03.04): the HorizontalOrientable behavior
    /// resolves the look-derived variant and calls this on THAT block, so the footprint checked here matches the one
    /// that will actually be placed. Falls back to NORTH for a missing/odd variant.
    /// </summary>
    public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
    {
        if (!base.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false; // master cell itself

        BlockFacing facing = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;
        BlockPos[] cells = BlockEntityChopper.GetCells(blockSel.Position, facing);
        for (int i = 1; i < cells.Length; i++) // skip [0] = master (already checked above)
        {
            BlockSelection bs = blockSel.Clone();
            bs.Position = cells[i];
            if (!base.CanPlaceBlock(world, byPlayer, bs, ref failureCode)) return false; // base sets the localized failure
        }
        return true;
    }

    /// <summary>Once the frame is set, fill its non-master footprint cells with ghost-fillers, each pointing back at
    /// this master (<see cref="BlockEntityChopper.PlaceFootprintGhosts"/> — server-only; mirrors
    /// <c>BlockPulverizer.WasPlaced</c> → <c>PlaceFakeBlock</c>). <see cref="CanPlaceBlock"/> has already guaranteed
    /// the cells are free, so the fillers land in clear space.</summary>
    public override void OnBlockPlaced(IWorldAccessor world, BlockPos blockPos, ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(world, blockPos, byItemStack);
        if (world.BlockAccessor.GetBlockEntity(blockPos) is BlockEntityChopper be)
        {
            be.PlaceFootprintGhosts();
        }
    }

    /// <summary>Route a right-click on the frame to the master BE (mirrors <see cref="BlockSawmill.OnBlockInteractStart"/>
    /// / <c>BlockPulverizer.OnBlockInteractStart</c>). <see cref="BlockEntityChopper.OnInteract"/> handles the whole
    /// interaction — the part-install (TryAddPart, T03.07) and the manual log I/O (T05.02).</summary>
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityChopper be)
        {
            return be.OnInteract(byPlayer, blockSel);
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    // Per-cell collision / selection fitted to the multiblock footprint (T03.02 / T03.06). The master cell returns its
    // own [0,1]-clamped slice from the shared footprint table on the BE, so re-shaping the machine is a one-table edit
    // and the structure stays tunnel-free (memory vs-multiblock-collision-broadphase). The ghost-filler cells route
    // the SAME way via Principal (BlockChopperGhost), so all footprint cells collide from one source. Falls back to
    // the JSON cube before the BE exists / for an off-footprint query. The per-cell boxes are fitted to the authored
    // dropmaul model (AutoChopperPlan/scan_collision.py), and the selection outline tracks the same boxes.
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BlockEntityChopper be && be.GetCellCollisionBoxes(pos) is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetCollisionBoxes(blockAccessor, pos);
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BlockEntityChopper be && be.GetCellSelectionBoxes(pos) is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetSelectionBoxes(blockAccessor, pos);
    }

    /// <summary>
    /// Teardown, space-release half (T03.05): remove the ghost-fillers BEFORE the frame's own removal, so breaking any
    /// cell frees the whole footprint and never leaves orphaned fillers. (Removal by any OTHER channel — an explosion,
    /// worldedit, a third-party <c>SetBlock</c> — is caught by <see cref="OnBlockRemoved"/> below instead. A filler
    /// orphaned anyway — its chunk was unloaded when teardown ran, so it was never reached — can be mined away by the
    /// orphan guard in <see cref="BlockChopperGhost.OnBlockBroken"/>. That guard covers a DANGLING Principal only; a
    /// filler whose Principal is null still refuses to break, matching vanilla.) The ghost's <see cref="BlockChopperGhost.OnBlockBroken"/> routes a player break on a
    /// filler cell back to this master, so this runs exactly once per multiblock. The DROP half of teardown (the
    /// installed parts + head) rides the standard break path through <see cref="GetDrops"/> — see there.
    /// <c>base.OnBlockBroken</c> → <c>SpawnDropsAndRemoveBlock</c> first runs the BE's own <c>OnBlockBroken</c> (the
    /// seam where E05's input log will be released), then calls <see cref="GetDrops"/> while the BE is still alive, then
    /// removes the block. Direct mirror of <see cref="BlockSawmill.OnBlockBroken"/>.
    /// </summary>
    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityChopper be)
        {
            be.RemoveFootprintGhosts(); // must precede base — base removes this BE
        }
        base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
    }

    /// <summary>
    /// Teardown, space-release half — the UNIVERSAL seam. <see cref="OnBlockBroken"/> above only covers a survival
    /// break; the engine removes a block through several channels that never enter it. An EXPLOSION runs
    /// <c>Block.OnBlockExploded</c> (<c>vsapi/.../Block.cs:2523</c>), which stages
    /// <c>BulkBlockAccessor.SetBlock(0, pos)</c>, rolls the drops, and then calls only the BLOCK ENTITY's
    /// <c>OnBlockBroken()</c> — never this Block class's. Worldedit / schematics / other mods likewise just
    /// <c>SetBlock</c>. All of them funnel through <c>Block.OnBlockRemoved</c>, whose engine contract is literally
    /// "always called when a block has been removed through whatever method, except during worldgen or via
    /// ExchangeBlock()" — including the bulk path, where <c>BlockAccessorRelaxedBulkUpdate.Commit</c> calls
    /// <c>OnBlockRemoved</c> on the OLD block of every staged cell whose id actually CHANGES (same-id rewrites and
    /// exchange-only updates are skipped by its own guard). Without this, blowing up the frame orphaned the
    /// whole ghost footprint: invisible, solid, and (because <see cref="BlockChopperGhost.OnBlockBroken"/> routed its
    /// break to a Principal that is now air, then deliberately skipped base) impossible to mine away.
    /// Same hook as vanilla <c>BlockLargeGear3m.OnBlockRemoved</c>, which tears its fake-block ring down here too —
    /// but the ORDER is deliberately inverted: vanilla calls base first and then nulls each filler's Principal, while
    /// this must clear the fillers first, because the ownership test that decides which cells are ours reads the very
    /// Principal pointers vanilla discards, and the BE that answers it dies inside base.
    ///
    /// Runs BEFORE base for that reason. The BE is still reachable here even under a bulk commit that already zeroed
    /// the cell's block id — <c>GetBlockEntity</c> reads the chunk's BE map, not its
    /// block-id array, and the BE is only unregistered inside <c>base.OnBlockRemoved</c>. Idempotent, so the retained
    /// call in <see cref="OnBlockBroken"/> (which keeps the ghosts clearing BEFORE drops on the normal path) costs
    /// nothing: by the time base's <c>SetBlock(0)</c> re-enters here, every cell already reads air and the
    /// "is not BlockChopperGhost" guard skips it. Direct mirror of <see cref="BlockSawmill.OnBlockRemoved"/>.
    /// </summary>
    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityChopper be)
        {
            be.RemoveFootprintGhosts(); // must precede base — base unregisters this BE
        }
        base.OnBlockRemoved(world, pos);
    }

    /// <summary>
    /// Teardown, DROP half (T03.05): the frame block itself PLUS every installed part — mirrors
    /// <see cref="BlockSawmill.GetDrops"/> → <c>BEPulverizer.getDrops</c> (frame + parts by flag). The frame stack comes
    /// from <c>base.GetDrops</c>, which the <c>HorizontalOrientable</c> behavior resolves to the canonical
    /// <c>chopper-frame-north</c> (matching the pick / creative normalization in <c>frame.json</c>, and honoring any
    /// JSON drop / <paramref name="dropQuantityMultiplier"/>); the master BE then appends the installed parts
    /// (<see cref="BlockEntityChopper.AppendPartDrops"/>): the mandatory drive / arm by their flags, the clamp-bed as a
    /// whole hand choppingblock, and the head — each a WHOLE item (the metal parts come back intact, never
    /// multiplier-reduced). The base path only calls this once per teardown (the ghost route short-circuits to this
    /// master without a second drop), and <see cref="BlockEntityChopper.AppendPartDrops"/> is a pure read, so a racing /
    /// repeated break can't double the drop. Falls back to the bare frame before the BE exists. Server-side,
    /// non-creative only (the base gates that); the held input log (E05) is released separately by the BE's own
    /// <c>OnBlockBroken</c>.
    /// </summary>
    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
    {
        ItemStack[] frameDrops = base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityChopper be)
        {
            return be.AppendPartDrops(frameDrops);
        }
        return frameDrops;
    }
}
