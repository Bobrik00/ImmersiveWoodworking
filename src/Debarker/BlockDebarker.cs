using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

/// <summary>
/// Master block of the automated DEBARKER (AutoDebarkerPlan): the single placed <c>debarker-frame</c>, the mech-powered
/// twin of hand debarking on the sawhorse. It carries the <see cref="BlockEntityDebarker"/> that owns all machine
/// state; the other cells of its footprint are invisible <see cref="BlockDebarkerGhost"/> fillers that route every
/// click back here, so the multiblock acts as one object. A calque of <see cref="BlockChopper"/>.
///
/// Here: the right-click route into the BE (T03.01) with its one-press hold (T05.02), the master cell's collision and
/// selection (T03.02), the footprint gate with the ghost placement (T03.03), the teardown with its drop (T03.04) and
/// the interaction help by the machine's state (T08.01). Orientation is the <c>side</c> variantgroup with the vanilla
/// HorizontalOrientable behavior in <c>frame.json</c>, whose choice of variant <see cref="TryPlaceBlock"/> replaces so
/// that the bark side faces the player (<see cref="GetLookAwareBlockVariant"/>); <see cref="CanPlaceBlock"/> and
/// <see cref="OnBlockPlaced"/> run on the chosen variant, so both see the facing the frame is placed with.
/// The hover name and info are here too (T08.03).
/// </summary>
public class BlockDebarker : Block, Vintagestory.GameContent.ILookAwarePlacement
{
    // Interaction help (T08.01), the chopper's shape (BlockChopper.SelectInteractions): the icon lists are resolved once
    // on the client in OnLoaded, and GetPlacedBlockInteractionHelp picks the lines by the master BE's state. A filler
    // forwards its help here with the selection moved to the master cell (BlockDebarkerGhost), so every cell shows the
    // same lines. The BE marks the aimed cell dirty when a sync changes them (BlockEntityDebarker.RefreshInteractionHelpOnChange).

    // The mechanism part icons by code, for the codes MissingMandatoryParts returns.
    readonly Dictionary<string, ItemStack> partStacks = new();
    // Every knife set metal, and every log the machine takes.
    ItemStack[] knifeStacks = System.Array.Empty<ItemStack>();
    ItemStack[] inputStacks = System.Array.Empty<ItemStack>();

    // The Ctrl+RMB lines carry no icons, so one instance each serves every state. OnInteract takes the log or the knife
    // set only with an empty hand (decision В6), and with an item in hand the same chord installs or loads the held item instead;
    // the text says so and RequireFreeHand draws the empty-hand frame, as vanilla's shelf does for its take.
    static readonly WorldInteraction TakeLogInteraction = new()
    {
        ActionLangCode = "immersivewoodworking:blockhelp-debarker-takelog",
        HotKeyCode = "ctrl",
        MouseButton = EnumMouseButton.Right,
        RequireFreeHand = true
    };
    static readonly WorldInteraction DiscardLogInteraction = new()
    {
        ActionLangCode = "immersivewoodworking:blockhelp-debarker-discardlog",
        HotKeyCode = "ctrl",
        MouseButton = EnumMouseButton.Right,
        RequireFreeHand = true
    };
    static readonly WorldInteraction RemoveKnivesInteraction = new()
    {
        ActionLangCode = "immersivewoodworking:blockhelp-debarker-removeknives",
        HotKeyCode = "ctrl",
        MouseButton = EnumMouseButton.Right,
        RequireFreeHand = true
    };

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        if (api is not ICoreClientAPI capi) return; // the help is drawn only on the client

        foreach (string code in BlockEntityDebarker.MandatoryParts)
        {
            Item? item = api.World.GetItem(new AssetLocation("immersivewoodworking", code));
            if (item != null) partStacks[code] = new ItemStack(item);
        }

        // The knife sets this machine installs: our own domain only, as TryAddPart checks, and not the blank, whose
        // first code part is debarkerknifeblank. A plain stack per metal, like the part icons above, and not
        // GetHandBookStacks: with EnableDebarker off the FeatureGate excludes these items from the handbook, which
        // empties that list, and a machine built before the switch would show the line without icons.
        knifeStacks = ObjectCacheUtil.GetOrCreate(api, "iwDebarkerKnifeStacks", () =>
        {
            var list = new List<ItemStack>();
            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                if (obj?.Code == null || obj.Code.Domain != "immersivewoodworking" || obj.FirstCodePart() != "debarkerknife") continue;
                list.Add(new ItemStack(obj));
            }
            return list.ToArray();
        });

        // The logs: the sawhorse pair's input list narrowed by the machine's own filter (BlockEntityDebarker.IsValidInput,
        // T05.03), not a second rule. The pair lists every log it can strip (CanProcessInput), so no log the filter passes
        // is missing. Built here rather than on first use like the chopper's list: the filter reads the block registry
        // alone, whole by now, and the server's product remap, which reaches a client later, holds plank and beam codes
        // only, never a debarked log.
        inputStacks = ObjectCacheUtil.GetOrCreate(api, "iwDebarkerInputStacks", () =>
        {
            var list = new List<ItemStack>();
            foreach (ItemStack stack in BlockEntitySawhorse.GetOrCreateSawableStacks(api, capi))
            {
                if (BlockEntityDebarker.IsValidInput(api.World, stack)) list.Add(stack);
            }
            return list.ToArray();
        });
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
        => SelectInteractions(world, selection).Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));

    /// <summary>
    /// The lines for the machine's state, on the client from the synced BE:
    ///  • not assembled: install the missing mechanism parts (any order), and the knife set once the disc is in, so
    ///    both lines can show at once (decision В6);
    ///  • assembled and empty: put a log in;
    ///  • Ctrl+RMB with an empty hand, in the order <see cref="BlockEntityDebarker.OnInteract"/> tries it: a loaded log
    ///    comes back whole under the grace or is lost past it (<see cref="BlockEntityDebarker.LogRecoverable"/>), and the
    ///    knife set comes off only with no log in. The log lines skip the assembly check, as the take does.
    /// No line for the drive, the disc or the feed coming off: they come back only when the frame is broken. Nothing about
    /// power either: the block info shows that (T08.03). Empty with no debarker BE at the selection.
    /// </summary>
    WorldInteraction[] SelectInteractions(IWorldAccessor world, BlockSelection selection)
    {
        if (selection == null || world.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityDebarker be)
        {
            return System.Array.Empty<WorldInteraction>();
        }

        var list = new List<WorldInteraction>();

        if (!be.IsComplete)
        {
            string[] missing = be.MissingMandatoryParts();
            if (missing.Length > 0)
            {
                list.Add(new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-debarker-installpart",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = StacksFor(missing)
                });
            }
            if (be.HasDisc && !be.HasKnives)
            {
                list.Add(new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-debarker-installknives",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = knifeStacks
                });
            }
        }
        else if (be.InputEmpty)
        {
            list.Add(new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-debarker-putlog",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = inputStacks
            });
        }

        if (!be.InputEmpty) list.Add(be.LogRecoverable ? TakeLogInteraction : DiscardLogInteraction);
        else if (be.HasKnives) list.Add(RemoveKnivesInteraction);

        return list.ToArray();
    }

    /// <summary>The cached icons for the given part codes, skipping any that did not resolve.</summary>
    ItemStack[] StacksFor(string[] codes)
    {
        var list = new List<ItemStack>();
        foreach (string code in codes) if (partStacks.TryGetValue(code, out ItemStack? stack)) list.Add(stack);
        return list.ToArray();
    }

    /// <summary>
    /// The hover name (T08.03), a calque of <see cref="BlockChopper.GetPlacedBlockName"/>. An assembled machine is the
    /// "Debarker" (<c>block-debarker</c>): the block stays <c>debarker-frame-*</c> once built, and the base name, read off
    /// the frame stack <see cref="Block.OnPickBlock"/> gives, would say "Debarker frame" for good. The fillers ask here
    /// (<see cref="BlockDebarkerGhost.GetPlacedBlockName"/>), so every cell shows one name. Before the BE exists and while
    /// a part or the knife set is missing, the frame's name.
    /// </summary>
    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityDebarker be && be.IsComplete)
        {
            return Lang.Get("immersivewoodworking:block-debarker");
        }
        return base.GetPlacedBlockName(world, pos);
    }

    /// <summary>
    /// The hover info without the frame's description (T08.03), a calque of <see cref="BlockChopper.GetPlacedBlockInfo"/>.
    /// Base appends <c>blockdesc-debarker-frame-*</c> ("…install the parts into it to assemble the machine") after the BE's
    /// <see cref="BlockEntityDebarker.GetBlockInfo"/>. On the placed machine the line only restates the list of missing
    /// parts while unassembled and is wrong once assembled, and only the master cell would carry it: a filler's code
    /// resolves no description. So the line is cut out here, and the master cell differs from the fillers only by the
    /// reinforcement lines, which only the frame can carry (T03.04). The held frame's tooltip and the handbook
    /// read the description through <c>GetItemDescText</c>, not this method, and keep it.
    /// </summary>
    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        string info = base.GetPlacedBlockInfo(world, pos, forPlayer);
        if (Code == null) return info;

        // The very string base appended, from the lang code it builds, so the cut holds in every language and does
        // nothing when the description is missing.
        string descCode = Code.Domain + ":" + ItemClass.ToString().ToLowerInvariant() + "desc-" + Code.Path;
        string desc = Lang.GetMatching(descCode);
        if (desc != descCode && desc.Length > 0)
        {
            int idx = info.LastIndexOf(desc, System.StringComparison.Ordinal);
            if (idx >= 0) info = info.Remove(idx, desc.Length).TrimEnd();
        }
        return info;
    }

    // The engine shows a refused placement as Lang.Get("placefailure-" + failureCode), always in the game domain, so the
    // message lives in this mod's lang files under the key "game:placefailure-iw-debarker-footprint".
    const string FootprintBlockedCode = "iw-debarker-footprint";

    /// <summary>
    /// The variant a player's placement gets: the look turned a quarter clockwise, so the machine's front, the side the
    /// bark drops out of (local -X, the chute), faces the player (the author's call at T07.07, 2026-11-25). Vanilla
    /// HorizontalOrientable names the variant after the look, which under <see cref="BlockEntityDebarker.RotateToFacing"/>
    /// turns the log's exit end (local -Z) to the player and the chute to the player's right. Looking north now gives
    /// <c>debarker-frame-east</c>: the chute toward the player, the log out to the player's left, the coupling at the
    /// back. Only the choice of variant moved; each variant's model, footprint and calibration are as they were.
    /// <c>GetInterface</c> finds the block before its behaviors, so a caller of the interface gets this choice too.
    /// </summary>
    public Block GetLookAwareBlockVariant(IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel)
    {
        BlockFacing look = SuggestedHVOrientation(byPlayer, blockSel)[0];
        return byPlayer.Entity.World.BlockAccessor.GetBlock(CodeWithVariant("side", look.GetCW().Code));
    }

    /// <summary>
    /// A player's placement in place of the HorizontalOrientable behavior's: vanilla's check and place, on the variant
    /// <see cref="GetLookAwareBlockVariant"/> picks, so <see cref="CanPlaceBlock"/> checks the footprint of the very
    /// variant that goes down. Base would run the behavior first, and its own choice would win. Skipping the behaviors'
    /// TryPlaceBlock costs nothing in vanilla: besides HorizontalOrientable the frame carries only the Reinforcable that
    /// vanilla adds to every block of a hard material, which hooks OnBlockPlaced (still run by DoPlaceBlock) and not
    /// TryPlaceBlock. A behavior another mod adds to all blocks would lose its TryPlaceBlock on the frame too. The
    /// behavior stays in <c>frame.json</c> for the canonical -north drop and pick and the schematic rotation.
    /// </summary>
    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        Block oriented = GetLookAwareBlockVariant(byPlayer, itemstack, blockSel);
        if (!oriented.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;
        return oriented.DoPlaceBlock(world, byPlayer, blockSel, itemstack);
    }

    /// <summary>
    /// The frame goes down only where its whole footprint is free: the master cell by the base check, then every other
    /// cell of <see cref="BlockEntityDebarker.GetCells"/> by the same check on a cloned selection, as vanilla
    /// <c>BlockPulverizer.CanPlaceBlock</c> checks the two cells above it. Base also refuses a creature in the cell and another
    /// player's claim, with the vanilla messages. A block in an outer cell gets the machine's own message instead of
    /// vanilla "another block is in the way": the player aims at a free cell and would not see what is meant.
    /// </summary>
    public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
    {
        if (!base.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;

        // Block.Variant is a RelaxedReadOnlyDictionary: an absent key reads null and falls back to NORTH, as in Initialize.
        BlockFacing facing = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;
        BlockPos[] cells = BlockEntityDebarker.GetCells(blockSel.Position, facing);
        for (int i = 1; i < cells.Length; i++)
        {
            BlockSelection bs = blockSel.Clone();
            bs.Position = cells[i];
            if (!base.CanPlaceBlock(world, byPlayer, bs, ref failureCode))
            {
                if (failureCode == "notreplaceable") failureCode = FootprintBlockedCode;
                return false;
            }
        }
        return true;
    }

    /// <summary>Once the frame and its BE stand, fill the rest of the footprint with fillers pointing back at it
    /// (<see cref="BlockEntityDebarker.PlaceFootprintGhosts"/>, server only). The engine calls this on every SetBlock of
    /// the frame, a command's too, which never passed <see cref="CanPlaceBlock"/>; the chopper does the same. Vanilla
    /// <c>BlockPulverizer</c> places its top from <c>WasPlaced</c> inside its own <c>TryPlaceBlock</c> instead, so only
    /// a player's placement gets one.</summary>
    public override void OnBlockPlaced(IWorldAccessor world, BlockPos blockPos, ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(world, blockPos, byItemStack);
        if (world.BlockAccessor.GetBlockEntity(blockPos) is BlockEntityDebarker be)
        {
            be.PlaceFootprintGhosts();
        }
    }

    /// <summary>
    /// The teardown for every way the frame can go (T03.04): a player's break on the frame or, through
    /// <see cref="BlockDebarkerGhost.OnBlockBroken"/>, on any filler; an explosion; worldedit; another mod's SetBlock.
    /// Each of them ends in this hook (memory vs-onblockremoved-universal-seam), so the fillers are cleared here and only
    /// here. Before base, because base unregisters the BE that <see cref="BlockEntityDebarker.RemoveFootprintGhosts"/>
    /// needs; the BE is still found when the cell already reads air, since the lookup goes through the chunk's BE map.
    ///
    /// The chopper clears its fillers from its OnBlockBroken as well, ahead of base. That order breaks on a reinforced
    /// frame: vanilla gives every wooden block the Reinforcable behavior, which refuses the break from inside base, and
    /// the fillers would already be gone around a frame that stays. Here a refused break removes nothing.
    ///
    /// The input log is not dropped here (T05.02). A break and an explosion release it from the BE's own OnBlockBroken
    /// (<see cref="BlockEntityDebarker.OnBlockBroken"/>). On the paths that reach only this hook the log goes with the
    /// machine, as a vanilla container's contents go on a bare SetBlock (memory vs-setblock-does-not-notify-or-drop) and
    /// as on the chopper. A drop here would duplicate it under WorldEdit: its history keeps the BE tree of every replaced
    /// block and an undo loads it back, log included (<c>BlockAccessorRevertable</c>, decompiled 1.22).
    /// </summary>
    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityDebarker be)
        {
            be.RemoveFootprintGhosts();
        }
        base.OnBlockRemoved(world, pos);
    }

    /// <summary>
    /// The drop of a broken or exploded frame (T03.04): base gives the frame, which the HorizontalOrientable behavior
    /// turns into the canonical <c>debarker-frame-north</c> whatever the facing, and the master BE adds the installed parts
    /// and the knife set (<see cref="BlockEntityDebarker.AppendPartDrops"/>). Both engine callers ask before the block
    /// goes, so the BE still answers: <c>SpawnDropsAndRemoveBlock</c> before its SetBlock(0), <c>OnBlockExploded</c>
    /// before the bulk commit. A break started on a filler drops the same set here, at the frame's cell. A creative
    /// player's break skips the drop altogether, and an explosion rolls its drop chance once for the whole list, so a
    /// miss loses the parts and the knives with the frame.
    /// </summary>
    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer? byPlayer, float dropQuantityMultiplier = 1f)
    {
        ItemStack[] frameDrops = base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityDebarker be)
        {
            return be.AppendPartDrops(frameDrops);
        }
        return frameDrops;
    }

    /// <summary>Route a right-click on the frame to the master BE, like <see cref="BlockChopper.OnBlockInteractStart"/>.
    /// The ghost cells reach the same <see cref="BlockEntityDebarker.OnInteract"/> through their principal.</summary>
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityDebarker be)
        {
            return be.OnInteract(byPlayer, blockSel);
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    /// <summary>
    /// Keep every click the machine took alive until the button is let go, so one press on one cell does one action
    /// (T05.02). A hold that ends on the first step is started over by the engine every 0.25 s while the button is
    /// down (memory vs-block-interact-repeats-while-held), and the clicks of this machine do not repeat safely: a log or
    /// knife set taken back lands in the active hotbar slot when that is the first empty one, and the repeat would put
    /// it back in; a log taken with the knives still in would let the repeat take the knives too. Moving the aim to
    /// another cell of the machine with the button held is not covered: the client cancels the hold there (MovedAway)
    /// and starts a new click at once, since the 0.25 s count runs from the press (memory
    /// vs-held-rmb-sweep-skips-fast-blocks), and that click can put the taken log back or take the knives. The chopper
    /// and the sawmill let these clicks repeat (the sawmill holds only its Shift+RMB cut-mode cycle), so their Ctrl+RMB
    /// takes the log and then the head or the blade kit on a long press. The fillers answer their own step by the same
    /// test (<see cref="BlockDebarkerGhost.OnBlockInteractStep"/>).
    /// </summary>
    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityDebarker) return true;
        return base.OnBlockInteractStep(secondsUsed, world, byPlayer, blockSel);
    }

    // The master cell's own slice of the footprint table on the BE (BlockEntityDebarker.Footprint), the same table the
    // fillers read through BlockDebarkerGhost, so entities collide with the whole machine from one source. Before the BE
    // exists the JSON full cube stands in. Particles do not ask these: they read GetParticleCollisionBoxes, which still
    // gives the JSON cube (memory vs-particle-collision-boxes); the bark chips of T07.05 decide what they should land on.
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BlockEntityDebarker be && be.GetCellCollisionBoxes(pos) is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetCollisionBoxes(blockAccessor, pos);
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BlockEntityDebarker be && be.GetCellSelectionBoxes(pos) is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetSelectionBoxes(blockAccessor, pos);
    }
}
