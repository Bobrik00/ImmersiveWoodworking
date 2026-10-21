using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace ImmersiveWoodworking;

/// <summary>
/// T04.01 — the automated sawmill's node on Vintage Story's mechanical-power network. A terminal CONSUMER
/// (dead-end, like the pulverizer/quern): an axle/angledgears/toggle brought to its connector face feeds it
/// <see cref="BEBehaviorMPConsumer.TrueSpeed"/> = |Network.Speed × GearedRatio|, &gt;0 while the network turns and
/// 0 at rest. Everything downstream (load T04.02, effective speed T04.04, the cut cycle E06) reads the RPM from
/// here via the master's <see cref="BlockEntitySawmill.NetworkSpeed"/>.
///
/// WHERE IT LIVES — and why NOT on the master. The master cell of the 19-cell footprint is INTERIOR (its four
/// horizontal neighbours at Y=0 are all filler cells, see <see cref="BlockEntitySawmill.FootprintLocal"/>), so no
/// external axle can ever sit adjacent to it — the vanilla "connector on the master block" pattern (BlockPulverizer,
/// whose master IS its whole exposed ground cell) is geometrically impossible here. The engine also requires the
/// MP node to sit on the very cell the axle touches (<c>BEBehaviorMPBase.tryConnect</c> reads
/// <c>GetBlockEntity(connectorPos).GetBehavior&lt;BEBehaviorMPBase&gt;()</c> and dereferences it). So this behavior
/// is attached — via the <c>sawmill-ghostpower</c> blocktype's entityBehaviors — to the BE of the power-input ghost
/// occupying the exposed -X gear-tower cell (where the model's <c>rotor_default</c> disc sits, axis X). The master
/// reads its speed cross-cell. The other 17 ghost cells carry no MP role (plain <see cref="BlockSawmillGhost"/>),
/// exactly as vanilla's pulverizer-top ghost carries none.
///
/// Subclasses <see cref="BEBehaviorMPConsumer"/> for the free terminal-consumer plumbing (empty GetMechPowerExits,
/// TrueSpeed, Resistance, OnConnected/OnDisconnected). It only customizes:
///   • <see cref="BEBehaviorMPBase.OutFacingForNetworkDiscovery"/> — set to the input face so the SERVER
///     self-discovers/rejoins the network in base.Initialize (survives chunk reload without touching the
///     server-authoritative networkid, which the base deliberately never reloads from tree).
///   • <see cref="GetResistance"/> / <see cref="JoinNetwork"/> (T04.02) — the network load keyed on the master's
///     assembly, and the anti-burst speed limit on joining an already-spinning line.
///
/// THIS NODE RENDERS NOTHING, so it also carries no spin axis (T07.08). <c>ghostpower.json</c> declares
/// <c>mechPartShape: null</c>, the vanilla quern/grindingwheel convention: <c>MechNetworkRenderer.AddDevice</c> bails
/// on a null <see cref="BEBehaviorMPBase.Shape"/>, so the engine never builds an instanced renderer for this cell.
/// (An EMPTY shape is NOT the same thing — that was F-L8-19: a renderer got built and issued a per-frame draw call for
/// a mesh with zero triangles.) The visible <c>rotor_default</c> disc belongs to the master's own E07 renderer,
/// <see cref="BlockEntitySawmill"/>.Render — one gear, not two (memory vs-sawmill-procedural-anim). Consequence for
/// <see cref="BEBehaviorMPBase.AxisSign"/>: only <c>MechBlockRenderer</c> subclasses ever read it, so whatever the
/// consumer base seeds from <c>Block.Variant["side"]</c> is inert here and this class deliberately leaves it alone.
/// </summary>
public class BEBehaviorSawmillMP : BEBehaviorMPConsumer
{
    public BEBehaviorSawmillMP(BlockEntity blockentity) : base(blockentity) { }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        // Feed facing comes from THIS power-ghost's own "side" variant (stamped to the master's facing at
        // placement) — available now, unlike the master link (Principal is set just AFTER Initialize during
        // placement / chunk load, so it can't be read here). OutFacingForNetworkDiscovery MUST be set before
        // base.Initialize, which is where the server fires CreateJoinAndDiscoverNetwork on it.
        BlockFacing feed = BlockFacing.FromCode(Block.Variant["side"]) ?? BlockFacing.NORTH;
        OutFacingForNetworkDiscovery = BlockEntitySawmill.InputFaceFor(feed);

        // base wiring + MPConsumer (reads mechPartShape → null here, so the render device is dropped again right away)
        // + server network discovery. Nothing to add on top: with no renderer there is no spin axis to re-derive.
        base.Initialize(api, properties);
    }

    // ───────────────────────── Network load + speed limit (T04.02) ─────────────────────────

    // Network load of an unassembled / half-built frame: barely-present, so an empty footprint doesn't tax the
    // network for nothing. Matches the no-axle branch of BEBehaviorMPPulverizer.GetResistance (0.005f). The ASSEMBLED
    // load is configurable (SawmillResistance); this floor is not — it just means "the mechanism isn't really there".
    const float IncompleteResistance = 0.005f;

    ImmersiveWoodworkingModSystem? modSystem;
    // Live, server-synced config (same accessor pattern as BlockEntitySawmill/BlockEntitySawhorse) — a server can
    // retune SawmillResistance without a recompile, and the value reaches clients.
    ImmersiveWoodworkingConfig Config => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).Config;

    /// <summary>The master sawmill BE this power node serves, reached cross-cell through the power-ghost's
    /// <see cref="BEMultiblockSawmill.Principal"/> link (the node lives on the input ghost, never on the master —
    /// see the class summary), or null while that link is unset (mid-placement) or the master's chunk isn't loaded.
    /// Resolved fresh each call — a cheap BE lookup, exactly as the pulverizer re-fetches its BEPulverizer in
    /// GetResistance.</summary>
    BlockEntitySawmill? Master
    {
        get
        {
            if (Blockentity is not BEMultiblockSawmill ghost || ghost.Principal == null || Api == null) return null;
            return Api.World.BlockAccessor.GetBlockEntity(ghost.Principal) as BlockEntitySawmill;
        }
    }

    /// <summary>
    /// How much load the sawmill puts on its mechanical network — queried by the network every tick during the
    /// torque/resistance balance (<c>MechanicalNetwork.updateNetwork → GetTorque → GetResistance</c>). Mirrors
    /// <c>BEBehaviorMPPulverizer.GetResistance</c>'s <c>hasAxle ? 0.085 : 0.005</c> split, but keyed on the MASTER's
    /// assembly: an assembled sawmill (<see cref="BlockEntitySawmill.IsComplete"/>) loads the line like a real
    /// machine, a bare / half-built frame barely drags it. Load depends on the MECHANISM, not the blade kit — the
    /// blades only set cut SPEED (T04.04), so this never reads them. The optional flywheel eases the draw
    /// (× the baked flywheel network-load-relief constant); it lowers LOAD only, never the
    /// speed ceiling.
    /// </summary>
    public override float GetResistance()
    {
        BlockEntitySawmill? master = Master;
        if (master == null || !master.IsComplete) return IncompleteResistance;

        // The flywheel network-load relief was a dev tuning dial, finalized in-game and baked as a constant
        // (2026-06-21; was config).
        const float FlywheelResistanceMult = 0.6f;
        float resistance = Config.SawmillResistance;
        if (master.HasFlywheel) resistance *= FlywheelResistanceMult;
        return resistance;
    }

    /// <summary>
    /// Anti-burst speed limit on joining an already-spinning network — copied 1:1 from
    /// <c>BEBehaviorMPPulverizer.JoinNetwork</c> (the ×1.6 and the &gt;1 threshold are vanilla-calibrated; copied, not
    /// "improved", so the sawmill behaves like every other consumer on the line). Without it a node dropped onto a
    /// fast, low-resistance network would instantly adopt that speed — a visible jolt of the saw frame. We damp the
    /// network's <c>Speed</c> back toward a sane value AND its <c>clientSpeed</c> (or the client shows a jolt the
    /// server doesn't); if the line genuinely has the torque to drive faster, the resistance/torque balance spins it
    /// back up afterwards. <c>base.JoinNetwork</c> first — it performs the actual join (and fires
    /// <c>BEBehaviorMPConsumer.OnConnected</c>); the limit is a correcting step on top. Null-guarded: <c>network</c>
    /// is null when the node LEAVES a network, which must not throw.
    /// </summary>
    public override void JoinNetwork(MechanicalNetwork network)
    {
        base.JoinNetwork(network);

        // network is null when the node LEAVES a network — nothing to damp then, and dereferencing it would throw.
        // (The vanilla original folds this into `network == null ? 0f : …`; under this mod's Nullable=enable the
        // compiler can't carry that test into the block below, so the guard is explicit — behaviour is identical.)
        if (network == null) return;

        float speed = Math.Abs(network.Speed * GearedRatio) * 1.6f;
        if (speed > 1f)
        {
            network.Speed /= speed;
            network.clientSpeed /= speed;
        }
    }
}
