using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// The block entity carried by every invisible ghost-filler cell of the chopper multiblock
/// (<see cref="BlockChopperGhost"/>) — the mod's analogue of the sawmill's <see cref="BEMultiblockSawmill"/> /
/// vanilla <c>BEMPMultiblock</c>. Its core job is to remember which cell is the master
/// (<see cref="Principal"/>, the placed <c>chopper-frame</c>) so the ghost's proxy methods can route every player
/// click / break / pick / particle query back to the master <see cref="BlockEntityChopper"/>. It has no machine
/// state of its own. That same pointer is what makes this BE the footprint's <see cref="IMultiblockOffset"/>
/// (<see cref="GetControlBlockPos"/>, AuditFixPlan T07.02): the engine asks the clicked block — block entity
/// included — where the multiblock's control cell is before it tests a land claim for USE, so the claim guarding
/// the master is the one a click on any filler answers to.
///
/// <see cref="Principal"/> is SERIALIZED (three ints with a (-1,-1,-1) "null" sentinel, exactly like the sawmill /
/// the vanilla original) so the link survives a chunk reload — without it the ghost's proxies would hit
/// <c>Principal == null</c> and degrade to safe no-ops ("never break", see <see cref="BlockChopperGhost"/>),
/// freezing the multiblock as un-dismantlable. The footprint placement (T03.03) stamps <see cref="Principal"/>
/// onto each ghost at the moment it sets the block.
///
/// A <see cref="BlockEntityContainer"/> (like the sawmill's) because E05/T05.04 makes the perimeter cell the item-flow
/// INPUT port a vanilla chute/hopper docks to (the master is interior to the footprint and unreachable): the container
/// gets a tiny, permanently-empty proxy inventory whose auto-push hook returns the MASTER's input slot cross-cell.
/// That hook + the master accessor are added in E05 — at T03.01 the inventory exists only to satisfy the container
/// contract, so the ghost class registers and serializes cleanly. The power-input ghost (<see cref="BlockChopperGhostPower"/>)
/// additionally carries the mechanical node on this same BE class (its blocktype adds the MP entityBehavior in E04),
/// while the master reads the speed cross-cell — the same interior-master pattern (memory vs-mp-consumer-interior-master).
/// </summary>
public class BEMultiblockChopper : BlockEntityContainer, IMultiblockOffset
{
    /// <summary>World position of the master cell (the placed <c>chopper-frame</c> carrying the
    /// <see cref="BlockEntityChopper"/>), or null when unset. Every proxy method on <see cref="BlockChopperGhost"/>
    /// reads this to find the master; null makes them safe no-ops.</summary>
    public BlockPos? Principal { get; set; }

    // The chute/hopper requires the cell it docks to BE a BlockEntityContainer with a live Inventory — but this cell
    // owns no items: the real input slot lives on the master. So this inventory is a single, permanently-empty
    // placeholder whose ONLY purpose is to satisfy the container contract and carry the auto-PUSH delegate, which
    // resolves the master cross-cell and hands back ITS input slot (BlockEntityItemFlow then moves the log directly
    // between the chute and the master slot — never through here). Empty ⇒ the container base's LateInitialize /
    // DropAll / (de)serialize all run on nothing, so they can never touch or double-drop the master's real inventory.
    readonly InventoryGeneric ioProxyInv;
    public override InventoryBase Inventory => ioProxyInv;
    public override string InventoryClassName => "choppermultiblock";

    public BEMultiblockChopper()
    {
        // invId must be passed as separate className + instanceId — the single-string `InventoryGeneric(int, invId, api)`
        // overload splits invId on '-' and indexes [1], so a dash-less id throws IndexOutOfRange at construction (memory
        // vs-inventorygeneric-invid-dash). The id is a throwaway anyway: the container base overwrites it via
        // LateInitialize(InventoryClassName + "-" + Pos) on Initialize.
        ioProxyInv = new InventoryGeneric(1, "choppermultiblock", "0", null);
        // Lazy (evaluated at chute-query time, when Api + Principal are set): route the engine's auto-PUSH hook to the
        // master, which holds ALL the input policy (the back-central input dock, filter, assembled/empty gates) so the
        // manual put and the hopper path share ONE source and can't drift. We pass THIS ghost's own Pos as the docked
        // cell so the master can tell WHICH footprint cell the chute abuts (the policy accepts only the two central back
        // cells — see BlockEntityChopper.IsBackCentralInputDock). Null master (mid-placement / master chunk not loaded) →
        // null slot → the chute simply skips this cell that tick. No auto-PULL hook: the product is ejected, never stored.
        ioProxyInv.OnGetAutoPushIntoSlot = (atBlockFace, fromSlot) => MasterChopper?.GetAutoPushIntoSlot(atBlockFace, fromSlot, Pos);
    }

    /// <summary>The master chopper BE this ghost belongs to, reached cross-cell through <see cref="Principal"/>, or
    /// null while that link is unset (mid-placement) or the master's chunk isn't loaded. Resolved fresh each call — a
    /// cheap BE lookup, the same pattern as <see cref="BEBehaviorChopperMP"/>.</summary>
    BlockEntityChopper? MasterChopper
        => Principal != null && Api != null ? Api.World.BlockAccessor.GetBlockEntity(Principal) as BlockEntityChopper : null;

    /// <summary>
    /// Land-claim redirect for the whole footprint (AuditFixPlan T07.02, F-L3-02): tell the engine that a USE test
    /// aimed at this filler cell belongs to the MASTER cell, because that is where the click actually lands
    /// (<see cref="BlockChopperGhost.OnBlockInteractStart"/> routes it there).
    ///
    /// <para>The engine reads this in exactly ONE place — <c>WorldMap.GetBlockingLandClaimant</c>, and only in its
    /// <c>EnumBlockAccessFlags.Use</c> branch: it asks the clicked block for <see cref="IMultiblockOffset"/> via
    /// <c>Block.GetInterface</c>, which also queries the BLOCK ENTITY, and that is why this lives here rather than on
    /// <see cref="BlockChopperGhost"/> (the pointer it needs is already here, and one implementation covers the plain
    /// filler and the power filler alike, since both carry this BE class). Without it a claim boundary drawn across a
    /// standing 3×3×4 footprint — trivially produced by claiming or resizing AFTER the machine was built — leaves the
    /// filler cells outside as unguarded doors into the machine inside: the engine grants Use on the empty cell and
    /// our routing then pulls the loaded log or the fitted chopper head out of the protected master.</para>
    ///
    /// <para>Returns a COPY and never touches the argument. The interface's own doc comment claims the passed-in
    /// position is modified, but that is not how it is used: the caller hands over the live
    /// <c>BlockSelection.Position</c> it goes on to interact with, and vanilla's own implementation
    /// (<c>BlockMultiblock.GetControlBlockPos</c> → <c>pos.AddCopy(OffsetInv)</c>) leaves it alone too. Mutating it
    /// would move the player's aim.</para>
    ///
    /// <para>Answering with the clicked cell is the safe fallback, not a silent pass: it is exactly the cell the
    /// interaction itself falls back to. <see cref="Principal"/> unset (mid-placement, orphaned filler) or DANGLING
    /// (master gone, pointer left behind) both make <see cref="BlockChopperGhost.OnBlockInteractStart"/> drop to
    /// <c>base</c> and act here, so the claim guarding this cell is the right one to consult. Runs on both sides and
    /// may be reached before <see cref="BlockEntity.Initialize"/> has set <c>Api</c>, hence the null-tolerant
    /// resolve.</para>
    ///
    /// <para>One case stays open and cannot be closed from here: the claim list the engine scans is picked by map
    /// REGION from the original position, before this redirect, so a footprint straddling a region border still
    /// misses a claim registered only in the master's region. That is engine-side and needs a 512-block boundary to
    /// cut the machine.</para>
    /// </summary>
    public BlockPos GetControlBlockPos(BlockPos pos)
    {
        BlockPos? principal = Principal;
        if (principal == null) return pos;
        if (Api != null && Api.World.BlockAccessor.GetBlockEntity(principal) is not BlockEntityChopper) return pos;
        return principal.Copy();
    }

    /// <summary>This ghost cell's collision / selection boxes, taken once from the master's per-facing footprint table
    /// and then held (AuditFixPlan T05.05). Null until the master can be resolved — mid-placement, or its chunk not
    /// loaded — and re-tried on the next ask, so a machine straddling a chunk border still fills in.
    ///
    /// <para>Holding the answer for good is what the inputs allow: <see cref="Principal"/> and <see cref="Pos"/> are
    /// fixed once the footprint is stamped, and the master's facing comes from its block variant and never moves in
    /// place (rotating means break and re-place, which retires this BE with the rest of the footprint). Without it
    /// every ask cost TWO block-entity lookups — this cell's own, then the cross-cell one for the master — and the
    /// footprint regularly straddles a chunk border. Both callers on <see cref="BlockChopperGhost"/> route here, and
    /// the master returns the same shared array to both, so one field serves collision and selection alike.</para>
    /// </summary>
    public Cuboidf[]? CellBoxes(IBlockAccessor blockAccessor)
    {
        Cuboidf[]? cached = cellBoxes;
        if (cached != null) return cached;

        // Resolve through the accessor the ENGINE handed the block method, as the old per-query route did, rather than
        // through Api.World — the collision and particle paths pass their own.
        if (Principal == null || blockAccessor.GetBlockEntity(Principal) is not BlockEntityChopper master) return null;

        Cuboidf[]? boxes = master.GetCellCollisionBoxes(Pos);
        // Hold it only once the master has read its facing off the block variant: asked before that it answers from the
        // NORTH default, and holding THAT would freeze the wrong frame onto this cell for the machine's whole life.
        if (boxes != null && master.FacingResolved) cellBoxes = boxes;
        return boxes;
    }

    volatile Cuboidf[]? cellBoxes;

    // The proxy inventory holds nothing perishable/coolable and the real I/O is the master's, so skip the container
    // base's 10s perish/room tick — it would otherwise run on every ghost cell of every machine for nothing.
    protected override void OnTick(float dt) { }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving); // container base: restores the (empty) proxy inventory
        int cx = tree.GetInt("cx");
        int cy = tree.GetInt("cy");
        int cz = tree.GetInt("cz");
        // (-1,-1,-1) is the "no principal" sentinel — it cannot arise spontaneously for a real master cell, so it
        // can never collide with a genuine position. Mirrors the sawmill / vanilla BEMPMultiblock.
        Principal = (cx == -1 && cy == -1 && cz == -1) ? null : new BlockPos(cx, cy, cz);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree); // container base: writes the (empty) proxy inventory under its own "inventory" subtree
        // Write the (-1,-1,-1) sentinel for a null principal so a reload restores exactly the same "unset" state.
        tree.SetInt("cx", Principal?.X ?? -1);
        tree.SetInt("cy", Principal?.Y ?? -1);
        tree.SetInt("cz", Principal?.Z ?? -1);
    }

    /// <summary>Delegate the hover info to the master, so looking at any cell of the multiblock shows the same
    /// block info (mirrors the sawmill / vanilla BEMPMultiblock). Deliberately does NOT call base — the container
    /// base would append a "Stored food perish speed" line, meaningless for the empty proxy inventory. Safe no-op
    /// while <see cref="Principal"/> is unset.</summary>
    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder sb)
    {
        if (Principal == null) return;
        Api?.World?.BlockAccessor.GetBlockEntity(Principal)?.GetBlockInfo(forPlayer, sb);
    }
}
