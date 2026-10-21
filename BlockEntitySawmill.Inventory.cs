using Vintagestory.API.Common;

namespace ImmersiveWoodworking;

/// <summary>
/// E05 (T05.01) — the sawmill's own INPUT inventory: a single fixed input slot (slot 0, one log / debarked log),
/// modelled on vanilla <c>InventoryQuern</c>. There is NO output slot — a completed pass EJECTS the plank/supportbeam
/// batch forward out of the machine (pulverizer-style, see <see cref="EjectBatch"/>), so the product is never buffered
/// here. Kept on a plain <see cref="BlockEntity"/> (not a <c>BlockEntityContainer</c>) and (de)serialized by hand in the
/// main partial's <c>To/FromTreeAttributes</c> — matching the established teardown design, which releases the held log
/// via the BE's own break path (see <see cref="BlockSawmill.GetDrops"/>/<see cref="BlockSawmill.OnBlockBroken"/>) rather
/// than a container auto-drop.
///
/// This file is the container field + lifecycle (<c>LateInitialize</c> happens in the main <c>Initialize</c>) + named
/// slot accessors. The acceptance filter (T05.03), the manual RMB put/take (T05.02) and the hopper auto-input (T05.04)
/// live in their own files.
/// </summary>
public partial class BlockEntitySawmill
{
    /// <summary>Id stem for the inventory instance (<c>InventoryClassName + "-" + Pos</c>), mirroring
    /// <c>BEPulverizer.InventoryClassName</c>. Not a registered GUI inventory class — the machine has no dialog — so
    /// it only forms the <c>LateInitialize</c> id.</summary>
    public const string InventoryClassName = "sawmill";

    // Created by the field initializer (runs in the default constructor, before Initialize / any FromTreeAttributes),
    // so the slot always exists before it's populated on load. Given its world-scoped id + Api in Initialize.
    readonly InventorySawmill inventory = new InventorySawmill(null!, null!);

    /// <summary>The sawmill's input inventory (input slot 0 only). Owned by this master cell only — the ghost fillers
    /// have none.</summary>
    public InventorySawmill Inventory => inventory;

    /// <summary>The single input slot (slot 0): one log / debarked log, capped to one item. The acceptance filter
    /// (T05.03) and the hopper push target (T05.04) key on this slot.</summary>
    public ItemSlot InputSlot => inventory[InventorySawmill.InputSlotIndex];

    /// <summary>True while the input slot is empty — a free machine ready to take the next log (the "one log, no input
    /// buffer" rule the manual put + hopper push both gate on, T05.05).</summary>
    public bool InputEmpty => InputSlot.Empty;
}
