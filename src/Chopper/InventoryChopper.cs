using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace ImmersiveWoodworking;

/// <summary>
/// T05.01 — the automated chopper's INPUT inventory, modelled on vanilla <c>InventoryQuern</c> (a single input slot,
/// manually (de)serialized by the owning <see cref="BlockEntityChopper"/>) and a direct calque of the sawmill's
/// <see cref="InventorySawmill"/>. There is NO output slot: a completed pass ejects the firewood/stick batch forward out
/// of the machine (<c>EjectBatch</c>, E06, pulverizer-style), so the product is never buffered. The one slot index is
/// fixed here once so every later task addresses it by name, never by a guessed number:
/// <list type="bullet">
///   <item>slot <see cref="InputSlotIndex"/> (0) = INPUT — one log / debarked log / firewood (the chopping
///   block's full accept set, resolved by the filter in T05.03).</item>
/// </list>
/// The input is capped to a single item (<c>MaxSlotStackSize = 1</c>) — the tier rule "one log at a time, no input
/// buffer".
///
/// The container + slot model, plus the acceptance filter (T05.03, <see cref="GetSuitability"/>): the input slot takes
/// only a CHOPPABLE stack — full chopping-block parity via <see cref="BlockEntityChoppingBlock.IsChoppable"/>. Unlike
/// the sawmill's two-layer filter there is NO "product resolves" layer: the chop output is always vanilla
/// <c>game:firewood</c> / <c>game:stick</c> (never a modded per-species product), so it always resolves — nothing to
/// gate at the door. The hopper auto-push target (<c>GetAutoPushIntoSlot</c> on the master BE, T05.04) lives in its own
/// task; there is no auto-pull (nothing to pull — the product is ejected, not stored).
/// </summary>
public class InventoryChopper : InventoryBase, ISlotProvider
{
    /// <summary>Fixed index of the input slot (one log / debarked log / firewood). The filter (T05.03) and
    /// hopper push (T05.04) key on this — set once, never guessed.</summary>
    public const int InputSlotIndex = 0;

    const int TotalSlots = 1;

    ItemSlot[] slots;
    public ItemSlot[] Slots => slots;

    public InventoryChopper(string inventoryId, ICoreAPI api) : base(inventoryId, api)
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

    /// <summary>Input (slot 0) is capped to a single log — the tier's "one log, no input buffer" rule. Applied in
    /// <see cref="NewSlot"/> (not just the constructor) so the cap survives a reload: <c>FromTreeAttributes</c> rebuilds
    /// the slot array through <see cref="NewSlot"/>, which would otherwise reset the cap to the default.</summary>
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
    /// 0 once it already holds a log (one log, no buffer — so a chute can't churn on a loaded input); otherwise only a
    /// CHOPPABLE stack, reusing the SAME single gate as the manual put (<see cref="BlockEntityChoppingBlock.IsChoppable"/>
    /// = <c>IsLog || IsFirewood</c>, full parity with the hand chopping block, mod-agnostic by code shape — a modded
    /// <c>wildcrafttree:log-placed-azobe-ud</c> passes automatically — the mod-independence rule "accept by code shape,
    /// never by species", <c>source-conventions.md</c>). Anything else → 0; everything else
    /// defers to the default. Simpler than <see cref="InventorySawmill.GetSuitability"/>: the chopper needs NO
    /// product-resolves layer (the output is always vanilla firewood / sticks, which always resolves — nothing to gate
    /// here), and no <c>Api</c> guard (<see cref="BlockEntityChoppingBlock.IsChoppable"/> is a pure code-shape check, it
    /// touches no world). This only GATES what may land in the input; the hopper PUSH target is the separate
    /// <c>GetAutoPushIntoSlot</c> hook on the master BE (T05.04).
    /// </summary>
    public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge)
    {
        if (targetSlot == slots[InputSlotIndex])
        {
            if (!slots[InputSlotIndex].Empty) return 0f;                                    // one log, no buffer: a loaded input refuses more, so a chute won't churn on it
            // Full chopping-block parity (IsLog || IsFirewood || IsHalfLog); anything else refused at the door. Config-
            // gated so a chute can't push FIREWOOD in when AllowFirewoodToSticks is off (same gate as the manual put /
            // IsValidInput). Api is live during any external move, so the mod-system lookup is safe here.
            var config = Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().Config;
            if (!BlockEntityChoppingBlock.IsChoppable(sourceSlot.Itemstack, config)) return 0f;
        }

        return base.GetSuitability(sourceSlot, targetSlot, isMerge);
    }
}
