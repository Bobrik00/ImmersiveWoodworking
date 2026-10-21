using Vintagestory.API.Common;

namespace ImmersiveWoodworking;

/// <summary>
/// E05 (T05.01) — the chopper's own INPUT inventory: a single fixed input slot (slot 0, one log / debarked log /
/// firewood), modelled on vanilla <c>InventoryQuern</c> and a direct calque of the sawmill's
/// <see cref="BlockEntitySawmill"/> Inventory partial. There is NO output slot — a completed pass EJECTS the firewood /
/// stick batch forward out of the machine (pulverizer-style, E06), so the product is never buffered here. Kept on a
/// plain <see cref="BlockEntity"/> (not a <c>BlockEntityContainer</c>) and (de)serialized by hand in the main partial's
/// <c>To/FromTreeAttributes</c> — matching the teardown design, which releases the held log via the BE's own break path
/// (<see cref="BlockChopper.GetDrops"/>/<see cref="BlockChopper.OnBlockBroken"/>) rather than a container auto-drop.
///
/// This file is the container field + lifecycle (<c>LateInitialize</c> happens in the main <c>Initialize</c>) + named
/// slot accessors. The acceptance filter (T05.03), the manual RMB put/take (T05.02) and the hopper auto-input (T05.04)
/// live in their own files.
/// </summary>
public partial class BlockEntityChopper
{
    /// <summary>Id stem for the inventory instance (<c>InventoryClassName + "-" + Pos</c>), mirroring
    /// <c>BEPulverizer.InventoryClassName</c> / <see cref="BlockEntitySawmill.InventoryClassName"/>. Not a registered
    /// GUI inventory class — the machine has no dialog — so it only forms the <c>LateInitialize</c> id. A single,
    /// dash-free stem kept separate from the instance id (memory vs-inventorygeneric-invid-dash).</summary>
    public const string InventoryClassName = "chopper";

    // Created by the field initializer (runs in the default constructor, before Initialize / any FromTreeAttributes),
    // so the slot always exists before it's populated on load. Given its world-scoped id + Api in Initialize. Separate
    // className + instanceId ctor args (never a single dash-parsed id — memory vs-inventorygeneric-invid-dash).
    readonly InventoryChopper inventory = new InventoryChopper(null!, null!);

    /// <summary>The chopper's input inventory (input slot 0 only). Owned by this master cell only — the ghost fillers
    /// have none.</summary>
    public InventoryChopper Inventory => inventory;

    /// <summary>The single input slot (slot 0): one log / debarked log / firewood, capped to one item. The
    /// acceptance filter (T05.03) and the hopper push target (T05.04) key on this slot.</summary>
    public ItemSlot InputSlot => inventory[InventoryChopper.InputSlotIndex];

    /// <summary>True while the input slot is empty — a free machine ready to take the next log (the "one log, no input
    /// buffer" rule the manual put + hopper push both gate on).</summary>
    public bool InputEmpty => InputSlot.Empty;
}
