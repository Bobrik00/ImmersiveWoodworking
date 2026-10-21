using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace ImmersiveWoodworking;

/// <summary>
/// T04.01 — the automated chopper's node on Vintage Story's mechanical-power network, the analogue of the sawmill's
/// <see cref="BEBehaviorSawmillMP"/>. A terminal CONSUMER (dead-end, like the pulverizer/quern): an axle/angledgears/
/// toggle brought to its connector face feeds it <see cref="BEBehaviorMPConsumer.TrueSpeed"/> = |Network.Speed ×
/// GearedRatio|, &gt;0 while the network turns and 0 at rest. Everything downstream (load T04.02, effective speed
/// T04.03, the chop cycle E06) reads the RPM from here via the master's <see cref="BlockEntityChopper.NetworkSpeed"/>.
///
/// WHERE IT LIVES — and why NOT on the master. The master cell of the footprint is INTERIOR (its horizontal
/// neighbours at Y=0 are filler cells, see <see cref="BlockEntityChopper.FootprintLocal"/>), so no external axle can
/// ever sit adjacent to it — the vanilla "connector on the master block" pattern (BlockPulverizer, whose master IS its
/// whole exposed ground cell) is geometrically impossible here. The engine also requires the MP node to sit on the very
/// cell the axle touches (<c>BEBehaviorMPBase.tryConnect</c> reads
/// <c>GetBlockEntity(connectorPos).GetBehavior&lt;BEBehaviorMPBase&gt;()</c> and dereferences it). So this behavior is
/// attached — via the <c>chopper-ghostpower</c> blocktype's entityBehaviors — to the BE of the power-input ghost
/// occupying the exposed drive-cam cell (<see cref="BlockEntityChopper.InputCellLocal"/>, where the model's input cam
/// disc sits). The master reads its speed cross-cell through <see cref="BEMultiblockChopper.Principal"/>. The other
/// ghost cells carry no MP role (plain <see cref="BlockChopperGhost"/>), exactly as vanilla's pulverizer-top ghost
/// carries none. Memory vs-mp-consumer-interior-master.
///
/// Subclasses <see cref="BEBehaviorMPConsumer"/> for the free terminal-consumer plumbing (empty GetMechPowerExits,
/// TrueSpeed, Resistance, OnConnected/OnDisconnected). It customizes:
///   • <see cref="BEBehaviorMPBase.OutFacingForNetworkDiscovery"/> — set to the input face BEFORE base.Initialize, so
///     the SERVER self-discovers/rejoins the network there (survives chunk reload without touching the
///     server-authoritative networkid, which the base deliberately never reloads from tree).
///   • <see cref="GetResistance"/> / <see cref="JoinNetwork"/> (T04.02) — the network load keyed on the master's
///     assembly, and the anti-burst speed limit on joining an already-spinning line.
/// The torque/head gate (EffectiveChopSpeed) is the master's, T04.03.
///
/// THIS NODE RENDERS NOTHING, so it also carries no spin axis (T07.08). <c>ghostpower.json</c> declares
/// <c>mechPartShape: null</c>, the vanilla quern/grindingwheel convention: <c>MechNetworkRenderer.AddDevice</c> bails
/// on a null <see cref="BEBehaviorMPBase.Shape"/>, so the engine never builds an instanced renderer for this cell.
/// (An EMPTY shape is NOT the same thing — that was F-L8-19: a renderer got built and issued a per-frame draw call for
/// a mesh with zero triangles.) The visible input rotor belongs to the master's own renderer,
/// <see cref="ChopperRenderer"/>, driven by <c>animAngleSigned</c> — one gear, not two (memory
/// vs-sawmill-procedural-anim). Consequence for <see cref="BEBehaviorMPBase.AxisSign"/>: only
/// <c>MechBlockRenderer</c> subclasses ever read it, so whatever the consumer base seeds from
/// <c>Block.Variant["side"]</c> is inert here and this class deliberately leaves it alone.
/// </summary>
public class BEBehaviorChopperMP : BEBehaviorMPConsumer
{
    public BEBehaviorChopperMP(BlockEntity blockentity) : base(blockentity) { }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        // Feed facing comes from THIS power-ghost's own "side" variant (stamped to the master's facing at placement) —
        // available now, unlike the master link (Principal is set just AFTER Initialize during placement / chunk load,
        // so it can't be read here). OutFacingForNetworkDiscovery MUST be set before base.Initialize, which is where the
        // server fires CreateJoinAndDiscoverNetwork on it.
        BlockFacing feed = BlockFacing.FromCode(Block.Variant["side"]) ?? BlockFacing.NORTH;
        OutFacingForNetworkDiscovery = BlockEntityChopper.InputFaceFor(feed);

        // base wiring + MPConsumer (reads mechPartShape → null here, so the render device is dropped again right away)
        // + server network discovery. Nothing to add on top: with no renderer there is no spin axis to re-derive.
        base.Initialize(api, properties);
    }

    // ───────────────────────── Network load + speed limit (T04.02) ─────────────────────────

    // Network load of an unassembled / half-built frame: barely-present, so an empty footprint doesn't tax the
    // network for nothing. Matches the no-axle branch of BEBehaviorMPPulverizer.GetResistance (0.005f), same value as
    // BEBehaviorSawmillMP. The ASSEMBLED load is configurable (ChopperResistance); this floor is not — it just means
    // "the mechanism isn't really there".
    const float IncompleteResistance = 0.005f;

    ImmersiveWoodworkingModSystem? modSystem;
    // Live, server-synced config (same accessor pattern as BlockEntityChopper/BlockEntitySawmill) — a server can retune
    // ChopperResistance without a recompile, and the value reaches clients.
    ImmersiveWoodworkingConfig Config => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).Config;

    /// <summary>The master chopper BE this power node serves, reached cross-cell through the power-ghost's
    /// <see cref="BEMultiblockChopper.Principal"/> link (the node lives on the input ghost, never on the master — see
    /// the class summary), or null while that link is unset (mid-placement) or the master's chunk isn't loaded.
    /// Resolved fresh each call — a cheap BE lookup, exactly as the pulverizer re-fetches its BEPulverizer in
    /// GetResistance and BEBehaviorSawmillMP re-fetches its master.</summary>
    BlockEntityChopper? Master
    {
        get
        {
            if (Blockentity is not BEMultiblockChopper ghost || ghost.Principal == null || Api == null) return null;
            return Api.World.BlockAccessor.GetBlockEntity(ghost.Principal) as BlockEntityChopper;
        }
    }

    /// <summary>
    /// How much load the chopper puts on its mechanical network — queried by the network every tick during the
    /// torque/resistance balance (<c>MechanicalNetwork.updateNetwork → GetTorque → GetResistance</c>). Mirrors
    /// <c>BEBehaviorMPPulverizer.GetResistance</c>'s <c>hasAxle ? 0.085 : 0.005</c> split, but keyed on the MASTER's
    /// assembly: an assembled chopper (<see cref="BlockEntityChopper.IsComplete"/>) loads the line like a real machine,
    /// a bare / half-built frame barely drags it. Load depends on the MECHANISM, not the head — the head only sets the
    /// WEAR tier (T04.03/E06), so this never reads it. (The optional flywheel that used to ease this draw was cut
    /// 2026-07-04, so the assembled load is simply the live <see cref="ImmersiveWoodworkingConfig.ChopperResistance"/>.)
    /// </summary>
    public override float GetResistance()
    {
        BlockEntityChopper? master = Master;
        if (master == null || !master.IsComplete) return IncompleteResistance;

        return Config.ChopperResistance;
    }

    /// <summary>
    /// Anti-burst speed limit on joining an already-spinning network — copied 1:1 from
    /// <c>BEBehaviorMPPulverizer.JoinNetwork</c> (the ×1.6 and the &gt;1 threshold are vanilla-calibrated; copied, not
    /// "improved", so the chopper behaves like every other consumer on the line). Without it a node dropped onto a
    /// fast, low-resistance network would instantly adopt that speed — a visible jolt of the axe arm. We damp the
    /// network's <c>Speed</c> back toward a sane value AND its <c>clientSpeed</c> (or the client shows a jolt the server
    /// doesn't); if the line genuinely has the torque to drive faster, the resistance/torque balance spins it back up
    /// afterwards. <c>base.JoinNetwork</c> first — it performs the actual join (and fires
    /// <c>BEBehaviorMPConsumer.OnConnected</c>); the limit is a correcting step on top. Null-guarded: <c>network</c> is
    /// null when the node LEAVES a network, which must not throw.
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
