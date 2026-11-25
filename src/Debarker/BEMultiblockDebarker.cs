using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// The block entity of every debarker filler cell (<see cref="BlockDebarkerGhost"/> and
/// <see cref="BlockDebarkerGhostPower"/>), a calque of <see cref="BEMultiblockChopper"/>. It remembers which cell is the
/// master (<see cref="Principal"/>) so the filler block's routing methods can find the <see cref="BlockEntityDebarker"/>;
/// it has no machine state of its own.
///
/// <see cref="Principal"/> is serialized with a (-1,-1,-1) "unset" sentinel, as in the chopper and vanilla
/// <c>BEMPMultiblock</c>: without the serialization a chunk reload would leave every routing method on its
/// Principal-null fallback, and without the sentinel an unset pointer would come back as (0,0,0).
///
/// It also holds this cell's collision and selection boxes once the master has answered for them
/// (<see cref="CellBoxes"/>, T03.02).
///
/// A <see cref="BlockEntityContainer"/> for the chute and hopper input (T05.04). A vanilla chute or hopper pushes only
/// into a cell whose block entity is one, and the master's own block entity is not, so every filler carries a one-slot
/// proxy inventory that stays empty. Its auto-push hook hands the chute the master's input slot, and the master decides
/// which cell and face may feed (<see cref="BlockEntityDebarker.GetAutoPushIntoSlot"/>).
/// </summary>
public class BEMultiblockDebarker : BlockEntityContainer, IMultiblockOffset
{
    /// <summary>World position of the master cell, or null when unset. The frame stamps it when it places the fillers
    /// (T03.03). Setting it drops the boxes held by <see cref="CellBoxes"/>: they belong to the old master, and a
    /// re-stamp (self-heal) or a client resync may point the cell elsewhere or nowhere.</summary>
    public BlockPos? Principal
    {
        get => principalPos;
        set { principalPos = value; cellBoxes = null; }
    }

    BlockPos? principalPos;

    // The proxy inventory (T05.04). The chute docks by the block entity's class, so this cell must be a container, but
    // the log lives in the master's slot: BlockEntityItemFlow moves it straight from the chute into the slot the hook
    // returns, and nothing ever lands here. Kept empty, the container base's drop on break and its (de)serialization run
    // on nothing and can never drop or save the master's log a second time.
    readonly InventoryGeneric ioProxyInv;
    public override InventoryBase Inventory => ioProxyInv;
    public override string InventoryClassName => "debarkermultiblock";

    public BEMultiblockDebarker()
    {
        // className and instanceId go in separately: the single-string ctor splits its id on '-' and throws on a dash-free
        // one (memory vs-inventorygeneric-invid-dash). The container base re-ids it in Initialize.
        ioProxyInv = new InventoryGeneric(1, "debarkermultiblock", "0", null);
        // Closed to puts, so it stays empty against another mod's transport too: an item put in through GetBestSuitedSlot
        // or TryPutInto would sit here unseen and vanish when the teardown clears this cell with SetBlock(0), which drops
        // nothing. The chute is not affected: TryPutInto asks the receiving slot's inventory, the master's.
        ioProxyInv.PutLocked = true;
        // Resolved at push time: neither Api nor Principal exists yet in the constructor. The master holds the whole
        // policy (the cell, the face and the hand put's gates), so the filler decides nothing; it passes its own Pos for
        // the cell check. No master (mid-placement, its chunk not loaded) gives null, and the chute keeps its item this
        // time. No pull hook: InventoryBase answers a pull with null, and the debarker has no output slot (decision В3).
        ioProxyInv.OnGetAutoPushIntoSlot = (atBlockFace, fromSlot) => MasterDebarker?.GetAutoPushIntoSlot(atBlockFace, fromSlot, Pos);
    }

    /// <summary>The master's block entity through <see cref="Principal"/>, or null while the pointer is unset or the
    /// master's chunk is not loaded. Looked up on every call, as <see cref="BEBehaviorDebarkerMP"/> does.</summary>
    BlockEntityDebarker? MasterDebarker
    {
        get
        {
            BlockPos? principal = Principal;
            return principal != null && Api != null ? Api.World.BlockAccessor.GetBlockEntity(principal) as BlockEntityDebarker : null;
        }
    }

    /// <summary>
    /// Land-claim redirect for USE (the chopper's AuditFixPlan T07.02 fix, see <see cref="BEMultiblockChopper.GetControlBlockPos"/>).
    /// The engine tests a claim at the clicked cell, but the filler sends the click on to the master. Without this a
    /// claim edge drawn across a standing machine leaves the outer fillers as open doors into it. The engine asks for
    /// <see cref="IMultiblockOffset"/> in the Use branch of <c>WorldMap.GetBlockingLandClaimant</c> through
    /// <c>Block.GetInterface</c>, which queries the block entity too, so it lives next to the pointer it needs.
    ///
    /// Returns a copy and leaves the argument alone: the caller passes the live <c>BlockSelection.Position</c>.
    /// Principal unset or dangling answers with the clicked cell, the same cell the click itself falls back to. May run
    /// before <see cref="BlockEntity.Initialize"/> has set <c>Api</c>.
    /// </summary>
    public BlockPos GetControlBlockPos(BlockPos pos)
    {
        BlockPos? principal = Principal;
        if (principal == null) return pos;
        if (Api != null && Api.World.BlockAccessor.GetBlockEntity(principal) is not BlockEntityDebarker) return pos;
        return principal.Copy();
    }

    /// <summary>
    /// This filler's collision and selection boxes, asked of the master once and then held (the chopper's AuditFixPlan
    /// T05.05). Null until the master can be resolved (mid-placement, or its chunk not loaded) and tried again on the next
    /// ask, so a machine across a chunk border still fills in. Both box routes of <see cref="BlockDebarkerGhost"/> land
    /// here, since collision and selection are the same boxes.
    ///
    /// Holding them is what the inputs allow: <see cref="BlockEntity.Pos"/> is fixed, the master's facing never changes
    /// in place, and a new <see cref="Principal"/> drops the hold. Without it every ask costs two block-entity lookups,
    /// this cell's and the master's, and the aiming ray asks every frame.
    /// </summary>
    public Cuboidf[]? CellBoxes(IBlockAccessor blockAccessor)
    {
        Cuboidf[]? cached = cellBoxes;
        if (cached != null) return cached;

        // Read the pointer once: a resync on the main thread may rewrite it while entity physics asks from its own thread.
        // The accessor is the one the engine handed the block method, not Api.World's: the collision and break-particle
        // paths pass their own.
        BlockPos? principal = Principal;
        if (principal == null || blockAccessor.GetBlockEntity(principal) is not BlockEntityDebarker master) return null;

        // Read the flag BEFORE the boxes. Read after, an Initialize on the main thread could raise it between this thread's
        // two reads (entity physics asks off the main thread), and a NORTH-default answer would be held as final.
        bool facingResolved = master.FacingResolved;
        Cuboidf[]? boxes = master.GetCellCollisionBoxes(Pos);
        if (boxes != null && facingResolved) cellBoxes = boxes;
        return boxes;
    }

    volatile Cuboidf[]? cellBoxes;

    // The container base reads and writes the proxy's "inventory" subtree. A filler stamped before T05.04 has none, and
    // reading the missing subtree leaves the empty slot as it is (InventoryBase.SlotsFromTreeAttributes returns early on null).
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        int cx = tree.GetInt("cx");
        int cy = tree.GetInt("cy");
        int cz = tree.GetInt("cz");
        Principal = (cx == -1 && cy == -1 && cz == -1) ? null : new BlockPos(cx, cy, cz);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetInt("cx", Principal?.X ?? -1);
        tree.SetInt("cy", Principal?.Y ?? -1);
        tree.SetInt("cz", Principal?.Z ?? -1);
    }

    /// <summary>The master's hover info, so every cell of the machine reads the same. Deliberately skips base: base runs
    /// the BE behaviors, and the power filler's MP behavior (T04.01) prints its network lines in entity debug mode on that
    /// one cell only; the container base would also add its "Stored food perish speed" line for the empty proxy, the
    /// chopper's reason. Safe no-op while <see cref="Principal"/> is unset.</summary>
    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder sb)
    {
        if (Principal == null) return;
        Api?.World?.BlockAccessor.GetBlockEntity(Principal)?.GetBlockInfo(forPlayer, sb);
    }
}
