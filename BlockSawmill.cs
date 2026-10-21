using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

/// <summary>
/// Master block of the automated sawmill — the single placed <c>sawmill-frame</c>, the mech-powered upgrade tier
/// over the hand sawhorses. It carries the <see cref="BlockEntitySawmill"/> that owns all machine state. The other
/// cells of its footprint are invisible <see cref="BlockSawmillGhost"/> fillers (placed in T03.03) that route every
/// click / break back here, so the multiblock behaves as one object — exactly like vanilla's <c>BlockPulverizer</c>
/// + <c>BlockMPMultiblockPulverizer</c>, just with more cells.
///
/// The footprint is the collision volume fitted to the model the user authored — a solid base on the lower layers
/// plus the center saw tower and the -X gear/crank tower (currently 19 cells; the count is whatever
/// <see cref="BlockEntitySawmill.FootprintLocal"/> holds) — and it has NO -Y pit cell: every cell is at/above the
/// master, so the frame places STANDARD on flat ground. <see cref="CanPlaceBlock"/>
/// refuses unless all footprint cells are free, and <see cref="OnBlockPlaced"/> fills the non-master cells with
/// ghost-fillers (kept solely so each cell is solid/clickable — that per-cell occupancy IS how VS gives a multiblock
/// real collision, memory vs-multiblock-collision-broadphase). Per-cell collision (T03.02) and the orientation
/// variant (T03.04 — the "side" variantgroup + the vanilla HorizontalOrientable behavior in frame.json, which orients
/// placement to the player's look and runs this CanPlaceBlock/OnBlockPlaced on the oriented variant) are wired; the
/// full teardown DROP (T03.05) is done and the authored shape is wired as a static preview (T03.06). The machine
/// stays inert (no power / cut) until the parts are installed (T03.07, IsComplete).
/// </summary>
public class BlockSawmill : Block
{
    // ───────────────────────── Interaction help (T08.01) ─────────────────────────
    //
    // The hint shown when looking at the machine depends on the master BE's assembly + I/O state (mirrors the
    // sawhorse / chopping block). The heavy stack lists (every sawmill part item, every blade-kit metal, every sawable
    // log) are resolved ONCE client-side in OnLoaded and cached process-wide, then GetPlacedBlockInteractionHelp PICKS
    // the matching set per state. A look at a ghost-filler cell routes here too (BlockSawmillGhost forwards to this
    // master block with the selection at the master cell), so any cell shows the same help. Lang keys blockhelp-sawmill-*.

    // Item icons for the part-install hints — the 4 mandatory mechanism parts, by code. Mod items that always resolve;
    // resolved once in OnLoaded (client). The flywheel is a dormant code stub with no item, so it has no hint here.
    static readonly string[] PartHintCodes = { "sawmillsash", "sawmillcrankshaft", "sawmilllevers", "sawmillcarriage" };
    readonly Dictionary<string, ItemStack> partStacks = new Dictionary<string, ItemStack>();

    // The blade-kit metal variants (sawmillblade-*) for the "insert blades" hint, and the sawable logs for the "place
    // log" hint (the same list the sawhorse builds — reused via a shared cache). Resolved once, cached process-wide.
    ItemStack[] bladeStacks = System.Array.Empty<ItemStack>();
    ItemStack[] sawableStacks = System.Array.Empty<ItemStack>();

    // State-independent interactions, pre-built in OnLoaded (their icons don't vary): the Ctrl+RMB log removal (take an
    // untouched log back / discard a begun cut) and the Ctrl+RMB blade-kit removal (shown only when no log is loaded —
    // a loaded log takes precedence on that gesture, matching OnInteract). (No "take product" hint — the product is
    // ejected forward out of the machine on completion, pulverizer-style, never stored in a slot to take.)
    WorldInteraction? takeLogInteraction;
    WorldInteraction? discardLogInteraction;
    WorldInteraction? removeInteraction;

    /// <summary>Resolve the cached interaction-help stacks (client-only). Mirrors <see cref="BlockSawhorse.OnLoaded"/>:
    /// the part / blade / sawable-log icon lists are resolved once here and reused per state in
    /// <see cref="GetPlacedBlockInteractionHelp"/>.</summary>
    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        if (api is not ICoreClientAPI capi) return; // interaction help is a purely client-side hint

        // Part icons (mod items, always resolve): the 4 mandatory parts, by code.
        foreach (string code in PartHintCodes)
        {
            Item? it = api.World.GetItem(new AssetLocation("immersivewoodworking", code));
            if (it != null) partStacks[code] = new ItemStack(it);
        }

        // Blade-kit icons: every sawmillblade-{metal} handbook stack, so the "insert blades" hint shows the metals.
        // GetHandBookStacks wrapped in try/catch (a modded override could throw during block load) — the sawhorse idiom.
        bladeStacks = ObjectCacheUtil.GetOrCreate(api, "iwSawmillBladeStacks", () =>
        {
            List<ItemStack> list = new List<ItemStack>();
            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                if (obj?.Code == null || obj.FirstCodePart() != "sawmillblade") continue;
                List<ItemStack>? stacks;
                try { stacks = obj.GetHandBookStacks(capi); } catch { continue; }
                if (stacks != null) list.AddRange(stacks);
            }
            return list.ToArray();
        });

        // Sawable input icons — reuse the SAME process-wide cache AND the SAME builder the sawhorse uses (the
        // accepted-input list is identical), so whichever station loads first builds it once (F-S12-02).
        sawableStacks = BlockEntitySawhorse.GetOrCreateSawableStacks(api, capi);

        // Ctrl+RMB on a loaded log: take it back whole (within the grace) / discard it (past the grace) — the progress-dependent
        // split mirrors the sawhorse. Picked in SelectInteractions by the BE's LogRecoverable state.
        takeLogInteraction = new WorldInteraction
        {
            ActionLangCode = "immersivewoodworking:blockhelp-sawmill-takelog",
            HotKeyCode = "ctrl",
            MouseButton = EnumMouseButton.Right
        };
        discardLogInteraction = new WorldInteraction
        {
            ActionLangCode = "immersivewoodworking:blockhelp-sawmill-discardlog",
            HotKeyCode = "ctrl",
            MouseButton = EnumMouseButton.Right
        };
        removeInteraction = new WorldInteraction
        {
            ActionLangCode = "immersivewoodworking:blockhelp-sawmill-removeblades",
            HotKeyCode = "ctrl",
            MouseButton = EnumMouseButton.Right
        };
    }

    /// <summary>Pick the interaction-help set by the master BE's state and append the block's own (base) help, mirroring
    /// <see cref="BlockSawhorse.GetPlacedBlockInteractionHelp"/>. Client-side; reads the server-synced BE state. A ghost
    /// cell forwards here with the selection at the master cell (see <see cref="BlockSawmillGhost"/>).</summary>
    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        return SelectInteractions(world, selection).Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
    }

    /// <summary>
    /// The interaction-help set matching the sawmill's current state:
    ///  • not yet assembled → install the next mandatory part, then the blade kit (after the sash);
    ///  • assembled, empty input → place a sawable log (the product is ejected forward on completion, not taken);
    ///  • Ctrl+RMB: with a log loaded → take it back (untouched) / discard it (begun); with no log → remove the blade
    ///    kit when one is installed (mechanism parts are committed).
    /// Empty array when there's no sawmill BE at the selection.
    /// </summary>
    WorldInteraction[] SelectInteractions(IWorldAccessor world, BlockSelection selection)
    {
        if (selection == null || world.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntitySawmill be)
        {
            return System.Array.Empty<WorldInteraction>();
        }

        var list = new List<WorldInteraction>();

        if (!be.IsComplete)
        {
            // Assembly: install the next mandatory part, or — once all four are in — the blade kit (it sits in the sash).
            string[] missing = be.MissingMandatoryParts();
            if (missing.Length > 0)
            {
                list.Add(new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-sawmill-installpart",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = StacksFor(missing)
                });
            }
            else if (!be.HasBladeKit)
            {
                list.Add(new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-sawmill-installblades",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = bladeStacks
                });
            }
        }
        else if (be.InputEmpty)
        {
            // Assembled and empty: feed a log. (No "take product" hint — the product is ejected forward out of the
            // machine on completion, pulverizer-style, not stored in a slot to take.)
            list.Add(new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-sawmill-putlog",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = sawableStacks
            });
        }

        // (The optional flywheel hint was removed with the flywheel item — the install mechanic stays as a dormant
        // code stub in BlockEntitySawmill, but with no item there is nothing to hint at.)

        // The Ctrl+RMB "remove" gesture, state-dependent to match OnInteract: a LOADED LOG comes off first — taken back
        // WHOLE while under the recovery grace, scrapped once past the grace into the cut (be.LogRecoverable) — and only with NO log
        // does the blade kit come off (whenever one is installed; the mechanism parts are committed, recoverable only
        // by breaking the frame).
        if (!be.InputEmpty && takeLogInteraction != null && discardLogInteraction != null)
        {
            list.Add(be.LogRecoverable ? takeLogInteraction : discardLogInteraction);
        }
        else if (be.InputEmpty && be.HasBladeKit && removeInteraction != null)
        {
            list.Add(removeInteraction);
        }

        // Shift+RMB (empty hand) cycles the cut product — planks / beams / auto (SawModePlan). Only once assembled:
        // nothing to configure before that. Shown alongside whatever load/remove hint applies; the machine has no held
        // tool to carry the pit saw's F tool mode, so this is where the sawmill's product choice lives.
        if (be.IsComplete)
        {
            list.Add(new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-sawmill-cutmode",
                MouseButton = EnumMouseButton.Right,
                HotKeyCode = "shift"
            });
        }

        return list.ToArray();
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
    /// The hover NAME. Once the machine is assembled it presents as the finished "Лесопилка" (<c>block-sawmill</c>)
    /// rather than the "Рама лесопилки" frame it was placed as — the block <see cref="Block.Code"/> stays
    /// <c>sawmill-frame-*</c> even when built, so the base name (from <see cref="OnPickBlock"/> → the frame stack)
    /// would read "frame" forever. The ghost-filler cells route their name here too
    /// (<see cref="BlockSawmillGhost.GetPlacedBlockName"/>), so every cell of the multiblock shows the same name.
    /// Falls back to the base frame name before the BE exists or while still unassembled. Mirrors
    /// <see cref="BlockChopper.GetPlacedBlockName"/>.
    /// </summary>
    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntitySawmill be && be.IsComplete)
        {
            return Lang.Get("immersivewoodworking:block-sawmill");
        }
        return base.GetPlacedBlockName(world, pos);
    }

    /// <summary>
    /// The hover INFO. The base <see cref="Block.GetPlacedBlockInfo"/> appends the static block description
    /// (<c>blockdesc-sawmill-frame-*</c> = "…Установите в неё детали, чтобы собрать станок.") after the BE's live
    /// <see cref="BlockEntitySawmill.GetBlockInfo"/>. On the placed machine that line is redundant when unassembled
    /// (GetBlockInfo already lists the missing parts) and plain WRONG once assembled — and ONLY the master cell carries
    /// it (the ghost fillers resolve <c>blockdesc-sawmill-ghost</c>, which doesn't exist), so the master cell read
    /// differently from the tower/gear cells. Strip that one auto-appended line so every footprint cell reads the same
    /// live state. The handbook and the held-frame tooltip keep the full description — they resolve it via
    /// <c>Collectible.GetItemDescText</c>, not this method, so the assemble how-to stays where it belongs. Mirrors
    /// <see cref="BlockChopper.GetPlacedBlockInfo"/>.
    /// </summary>
    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        string info = base.GetPlacedBlockInfo(world, pos, forPlayer);
        if (Code == null) return info;

        // Recompute the EXACT resolved description string the base appended (the same lang code it uses — see
        // Block.GetPlacedBlockInfo), then remove that substring. Recomputing the resolved value keeps this robust
        // across languages, and the guards make it a safe no-op when the desc isn't present.
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
    /// Footprint gate (T03.03): the frame may only be placed where ALL footprint cells are free. Check the master cell
    /// with the base logic, then each non-master cell from the shared footprint table (<see cref="BlockEntitySawmill.GetCells"/>)
    /// via a cloned selection — exactly how vanilla <c>BlockPulverizer.CanPlaceBlock</c> checks its up-cells. The
    /// footprint is the model-fitted collision volume (a solid base + the center saw tower and -X gear tower,
    /// currently 19 cells) with NO -Y pit cell — every cell is at/above the master, so the frame places STANDARD on
    /// flat ground (no clearance/pit underneath); it only refuses if the cells above/around aren't free. On a blocked cell
    /// <c>base.CanPlaceBlock</c> has already set a localized <paramref name="failureCode"/> (e.g. "notreplaceable" /
    /// "entityintersecting"), so the player gets a sensible message. Facing comes from the to-be-placed block's "side"
    /// variant (T03.04): the HorizontalOrientable behavior resolves the look-derived variant and calls this on THAT
    /// block, so the footprint checked here matches the one that will actually be placed. Falls back to NORTH for a
    /// missing/odd variant.
    /// </summary>
    public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
    {
        if (!base.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false; // master cell itself

        BlockFacing facing = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;
        BlockPos[] cells = BlockEntitySawmill.GetCells(blockSel.Position, facing);
        for (int i = 1; i < cells.Length; i++) // skip [0] = master (already checked above)
        {
            BlockSelection bs = blockSel.Clone();
            bs.Position = cells[i];
            if (!base.CanPlaceBlock(world, byPlayer, bs, ref failureCode)) return false; // base sets the localized failure
        }
        return true;
    }

    /// <summary>Once the frame is set, fill its non-master footprint cells with ghost-fillers, each pointing back
    /// at this master (<see cref="BlockEntitySawmill.PlaceFootprintGhosts"/> — server-only; mirrors
    /// <c>BlockPulverizer.WasPlaced</c> → <c>PlaceFakeBlock</c>). <see cref="CanPlaceBlock"/> has already guaranteed
    /// the cells are free, so the fillers land in clear space.</summary>
    public override void OnBlockPlaced(IWorldAccessor world, BlockPos blockPos, ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(world, blockPos, byItemStack);
        if (world.BlockAccessor.GetBlockEntity(blockPos) is BlockEntitySawmill be)
        {
            be.PlaceFootprintGhosts();
        }
    }
    /// <summary>Route a right-click on the frame to the master BE (mirrors <c>BlockPulverizer.OnBlockInteractStart</c>).
    /// <see cref="BlockEntitySawmill.OnInteract"/> routes the part-install (TryAddPart / TryTake), log I/O and
    /// removal gestures.</summary>
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntitySawmill be)
        {
            return be.OnInteract(byPlayer, blockSel);
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    /// <summary>Keep ONLY the Shift+RMB cut-mode hold ALIVE (return true) so the engine doesn't re-fire
    /// <see cref="OnBlockInteractStart"/> ~4×/s and spin the mode on a single press — the cycle itself runs once on the
    /// start (memory vs-block-interact-repeats-while-held). Every OTHER interaction (put / take / install) returns false
    /// as before: those are idempotent, so the engine's repeat is harmless and their behavior is unchanged.</summary>
    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        => world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntitySawmill be && be.IsCutModeGesture(byPlayer);

    // Per-cell collision / selection fitted to the multiblock footprint (T03.02). The master cell returns its own
    // [0,1]-clamped slice from the shared footprint table on the BE, so re-shaping the machine is a one-table edit
    // and the structure stays tunnel-free (memory vs-multiblock-collision-broadphase). The ghost-filler cells
    // route the SAME way via Principal (BlockSawmillGhost), so all footprint cells collide from one source. Falls back to the
    // JSON cube before the BE exists / for an off-footprint query.
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BlockEntitySawmill be && be.GetCellCollisionBoxes(pos) is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetCollisionBoxes(blockAccessor, pos);
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BlockEntitySawmill be && be.GetCellSelectionBoxes(pos) is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetSelectionBoxes(blockAccessor, pos);
    }

    /// <summary>
    /// Teardown, space-release half (T03.03): remove the ghost-fillers BEFORE the frame's own removal, so breaking
    /// any cell frees the whole footprint and never leaves orphaned fillers. (Removal by any OTHER channel — an
    /// explosion, worldedit, a third-party <c>SetBlock</c> — is caught by <see cref="OnBlockRemoved"/> below instead.
    /// A filler orphaned anyway — its chunk was unloaded when teardown ran, so it was never reached — can be mined
    /// away by the orphan guard in <see cref="BlockSawmillGhost.OnBlockBroken"/>. That guard covers a DANGLING
    /// Principal only; a filler whose Principal is null still refuses to break, matching vanilla.) The ghost's <see cref="BlockSawmillGhost.OnBlockBroken"/> routes a player
    /// break on a filler cell back to this master, so this runs exactly once per multiblock. The DROP half of teardown
    /// (the installed parts + blade kit) rides the standard break path through <see cref="GetDrops"/> — see there
    /// (T03.05). <c>base.OnBlockBroken</c> → <c>SpawnDropsAndRemoveBlock</c> first runs the BE's own
    /// <c>OnBlockBroken</c> (the seam where E05's input log will be released), then calls <see cref="GetDrops"/> while
    /// the BE is still alive, then removes the block.
    /// </summary>
    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntitySawmill be)
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
    /// whole ghost footprint: invisible, solid, and (because <see cref="BlockSawmillGhost.OnBlockBroken"/> routed its
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
    /// "is not BlockSawmillGhost" guard skips it. Direct mirror of <see cref="BlockChopper.OnBlockRemoved"/>.
    /// </summary>
    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntitySawmill be)
        {
            be.RemoveFootprintGhosts(); // must precede base — base unregisters this BE
        }
        base.OnBlockRemoved(world, pos);
    }

    /// <summary>
    /// Teardown, DROP half (T03.05): the frame block itself PLUS every installed part — mirrors
    /// <c>BlockPulverizer.GetDrops</c> → <c>BEPulverizer.getDrops</c> (frame + parts by flag). The frame stack comes
    /// from <c>base.GetDrops</c>, which the <c>HorizontalOrientable</c> behavior resolves to the canonical
    /// <c>sawmill-frame-north</c> (matching the pick / creative normalization in <c>frame.json</c>, and honoring any
    /// JSON drop / <paramref name="dropQuantityMultiplier"/>); the master BE then appends the installed parts
    /// (<see cref="BlockEntitySawmill.AppendPartDrops"/>): the mandatory carriage / sash / crankshaft / levers
    /// by their flags, the optional flywheel if present, and the whole blade kit — each a WHOLE item (the bronze /
    /// iron parts come back intact, never multiplier-reduced). The base path only calls this once per teardown (the
    /// ghost route short-circuits to this master without a second drop), and it is a pure read, so a racing / repeated
    /// break can't double the drop. Falls back to the bare frame before the BE exists. Server-side, non-creative only
    /// (the base gates that); the held input log (E05) is released separately by the BE's <c>OnBlockBroken</c> — whole
    /// while under the recovery grace, else forfeit.
    /// </summary>
    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
    {
        ItemStack[] frameDrops = base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntitySawmill be)
        {
            return be.AppendPartDrops(frameDrops);
        }
        return frameDrops;
    }
}
