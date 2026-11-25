using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// The debark cycle (AutoDebarkerPlan E06): progress from the shaft's turn, frozen while idle (T06.01), the finish that
/// yields the debarked log and the bark (T06.02), the eject (T06.03), the start gate <c>CanDebark</c> (T06.04) and the
/// knife wear (T06.05). The disc scrapes continuously, so the progress follows the sawmill's continuous feed
/// (<c>OnSawmillTick</c> in its Sawing partial), not the chopper's per-strike steps.
/// </summary>
public partial class BlockEntityDebarker
{
    /// <summary>
    /// The start gate (T06.04): whether the machine may strip right now. One predicate, so every reader asks the same
    /// question and the server and the client cannot drift: the server tick (<see cref="OnDebarkerTick"/>), and on the
    /// client the progress estimate that turns the log and hides its bark strips (T07.04), the bark chips (T07.05), the
    /// scraping sound (T07.06) and the block info (T08.03). A calque of the chopper's <see cref="BlockEntityChopper.CanChop"/> and the
    /// sawmill's <see cref="BlockEntitySawmill.CanSaw"/>, lifted out of the tick's inline gate of T06.01 as it stood.
    ///
    /// Two terms where the chopper spells three. Assembly has no term of its own: <see cref="EffectiveDebarkSpeed"/>
    /// returns 0 on a machine missing a part or the knife set (<see cref="IsComplete"/>), as it does below
    /// <see cref="ImmersiveWoodworkingConfig.DebarkerMinTorque"/>. The log is asked <see cref="IsValidInput"/>, the input
    /// filter, which builds the debarked log's code through the same <see cref="BlockEntitySawhorse.ResolveDebarkProduct"/>
    /// that <see cref="CompleteDebark"/> calls, so a log the gate lets through always has a product at the finish. There
    /// is no output term: both products are thrown out (T06.03), so a finish is never held up.
    ///
    /// A pure read on either side: the parts and the slot come with the block entity sync, the network speed with the
    /// mech-network packet, the config on joining, and the resolve is a registry lookup. For a moment after one of them
    /// changes the client may disagree with the server, as <see cref="EffectiveDebarkSpeed"/> notes.
    /// </summary>
    public bool CanDebark()
        // Gate on EffectiveDebarkSpeed, never on NetworkSpeed > 0: a stopped or axle-less machine reads a few millionths,
        // and only the floor stops it (memory vs-mechnetwork-speed-never-zero). The log is asked IsValidInput, not just
        // whether the slot is full: a log whose mod was removed stays in the slot as a stub after a load, no filter on a
        // move ever sees it, and CompleteDebark would turn it into a stub debarked log. The free speed check goes first
        // and the resolve, which builds the product code and asks the registry, only on a running machine: the order of
        // the sawmill's CanSaw.
        => EffectiveDebarkSpeed() > 0f && IsValidInput(Api.World, InputSlot.Itemstack);

    /// <summary>
    /// The debark heartbeat (T06.01): a 25 ms tick, the period of vanilla <c>BEHelveHammer.onEvery25ms</c>, that
    /// <see cref="Initialize"/> registers on the server only, as the sawmill and the chopper register theirs. While
    /// <see cref="CanDebark"/> holds it adds the part of a log the shaft's turn strips (<see cref="DebarkAdvance"/>) to
    /// <c>debarkProgress</c>. Otherwise it returns and leaves the progress where it stands, so a log resumes from the same
    /// fraction once the line turns again, as the sawmill's feed does. The client never integrates here: it gets the
    /// progress by sync, and its own estimate comes with the render (T07.04).
    /// </summary>
    void OnDebarkerTick(float dt)
    {
        if (!CanDebark()) return;

        float before = debarkProgress;
        bool wasRecoverable = LogRecoverable;
        debarkProgress += DebarkAdvance(dt);
        if (debarkProgress > 1f) debarkProgress = 1f;

#if IW_DEBUG
        if (before <= 0f) { dbgLogStartMs = Api.World.ElapsedMilliseconds; dbgLogStartShaftAngle = DbgShaftAngle(); }
#endif

        // Sync on a 5% grid, as the sawmill does: the client's copy (the value the log's estimate of T07.04, and with
        // it the percent in the block info of T08.03, is pulled toward) moves only when the server marks the block entity dirty. Data only, with no retesselation, since nothing in
        // the chunk mesh depends on the progress. The step onto 1 is a grid step too. On top, the flip of LogRecoverable:
        // the take or scrap hint (T08.01) turns at LogRecoverThreshold, which falls on the grid only at multiples of 5%
        // (memory vs-hint-threshold-needs-own-sync).
        if ((int)(before * 20f) != (int)(debarkProgress * 20f) || wasRecoverable != LogRecoverable) MarkDirty();

        if (debarkProgress >= 1f)
        {
#if IW_DEBUG
            if (before < 1f) DbgLogStripped();
#endif
            CompleteDebark();
        }
    }

    /// <summary>
    /// The finish of one log (T06.02), which <see cref="OnDebarkerTick"/> calls once <c>debarkProgress</c> reaches 1, so
    /// it runs on the server only, the tick's side. The output is the sawhorse pair's with the bark spud (decision В2):
    /// the debarked log of the same wood and orientation, built by the pair's
    /// <see cref="BlockEntitySawhorse.ResolveDebarkProduct"/> (the log's own domain, then game, then the remap table,
    /// through <see cref="BlockEntitySawhorse.ResolveProductBlock"/>) and, with the bark chain on, the stack of bark the
    /// pair builds (<see cref="BarkDropTable.BuildBarkStack"/>) at the spud's
    /// <see cref="ImmersiveWoodworkingConfig.BarkChanceMultSpud"/>, without the spud's metal bonus: the knives' metal sets
    /// only their wear. No xSkills XP and no <c>carefulsawyer</c>, which the pair grants its players: no player stands at
    /// the machine.
    ///
    /// The order is the sawmill's and the chopper's <c>CompletePass</c> since ModDbFeedbackPlan T05.01: the output is
    /// computed while the log is still in the slot, then the log is taken and the progress zeroed, and only then the eject
    /// (T06.03) and the knife wear (T06.05) run, each in a try of its own. The eject goes through every mod's item-spawn
    /// hooks, and a throw out of a block entity tick leaves the engine's tick stamp unmoved, so the tick re-runs on every
    /// server frame (memory vs-be-tick-exception-keeps-ticking). With the log already taken, a re-run finds the slot empty,
    /// the gate shut and nothing to give twice; the catches keep such a throw inside the tick in the first place.
    /// </summary>
    void CompleteDebark()
    {
        Block? debarked = BlockEntitySawhorse.ResolveDebarkProduct(Api.World, InputSlot.Itemstack);
        string? wood = InputWoodSpecies;
        if (debarked == null || wood == null)
        {
            // A safeguard that never fires: the gate of this same tick (CanDebark) asked IsValidInput, which calls the
            // same ResolveDebarkProduct on the same stack and refuses a stub on top, and nothing touches the slot or the
            // registry in between. A log that resolves has its wood too, read off the same code part. The check stays
            // because the nullable compiler wants one anyway, and a bare ! would let a null from a future caller that
            // skips the gate destroy the log: the slot is emptied before the eject, so new ItemStack(null) would throw
            // inside the eject's try, be reported once per debarker and leave neither the log nor its bark. As the
            // sawmill and the chopper do on an empty batch, the progress goes back to 0 and the log stays; they log
            // nothing, this warns, once per log's worth of work rather than on every tick. Only the progress changed, so
            // no retesselation.
            Api.Logger.Warning("[immersivewoodworking] Debarker at {0}: no debarked log to strip {1} into, leaving it in the machine with its bark.", Pos, InputSlot.Itemstack?.Block?.Code);
            debarkProgress = 0f;
            MarkDirty();
            return;
        }

        // With the bark chain switched off the machine gives the debarked log alone, as the pair does (decision В4).
        ItemStack? bark = Config.EnableBarkChain
            ? BarkDropTable.BuildBarkStack(Api, modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>(), wood, Config.BarkChanceMultSpud)
            : null;

        // ModDbFeedbackPlan T05.01: take the log and zero the progress BEFORE anything leaves the machine. The eject gets
        // what is captured above; neither it nor the wear reads the slot. The slot change also raises SlotModified, whose
        // handler marks the block entity once more, as the chopper's does.
        debarkProgress = 0f;
        InputSlot.Itemstack = null;
        InputSlot.MarkDirty();
        MarkDirty(true);

        try
        {
            EjectDebarked(new ItemStack(debarked), bark);
        }
        catch (Exception e)
        {
            LogFinishThrowOnce("ejecting the debarked log and the bark", e);
        }

        // Last, in a try of its own, as the sawmill wears its blade kit: a spawn hook that throws on every eject can't
        // leave the knives unworn for good.
        try
        {
            DamageKnives();
        }
        catch (Exception e)
        {
            LogFinishThrowOnce("wearing the knives", e);
        }
    }

    // Transient: one report per loaded debarker, as the sawmill and the chopper keep (ModDbFeedbackPlan T05.01). A
    // foreign spawn hook that throws on every eject would otherwise print a stack trace per log.
    bool finishThrowLogged;

    /// <summary>Report a finish whose eject or knife wear threw (<see cref="CompleteDebark"/> has already taken the log,
    /// so the machine goes on with the next one), once per debarker, with the mod prefix the engine's own catch would not
    /// add. Twin of the sawmill's and the chopper's <c>LogPassThrowOnce</c>. Server-side.</summary>
    void LogFinishThrowOnce(string step, Exception e)
    {
        if (finishThrowLogged) return;
        finishThrowLogged = true;
        Api.Logger.Error("[immersivewoodworking] Debarker at {0}: {1} threw. The log is already consumed and the machine goes on; further throws on this debarker are not logged.", Pos, step);
        Api.Logger.Error(e);
    }

    // Where the debarked log and the bark leave the machine (T06.03): points in the master's LOCAL frame, in blocks from
    // the master cell's corner, authored facing SOUTH like FootprintLocal, and speeds in the engine's item motion units
    // (memory vs-item-drop-velocity-calibration). The author accepted all four in game at T07.07 (2026-11-25).
    //
    // The model's anchors, by the element matrices (the maths of AutoDebarkerPlan/scan_collision.py): the log's axis
    // runs along Z at x -4 px, y 34 px; the frame's exit end is at z -16 px; the chute slopes 12° down towards -X, and
    // the lip at its low end spans x -13.6..-12.3 px, y 1..5 px, z 1.5..14.5 px. Both points lie outside the footprint
    // because the cells on the way out are solid: (-1,2,-1) at the log's exit end is one full box, and (-1,0,0), which
    // holds the chute, is boxed up to y 0.5 across the whole cell. An item spawned inside a box stays in the frame
    // until its floatupwhenstuck behaviour shoves it out a moment later, to the nearest free spot within a block or
    // else straight up, rather than out along the throw (memory vs-entityitem-floatupwhenstuck). The item's box is 0.5
    // wide and 0.25 tall over the spawn point, which is its bottom centre (game:entities/item.json), so 0.3 past a face
    // clears the face by 0.05. So the bark appears about half a block out from the chute's lip. A solid block flush
    // against that face at floor level would take the bark in: the shove carries it out through a wall one block thick,
    // but behind a thicker one it rises and, once above y 0.5, slides into the frame over the chute, a pocket of
    // (-1,0,0) and (-1,1,0) open only to the -X side, where it is out of reach and despawns after 10 minutes like any
    // dropped item (despawn minSeconds 600, game:entities/item.json). So EjectDebarked sends the bark out with the log
    // whenever any block's box reaches into the bark's box at its point (the author's call at T07.07, 2026-11-25).
    static readonly (double X, double Y, double Z) LogEjectLocal = (-0.25, 2.0, -1.3);   // on the log's axis, past the exit end
    static readonly (double X, double Y, double Z) BarkEjectLocal = (-1.3, 0.2, 0.5);    // at the lip's height, past the -X face, mid-chute

    // Estimated from BehaviorPassivePhysics, not measured: from two blocks up the log flies about half a block before it
    // lands, so it settles in the first cell past the exit end, and on a hopper standing there. It is the sawmill's
    // feather push. The bark drops a fifth of a block and comes to rest about a quarter of a block from its spawn point.
    const double LogEjectSpeed = 0.02;
    const double BarkEjectSpeed = 0.02;

    // The item's box over its position, which is the box's bottom centre (hitboxSize 0.5 × 0.25 in
    // game:entities/item.json), shrunk by 0.01 as EntityBehaviorFloatUpWhenStuck tests it before the shove.
    static readonly Cuboidf StuckItemBox = new(-0.24f, 0.01f, -0.24f, 0.24f, 0.24f, 0.24f);

    /// <summary>
    /// The eject (T06.03): the debarked log along its own axis, out of the exit end, and the bark out of the chute's
    /// mouth under the disc (decision В3). There is no output slot and no hold on a full world: both are thrown under
    /// physics as the chopper's <see cref="BlockEntityChopper.EjectBatch"/> throws its firewood, and with a wall in the
    /// way they land where physics lets them and despawn by the world's rules. The exit end is local -Z, which is world
    /// <c>Facing.Opposite</c>: on the left of the player who placed the machine. The bark leaves through local -X, the
    /// side away from the coupling, which the author calls the machine's front and which faces that player (T07.07,
    /// 2026-11-25; <see cref="BlockDebarker.GetLookAwareBlockVariant"/>). Any block's box reaching into the bark's box
    /// at its point sends the bark out of the exit end with the log. Neither touches the roof, where the hopper of
    /// T05.04 feeds. <paramref name="bark"/> is null with the bark chain off or when the bark item is missing.
    /// Server-side, as its caller <see cref="CompleteDebark"/> is, which has already taken the log out of the slot.
    /// </summary>
    void EjectDebarked(ItemStack log, ItemStack? bark)
    {
        // Local +X and +Z on the world, by the turn RotateToFacing gives the footprint, so the points and the directions
        // follow the machine in all four facings. A point turns about the master cell's vertical centre line, the pivot of
        // the shape's rotateY in frame.json.
        Vec3i ax = RotateToFacing(new Vec3i(1, 0, 0), Facing);
        Vec3i az = RotateToFacing(new Vec3i(0, 0, 1), Facing);
        Vec3d WorldOf((double X, double Y, double Z) p) => Pos.ToVec3d().Add(
            0.5 + ax.X * (p.X - 0.5) + az.X * (p.Z - 0.5),
            p.Y,
            0.5 + ax.Z * (p.X - 0.5) + az.Z * (p.Z - 0.5));

        // Flat, with no lift, as the chopper tosses since the author asked for it 2026-07-14: the log already starts two
        // blocks up.
        Vec3d logAt = WorldOf(LogEjectLocal);
        Api.World.SpawnItemEntity(log, logAt, new Vec3d(-az.X * LogEjectSpeed, 0, -az.Z * LogEjectSpeed));

        // One stack whatever its size, as the sawhorse pair drops it: a stack above the bark item's max splits on pickup.
        // Any block's box reaching into the bark's box at its spawn point (a wall, a hopper on the ground, also a
        // bottom slab or deep snow) sends the bark out with the log instead (the author's call at T07.07, 2026-11-25):
        // behind a wall thicker than a block the shove would lift it into the pocket over the chute, where it despawns
        // out of reach. The test is the shove's own, so a hopper sunk with its top at floor level still takes the bark,
        // and one standing on the ground sends it on with the log.
        if (bark != null)
        {
            Vec3d barkAt = WorldOf(BarkEjectLocal);
            IBlockAccessor blocks = Api.World.BlockAccessor;
            if (Api.World.CollisionTester.GetCollidingCollisionBox(blocks, StuckItemBox, barkAt, false) == null)
                Api.World.SpawnItemEntity(bark, barkAt, new Vec3d(-ax.X * BarkEjectSpeed, 0, -ax.Z * BarkEjectSpeed));
            else
                Api.World.SpawnItemEntity(bark, WorldOf(LogEjectLocal),
                    new Vec3d(-az.X * LogEjectSpeed, 0, -az.Z * LogEjectSpeed));
        }

        // One sound per log, the debarked log's own place sound at its exit, so a log of another mod's wood sounds as
        // that mod made it. Since 1.22 BlockSounds.Place is a SoundAttributes; its point overload takes the internal Y
        // that Pos.ToVec3d gives, as vanilla BECrate's chalk sound passes it, and the server ranges the sound by that Y,
        // not by the dimension argument. Block.Sounds stays null on a block whose JSON has no sounds: BlockType copies
        // it only when set.
        BlockSounds? sounds = log.Block.Sounds;
        if (sounds != null) Api.World.PlaySoundAt(sounds.Place, logAt.X, logAt.Y, logAt.Z, Pos.dimension, null);
    }

    /// <summary>
    /// The knife wear (T06.05): one finished log costs the installed set
    /// <see cref="ImmersiveWoodworkingConfig.DebarkerKnifeWearMultiplier"/> points of its native durability, and a set
    /// worn to 0 breaks. How long a set lasts in each metal comes only from <c>durabilitybytype</c> in
    /// <c>itemtypes/debarker/knife.json</c>; this code never looks at the metal. Called once per log from
    /// <see cref="CompleteDebark"/>, after the log is taken and the eject has run. A calque of the sawmill's
    /// <see cref="BlockEntitySawmill.DamageBladeKit"/> and the chopper's <see cref="BlockEntityChopper.DamageHead"/>: the
    /// durability goes through <c>GetRemainingDurability</c> and <c>SetDurability</c> rather than the engine's
    /// <c>DamageItem</c>, which at zero plays the break sound and spawns the particles at an entity, and the machine has
    /// none.
    ///
    /// One difference from the calque: the wear goes into a clone of the set, which then replaces <c>knifeStack</c> in a
    /// single assignment, where the pair writes into the installed stack itself. It was planned against finding F-PT-15:
    /// <see cref="ToTreeAttributes"/> puts the live stack into the tree, and an autosave serializes that tree on the chunk
    /// thread while this tick runs (memory vs-totreeattributes-not-side-effect-free). The engine allows that write after
    /// all: a stack's attributes sit in a <c>ConcurrentSmallDictionary</c>, built for reads and writes at once, so a save
    /// takes the old durability or the new one. The clone stays as a cheap guard. No tick writes into an installed set (the
    /// install stores a clone too), a behaviour that throws inside <c>SetDurability</c> leaves the installed set as it was,
    /// and any of the three values the field can hold is a state worth saving: the old set, the worn one, or none after a
    /// break.
    ///
    /// A broken set is destroyed, not dropped. <see cref="IsComplete"/> turns false, so <see cref="CanDebark"/> shuts the
    /// gate, neither the hand nor a chute loads the next log, and the network load falls to the unassembled machine's
    /// (<see cref="BEBehaviorDebarkerMP.GetResistance"/>), all without a line of their own here. A worn set that the player
    /// takes out (<see cref="TryTakeKnives"/>) or that drops on teardown (<see cref="AppendPartDrops"/>) carries its
    /// durability, so the slot bar and the tooltip show the wear. Server-side, as its caller is.
    /// </summary>
    void DamageKnives()
    {
        ItemStack? knives = knifeStack;
        if (knives == null) return;

        float rate = Config.DebarkerKnifeWearMultiplier;
        if (rate <= 0f) return; // wear off; Sanitize clamps a negative to 0

        int max = knives.Collectible.GetMaxDurability(knives);
        if (max <= 1) return; // a set whose item declares no durability does not wear

        // Integer durability has no step below 1, so any positive rate costs at least 1 point per log.
        int cost = (int)Math.Round(rate);
        if (cost < 1) cost = 1;

        int left = knives.Collectible.GetRemainingDurability(knives) - cost;
        if (left > 0)
        {
            ItemStack worn = knives.Clone();
            worn.Collectible.SetDurability(worn, left);
            knifeStack = worn;
            // Data only, as in the pair: nothing in the chunk mesh depends on the durability. The tree sync carries the
            // worn set to the client, where the block info of T08.03 reads its durability.
            MarkDirty();
            return;
        }

        // CompleteDebark has already zeroed the progress, so unlike the pair this does not reset it again. No
        // UpdateRenderer either: this runs on the server, where it is a no-op, and clients drop the knife mesh when the
        // cleared stack syncs (FromTreeAttributes). The BlockPos overload plays at the master's centre, as the pair's
        // point at Pos + 0.5 does, and also keeps the dimension.
        knifeStack = null;
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/effect/toolbreak"), Pos, 0, null);
        MarkDirty(true);
    }

#if IW_DEBUG
    // For the T08.05 run (the DoD of T06.01): one server line per stripped log with the seconds it took and the shaft's
    // revolutions as the network's own angle counts them, next to the R = DebarkerDefaultWork / DebarkerSpeed the tick
    // integrates towards. The tick counts from the speed and the engine from its angle, so the two numbers check each
    // other. Measure a log stripped in one go at the default length: the seconds include any stall, the revolutions any
    // turn of the line below DebarkerMinTorque while the progress stands, the start is sampled after the first tick's
    // advance (a log that ends within a tick or two measures nothing), and a log begun before a load prints nothing.
    long dbgLogStartMs = -1;
    float dbgLogStartShaftAngle;

    // The node's angle before the engine's % 2π: the network's angle, which the server never folds, times the ratio.
    float DbgShaftAngle() => InputMechBehavior is { Network: { } net } node ? net.AngleRad * node.GearedRatio : 0f;

    void DbgLogStripped()
    {
        if (dbgLogStartMs < 0) return;
        Api.Logger.Notification(
            "[immersivewoodworking] [IW-DBG] Debarker at {0} stripped a log in {1:0.0} s, shaft {2:0.00} rev by the network angle, expected {3:0.00}.",
            Pos, (Api.World.ElapsedMilliseconds - dbgLogStartMs) / 1000f,
            Math.Abs(DbgShaftAngle() - dbgLogStartShaftAngle) / GameMath.TWOPI, Config.DebarkerFwdWrapBound / GameMath.TWOPI);
        dbgLogStartMs = -1;
    }
#endif
}
