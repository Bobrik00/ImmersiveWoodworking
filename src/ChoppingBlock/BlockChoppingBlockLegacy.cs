using Vintagestory.API.Common;

namespace ImmersiveWoodworking;

/// <summary>
/// Migration husk for the chopping-block data-block redesign (E05 / T05.01). Worlds made before the redesign saved
/// the stand as a per-species variant code <c>choppingblock-{wood}</c>; the live block is now the single variant-less
/// <c>choppingblock</c> (<see cref="BlockChoppingBlock"/>). This class backs <c>blocktypes/choppingblock_legacy.json</c>,
/// which re-registers those old codes (same base <c>code: "choppingblock"</c> + a <c>wood</c> variantgroup) so an old
/// <c>choppingblock-oak</c> still resolves on load instead of becoming "unknown block".
///
/// It deliberately carries NO behaviour of its own: it shares the <c>ChoppingBlock</c> BlockEntity
/// (<see cref="BlockEntityChoppingBlock"/>), whose self-heal (T05.02) reads the species from <see cref="LegacyWood"/>,
/// <c>ExchangeBlock</c>s the position to the live <c>choppingblock</c>, and stamps the species onto the new BE — after
/// which no husk remains. Kept separate from <see cref="BlockChoppingBlock"/> on purpose: the live block's
/// <c>OnLoaded</c> generates the per-species creative/handbook stacks, which a hidden legacy variant must NOT do.
/// The JSON hides it from creative/handbook (empty creative filter + <c>handbook.exclude</c>) and no recipe targets
/// it, so it never surfaces to the player.
/// </summary>
public class BlockChoppingBlockLegacy : Block
{
    /// <summary>The wood species baked into this legacy block's code (e.g. "oak", "azobe"), read from the <c>wood</c>
    /// variant the old <c>choppingblock-{wood}</c> code preserves — the species source the self-heal (T05.02) writes
    /// onto the migrated <see cref="BlockEntityChoppingBlock"/>. Null only if the variant is somehow absent.</summary>
    public string? LegacyWood => Variant != null && Variant.TryGetValue("wood", out string? wood) ? wood : null;
}
