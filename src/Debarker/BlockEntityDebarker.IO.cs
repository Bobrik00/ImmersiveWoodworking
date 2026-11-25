using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// Log input flow (AutoDebarkerPlan): the manual RMB put and Ctrl+RMB take or forfeit (T05.02) and the chute or hopper
/// auto-input through the filler proxy (T05.04). A port of <see cref="BlockEntityChopper"/>'s IO partial.
///
/// The hand path is server-authoritative like the chopper's: the client swallows the click and waits for the synced
/// slot, the server moves the log. RMB with a log loads it (<see cref="TryPutLog"/>). Ctrl+RMB with an empty hand takes
/// it back whole while the strip is under the mod-wide grace <see cref="ImmersiveWoodworkingConfig.LogRecoverThreshold"/>
/// (<see cref="TryTakeLog"/>) and scraps it past the grace (<see cref="DiscardLog"/>), the rule of the sawhorse's
/// <see cref="BlockEntitySawhorse.LogRecoverable"/>. A break of the frame releases it by the same rule
/// (<see cref="OnBlockBroken"/>). A hopper or chute on the roof feeds one log at a time through the cell above the log
/// (<see cref="GetAutoPushIntoSlot"/>). Every load, the chute's included, and every take, scrap and break zero
/// <c>debarkProgress</c>.
/// </summary>
public partial class BlockEntityDebarker
{
    /// <summary>
    /// RMB with a log in hand: move one log from the hand into the input slot. False, and nothing moves, when the
    /// machine is not assembled (<see cref="IsComplete"/>), a log is already in, or the held item fails the input filter
    /// (<see cref="IsValidInput"/>). The filter is asked here because the slot is written directly, which skips
    /// <see cref="InventoryDebarker.CanContain"/>. The client stops at the gates and the server does the move.
    /// </summary>
    bool TryPutLog(ItemSlot fromSlot)
    {
        if (!IsComplete || !InputEmpty) return false;
        if (!IsValidInput(Api.World, fromSlot.Itemstack)) return false;

        if (Api.Side != EnumAppSide.Server) return true;

        InputSlot.Itemstack = fromSlot.TakeOut(1);
        InputSlot.MarkDirty();
        fromSlot.MarkDirty();

        // Zeroed on the load too: the slot can empty without any reset (a schematic mapping that dropped the log, an
        // autosave snapshot caught between the slot and the progress), and the next log would start from the old one's
        // strip.
        debarkProgress = 0f;

        // Server only, so dualCallByPlayer stays null: passing byPlayer would leave out the one player whose client never
        // gets here (the chopper's AuditFixPlan T06.01).
        Api.World.PlaySoundAt(Block.Sounds.Place, Pos, -0.25, null);
        MarkDirty(true);
        return true;
    }

    /// <summary>
    /// Ctrl+RMB on a log under the grace (<see cref="LogRecoverable"/>): give it back whole, to the player's inventory or,
    /// as a fallback, at the player's feet. False when the slot is empty or the log is past the
    /// grace; <see cref="OnInteract"/> then tries <see cref="DiscardLog"/>, so one gesture removes the log either way.
    /// </summary>
    bool TryTakeLog(IPlayer byPlayer)
    {
        if (InputEmpty || !LogRecoverable) return false;

        if (Api.Side != EnumAppSide.Server) return true;

        ItemStack stack = InputSlot.TakeOutWhole();
        debarkProgress = 0f;
        // One log, so true means it went in whole. The take wants an empty hand, and the empty selected slot takes the
        // log in survival and creative alike (memory vs-trygiveitemstack-partial-fill), so the drop is a fallback for an
        // inventory that refuses it. It goes to the feet, in the form of vanilla's BEFruitTreePart and
        // BlockJonasLensTower: the master cell's middle is inside the frame in every facing, and a log dropped there
        // would land inside the machine.
        if (!byPlayer.InventoryManager.TryGiveItemstack(stack))
        {
            Api.World.SpawnItemEntity(stack, byPlayer.Entity.Pos.XYZ.Add(0, 0.5, 0));
        }

        Api.World.PlaySoundAt(Block.Sounds.Place, Pos, -0.25, null);
        MarkDirty(true);
        return true;
    }

    /// <summary>Ctrl+RMB on a log past the grace: the partly stripped log is lost, as on the sawhorse. False when the
    /// slot is empty or the log is still under the grace (<see cref="TryTakeLog"/> handles that one).</summary>
    bool DiscardLog()
    {
        if (InputEmpty || LogRecoverable) return false;

        if (Api.Side != EnumAppSide.Server) return true;

        InputSlot.Itemstack = null;
        InputSlot.MarkDirty();
        debarkProgress = 0f;
        MarkDirty(true);
        return true;
    }

    /// <summary>
    /// The break releases the loaded log, by the rule of the Ctrl+RMB take: whole under the grace, lost past it. A plain
    /// BlockEntity has no container drop, and <see cref="AppendPartDrops"/> leaves the log out.
    ///
    /// The engine calls this hook from the frame's break (<c>SpawnDropsAndRemoveBlock</c>, a break started on a filler
    /// included) and from <c>Block.OnBlockExploded</c>. <see cref="BlockDebarker"/> does not override OnBlockBroken
    /// (T03.04 tears down in OnBlockRemoved), so the base break reaches this hook. A frame reinforced with the plumb and
    /// square refuses a player's break and an explosion in its Reinforcable behavior, before this hook, so the log stays
    /// in the machine that stays. The engine calls the hook outside the creative check and the explosion's drop
    /// roll, so a creative break and an explosion that loses the frame and its parts still give the log back, as a
    /// vanilla container gives its contents. A removal by WorldEdit, a command or another mod's SetBlock reaches only
    /// <see cref="BlockDebarker.OnBlockRemoved"/>, and the log goes with the machine there (see that method).
    /// </summary>
    public override void OnBlockBroken(IPlayer? byPlayer = null)
    {
        base.OnBlockBroken(byPlayer);
        if (Api?.Side != EnumAppSide.Server || InputEmpty) return;

        if (LogRecoverable)
            Api.World.SpawnItemEntity(InputSlot.TakeOutWhole(), Pos.ToVec3d().Add(0.5, 0.6, 0.5));
        else
            InputSlot.Itemstack = null;

        InputSlot.MarkDirty();
        debarkProgress = 0f;
    }

    /// <summary>Whether the loaded log comes back whole on a Ctrl+RMB take or a break: always when untouched (even at a
    /// grace of 0), otherwise while <c>debarkProgress</c> is under
    /// <see cref="ImmersiveWoodworkingConfig.LogRecoverThreshold"/> (0.25 by default). The body of the chopper's and the
    /// sawmill's <c>LogRecoverable</c>. The config is synced, and the tick (<see cref="OnDebarkerTick"/>) syncs the
    /// progress where this flips, so the client's interaction help (T08.01), refreshed on each sync, can flip at the
    /// threshold itself (memory vs-hint-threshold-needs-own-sync). True with no log: callers check
    /// <see cref="InputEmpty"/> first.</summary>
    public bool LogRecoverable => debarkProgress <= 0f || debarkProgress < Config.LogRecoverThreshold;

    /// <summary>
    /// The chute and hopper input (T05.04): the input slot, or null. The vanilla <c>BlockEntityItemFlow</c> asks through a
    /// filler's proxy inventory (<see cref="BEMultiblockDebarker"/>), then moves the log with <c>TryPutInto</c>, which
    /// asks the filter again in <see cref="InventoryDebarker.CanContain"/>. A calque of the chopper's
    /// <c>GetAutoPushIntoSlot</c> with its own dock and without its <c>HeadClearOfBed</c>, which waits for the chopper's
    /// axe to lift off the bed; the debarker has no head, and its lift frame only shows on the client whether a log is
    /// loaded (T07.01), so nothing here waits for it.
    ///
    /// The gates of the hand put (<see cref="TryPutLog"/>): assembled, slot empty, a log the machine can strip. On top,
    /// the dock (<see cref="IsFeedDock"/>). Any other cell or face, or a machine that cannot take the log, answers null,
    /// and the chute keeps its item; a slot handed back for a log the filter refuses would only make the chute try a move
    /// that fails on every tick.
    ///
    /// The chute writes the slot past <see cref="TryPutLog"/>, which zeroes <c>debarkProgress</c> on the hand path, so
    /// it is zeroed here while the slot is still empty. If the move then fails, the slot stays empty and the zero means
    /// nothing.
    /// </summary>
    internal ItemSlot? GetAutoPushIntoSlot(BlockFacing atBlockFace, ItemSlot? fromSlot, BlockPos dockCell)
    {
        if (!IsFeedDock(atBlockFace, dockCell) || !IsComplete || !InputEmpty) return null;
        if (!IsValidInput(Api.World, fromSlot?.Itemstack)) return null;

        debarkProgress = 0f;
        return InputSlot;
    }

    /// <summary>Only the top face of <see cref="FeedCellLocal"/> feeds. <paramref name="atBlockFace"/> is this machine's
    /// face the chute touches (the engine reverses the chute's output face, so a hopper on the roof pushing down arrives
    /// as UP, the convention of vanilla <c>BEBarrel</c>), and UP is the same in every facing. <paramref name="dockCell"/>
    /// is the filler's world position, turned into the master's local frame by <see cref="LocalOf"/>. A chute pushing
    /// sideways into that cell, or down onto any other cell of the roof, gets nothing.</summary>
    bool IsFeedDock(BlockFacing atBlockFace, BlockPos dockCell)
    {
        if (atBlockFace != BlockFacing.UP) return false;
        Vec3i local = LocalOf(dockCell);
        return local.X == FeedCellLocal.X && local.Y == FeedCellLocal.Y && local.Z == FeedCellLocal.Z;
    }

    /// <summary>Server: save and sync every change of the input slot (T05.04), subscribed in <see cref="Initialize"/>.
    /// The chute writes the slot past <see cref="TryPutLog"/> and marks only its own block entity and the two slots, so
    /// without this nothing would resend this machine's tree, and its chunk would be saved only if the chute shares it.
    /// The synced tree is also how a fed log reaches the client render (E07); the server tick of T06.01 checks the slot
    /// on its own and needs no signal to start. The hand paths mark the block entity themselves as well, as the
    /// chopper's do.</summary>
    void OnInventorySlotModified(int slotId) => MarkDirty(true);
}
