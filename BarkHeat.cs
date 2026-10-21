using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// How much heat reaches a container cell, for <see cref="ItemIwGreenBark"/>'s faster drying near a fire. One instance
/// per mod-system instance, i.e. per SIDE: single-player runs the server and the client in one process, so a static
/// cache would hand one side's answer (and its world's block entities) to the other.
///
/// <para>The rule is vanilla's own "near a heat source", copied from <c>EntityBehaviorBodyTemperature</c>'s outdoor scan:
/// every cell of the ±3 cube around the receiver, <see cref="Block.GetInterface{T}"/> for <see cref="IHeatSource"/>, the
/// source's <see cref="IHeatSource.GetHeatStrength"/> scaled by <c>min(1, 9 / (8 + d²^1.25))</c> over
/// <see cref="BlockPos.DistanceSqToNearerEdge"/>, summed. Found by INTERFACE and by live strength, never by a list of
/// block codes, so a lit firepit, forge, oven, bloomery, coal pile, pit kiln, burning pile, fire, embers, lava and
/// boiling water all count, as does any mod block that implements the interface, and a cold one adds nothing
/// (every ground pile implements it and answers 0 unless it burns). <c>WalkBlocks</c> visits the fluid layer as well as
/// the solid one, which is how lava and boiling water are seen.</para>
/// </summary>
sealed class BarkHeat
{
    const int Radius = 3;

    /// <summary>Summed heat that counts as "fully heated": a lit firepit (strength 10) in the next cell.</summary>
    const float FullHeat = 10f;

    // The rate is asked for on every transition update of every drying slot, and on the client every frame for every
    // slot drawn in an open chest, while a fire changes state in seconds at the quickest (firepit syncs every 500 ms).
    const long TtlMs = 5000;
    const long PurgeEveryMs = 60000;

    readonly Dictionary<BlockPos, Entry> cache = new();
    readonly List<BlockPos> expired = new();
    long nextPurgeMs;

    // Block id → "might be a heat source". Without it GetInterface falls through to GetBlockEntity on every cell of the
    // cube, air included. Built lazily from the finished block registry of this side.
    bool[]? candidates;

    // Scan state, held in fields so the WalkBlocks callback is one cached delegate instead of a closure per scan.
    readonly Action<Block, int, int, int> onBlock;
    readonly BlockPos minPos = new(0), maxPos = new(0), probe = new(0);
    IWorldAccessor? scanWorld;
    BlockPos? receiver;
    double rx, ry, rz;
    float sum;

    readonly struct Entry
    {
        public readonly float Fraction;
        public readonly long ExpiresMs;
        public Entry(float fraction, long expiresMs) { Fraction = fraction; ExpiresMs = expiresMs; }
    }

    public BarkHeat() { onBlock = OnBlock; }

    /// <summary>Heat reaching <paramref name="pos"/> as a fraction of <see cref="FullHeat"/>, 0..1. Cached per cell for
    /// <see cref="TtlMs"/>. A hit is one dictionary lookup keyed by the caller's own BlockPos; the key is copied only
    /// when a new cell is inserted. Main thread of the owning side only, like every caller of GetTransitionRateMul.</summary>
    public float HeatFraction(IWorldAccessor world, BlockPos pos)
    {
        long now = world.ElapsedMilliseconds;
        bool known = cache.TryGetValue(pos, out Entry entry);
        if (known && entry.ExpiresMs > now) return entry.Fraction;

        float fraction = GameMath.Clamp(Scan(world, pos) / FullHeat, 0f, 1f);
        if (known) cache[pos] = new Entry(fraction, now + TtlMs);
        else cache.Add(pos.Copy(), new Entry(fraction, now + TtlMs));

        if (now >= nextPurgeMs) Purge(now);
        return fraction;
    }

    float Scan(IWorldAccessor world, BlockPos at)
    {
        candidates ??= BuildCandidates(world);

        scanWorld = world;
        receiver = at;
        rx = at.X + 0.5;
        ry = at.Y + 0.5;
        rz = at.Z + 0.5;
        sum = 0f;

        minPos.Set(at.X - Radius, at.Y - Radius, at.Z - Radius);
        maxPos.Set(at.X + Radius, at.Y + Radius, at.Z + Radius);
        minPos.SetDimension(at.dimension);
        maxPos.SetDimension(at.dimension);
        probe.SetDimension(at.dimension);

        world.BlockAccessor.WalkBlocks(minPos, maxPos, onBlock);

        scanWorld = null;
        receiver = null;
        return sum;
    }

    void OnBlock(Block block, int x, int y, int z)
    {
        bool[] ids = candidates!;
        int id = block.BlockId;
        if (id >= 0 && id < ids.Length && !ids[id]) return;

        probe.Set(x, y, z);
        IHeatSource? source = block.GetInterface<IHeatSource>(scanWorld!, probe);
        if (source == null) return;

        float strength = source.GetHeatStrength(scanWorld!, probe, receiver!);
        if (strength <= 0f) return;

        double d2 = probe.DistanceSqToNearerEdge(rx, ry, rz);
        sum += strength * Math.Min(1f, 9f / (8f + (float)Math.Pow(d2, 1.25)));
    }

    /// <summary>The ids GetInterface can answer for: the block itself, one of its block behaviors, or a block entity
    /// (whose class or behaviors may implement the interface; which of them do is only known per instance). An id
    /// outside this array, which a block registered after the build would have, is treated as a candidate.</summary>
    static bool[] BuildCandidates(IWorldAccessor world)
    {
        IList<Block> blocks = world.Blocks;
        bool[] ids = new bool[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            Block? block = blocks[i];
            if (block == null) continue;
            bool maybe = block is IHeatSource || block.EntityClass != null;
            if (!maybe)
            {
                foreach (BlockBehavior behavior in block.BlockBehaviors)
                {
                    if (behavior is IHeatSource) { maybe = true; break; }
                }
            }
            ids[i] = maybe;
        }
        return ids;
    }

    void Purge(long now)
    {
        nextPurgeMs = now + PurgeEveryMs;
        foreach (KeyValuePair<BlockPos, Entry> kv in cache)
        {
            if (kv.Value.ExpiresMs <= now) expired.Add(kv.Key);
        }
        foreach (BlockPos key in expired) cache.Remove(key);
        expired.Clear();
    }
}
