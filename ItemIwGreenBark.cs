using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// The two GREEN bark kinds (<c>bark-tan-green</c>, <c>bark-birch-green</c>, via <c>classByType</c> in bark.json): the
/// only bark that carries a Dry transition. Everything here is about WHERE it dries and how fast.
///
/// <para>Vanilla sets the Dry rate by location alone. The player's inventory runs at 1. Every block container, a ground
/// pile included, runs at 0.25, because <c>InWorldContainer.Inventory_OnAcquireTransitionSpeed</c> answers 0.25 for Dry
/// and Melt, so at the default 96 h a chest or a pile took 384 h. This class keeps the chest at the vanilla quarter, lifts a ground pile to
/// pocket speed (author decision 2026-09-26: a pile of bark on the floor is the natural place to dry it), and near any
/// <see cref="IHeatSource"/> pulls the rate up towards <see cref="HeatTargetRate"/>, twice the pocket, in any block
/// container. A lit firepit in the next cell reaches the target: a pile goes 1 → 2, a chest 0.25 → 2.</para>
///
/// <para>The seam is <see cref="CollectibleObject.GetTransitionRateMul"/>, virtual on the collectible and not routed
/// through CollectibleBehavior, so the item needs a class of its own. It is called with the slot, which is how the
/// position is found: <c>BlockEntityContainer.Initialize</c> gives a block container's inventory its <c>Pos</c> on both
/// sides, and a player's inventory, a dropped item and a bag on the ground carry none and keep their vanilla rate. One
/// vanilla container loses it again, see <see cref="RestoreContainerPos"/>.</para>
/// </summary>
public class ItemIwGreenBark : Item, IContainedCustomName
{
    /// <summary>The Dry rate near full heat, relative to the player's inventory.</summary>
    const float HeatTargetRate = 2f;

    /// <summary><c>BlockEntityGroundStorage.InventoryClassName</c>; <c>InventoryBase.ClassName</c> is the part of the
    /// inventory id before the first dash.</summary>
    const string PileInventoryClass = "groundstorage";

    /// <summary>
    /// Most game hours of one update that may run at the heated rate.
    ///
    /// <para>The engine samples the rate at the moment of an update and charges it to the whole gap since the last one
    /// (<c>UpdateAndGetTransitionStatesNative</c>). A firepit or an oven burns fuel by real time, so in an unloaded chunk it
    /// never burns out: a pile left beside a lit fire and revisited a week later would dry the whole week at the heated
    /// rate in one tick. A container ticks every 10 s, about 0.08 game hours, and the longest sleep runs about 10 game
    /// hours, so 12 cuts neither; it only trims gaps no fire was really watched through. The cap cannot tell a fuelled
    /// source from a constant one, so lava, boiling water and embers, which do keep heating an unloaded chunk, are trimmed
    /// too: a chest by lava left alone for a week comes back part dry instead of dry. Accepted as the price of one simple
    /// rule. Server only: on the client the
    /// rate feeds nothing but what the player reads, and there the cap would skew the percent a chest tooltip
    /// extrapolates from a copy the server sent long ago.</para>
    /// </summary>
    const double HeatCapHours = 12;

    /// <summary>
    /// The server re-sends a pile to clients each time its drying crosses a 1/<c>SyncSteps</c> step.
    ///
    /// <para>A ground pile has no inventory dialog, so vanilla syncs it only when a stack's code changes. Its client copy
    /// would otherwise keep the drying state of whenever bark was last added, and the pile readout
    /// (<see cref="GetContainedInfo"/>) would stand still. 20 steps are 20 small block-entity packets per pile over the
    /// whole drying time, and each makes every client that has the chunk loaded redraw that chunk.</para>
    /// </summary>
    const int SyncSteps = 20;

    ImmersiveWoodworkingModSystem? modSystem;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        modSystem = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
    }

    public override float GetTransitionRateMul(IWorldAccessor world, ItemSlot inSlot, EnumTransitionType transType)
    {
        float mul = base.GetTransitionRateMul(world, inSlot, transType);
        // mul 0 is a deliberate stop (a sealed barrel with a recipe, creative, a crafting output); heat does not restart it.
        if (transType != EnumTransitionType.Dry || mul <= 0f) return mul;

        InventoryBase? inv = inSlot.Inventory;
        BlockPos? pos = inv?.Pos;
        if (inv == null || pos == null) return mul;

        // Never SLOWER than the pocket, rather than exactly 1: a mod that makes piles dry faster keeps its bonus.
        if (inv.ClassName == PileInventoryClass && mul < 1f) mul = 1f;
        if (mul >= HeatTargetRate || modSystem == null) return mul;

        float heat = modSystem.BarkHeat.HeatFraction(world, pos);
        if (heat <= 0f) return mul;
        if (world.Side == EnumAppSide.Server) heat *= HeatedShareOfGap(world, inSlot.Itemstack);

        return mul + (HeatTargetRate - mul) * heat;
    }

    /// <summary>The share of the gap about to be charged that may run heated, 0..1 (see <see cref="HeatCapHours"/>).
    /// Read before the engine writes <c>lastUpdatedTotalHours</c>, which it does only after asking for the rate.</summary>
    static float HeatedShareOfGap(IWorldAccessor world, ItemStack? stack)
    {
        if (stack?.Attributes?["transitionstate"] is not ITreeAttribute state) return 1f;
        double now = world.Calendar.TotalHours;
        double gap = now - state.GetDouble("lastUpdatedTotalHours", now);
        return gap > HeatCapHours ? (float)(HeatCapHours / gap) : 1f;
    }

    public override TransitionState[] UpdateAndGetTransitionStates(IWorldAccessor world, ItemSlot inslot)
    {
        InventoryBase? inv = inslot.Inventory;
        if (world.Side != EnumAppSide.Server || inv?.Pos == null || inv.ClassName != PileInventoryClass)
        {
            return base.UpdateAndGetTransitionStates(world, inslot);
        }

        int stepBefore = SyncStep(inslot.Itemstack);
        TransitionState[] states = base.UpdateAndGetTransitionStates(world, inslot);

        // Converted to dry bark: the container already syncs on a code change (InWorldContainer.OnTick).
        ItemStack? after = inslot.Itemstack;
        if (after?.Collectible == this && SyncStep(after) != stepBefore)
        {
            world.BlockAccessor.GetBlockEntity(inv.Pos)?.MarkDirty();
        }
        return states;
    }

    /// <summary>
    /// Give a just-placed container's inventory back the <c>Pos</c> that <see cref="GetTransitionRateMul"/> finds heat
    /// by. Hooked to the server's <c>DidPlaceBlock</c>.
    ///
    /// <para>A vanilla crate placed from a stack whose type, label or lid state differs from the block's defaults (every
    /// crate crafted from a wood other than aged, any labelled crate) rebuilds its inventory in <c>OnBlockPlaced</c>,
    /// after <c>Initialize</c> set <c>Pos</c>, and never sets it again (BlockEntityCrate.OnBlockPlaced). Such a crate
    /// dried at the cold rate beside a fire until its chunk reloaded. The chest does the same rebuild but restores
    /// <c>Pos</c> itself. Assigning the block entity's own position is what <c>Initialize</c> does, so this changes
    /// nothing for a container that already has it. The placing player's client keeps the rebuilt inventory without
    /// <c>Pos</c> until the chunk reloads, so its tooltip can show the cold rate there; the drying itself is the
    /// server's.</para>
    /// </summary>
    internal static void RestoreContainerPos(IWorldAccessor world, BlockPos? pos)
    {
        if (pos == null) return;
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer container
            && container.Inventory != null && container.Inventory.Pos == null)
        {
            container.Inventory.Pos = container.Pos;
        }
    }

    int SyncStep(ItemStack? stack) => (int)(DriedFraction(stack) * SyncSteps);

    /// <summary>How far the stack has dried, 0..1, read straight from its stored transition state WITHOUT advancing it.
    /// On the client that is the state the server last sent, so the pile readout never runs ahead of the server.</summary>
    float DriedFraction(ItemStack? stack)
    {
        if (stack?.Attributes?["transitionstate"] is not ITreeAttribute state) return 0f;
        TransitionableProperties[]? props = TransitionableProps;
        if (props == null) return 0f;

        int k = Array.FindIndex(props, p => p?.Type == EnumTransitionType.Dry);
        float[]? fresh = (state["freshHours"] as FloatArrayAttribute)?.value;
        float[]? hours = (state["transitionHours"] as FloatArrayAttribute)?.value;
        float[]? done = (state["transitionedHours"] as FloatArrayAttribute)?.value;
        if (k < 0 || fresh == null || hours == null || done == null) return 0f;
        if (k >= fresh.Length || k >= hours.Length || k >= done.Length || hours[k] <= 0f) return 0f;

        return GameMath.Clamp((done[k] - fresh[k]) / hours[k], 0f, 1f);
    }

    /// <summary>The pile's content line: vanilla shows nothing about drying for a ground pile, so this is where the
    /// player sees both the progress and what the spot does to it (pocket speed on the floor, up to twice that by a
    /// fire). The rate is this side's own answer, the percent the server's last word.</summary>
    public string? GetContainedInfo(ItemSlot inSlot)
    {
        ItemStack? stack = inSlot.Itemstack;
        if (stack == null) return null;

        int percent = (int)(DriedFraction(stack) * 100f);
        double rate = Math.Round(GetTransitionRateMul(api.World, inSlot, EnumTransitionType.Dry), 1);
        return Lang.Get("immersivewoodworking:contained-greenbark-drying", stack.GetName(), percent, rate);
    }

    /// <summary>A plain title: null here would make the pile reuse its content line, percent and all, as the title
    /// (see <see cref="TarRig.ContainedPileTitle"/>).</summary>
    public string? GetContainedName(ItemSlot inSlot, int quantity) => TarRig.ContainedPileTitle(inSlot, quantity);
}
