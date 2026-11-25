using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace ImmersiveWoodworking;

/// <summary>
/// The debarker's input inventory (AutoDebarkerPlan T05.01): one slot for one log, (de)serialized by hand by the owning
/// <see cref="BlockEntityDebarker"/>, as <c>BEQuern</c> does with its inventory. A calque of
/// <see cref="InventoryChopper"/>; its acceptance filter (T05.03) refuses in <see cref="CanContain"/>. The chopper's
/// <c>GetSuitability</c> only ranks, and its refusals are the explicit checks in its put and chute hook. There is no
/// output slot: the debarked log and the bark are ejected
/// (decision В3, T06.03), so no product is buffered here and a hopper only pushes in (T05.04).
/// </summary>
public class InventoryDebarker : InventoryBase, ISlotProvider
{
    /// <summary>Fixed index of the input slot. The filter (T05.03) and the hopper push (T05.04) address it by this
    /// name, never by a guessed number.</summary>
    public const int InputSlotIndex = 0;

    const int TotalSlots = 1;

    ItemSlot[] slots;
    public ItemSlot[] Slots => slots;

    // className and instanceId go in separately: the single-string base ctor splits its id on '-' and throws on a
    // dash-free one (memory vs-inventorygeneric-invid-dash). The owner re-ids the inventory in Initialize anyway.
    public InventoryDebarker(string className, string instanceId, ICoreAPI api) : base(className, instanceId, api)
    {
        slots = GenEmptySlots(TotalSlots);
    }

    public override int Count => TotalSlots;

    public override ItemSlot this[int slotId]
    {
        get
        {
            // Out of range reads null, the InventoryBase contract (the engine only indexes 0..Count-1). null! keeps
            // the public signature non-nullable.
            if (slotId < 0 || slotId >= Count) return null!;
            return slots[slotId];
        }
        set
        {
            if (slotId < 0 || slotId >= Count) throw new ArgumentOutOfRangeException(nameof(slotId));
            slots[slotId] = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    /// <summary>The input holds a single log. The cap lives here because <c>GenEmptySlots</c> builds every slot through
    /// this method; a slot set through the indexer would not carry it. A reload keeps it: <see cref="FromTreeAttributes"/>
    /// refills the constructor's array in place, and <c>SlotsFromTreeAttributes</c> builds new slots only when handed
    /// no array.</summary>
    protected override ItemSlot NewSlot(int slotId)
    {
        if (slotId == InputSlotIndex) return new ItemSlotSurvival(this) { MaxSlotStackSize = 1 };
        return new ItemSlotSurvival(this);
    }

    public override void FromTreeAttributes(ITreeAttribute tree)
    {
        slots = SlotsFromTreeAttributes(tree, slots);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        SlotsToTreeAttributes(slots, tree);
    }

    /// <summary>The input filter (T05.03, <see cref="BlockEntityDebarker.IsValidInput"/>) at the slot itself.
    /// <c>ItemSlot.TryPutInto</c> (every slot class) and <c>ItemSlot.CanHold</c> ask this before anything moves, so the
    /// chute (T05.04), which moves with <c>TryPutInto</c>, cannot land a log the machine cannot strip. Writing
    /// <c>slot.Itemstack</c> directly skips it and the slot cap: loads and mappings do that, and so does the put
    /// (T05.02), as the chopper's does, which checks the filter and the empty slot itself. A second log through
    /// <c>TryPutInto</c> is stopped by the slot cap, not here.</summary>
    public override bool CanContain(ItemSlot sinkSlot, ItemSlot sourceSlot)
        => (sinkSlot != slots[InputSlotIndex] || BlockEntityDebarker.IsValidInput(Api.World, sourceSlot.Itemstack))
           && base.CanContain(sinkSlot, sourceSlot);

    /// <summary>The same filter as a slot weight. The engine reads this only to rank slots in <c>GetBestSuitedSlot</c>.
    /// A 0 refuses only where a caller compares weights across inventories (the player's shift-click, which never sees
    /// this machine: it has no dialog to open); <c>InventoryBase.GetBestSuitedSlot</c> itself and the default chute
    /// target still return the slot at weight 0 (memory <c>vs-getsuitability-ranks-not-refuses</c>). The refusal is
    /// <see cref="CanContain"/>; this keeps the weight in line with it.</summary>
    public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge)
    {
        if (targetSlot == slots[InputSlotIndex] && !BlockEntityDebarker.IsValidInput(Api.World, sourceSlot.Itemstack)) return 0f;
        return base.GetSuitability(sourceSlot, targetSlot, isMerge);
    }
}
