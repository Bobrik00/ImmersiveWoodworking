using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// Splits a total amount of dropped items into as-even-as-possible piles, with the remainder loaded
/// one-per-pile. Shared by the chopping block (<c>BlockEntityChoppingBlock.SpawnDistributed</c>) and the
/// sawhorse (<c>BlockEntitySawhorse.DropStageBatch</c>) so the two stations' pile split can never drift apart;
/// each caller keeps only its own spawn physics (scatter vs directional eject). Pile count is capped to the
/// amount so a pile is never empty (1 unit → 1 pile).
/// </summary>
static class DropDistribution
{
    /// <summary>
    /// With <see cref="ImmersiveWoodworkingConfig.DropsAtPlayer"/> on, drop <paramref name="stack"/> at the feet of
    /// <paramref name="byPlayer"/> and return true; otherwise (switch off, no living player entity) return false and
    /// leave the pile to the caller's own station physics. The one delivery path for both manual stations, so the
    /// chopping block and the sawhorse (rip and strip alike) can't drift apart on it.
    /// <para>An ordinary item entity rather than a TryGiveItemstack push, so the engine's own pickup
    /// (EntityBehaviorCollectEntities) does the collecting: it honours the player's "collect only while sneaking" mode
    /// (ItemCollectMode 1), plays the pickup sound, fires <c>onitemcollected</c> and leaves what doesn't fit on the
    /// ground. An entity with no ByPlayerUid has no pickup delay (EntityItem.CanCollect, VS 1.22), and the collect
    /// sphere (1.5 blocks around the body's middle) covers the feet. The velocity is an explicit zero because a null
    /// one gets a random push of up to 0.05 blocks/tick per axis, which would scatter the piles off the player again.
    /// Server-side (every caller is).</para>
    /// </summary>
    public static bool TrySpawnAtPlayer(ImmersiveWoodworkingConfig config, IWorldAccessor world, IPlayer? byPlayer, ItemStack stack)
    {
        if (!config.DropsAtPlayer) return false;

        EntityPlayer? catcher = byPlayer?.Entity;
        if (catcher == null || !catcher.Alive) return false;

        // Pos.XYZ is dimension-aware (InternalY), the same frame SpawnItemEntity takes. Half a block up, as the
        // flotsam in-hand break drops at the breaker.
        world.SpawnItemEntity(stack, catcher.Pos.XYZ.Add(0, 0.5, 0), new Vec3d());
        return true;
    }

    /// <summary>
    /// Per-pile stack sizes for <paramref name="amount"/> items spread across
    /// <c>min(max(1, dropCount), amount)</c> piles, as evenly as possible with the remainder spread
    /// one-per-pile (so the first <c>amount % piles</c> piles get one extra). The sizes sum to exactly
    /// <paramref name="amount"/>. Returns an empty array when <paramref name="amount"/> &lt;= 0.
    /// </summary>
    public static int[] SplitEvenly(int amount, int dropCount)
    {
        if (amount <= 0) return System.Array.Empty<int>();

        int piles = System.Math.Min(System.Math.Max(1, dropCount), amount);
        int baseSize = amount / piles;
        int remainder = amount % piles;

        int[] sizes = new int[piles];
        for (int i = 0; i < piles; i++) sizes[i] = baseSize + (i < remainder ? 1 : 0);
        return sizes;
    }
}
