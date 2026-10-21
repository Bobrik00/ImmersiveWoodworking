using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// The block entity carried by every invisible ghost-filler cell of the sawmill multiblock
/// (<see cref="BlockSawmillGhost"/>) — the mod's analogue of vanilla <c>BEMPMultiblock</c>. Its core job is to
/// remember which cell is the master (<see cref="Principal"/>, the placed <c>sawmill-frame</c>) so the ghost's
/// proxy methods can route every player click / break / pick / particle query back to the master
/// <see cref="BlockEntitySawmill"/>. It has no machine state of its own. That same pointer is what makes this BE the
/// footprint's <see cref="IMultiblockOffset"/> (<see cref="GetControlBlockPos"/>, AuditFixPlan T07.02): the engine
/// asks the clicked block — block entity included — where the multiblock's control cell is before it tests a land
/// claim for USE, so the claim guarding the master is the one a click on any filler answers to.
///
/// <see cref="Principal"/> is SERIALIZED (three ints with a (-1,-1,-1) "null" sentinel, exactly like the vanilla
/// original) so the link survives a chunk reload — without it the ghost's proxies would hit
/// <c>Principal == null</c> and degrade to safe no-ops ("never break", see <see cref="BlockSawmillGhost"/>),
/// freezing the multiblock as un-dismantlable. The footprint placement (T03.03) stamps <see cref="Principal"/>
/// onto each ghost at the moment it sets the block.
///
/// ITEM-FLOW INPUT (E05 / T05.04). The master cell is INTERIOR to the footprint, so a vanilla chute/hopper can never
/// abut it — and the engine's auto-transfer (<c>BlockEntityItemFlow</c>) only docks a neighbour that resolves to a
/// concrete <see cref="BlockEntityContainer"/> (<c>GetBlock(pos).GetBlockEntity&lt;BlockEntityContainer&gt;(pos)</c>;
/// the <c>IBlockEntityContainer</c> interface alone is NOT enough). So THIS perimeter BE is the container the chute
/// talks to: it is a <see cref="BlockEntityContainer"/> with a tiny, deliberately-empty OWN inventory whose auto-push
/// hook returns the MASTER's input slot (resolved cross-cell through <see cref="Principal"/>). The chute then moves the
/// log straight into the master's slot — the dummy inventory never stores anything, so there is no second inventory to
/// keep in sync and the master's teardown drop (T03.05) stays untouched. The direction rule lives on the master (a
/// TOP-face chute feeds the input — see <see cref="BlockEntitySawmill.GetAutoPushIntoSlot"/>), so a perimeter cell
/// doubles as an input port by which face the chute touches. There is NO auto-PULL: the product is ejected forward out
/// of the machine (pulverizer-style, <see cref="BlockEntitySawmill.EjectBatch"/>), never stored, so nothing can be
/// pulled. This mirrors how the power-input ghost (<see cref="BlockSawmillGhostPower"/>) exposes the mechanical node on
/// a perimeter cell while the master reads it cross-cell — the same interior-master pattern, for items instead of rotation.
///
/// Mechanical power (E04): this BE class is shared by the plain ghost (no power role) AND the power-input ghost
/// (<see cref="BlockSawmillGhostPower"/>), whose blocktype adds a <see cref="BEBehaviorSawmillMP"/> entityBehavior —
/// so on the input cell THIS BE also carries the network node, while the master reads its speed via
/// <see cref="BlockEntitySawmill.NetworkSpeed"/>. (The vanilla <c>BEMPMultiblock</c> "re-registers as a gear position
/// on load" line is the LargeGear <c>IGearAcceptor</c> path — a different multiblock; the sawmill does NOT use it, the
/// consumer behavior on the input ghost rejoins the network on its own.)
/// </summary>
public class BEMultiblockSawmill : BlockEntityContainer, IMultiblockOffset
{
    /// <summary>World position of the master cell (the placed <c>sawmill-frame</c> carrying the
    /// <see cref="BlockEntitySawmill"/>), or null when unset. Every proxy method on <see cref="BlockSawmillGhost"/>
    /// reads this to find the master; null makes them safe no-ops.</summary>
    public BlockPos? Principal { get; set; }

    // The chute/hopper requires the cell it docks to BE a BlockEntityContainer with a live Inventory — but this cell
    // owns no items: the actual input/output slots live on the master. So this inventory is a single, permanently-empty
    // placeholder whose ONLY purpose is to satisfy the container contract and carry the two auto-flow delegates, which
    // resolve the master cross-cell and hand back ITS slots (BlockEntityItemFlow then moves the item directly between
    // the chute and the master slot — never through here). Empty ⇒ the container base's LateInitialize / DropAll /
    // (de)serialize all run on nothing, so they can never touch or double-drop the master's real inventory.
    readonly InventoryGeneric ioProxyInv;
    public override InventoryBase Inventory => ioProxyInv;
    public override string InventoryClassName => "sawmillmultiblock";

    public BEMultiblockSawmill()
    {
        // invId must be passed as separate className + instanceId — the single-string `InventoryGeneric(int, invId, api)`
        // overload splits invId on '-' and indexes [1], so a dash-less id throws IndexOutOfRange at construction (the
        // crash this fixes). Matches the mod's own BlockEntityChoppingBlock convention. The id is a throwaway anyway:
        // the container base overwrites it via LateInitialize(InventoryClassName + "-" + Pos) on Initialize.
        ioProxyInv = new InventoryGeneric(1, "sawmillmultiblock", "0", null);
        // Lazy (evaluated at chute-query time, when Api + Principal are set): route the engine's auto-PUSH hook to the
        // master, which holds ALL the input policy (direction, filter, assembled/empty gates) so the manual put and the
        // hopper path share ONE source and can't drift. Null master (mid-placement / master chunk not loaded) → null
        // slot → the chute simply skips this cell that tick. No auto-PULL hook: the product is ejected, never stored.
        ioProxyInv.OnGetAutoPushIntoSlot = (atBlockFace, fromSlot) => MasterSawmill?.GetAutoPushIntoSlot(atBlockFace, fromSlot);
    }

    /// <summary>The master sawmill BE this ghost belongs to, reached cross-cell through <see cref="Principal"/>, or
    /// null while that link is unset (mid-placement) or the master's chunk isn't loaded. Resolved fresh each call — a
    /// cheap BE lookup, the same pattern as <see cref="BEBehaviorSawmillMP"/>.</summary>
    BlockEntitySawmill? MasterSawmill
        => Principal != null && Api != null ? Api.World.BlockAccessor.GetBlockEntity(Principal) as BlockEntitySawmill : null;

    /// <summary>
    /// Land-claim redirect for the whole footprint (AuditFixPlan T07.02, F-L3-02): tell the engine that a USE test
    /// aimed at this filler cell belongs to the MASTER cell, because that is where the click actually lands
    /// (<see cref="BlockSawmillGhost.OnBlockInteractStart"/> routes it there). Mirror of
    /// <see cref="BEMultiblockChopper.GetControlBlockPos"/>, which carries the full reasoning; the sawmill's 19-cell
    /// footprint is the wider target of the two.
    ///
    /// <para>The engine reads this in exactly ONE place — <c>WorldMap.GetBlockingLandClaimant</c>, and only in its
    /// <c>EnumBlockAccessFlags.Use</c> branch: it asks the clicked block for <see cref="IMultiblockOffset"/> via
    /// <c>Block.GetInterface</c>, which also queries the BLOCK ENTITY, and that is why this lives here rather than on
    /// <see cref="BlockSawmillGhost"/> (the pointer it needs is already here, and one implementation covers the plain
    /// filler and the power filler alike, since both carry this BE class).</para>
    ///
    /// <para>Returns a COPY and never touches the argument: the caller hands over the live
    /// <c>BlockSelection.Position</c> it goes on to interact with, so mutating it would move the player's aim. An
    /// unset or DANGLING <see cref="Principal"/> answers with the clicked cell, which is exactly where the
    /// interaction itself falls back to acting.</para>
    /// </summary>
    public BlockPos GetControlBlockPos(BlockPos pos)
    {
        BlockPos? principal = Principal;
        if (principal == null) return pos;
        if (Api != null && Api.World.BlockAccessor.GetBlockEntity(principal) is not BlockEntitySawmill) return pos;
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
    /// footprint regularly straddles a chunk border. Both callers on <see cref="BlockSawmillGhost"/> route here, and
    /// the master returns the same shared array to both, so one field serves collision and selection alike. The
    /// chopper's ghost BE carries the same field (mirror pair).</para>
    /// </summary>
    public Cuboidf[]? CellBoxes(IBlockAccessor blockAccessor)
    {
        Cuboidf[]? cached = cellBoxes;
        if (cached != null) return cached;

        // Resolve through the accessor the ENGINE handed the block method, as the old per-query route did, rather than
        // through Api.World — the collision and particle paths pass their own.
        if (Principal == null || blockAccessor.GetBlockEntity(Principal) is not BlockEntitySawmill master) return null;

        Cuboidf[]? boxes = master.GetCellCollisionBoxes(Pos);
        // Hold it only once the master has read its facing off the block variant: asked before that it answers from the
        // NORTH default, and holding THAT would freeze the wrong frame onto this cell for the machine's whole life.
        if (boxes != null && master.FacingResolved) cellBoxes = boxes;
        return boxes;
    }

    volatile Cuboidf[]? cellBoxes;

    // The proxy inventory holds nothing perishable/coolable and the real I/O is the master's, so skip the container
    // base's 10s perish/room tick — it would otherwise run on all ~18 ghost cells of every machine for nothing.
    protected override void OnTick(float dt) { }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving); // container base: restores the (empty) proxy inventory
        int cx = tree.GetInt("cx");
        int cy = tree.GetInt("cy");
        int cz = tree.GetInt("cz");
        // (-1,-1,-1) is the "no principal" sentinel — it cannot arise spontaneously for a real master cell, so it
        // can never collide with a genuine position. Mirrors vanilla BEMPMultiblock.
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
    /// block info (mirrors vanilla BEMPMultiblock). Deliberately does NOT call base — the container base would append
    /// a "Stored food perish speed" line, meaningless for the empty proxy inventory. Safe no-op while
    /// <see cref="Principal"/> is unset.</summary>
    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder sb)
    {
        if (Principal == null) return;
        Api?.World?.BlockAccessor.GetBlockEntity(Principal)?.GetBlockInfo(forPlayer, sb);
    }
}
