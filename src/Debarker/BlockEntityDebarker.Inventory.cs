using Vintagestory.API.Common;

namespace ImmersiveWoodworking;

/// <summary>
/// The debarker's single input slot (AutoDebarkerPlan T05.01) and its acceptance filter, reusing the sawhorse
/// debarking rules (T05.03). No output slot: the debarked log and the bark are ejected. A port of
/// <see cref="BlockEntityChopper"/>'s Inventory partial. The machine stays a plain <see cref="BlockEntity"/>, not a
/// <c>BlockEntityContainer</c>: the main partial's <c>To/FromTreeAttributes</c> (de)serialize the inventory by hand,
/// and the frame break releases the log itself (T05.02) instead of a container's auto-drop. The manual put and take
/// (T05.02) and the hopper input (T05.04) live in the IO partial, the log's collectible mappings in the Parts partial.
/// </summary>
public partial class BlockEntityDebarker
{
    /// <summary>Class part of the inventory id, <c>InventoryClassName + "-" + Pos</c>, as in
    /// <c>BEPulverizer.InventoryClassName</c>. It only forms the id passed to <c>LateInitialize</c>, which splits the
    /// id at the first dash, so it must stay free of dashes.</summary>
    public const string InventoryClassName = "debarker";

    // Built by the field initializer, so the slot exists before a load's FromTreeAttributes fills it; Initialize then
    // gives it the world id and the Api. The placeholder instance id is replaced there.
    readonly InventoryDebarker inventory = new InventoryDebarker(InventoryClassName, "0", null!);

    /// <summary>The input inventory. Only the master holds the log.</summary>
    public InventoryDebarker Inventory => inventory;

    /// <summary>The input slot, capped to one log.</summary>
    public ItemSlot InputSlot => inventory[InventoryDebarker.InputSlotIndex];

    /// <summary>True while the input slot holds no log.</summary>
    public bool InputEmpty => InputSlot.Empty;

    /// <summary>
    /// The input filter (AutoDebarkerPlan T05.03): the machine takes exactly the logs the sawhorse pair can strip. The
    /// rule is the pair's own gate, not a second copy (<c>source-conventions.md</c> §2):
    /// <see cref="BlockEntitySawhorse.DebarkProductResolves(IWorldAccessor, ItemStack)"/> first checks the code shape
    /// itself (<see cref="BlockEntitySawhorse.IsSawableLog"/>: a placed barked log <c>log-placed-{wood}-{rot}</c> of
    /// any domain, so a grown log, a log section, a debarked log, firewood or driftwood fails), then requires
    /// <c>debarkedlog-{wood}-{rot}</c> to exist in the log's own domain or in game. A wood mod that ships logs without
    /// debarked logs is refused at the door instead of leaving a log the machine could never finish.
    ///
    /// One clause on top of the pair's gate. A log whose mod was removed comes back as a stub block with the same
    /// code (<c>Block.IsMissing</c>, memory <c>vs-mod-removal-block-fate</c>). The world's block table keeps the
    /// stub of its debarked log as well, and <c>GetBlock</c> returns that stub instead of null, so the pair's gate
    /// passes such a log. Here it is refused.
    ///
    /// Static and world-parametrised like the pair's gate: the inventory, which holds no reference to the machine,
    /// filters with it (<see cref="InventoryDebarker.CanContain"/>), and the put (T05.02) and the chute (T05.04)
    /// ask the same question before they move a log. The start gate (<see cref="CanDebark"/>, T06.04) asks it of the
    /// log already in the slot: a stub is not moved in, it turns up in place when a world is loaded without the log's
    /// mod, so no filter on a move ever sees it.
    /// </summary>
    internal static bool IsValidInput(IWorldAccessor world, ItemStack? stack)
        => stack?.Block is { IsMissing: false } && BlockEntitySawhorse.DebarkProductResolves(world, stack);

    /// <summary>The wood species of the loaded log (oak … <c>aged</c>, modded woods included), or null while the slot
    /// is empty; the log render (T07.04) paints by it. Read through <see cref="BlockEntitySawhorse.GetWoodSpecies(ItemStack)"/>
    /// as the chopper and the sawmill do, and computed on every read rather than kept in a field: the slot also
    /// changes along paths that raise no <c>SlotModified</c> (<c>FromTreeAttributes</c> on a load and on every client
    /// sync, the collectible mappings), and a field would have to be refreshed on each of them.</summary>
    public string? InputWoodSpecies => BlockEntitySawhorse.GetWoodSpecies(InputSlot.Itemstack);
}
