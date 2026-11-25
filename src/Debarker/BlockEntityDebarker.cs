using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// Block entity of the debarker's master cell (the placed <c>debarker-frame</c>, AutoDebarkerPlan), the single owner
/// of all machine state. The filler cells (<see cref="BlockDebarkerGhost"/> + <see cref="BEMultiblockDebarker"/>)
/// route every interaction here. A calque of <see cref="BlockEntityChopper"/>, split into the same partial files, with
/// <c>.Debarking</c> in place of <c>.Chopping</c>; each partial names the tasks that own it.
///
/// T03.01 is the skeleton: the facing and the interaction entry point; T03.02 adds <c>FacingResolved</c>, the signal the
/// fillers wait for before holding their boxes; T03.03 the footprint self-heal call on load; T05.01 the inventory's
/// <c>LateInitialize</c>, the debark progress field and the (de)serialization of the input log and the progress; T03.06
/// the part flags, the knife stack and their (de)serialization, <see cref="IsComplete"/> and the click dispatch in
/// <see cref="OnInteract"/>, which T05.02 extends with the log; T05.04 the subscription that saves and syncs a log fed
/// by a chute; T06.01 the registration of the server tick that fills the progress; T07.02 the client renderer hookup,
/// <c>InitRenderer</c> on load and <c>UpdateRenderer</c> after every tree read (Render partial); T07.04 the log's mesh
/// and progress estimate after every tree read; T08.01 the interaction help refresh after every tree read; T08.03 the
/// hover info, <see cref="GetBlockInfo"/>.
/// </summary>
public partial class BlockEntityDebarker : BlockEntity
{
    /// <summary>The direction the machine is laid along, read from the block's <c>side</c> variant in
    /// <see cref="Initialize"/>. The footprint, collision, ghost placement, eject direction and render all turn off
    /// this one value. Not serialized: re-read from the block on every load, like <c>BEPulverizer.Facing</c>, so it can
    /// never drift from the block's own variant.</summary>
    public BlockFacing Facing { get; protected set; } = BlockFacing.NORTH;

    // Raised once Initialize has read Facing off the block variant. A filler holds this master's boxes for good only
    // after seeing it (BEMultiblockDebarker.CellBoxes): asked earlier, the master answers from the NORTH default above,
    // and a filler holding that would collide in the wrong frame for the machine's whole life. Volatile, so a reader
    // that sees it raised also sees the Facing written before it.
    internal volatile bool FacingResolved;

    // How far the loaded log is debarked, 0..1 of one log. The server tick of T06.01 accrues it from the shaft's turn
    // and leaves it as it is while the machine stands idle (memory vs-saw-hold-progress); the finish of T06.02 resets
    // it, and so do the load, the take, the scrap and the break of T05.02 and the chute's load of T05.04. Saved, so a
    // half debarked log resumes after a reload, and synced for the client render.
    float debarkProgress;

    // The installed parts (T03.06, the Parts partial installs them). The drive, the disc and the feed are flags: each
    // goes in as one plain item and comes back as a fresh one at teardown (T03.04). The knife set is kept as the stack
    // itself rather than a flag, because its durability has to survive the install, a reload and the teardown; like
    // vanilla BEHelveHammer.HammerStack it sits outside the inventory, which holds only the input log. Its metal sets
    // only the wear (T06.05).
    bool hasDrive;
    bool hasDisc;
    bool hasFeed;
    ItemStack? knifeStack;

    /// <summary>True once the drive, the disc, the feed and a knife set are all in. The one gate the power (T04.03), the
    /// log input (T05.02, T05.04) and the debark cycle (E06) check first. It turns false again when the player takes the
    /// knife set out or the set wears through (T06.05).</summary>
    public bool IsComplete => hasDrive && hasDisc && hasFeed && knifeStack != null;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);

        // Block.Variant is a RelaxedReadOnlyDictionary: an absent key reads null, so a frame without the variant falls
        // back to NORTH instead of throwing.
        Facing = BlockFacing.FromCode(Block.Variant["side"]) ?? BlockFacing.NORTH;
        FacingResolved = true;

        // The input inventory gets its world id and the Api on both sides, as BEPulverizer does. On a load
        // FromTreeAttributes has already filled the slot; LateInitialize resolves the log once more. A log whose mod was
        // removed stays in the slot: the engine keeps its code under a stand-in block (memory vs-mod-removal-block-fate).
        inventory.LateInitialize(InventoryClassName + "-" + Pos, api);

        // A chute writes the slot past TryPutLog (T05.04); the handler saves and syncs it. Server only, as in the chopper:
        // the client fills its slot from the synced tree, which raises no SlotModified.
        if (api.Side == EnumAppSide.Server) inventory.SlotModified += OnInventorySlotModified;

        // Footprint self-heal (BlockEntityDebarker.Multiblock). Deferred a beat so the neighbour chunks holding outer
        // cells get a chance to load first: EnsureFootprintGhosts skips a cell whose chunk is not loaded. The engine
        // already drops the callback when the block is removed or unloaded (BlockEntity.OnBlockRemoved/OnBlockUnloaded);
        // the identity check is the chopper's cheap second guard.
        if (api.Side == EnumAppSide.Server)
        {
            RegisterDelayedCallback(_ => { if (Api?.World.BlockAccessor.GetBlockEntity(Pos) == this) EnsureFootprintGhosts(); }, 100);

            // The debark heartbeat (T06.01), OnDebarkerTick in the Debarking partial.
            RegisterGameTickListener(OnDebarkerTick, 25);
        }
        else if (api is ICoreClientAPI client) InitRenderer(client);

#if IW_DEBUG
        api.Logger.Notification("[immersivewoodworking] Debarker BE initialized at {0}, facing {1}, input {2}, progress {3:0.00}.",
            Pos, Facing, InputSlot.Itemstack?.Collectible.Code.ToString() ?? "empty", debarkProgress);
#endif
    }

    /// <summary>
    /// Master-side interaction entry. The frame (<see cref="BlockDebarker.OnBlockInteractStart"/>) and every filler
    /// (<see cref="BlockDebarkerGhost.OnBlockInteractStart"/>, through <see cref="BEMultiblockDebarker.Principal"/>)
    /// land here, so a right-click on any cell reaches this one method.
    ///
    /// RMB with an item installs it when it is a part (<see cref="TryAddPart"/>) and otherwise loads it when it is a log
    /// (<see cref="TryPutLog"/>, T05.02), Ctrl or not. Ctrl+RMB with an empty hand removes the log first, whole or
    /// scrapped (<see cref="TryTakeLog"/>, <see cref="DiscardLog"/>), and the knife set only when no log is in
    /// (<see cref="TryTakeKnives"/>). The click is swallowed in every branch, as the chopper does, so a plain
    /// right-click never places the held block against the machine. Shift+RMB with a block in hand still does (the
    /// engine asks the held block first under Shift); the fillers of T03.03 are what keep the footprint itself
    /// occupied. One press on one cell does one of these: the frame and the fillers keep the hold alive
    /// (<see cref="BlockDebarker.OnBlockInteractStep"/>).
    /// </summary>
    public bool OnInteract(IPlayer byPlayer, BlockSelection blockSel)
    {
        ItemSlot hand = byPlayer.InventoryManager.ActiveHotbarSlot;

        if (!hand.Empty)
        {
            if (!TryAddPart(hand, byPlayer)) TryPutLog(hand);
            return true;
        }

        // The remove gesture wants an empty hand (decision В6, confirmed by the author 2026-11-21), unlike the chopper
        // and the sawmill, which remove with anything in hand. Ctrl without Shift is their chord (the chopper's
        // AuditFixPlan T03.05).
        if (byPlayer.Entity.Controls.CtrlKey && !byPlayer.Entity.Controls.ShiftKey)
        {
            if (!TryTakeLog(byPlayer) && !DiscardLog()) TryTakeKnives(byPlayer);
        }
        return true;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);

        hasDrive = tree.GetBool("hasDrive");
        hasDisc = tree.GetBool("hasDisc");
        hasFeed = tree.GetBool("hasFeed");

        // A stack read from the tree comes back unresolved; resolve it here, as BEHelveHammer does with its hammer.
        knifeStack = tree.GetItemstack("knifeStack");
        knifeStack?.ResolveBlockOrItem(worldForResolving);

        // On the client the field is the server's progress as of the last sync; the render keeps its own estimate.
        float syncedBefore = debarkProgress;
        debarkProgress = tree.GetFloat("debarkProgress");

        // The inventory has its own "inventory" subtree, as in BEQuern. On a load this runs before Initialize: the
        // inventory has no Api yet and SlotsFromTreeAttributes leaves the stack unresolved, so it is resolved here
        // against worldForResolving.
        ITreeAttribute? invTree = tree.GetTreeAttribute("inventory");
        if (invTree != null)
        {
            inventory.FromTreeAttributes(invTree);
            foreach (ItemSlot slot in inventory) slot.Itemstack?.ResolveBlockOrItem(worldForResolving);
        }

        // The authoritative path for the renderer: another player's install or take and a knife break reach a client
        // only with this sync; the clicker's own prediction goes through the Parts partial and is corrected by the
        // master's next sync (a click the server rejects resends only the clicked cell's neighbourhood, which may not
        // reach the master). On a load this runs before Initialize with capi still null, and InitRenderer builds the
        // first renderer.
        UpdateRenderer();

        // T07.04: the log follows the synced slot, its progress estimate the synced progress. The estimate first, since
        // the log's first merge reads it. Both wait for InitRenderer on a load.
        ReconcileClientProgress(syncedBefore);
        UpdateLogMesh();

        // T08.01: the interaction help of a player aiming at a filler follows this sync too.
        RefreshInteractionHelpOnChange();
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);

        tree.SetBool("hasDrive", hasDrive);
        tree.SetBool("hasDisc", hasDisc);
        tree.SetBool("hasFeed", hasFeed);
        if (knifeStack != null) tree.SetItemstack("knifeStack", knifeStack);

        tree.SetFloat("debarkProgress", debarkProgress);

        ITreeAttribute invTree = new TreeAttribute();
        inventory.ToTreeAttributes(invTree);
        tree["inventory"] = invTree;
    }

    /// <summary>
    /// The hover info (T08.03): what the machine is doing now. The master's own cell shows it, and every filler through
    /// <see cref="BEMultiblockDebarker.GetBlockInfo"/>, so any cell reads the same. A calque of
    /// <see cref="BlockEntityChopper.GetBlockInfo"/> and <see cref="BlockEntitySawmill.GetBlockInfo"/>: an unassembled
    /// machine lists the parts still missing and nothing else; an assembled one shows the knives' metal and durability,
    /// the loaded log with its progress, and then either the drive speed with what the machine is doing or the one reason
    /// it stands. The engine asks on the client every 500 ms while the player aims at a cell, so everything here is the
    /// client's copy: the parts, the knife set and the slot from the block entity sync, the progress estimate of the
    /// render, the network speed from the mech-network packet and the config from joining. The client initializes a block
    /// entity before the HUD can find it, so <see cref="Config"/> and the one <see cref="CanDebark"/> of the call are safe
    /// here (T06.04).
    /// </summary>
    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder sb)
    {
        base.GetBlockInfo(forPlayer, sb);

#if IW_DEBUG
        // The power node as the master sees it, for the T08.05 run, first so that it shows on an unassembled frame too.
        // With entity debug mode on, the axle at the coupling prints its networkid and a "speed" (Network.Speed × its
        // GearedRatio, a magnitude on the client): the node has joined the axle's network when the ids match, and
        // NetworkSpeed should read the same speed. "effective" is EffectiveDebarkSpeed as the client sees it (T04.03): 0
        // with a part or the knives missing or below DebarkerMinTorque, the speed itself from the floor up.
        BEBehaviorDebarkerMP? dbgNode = InputMechBehavior;
        sb.AppendLine($"[IW-DBG] networkid={(dbgNode == null ? "no node" : dbgNode.NetworkId.ToString())} speed={NetworkSpeed:G4}"
            + $" angle={AngleRad:0.00} effective={EffectiveDebarkSpeed():G4}");
#endif

        // Inert until assembled: list what IsComplete still lacks and stop there. The knife set bolts onto the disc, so
        // it is listed once the disc is in, as the install hint shows it (T08.01). A set worn through (T06.05) is listed
        // the same way: that is the "no knives" reason to stand.
        if (!IsComplete)
        {
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-debarker-incomplete"));
            foreach (string code in MissingMandatoryParts())
                sb.AppendLine("• " + Lang.Get("immersivewoodworking:item-" + code));
            if (HasDisc && !HasKnives)
                sb.AppendLine("• " + Lang.Get("immersivewoodworking:blockinfo-debarker-needknives"));
            return;
        }

        // A set whose code no longer resolves (FromTreeAttributes keeps it) has no collectible; skipped, as the render
        // skips it, rather than an error line in the HUD every 500 ms.
        if (knifeStack?.Collectible != null)
        {
            // The set's name carries no metal (item-debarkerknife-*), so the line names it by vanilla's material word.
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-debarker-knives",
                Lang.Get("game:material-" + knifeStack.Collectible.Variant["metal"])));
            int max = knifeStack.Collectible.GetMaxDurability(knifeStack);
            if (max > 1)
            {
                // At DebarkerKnifeWearMultiplier 0 DamageKnives charges nothing, and the line says the set keeps its value.
                int left = knifeStack.Collectible.GetRemainingDurability(knifeStack);
                sb.AppendLine(Config.DebarkerKnifeWearMultiplier <= 0f
                    ? Lang.Get("immersivewoodworking:blockinfo-debarker-knives-nowear", left, max)
                    : Lang.Get("immersivewoodworking:blockinfo-debarker-knives-durability", left, max));
            }
        }

        // The log's own name carries the species ("Oak log"). The percent is the render's estimate (T07.04), the clock
        // the bark strips go out by, rather than the synced debarkProgress on its 5 % grid, so the two move together.
        // Only once begun, as on the siblings: a fresh log reads as its name alone.
        ItemStack? log = InputSlot.Itemstack;
        if (log != null)
        {
            sb.AppendLine(log.GetName());
            if (clientProgress > 0f)
            {
                int percent = GameMath.Clamp((int)(clientProgress * 100f), 0, 100);
                sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-debarker-progress", percent));
            }
        }

        // Why it stands, in the order of EffectiveDebarkSpeed's gate. The assembly is settled above, so a 0 here is the
        // speed floor: with nothing meeting the coupling that is "no drive", with an axle there the line turns too slowly
        // (an axle nothing turns as well). Running, the drive speed, then idle or debarking by the one CanDebark of the
        // call; a log CanDebark refuses (a stub left by a removed mod) gets no state line.
        if (EffectiveDebarkSpeed() <= 0f)
        {
            // Two places, the speed cut down and the floor rounded up, not both rounded: a speed below the floor then
            // never reads as the floor itself ("0.05, needs at least 0.05" at 0.046 against 0.05).
            sb.AppendLine(DriveConnected()
                ? Lang.Get("immersivewoodworking:blockinfo-debarker-lowspeed",
                    (System.MathF.Floor(NetworkSpeed * 100f) / 100f).ToString("0.00"),
                    (System.MathF.Ceiling(Config.DebarkerMinTorque * 100f) / 100f).ToString("0.00"))
                : Lang.Get("immersivewoodworking:blockinfo-debarker-nopower"));
        }
        else
        {
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-debarker-speed", NetworkSpeed.ToString("0.00")));
            if (log == null) sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-debarker-idle"));
            else if (CanDebark()) sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-debarker-running"));
        }
    }
}
