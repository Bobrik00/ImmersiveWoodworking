using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// E06 — the automated saw CYCLE: the server-side heartbeat that turns mechanical power into a finished
/// plank/supportbeam batch. T06.01 is the heartbeat itself — <see cref="OnSawmillTick"/> integrates the effective
/// rip speed (E04) into <c>sawmillProgress</c> while the start gates hold, freezing (never resetting) the moment the
/// drive stalls or the log is gone, so a half-sawn log resumes from the same fraction once the gates clear. Pass
/// completion (T06.02 — <see cref="CompletePass"/>) computes the whole-log batch, EJECTS it forward out of the machine
/// (T06.03's <see cref="EjectBatch"/>, pulverizer-style — the product is never stored in an output inventory), then
/// consumes the log and resets the progress; folding the start conditions into one <c>CanSaw()</c> predicate is T06.05.
///
/// Modelled on vanilla <c>BEHelveHammer.onEvery25ms</c>: a 25 ms server tick that accumulates an instantaneous,
/// speed-scaled increment and fires a hook once it crosses its threshold. The progress is the FEED STROKE — a 0..1
/// fraction of one pass — not a wall-clock timer, so at zero speed the increment is zero and it simply holds (memory
/// vs-saw-hold-progress). The field + its serialization live in the main partial; this file is only the cycle logic.
/// </summary>
public partial class BlockEntitySawmill
{
    /// <summary>
    /// E06/T06.05 — the single START-GATE predicate for the saw cycle: the machine may cut RIGHT NOW iff it is fully
    /// assembled, a sawable &amp; product-resolvable log is loaded, and the drive turns above the torque floor. This is
    /// T06.05's deliverable: the start conditions the other epics already own — assembly (<see cref="IsComplete"/>), a
    /// resolvable log (E05/T05.03 via <see cref="BlockEntitySawhorse.SawProductResolves"/>, the alloc-free twin of <see cref="GetPassBatch"/>) and a kitted, above-torque drive
    /// (E04 <see cref="EffectiveSawSpeed"/>) — lifted into ONE predicate so the server heartbeat
    /// (<see cref="OnSawmillTick"/>), the client feed estimate
    /// (<see cref="AdvanceClientFeed"/>) and the cut effects (sawdust T07.04, loop sound T07.05) all gate on the
    /// IDENTICAL condition and can never drift. Side-agnostic: every term is a pure read of server-synced state (the
    /// network angle is interpolated on the client), so it answers the same on both sides. The torque floor and the
    /// "fully kitted" check are already folded into <see cref="EffectiveSawSpeed"/> &gt; 0 (it returns 0 without a
    /// blade kit / below the minimum torque); together with the explicit <see cref="IsComplete"/> those two are
    /// exactly <see cref="IsMechanismTurning"/>, which is how they are spelled here — assembly still reads as an
    /// explicit gate (the DoD), and the effects layer's "turning" and this "sawing" can no longer drift apart.
    /// </summary>
    public bool CanSaw()
        // Conjunct ORDER is load-bearing (AuditFixPlan T05.03 / F-L10-02): the free drive gate comes first — as
        // IsMechanismTurning, the very predicate the effects layer already gates on, so "sawing implies turning" is
        // stated rather than restated — and the product resolve, which walks the input's code, builds the product code
        // and hits the registry, is asked only of a mill that is assembled and running. This predicate runs every
        // 25 ms server tick AND twice per opaque client frame (OnSawmillTick, AdvanceClientFeed + the cut effects), so
        // a stalled or unpowered mill with a log sitting in it used to pay the full price ~40 times a second to be
        // told "no". Same shape as the chopper's tick, which likewise gates on the drive before it looks at the log.
        //
        // Product-resolve gate via SawProductResolves, NOT GetPassBatch() != null (F-S04-05): identical null conditions
        // (kind/wood/product-resolvable) but WITHOUT the throwaway product ItemStack GetPassBatch builds only to test
        // for null. That removed ONE object out of 28, which is what the note here used to call the job done (F-L10-01,
        // conflict K2). Of the 27 that stayed, 24 were code parts: five FirstCodePart calls, each splitting the whole
        // block code into an array plus a string per part, of which exactly one string was kept. T05.03 took those 24 —
        // the parts are compared in place now (CodeParts) and the input is classified once instead of twice. Measured
        // on log-placed-oak-ud: 960 bytes per call for the classification alone, now 32. What remains, and only past
        // the two gates above, is 3 objects: the species substring, the product code string and its AssetLocation.
        => IsMechanismTurning() && BlockEntitySawhorse.SawProductResolves(Api.World, InputSlot.Itemstack, sawMode);

    // The previous tick's/frame's drive angle, so the feed can be advanced by the SHAFT's rotation rather than by a
    // wall-clock timer. Transient (not serialized): re-seeded to the current angle whenever the feed isn't accruing, so
    // it never banks a jump across an idle gap. The server uses this field; the client mirror is lastClientFeedAngle.
    float lastFeedAngle;

    // AuditFixPlan T07.07 / F-L4-10 — the seed latch that goes WITH lastFeedAngle: false until the reference above holds
    // a real angle sample. A fresh BE (chunk load, world load, placement) starts both at their defaults, so a mill that
    // is already assembled, powered and loaded on its FIRST tick used to difference the live network angle against 0 and
    // bank the whole thing as feed — up to half a revolution after the wrap fold, i.e. up to 0.5/R of a pass (R =
    // SawmillDefaultWork/SawmillSpeed revolutions; at the top of the speed band R falls to ~0.5, so that is a whole pass)
    // for every walk out of the chunk and back. The latch is closed by the
    // !CanSaw() branch too (which already re-seeds the reference), so a stall→resume still costs no tick: the only call
    // it ever swallows is the first one of a BE that loads mid-cut. The client mirror carries its own (clientFeedSeeded).
    bool feedSeeded;

#if IW_DEBUG
    // AuditFixPlan T01.02 / F-L4-08 — TEMPORARY diagnostic, delete once the finding is settled.
    // FeedAdvance runs on every 25 ms server tick AND twice per client frame, so a per-call line would drown the log:
    // these accumulate instead and one summary line is emitted per DbgFeedSample calls, per side.
    const int DbgFeedSample = 80;
    int dbgFeedCalls;
    int dbgFeedFolds;
    float dbgFeedMaxAbs;
#endif

    /// <summary>
    /// The pass-fraction the drive has turned through since <paramref name="lastAngle"/> — the rigid coupling between the
    /// saw stroke and the feed. One full pass is R = SawmillDefaultWork / <see cref="ImmersiveWoodworkingConfig.SawmillSpeed"/>
    /// shaft revolutions (one ratchet tooth per revolution = one saw stroke), so the carriage/wheel/spring (E07) advance in
    /// lockstep with the visible reciprocation and can never outrun it, whatever the network RPM. Returns the wrap-
    /// corrected forward rotation of <see cref="AngleRad"/> (which wraps to [0,2π) each revolution) over
    /// <see cref="ImmersiveWoodworkingConfig.SawmillFwdWrapBound"/> (= 2π·R), and
    /// advances <paramref name="lastAngle"/> to the current angle. Magnitude (either rotation sense feeds), taken as the
    /// Nyquist HYBRID of the wrapped |Δ| below π/step and the continuous <c>NetworkSpeed·5·<paramref name="dt"/></c> above
    /// it, so a fast network can't fold a forward step into a small reverse and freeze the cut (ModDbBugfixPlan T02.03,
    /// folded into SpeedDecouplingPlan T04.02 — the chopper's OnChopperTick twin).
    ///
    /// <para>AuditFixPlan T07.07 / F-L4-10 — <paramref name="seeded"/> is the reference's own validity flag, and it lives
    /// HERE rather than at the two call sites so a third caller cannot forget it: an UNSEEDED reference is not "the angle
    /// one tick ago", it is 0, and differencing the live network angle against 0 hands the pass up to half a revolution
    /// of free feed. So the first call after a (re)load only takes the sample and returns 0. The delta arithmetic below
    /// is deliberately untouched (the fix is the reference, not the formula): the wrap fold cannot tell a real 0 → π step
    /// from a bogus one, which is exactly why the seed has to be explicit. The ROOT of this group — a feed differentiated
    /// out of another mod system's absolute, non-persisted, precision-losing angle — is not fixed here; the machine's own
    /// work counter is <c>SpeedDecouplingPlan</c>'s, and until it lands this latch only removes the per-load gift.</para>
    /// </summary>
    float FeedAdvance(ref float lastAngle, ref bool seeded, float dt)
    {
        float ang = AngleRad;
        if (!seeded) { lastAngle = ang; seeded = true; return 0f; }
        float d = ang - lastAngle;
#if IW_DEBUG
        // AuditFixPlan T01.02 / F-L4-08 — TEMPORARY. The finding claims the PRE-FOLD delta exceeds pi on the SERVER
        // while the CLIENT stays under it, so the two sides fold a different number of revolutions and the feed forks.
        // Read it as: a non-zero folds= on one side against a zero folds= on the other, at the same netSpeed.
        // dRaw/angle are the newest raw sample (the plan asked for the raw AngleRad and the delta BEFORE the modulo).
        {
            float dbgAbs = d < 0f ? -d : d;
            dbgFeedCalls++;
            if (dbgAbs > GameMath.PI) dbgFeedFolds++;
            if (dbgAbs > dbgFeedMaxAbs) dbgFeedMaxAbs = dbgAbs;
            if (dbgFeedCalls >= DbgFeedSample)
            {
                Api.World.Logger.Notification(
                    "[IW-DBG] feedadv side={0} pos={1} calls={2} folds={3} maxAbsD={4:0.00000} angle={5:0.00000} dRaw={6:0.00000} netSpeed={7:0.00000} speedMult={8:0.000} wrapBound={9:0.000}",
                    Api.Side, Pos, dbgFeedCalls, dbgFeedFolds, dbgFeedMaxAbs, ang, d, NetworkSpeed, Config.SawmillSpeed, Config.SawmillFwdWrapBound);
                dbgFeedCalls = 0; dbgFeedFolds = 0; dbgFeedMaxAbs = 0f;
            }
        }
#endif
        // Per tick/frame the shaft turns far less than half a revolution at any sane network speed, so a |Δ| > π is the
        // [0,2π) wrap, not real motion — fold it back to the true short-arc delta.
        if (d > GameMath.PI) d -= GameMath.TWOPI;
        else if (d < -GameMath.PI) d += GameMath.TWOPI;
        lastAngle = ang;
        // Nyquist hybrid (ModDbBugfixPlan T02.03, folded into SpeedDecouplingPlan T04.02 — the chopper's OnChopperTick/
        // AdvanceAnimAngle twin, T03.04/T03.05). The wrapped |Δ| is EXACT only while the shaft turns less than π per
        // step; above that the [−π,π] fold above reads a real forward step as a small backward one, |Δ| collapses toward
        // 0 and the feed FREEZES — the pilorama stops cutting AND stops animating at effective TrueSpeed > 8π ≈ 25
        // (NetworkSpeed·5·dt > π), the same high-RPM report Lielac hit on the chopper. The continuous NetworkSpeed
        // recovers the true magnitude with no Nyquist limit (ShaftRadPerNetworkSpeedSecond). Below π/step keep the exact
        // wrapped magnitude (speed=1 and any normal RPM stay bit-identical — invariant I8); only above it fall back to the
        // speed-derived one. Both are rectified, so either rotation sense feeds. dt is this caller's step (25 ms server
        // tick, or one client frame from the renderer).
        float absD = d < 0f ? -d : d;
        float speedInc = NetworkSpeed * ShaftRadPerNetworkSpeedSecond * dt;
        float inc = speedInc > GameMath.PI ? speedInc : absD;
        // SpeedDecouplingPlan T01.02: the pass length is now the precomputed server work R = SawmillDefaultWork·M, and
        // the feed divisor is 2π·R = SawmillFwdWrapBound (filled once in Sanitize, never per tick — invariant I5). At
        // M=1 this is 2π·50, bit-identical to the pre-feature 2π·SawmillStepsPerLog. Full work-alignment + F1
        // verification is T02.04; the defensive floor guards a not-yet-clamped 0 bound from dividing by zero.
        float bound = Config.SawmillFwdWrapBound;
        return bound > 0f ? inc / bound : 0f;
    }

    /// <summary>
    /// E06/T06.01 — the saw-cycle heartbeat (registered server-side in <see cref="Initialize"/> at 25 ms, like
    /// <c>BEHelveHammer.onEvery25ms</c>). Server-only: the client renders the synced <c>sawmillProgress</c> (E07) but
    /// never integrates it. Accrues progress only while the machine may cut and freezes — WITHOUT resetting —
    /// otherwise, so a pass resumes from the same fraction once the drive/feed gates clear again.
    /// </summary>
    void OnSawmillTick(float dt)
    {
        if (Api.Side != EnumAppSide.Server) return;

        // Gate the ACCUMULATION (never the stored value) on the single start predicate (T06.05): a sawable,
        // product-resolvable log loaded (E05/T05.03) and a turning, kitted, above-torque drive (E04). CanSaw() false →
        // re-seed the reference and return WITHOUT touching sawmillProgress → it freezes and later resumes from the same
        // fraction (memory vs-saw-hold-progress) with no banked angle jump. There is NO output-full pause: the product
        // is ejected forward (never buffered), so a completed pass can never be blocked — pulverizer-style.
        //
        // This branch is also what CLOSES the seed latch (T07.07): the sample it takes is a valid reference, so a
        // stall→resume keeps costing nothing, and only a mill that loads already cutting spends its first tick seeding.
        if (!CanSaw()) { lastFeedAngle = AngleRad; feedSeeded = true; return; }

        // Feed = SHAFT ROTATION, not wall-clock: SawmillDefaultWork ratchet teeth spread over the R =
        // SawmillDefaultWork/SawmillSpeed revolutions of a pass (one tooth per work-clock revolution), so the cut is
        // paced by the saw stroke itself and the powered station is deliberately only as fast as its
        // drive — the feed can't outrun the visible reciprocation (the "carriage skips ahead" bug). EffectiveSawSpeed
        // stays the GATE (CanSaw needs it > 0: blade kit installed + above torque) but does NOT set the rate — the cut
        // is paced SOLELY by the network RPM (this FeedAdvance), with no base-seconds or blade-tier factor.
        float before = sawmillProgress;
        bool wasRecoverable = LogRecoverable; // recovery grace before this advance (drives the take↔discard hint sync below)
        sawmillProgress += FeedAdvance(ref lastFeedAngle, ref feedSeeded, dt);

        // The cut just BEGAN (0 → >0): sync once so the client's authoritative sawmillProgress (and with it the Ctrl+RMB
        // interaction hint, take → discard) reflects that the log is now begun. ONE extra sync per pass — a visible-state
        // boundary like pass-complete, not a per-tick sync (the client still interpolates the smooth feed itself, E07).
        if (before <= 0f && sawmillProgress > 0f) MarkDirty(true);

        if (sawmillProgress >= 1f)
        {
            sawmillProgress = 1f; // clamp to a full stroke; CompletePass resets it to 0 once the batch is ejected
            MarkDirty(true);      // pass-complete is a visible-state boundary — sync here, not every tick (sawhorse-style economy)
            CompletePass();
        }
        // Otherwise sync the RUNNING progress at 5% steps, so the block-info % advances LIVE instead of freezing at the
        // last-synced value until the next chunk (re)load ("progress only updates on server restart"): a BE needs a
        // periodic MarkDirty for the client's sawmillProgress to move at all. Data-only sync (redrawOnClient:false →
        // MarkBlockEntityDirty resends the tree, refreshing the % readout AND re-snapping the smooth client feed in
        // FromTreeAttributes) WITHOUT re-tesselating the ~716-element model each step — the movers render from the live
        // network angle, no re-mesh needed. ~20 syncs per pass (the sawhorse stage-sync economy); the 0→>0 start and the
        // pass-complete boundaries already sync above, so this never double-fires with them.
        //
        // The grace crossing is folded in for the same reason the sawhorse carries it (AuditFixPlan T03.05, F-L4-07):
        // the take↔discard hint switches at LogRecoverThreshold, this sync lands on the 1/20 grid, and the two only
        // coincide while the threshold is a multiple of 5%. At the default 0.25 they do — the miss the sawhorse showed
        // is not reachable here — but a configured 0.33 would leave the hint promising "take" for up to a twentieth of
        // the pass after the server committed the log. One term keeps the hint honest at ANY configured value; it can
        // fire at most once per pass and usually coincides with a grid step it would have made anyway.
        else if ((int)(before * 20f) != (int)(sawmillProgress * 20f) || wasRecoverable != LogRecoverable) MarkDirty();
    }

    /// <summary>
    /// E06/T06.02 — pass-complete hook (the plan's <c>OnPassComplete</c>), invoked from <see cref="OnSawmillTick"/>
    /// once <c>sawmillProgress</c> reaches a full feed stroke. Computes the whole-log batch FROM THE STILL-LOADED input
    /// (<see cref="GetPassBatch"/> reads the input slot, so this runs BEFORE the log is consumed — the sawhorse "compute
    /// before ConsumeLog" ordering lesson), EJECTS it forward out of the machine (<see cref="EjectBatch"/>, T06.03 —
    /// pulverizer-style, never buffered in an output slot), then consumes the input log and resets the progress under the
    /// next one. The eject can't fail, so completion is unconditional (no "overflow → pause"): a full world around the
    /// machine just means the planks pile up / despawn on the ground, exactly like the pulverizer's crushed output.
    /// Server-only by its caller (<see cref="OnSawmillTick"/> returns off-server before reaching here).
    /// </summary>
    void CompletePass()
    {
        // Compute the batch while the log is still present; the tick gates accrual on GetPassBatch() != null, so this is
        // normally non-null — but stay defensive (a corrupted / externally-edited input could null it) and clear the
        // dead progress rather than leaving it stuck at 1.0.
        (ItemStack product, int count)? batch = GetPassBatch();
        if (batch == null)
        {
            sawmillProgress = 0f;
            MarkDirty(true);
            return;
        }

        // Eject the whole batch forward, out of the machine (pulverizer-style — never buffered, so completion can't be
        // blocked by a full output), then consume the input log (sawhorse ConsumeLog analogue) and reset under the next.
        EjectBatch(batch.Value.product, batch.Value.count);
        DropSawdust(); // one wearandtear:sawdust per completed log (Wear & Tear compat) — out the same exit cell as the batch
        InputSlot.Itemstack = null;
        InputSlot.MarkDirty();
        sawmillProgress = 0f;
        MarkDirty(true);

        // One completed log wears the installed blade kit by the config charge (T06.06); may break it, which flips
        // IsComplete false so the next tick stalls the station until a new kit is installed.
        DamageBladeKit();
    }

    /// <summary>
    /// E06/T06.06 — wear the installed blade kit by ONE completed log. The kit carries native VS durability
    /// (<c>durabilitybytype</c> in <c>sawmillblade.json</c>), so this just charges
    /// <see cref="ImmersiveWoodworkingConfig.SawmillBladeWearMultiplier"/> points (default 1/log; 0 = wear off) against
    /// the stored <c>bladeStack</c> and, on reaching 0, breaks the kit. Called once per completed pass from
    /// <see cref="CompletePass"/>, AFTER the batch is deposited and the log consumed — the sawhorse's "wear on a
    /// completed section" analogue (<c>DamageActiveSaws</c>), one charge per whole log instead of per stage.
    ///
    /// Done by hand rather than via the engine's <c>DamageItem</c>/<c>DestroyItem</c>: those dereference a
    /// <c>byEntity</c> for the break sound/particles (Collectible.cs) and the machine has no entity. So we read/write
    /// the durability attribute directly (<c>GetRemainingDurability</c>/<c>SetDurability</c>) and, on a break, clear
    /// the stack + play the standard tool-break sound at the master. A worn kit removed (Ctrl+RMB) or dropped on
    /// teardown clones with its durability attribute intact, so the slot bar/tooltip show the wear in inventory.
    /// Server-only (its caller is).
    /// </summary>
    void DamageBladeKit()
    {
        if (bladeStack == null) return;

        float rate = Config.SawmillBladeWearMultiplier;
        if (rate <= 0f) return; // wear disabled (config Sanitize floors negatives to 0)

        int max = bladeStack.Collectible.GetMaxDurability(bladeStack);
        if (max <= 1) return; // the kit item declares no durability → nothing to wear

        int cost = (int)Math.Round(rate);
        if (cost < 1) cost = 1; // any positive rate costs at least 1/log — integer durability has no sub-1 granularity

        int left = bladeStack.Collectible.GetRemainingDurability(bladeStack) - cost;
        if (left > 0)
        {
            bladeStack.Collectible.SetDurability(bladeStack, left);
            // Data-only (redrawOnClient:false, F-S12-04 twin): the sash/blade mesh is cached by BladeMetalKey (the
            // collectible code, Render.cs), NOT by durability, so a wear decrement never changes the mesh — a
            // re-tesselation buys nothing. The tree resend still carries bladeStack with its durability attribute, so
            // the kit durability readout refreshes via FromTreeAttributes. (The BREAK path below stays
            // redrawOnClient:true — the saw faces really vanish there.)
            MarkDirty();
            return;
        }

        // Worn out → the kit breaks: destroyed (not dropped), IsComplete flips false, the station stops until a new
        // kit is installed. Reset the (already-0) progress defensively, sound the break at the master, refresh the
        // render (the saw faces vanish).
        bladeStack = null;
        sawmillProgress = 0f;
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/effect/toolbreak"), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null);
        UpdateRenderer();
        MarkDirty(true);
    }

    /// <summary>
    /// E06/T06.03 — drop the whole pass batch (<paramref name="total"/> × <paramref name="template"/>) at the machine's
    /// OUTPUT side, in the direction the carriage feeds the log: the planks/beams are NEVER stored in an output
    /// inventory — they fall out where the finished log exits, low and within one cell, so a chute/hopper placed in that
    /// cell catches them (and a completed pass can never be blocked by a full output).
    ///
    /// Direction = <see cref="Facing"/>.Opposite. The feed animation drives the carriage — and the log riding it —
    /// toward the model's local -Z (SawmillAnim's <c>carriage</c> Feed driver has a NEGATIVE Travel), and
    /// <see cref="RotateToFacing"/> maps local +Z onto <see cref="Facing"/>, so the log leaves toward local -Z =
    /// world <see cref="Facing"/>.Opposite. The earlier version ejected toward +Facing — straight BACKWARDS, against the
    /// log's travel — from <c>Pos.Y+1.2</c> with an upward+forward toss, so the boards arced ~4.5 blocks away on the
    /// wrong side. This drops them low (near ground) into the centre of the first cell PAST the exit face with only a
    /// feather push, so they land within one cell, on the log's exit side, ready for a hopper. Split into several visual
    /// PILES like the sawhorse (<see cref="DropDistribution.SplitEvenly"/>: planks → <see cref="ImmersiveWoodworkingConfig.PlankDropCount"/>
    /// = 4, beams → <see cref="ImmersiveWoodworkingConfig.BeamDropCount"/> = 2; 24 → 4×6, 6 → 2×3) laid out in a short
    /// row ACROSS the exit, all inside that one cell — so they read as several distinct items, not one fat stack, yet
    /// still all land in the hopper cell. Server-only (its sole caller <see cref="CompletePass"/> is).
    /// </summary>
    void EjectBatch(ItemStack template, int total)
    {
        if (total <= 0) return; // degenerate batch: nothing to eject

        int max = template.Collectible.MaxStackSize;

        // The log EXITS toward local -Z (the carriage Feed driver's negative Travel) = world Facing.Opposite. Drop the
        // boards there so they travel WITH the log, not against it (the "boards fly out the back" bug).
        Vec3i outDir = Facing.Opposite.Normali;

        // How far the footprint reaches toward the exit, in cells (local frame — rotation-invariant): the deepest -Z
        // cell. The drop lands one cell BEYOND that — the first free cell past the exit face, where the hopper goes —
        // at near-ground height so a chute/hopper there collects the batch and it can't arc away.
        int reach = 0;
        foreach (Vec3i cell in FootprintLocal) if (-cell.Z > reach) reach = -cell.Z;
        double dist = reach + 1; // centre of the first cell outside the exit face (reach = deepest cell; +1 = the next one)
        double cx = Pos.X + 0.5 + outDir.X * dist, cy = Pos.Y + 0.4, cz = Pos.Z + 0.5 + outDir.Z * dist;

        // Horizontal axis ACROSS the exit, to lay the piles out in a row (the perpendicular of outDir): if the exit runs
        // along Z, spread along X, and vice-versa. Keeps every pile inside the one exit cell so the hopper grabs them all.
        double perpX = outDir.Z != 0 ? 1 : 0, perpZ = outDir.X != 0 ? 1 : 0;

        // Sawhorse-style pile split: several visual stacks rather than one (planks → SawmillPlankDropCount, beams →
        // SawmillBeamDropCount; a support beam is a Block, a plank an Item). The automated sawmill's own pile counts
        // (E04/T04.05), each -1 = inherit the manual PlankDropCount/BeamDropCount (identical out of the box; the hand
        // sawhorses keep the manual fields). SplitEvenly caps piles to the total and sums back to it exactly.
        int dropCount = template.Class == EnumItemClass.Block ? Config.SawmillBeamDropCountEffective : Config.SawmillPlankDropCountEffective;
        int[] sizes = DropDistribution.SplitEvenly(total, dropCount);

        var rand = Api.World.Rand;
        const double rowSpread = 0.5; // half a block: the row of piles stays well inside the exit cell (±0.25 max)
        for (int i = 0; i < sizes.Length; i++)
        {
            // Even slot in the row across the exit (i-th of n in [-rowSpread/2 .. +rowSpread/2]) + a little random jitter
            // so successive batches don't land pixel-identical; a lone pile stays centred.
            double slot = sizes.Length > 1 ? ((i + 0.5) / sizes.Length - 0.5) * rowSpread : 0.0;
            double jx = (rand.NextDouble() * 2 - 1) * 0.05, jz = (rand.NextDouble() * 2 - 1) * 0.05;
            Vec3d pos = new Vec3d(cx + perpX * slot + jx, cy, cz + perpZ * slot + jz);

            // Feather push along the exit direction, no vertical pop — the pile settles straight down into the cell.
            Vec3d velocity = new Vec3d(outDir.X * 0.02, 0, outDir.Z * 0.02);

            // One pile is normally a single entity (6 ≪ MaxStackSize); split defensively if a pile ever exceeds it.
            int remaining = sizes[i];
            while (remaining > 0)
            {
                int n = remaining < max ? remaining : max;
                ItemStack drop = template.Clone();
                drop.StackSize = n;
                Api.World.SpawnItemEntity(drop, pos, velocity);
                remaining -= n;
            }
        }
    }

    // One unit of sawdust per completed log, for Wear & Tear (mod) compatibility — see the sawhorse's DropSawdust
    // for the rationale (FIXED wearandtear domain, soft no-op without the mod, baked as a constant not a config
    // knob). Routed through EjectBatch so it leaves via the same exit cell as the plank/beam batch and a chute or
    // hopper parked there collects it too. Server-only (its caller CompletePass is).
    const int SawdustPerLog = 1;

    void DropSawdust()
    {
        Item? dust = Api.World.GetItem(new AssetLocation("wearandtear", "sawdust"));
        // See the sawhorse's DropSawdust: a REMOVED Wear & Tear returns a non-null stub with IsMissing, so gate on both.
        if (dust == null || dust.IsMissing) return; // Wear & Tear absent or removed — soft no-op
        EjectBatch(new ItemStack(dust, SawdustPerLog), SawdustPerLog);
    }
}
