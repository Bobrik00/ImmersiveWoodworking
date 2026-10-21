using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace ImmersiveWoodworking;

/// <summary>
/// T05.01 — the automated sawmill's INPUT inventory, modelled on vanilla <c>InventoryQuern</c> (a single input slot,
/// manually (de)serialized by the owning <see cref="BlockEntitySawmill"/>). There is NO output slot: a completed pass
/// ejects the plank/supportbeam batch forward out of the machine (<see cref="BlockEntitySawmill.EjectBatch"/>,
/// pulverizer-style), so the product is never buffered. The one slot index is fixed here once so every later task
/// addresses it by name, never by a guessed number:
/// <list type="bullet">
///   <item>slot <see cref="InputSlotIndex"/> (0) = INPUT — one <c>log-placed-{wood}</c> / <c>debarkedlog-{wood}</c>.</item>
/// </list>
/// The input is capped to a single item (<c>MaxSlotStackSize = 1</c>) — the tier rule "one log at a time, no input
/// buffer" (E05/T05.05).
///
/// The container + slot model, plus the acceptance filter (T05.03, <see cref="GetSuitability"/>): the input slot takes
/// only a sawable, product-resolvable log. The hopper auto-push target (T05.04, <c>GetAutoPushIntoSlot</c>) lives on
/// the master BE — the other InventoryQuern hook that wires chute/hopper INPUT routing (there is no auto-pull: nothing
/// to pull, the product is ejected, not stored).
/// </summary>
public class InventorySawmill : InventoryBase, ISlotProvider
{
    /// <summary>Fixed index of the input slot (one log / debarked log). The filter (T05.03) and hopper push
    /// (T05.04) key on this — set once, never guessed.</summary>
    public const int InputSlotIndex = 0;

    const int TotalSlots = 1;

    ItemSlot[] slots;
    public ItemSlot[] Slots => slots;

    public InventorySawmill(string inventoryId, ICoreAPI api) : base(inventoryId, api)
    {
        slots = GenEmptySlots(TotalSlots);
    }

    public override int Count => TotalSlots;

    public override ItemSlot this[int slotId]
    {
        get
        {
            // Out-of-range returns null, matching the vanilla InventoryBase contract (engine code only ever indexes
            // 0..Count-1). null! silences the nullable warning without widening the public signature to ItemSlot?.
            if (slotId < 0 || slotId >= Count) return null!;
            return slots[slotId];
        }
        set
        {
            if (slotId < 0 || slotId >= Count) throw new ArgumentOutOfRangeException(nameof(slotId));
            slots[slotId] = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    /// <summary>Input (slot 0) is capped to a single log — the tier's "one log, no input buffer" rule (T05.05).
    /// Applied in <see cref="NewSlot"/> (not just the constructor) so the cap survives a reload: <c>FromTreeAttributes</c>
    /// rebuilds the slot array through <see cref="NewSlot"/>, which would otherwise reset the cap to the default.</summary>
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

    /// <summary>
    /// Acceptance filter (T05.03) for any EXTERNAL insertion into the input slot — drag, shift-click, hopper/chute push:
    /// 0 once it already holds a log (one log, no buffer — T05.05, so a chute can't churn on a loaded input); otherwise
    /// only a sawable log whose product resolves, reusing the SAME two-layer gate as the manual put
    /// (<see cref="BlockEntitySawhorse.IsSawableInput"/> shape + <see cref="BlockEntitySawhorse.SawProductResolves"/>
    /// mod-independence #3). Anything else → 0; everything else defers to the default. This only GATES what may land in
    /// the input; the hopper PUSH target is the separate <c>GetAutoPushIntoSlot</c> hook (T05.04).
    /// </summary>
    public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge)
    {
        // Input: only a sawable log whose product resolves (mod-independence). Api is set by LateInitialize well
        // before any drag/hopper; guard anyway so a pre-init query just defers to the base rather than NRE'ing.
        if (targetSlot == slots[InputSlotIndex])
        {
            if (!slots[InputSlotIndex].Empty) return 0f;                              // one log, no buffer (T05.05): a loaded input refuses more, so a chute won't churn on it
            if (!BlockEntitySawhorse.IsSawableInput(sourceSlot.Itemstack)) return 0f;
            if (Api != null && !BlockEntitySawhorse.CanProduceAnySaw(Api.World, sourceSlot.Itemstack)) return 0f; // mode-agnostic door (SawModePlan): accept a log that either cut mode could turn into a product
        }

        return base.GetSuitability(sourceSlot, targetSlot, isMerge);
    }
}
