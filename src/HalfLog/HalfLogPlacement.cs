using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// One half-log slab orientation, as the three variant parts of <c>halflogslab-{wood}-{skin}-{rot}-{run}-{face}</c>. A plain
/// value: no world, no species, no block — the species is the caller's (a placement takes it off the held stack, the
/// wrench off the block it turns), and this is only the part a CLICK decides.
/// </summary>
public readonly struct HalfLogState
{
    /// <summary>Which half of the cell is filled, i.e. the face the slab lies flush against: down/up/north/east/south/west.</summary>
    public readonly string Rot;

    /// <summary>The axis the log runs along: <c>ns</c> (Z), <c>we</c> (X) or <c>ud</c> (Y).</summary>
    public readonly string Run;

    /// <summary>What faces AWAY from the supporting face: <c>bark</c> or <c>split</c>.</summary>
    public readonly string Face;

    public HalfLogState(string rot, string run, string face)
    {
        Rot = rot;
        Run = run;
        Face = face;
    }

    public override string ToString() => Rot + "-" + Run + "-" + Face;
}

/// <summary>
/// Turns a right-click into one of the 24 half-log slab states (HalfLogBlockPlan T04.01). Pure: same face, same hit
/// point and same yaw always give the same answer, on both sides and on any machine, which is what lets the server
/// re-derive a placement instead of trusting a client-sent state.
///
/// <para><b>The rule, in one paragraph.</b> <see cref="RotFor"/> picks WHICH HALF of the cell is filled exactly the
/// way a vanilla slab does — aim at the middle of a face and the slab lies flush against it, aim near an edge and it
/// lies against the side that edge leads to. <see cref="RunFor"/> then picks WHICH WAY THE LOG POINTS: the log lies
/// ACROSS your line of sight while staying in the plane of the supporting face, so turning 90 degrees turns the log
/// 90 degrees; when across-your-sight would run INTO the supporting wall (you are looking along the wall, not at it),
/// the log stands upright instead. <see cref="DefaultFace"/> puts the split plane outwards. Nothing else is read —
/// not the neighbours, not the pitch, not a keyboard modifier.
///
/// <para><b>Why the rot rule is vanilla verbatim</b> (<c>BlockBehaviorOmniRotatable</c> with
/// <c>rotateSides: true, facing: "block"</c>, vssurvivalmod BehaviorOmniRotatable.cs:145-201). Every slab a VS player
/// has ever placed behaves this way — <c>plankslab</c>, <c>cobbleslab</c>, <c>quartz slab</c>, and indappledgroves'
/// <c>logslab</c>, which is the closest existing thing to this block and carries the identical two properties. We
/// cannot literally attach the behaviour (it calls <c>CodeWithVariant("rot", …)</c> and drives the whole placement
/// itself, whereas here the placement is an ITEM's, three parts wide, and has to run through
/// <see cref="BlockHalfLogSlab.Get"/> with the species from the stack), so the branch is ported instead — with the
/// same 0.3 centre radius, the same tie-breaks and the same fall-through order, so a player cannot feel the seam.
///
/// <para><b>Why the run rule is across-the-sight and not along-it.</b> Both read naturally on a FLOOR (down/up),
/// where either horizontal axis is legal and the choice is pure taste. The wall cases decide it. A wall slab's legal
/// axes are <c>ud</c> and the ONE horizontal axis parallel to that wall, so the rule has to answer "log-cabin siding
/// or upright post?" — and the player asking for siding is standing in FRONT of the wall facing it, which is exactly
/// where along-the-sight is illegal and across-the-sight gives the horizontal course. The same rule then hands the
/// upright post to the player who turns to look ALONG the wall. Along-the-sight would have inverted both: facing a
/// wall head-on would stand every log on end. It also matches how the half-log is carried — <c>holdbothhandslarge</c>,
/// held lengthwise across the body — so the log lands the way it is held.
///
/// <para><b>All 12 legal rot/run pairs are reachable from a click</b>, which is why no placement-mode gesture is
/// invented here: the plan offered vanilla's <c>slabPlaceMode</c> (a lone item toggled in the crafting grid) as a
/// fallback if aim alone could not reach the states, and it can. Only <c>face</c> is left to the wrench (E05), by the
/// plan's own decision, and it is the one axis with no natural aiming gesture at all.
///
/// <para><b>What the wrench actually took</b> (E05, implemented in <c>BlockHalfLogSlab.Wrench.cs</c>): all three
/// parts, not just <c>face</c>. Right-click flips <c>face</c> — the axis this rule cannot reach at all. Left-click
/// flips <c>run</c> to the other axis legal for the current <c>rot</c>, which aim CAN reach but only by re-placing.
/// Shift walks <c>rot</c> (carrying <c>run</c> where it stays legal), which aim reaches only by breaking and aiming
/// again. So placement and wrench overlap on 12 of the 24 states by design: aim is the fast path for a fresh block,
/// the wrench is the correction for one already in a wall.
/// </summary>
public static class HalfLogPlacement
{
    /// <summary>
    /// What faces outward on a freshly placed slab: the SPLIT plane, i.e. the flat sawn face, always points away from
    /// the surface the slab is resting against — up on a floor, down on a ceiling, into the room off a wall.
    ///
    /// <para>Three reasons, and the first is the strongest: this is what the player has already SEEN. The creative
    /// entry, the handbook page and the item icon are all <c>down-we-split</c> (the single state
    /// <c>blocktypes/halflogslab.json</c> exposes, T03.05), so a placement that produced <c>bark</c> would contradict
    /// the picture the block was picked from. Second, it is the useful side: a floor is walked on and a wall panel is
    /// looked at, and both want the flat face. Third, it is one sentence for the handbook (T06.02) instead of a table
    /// — "the flat side faces you" holds for all six rots. Bark-out, the log-cabin look, is one wrench turn away
    /// (T05.02), which is where the plan put it.
    /// </summary>
    public const string DefaultFace = "split";

    /// <summary>Half-width of the "aimed at the middle of this face" square, in block units. Vanilla's number
    /// (BehaviorOmniRotatable.cs:157 etc.) and deliberately not retuned: the muscle memory being matched is the
    /// vanilla slab's, and a different radius here would make this block feel subtly wrong next to a plank slab.</summary>
    const double CenterRadius = 0.3;

    /// <summary>
    /// The state a click produces. <paramref name="face"/> and <paramref name="hitPosition"/> are the aimed-at block's
    /// (i.e. <c>blockSel.Face</c> / <c>blockSel.HitPosition</c>); <paramref name="yaw"/> is the placing player's
    /// <c>Entity.Pos.Yaw</c>.
    ///
    /// <para><b><c>HitPosition</c> stays relative to the block that was LOOKED AT, even when <c>DidOffset</c> already
    /// moved <c>Position</c> to the neighbouring cell</b>, and the whole rot rule rests on that. The engine says so
    /// itself, not just in a doc comment (vsapi BlockSelection.cs:34-56): <c>Block.SuggestedHVOrientation</c> has to
    /// UN-offset before it can use the two together — <c>blockSel.DidOffset ? blockSel.Position.AddCopy(blockSel.
    /// Face.Opposite) : blockSel.Position</c>, then <c>targetPos.X + blockSel.HitPosition.X</c> (Block.cs:1914-1918).
    /// It is also why only the two coordinates PERPENDICULAR to the face carry information: the third is pinned to
    /// the face's own plane, and the placement cell differs from the looked-at one along exactly that third axis.
    /// </summary>
    public static HalfLogState Select(BlockFacing face, Vec3d? hitPosition, float yaw)
    {
        BlockFacing rot = RotFor(face, hitPosition);
        return new HalfLogState(rot.Code, RunFor(rot, yaw), DefaultFace);
    }

    /// <summary>Convenience for the placement path (T04.02), which holds a selection and a player rather than three
    /// loose values. Reads <c>Entity.Pos.Yaw</c>, the player's LOOK yaw — the same field every vanilla orientation
    /// rule uses (BehaviorOmniRotatable.cs:210, 222, 233), and the one the rule wants, since <see cref="RunFor"/> is
    /// defined against the line of sight.
    ///
    /// <para><b>Not the body yaw, and the distinction is not pedantry — a maintainer who "fixes" this to
    /// <c>Entity.BodyYaw</c> changes behaviour.</b> The engine keeps three separate values. <c>Pos.Yaw</c> is the
    /// aim yaw and LEADS: for the local player it is assigned straight from the mouse. <c>EntityAgent.BodyYaw</c>
    /// (vsapi Common/Entity/EntityAgent.cs:82-85, "The yaw of the agents body") FOLLOWS it, eased by
    /// <c>PlayerHeadController.AdjustBodyAngles</c> and only once the gap passes ~69 degrees while standing still.
    /// <c>Pos.HeadYaw</c> is the derived residual between the two, i.e. the visible head turn. So the run axis tracks
    /// the camera immediately, which is exactly what the handbook text promises ("turn 90 degrees and the log turns
    /// 90 degrees"); reading <c>BodyYaw</c> instead would make it lag the camera in third person, and would pin it
    /// outright while edge-sitting, where <c>BodyYawLimits</c> clamps the body to ~11 degrees of the seated
    /// direction. In first-person and overhead cameras the two coincide (the head controller forces
    /// <c>BodyYaw = Pos.Yaw</c>), so the difference is only observable in third person.</summary>
    public static HalfLogState Select(BlockSelection blockSel, IPlayer byPlayer)
        => Select(blockSel.Face, blockSel.HitPosition, byPlayer.Entity.Pos.Yaw);

    /// <summary>
    /// Which half of the cell the slab fills, as the face it lies flush against. A port of vanilla's
    /// <c>rotateSides</c> + <c>facing: "block"</c> branch: aim within <see cref="CenterRadius"/> of the middle of the
    /// clicked face and the slab hugs that face; otherwise the aimed-at coordinate that is FARTHER from the middle
    /// wins and the slab hugs the side it points at. Covers all six faces and can return any of the six rots (never
    /// the clicked face's own direction, which is why no single face reaches all six — the union over the six faces
    /// does).
    ///
    /// <para>Ties fall through to the second coordinate (<c>&gt;</c>, not <c>&gt;=</c>), which is vanilla's behaviour
    /// and keeps the function total: a click dead on a diagonal still answers, and always the same answer.
    ///
    /// <para><b>The hit point is only meaningful on the two axes perpendicular to the face</b> — the third is pinned
    /// to 0 or 1 by definition — and the rule never reads it, so the placement cell being one step along that third
    /// axis costs nothing.
    ///
    /// <para><b>Aiming at a partial block compresses the hit range, and that is inherited, not a defect.</b> Click the
    /// side of an existing half-log slab and the hit's vertical span is only the half the slab occupies, so a
    /// <c>down</c> slab's side face can never answer <c>up</c>, and continuing a floor sideways means aiming at the
    /// LOWER part of the neighbour's face. A vanilla plank slab behaves identically for the identical reason. Aiming
    /// at the TOP of a <c>down</c> slab likewise places into the cell above (slabs do not merge in VS), leaving a
    /// half-cell gap — vanilla parity again, and the reason the side face is the natural target when laying a floor.
    /// </summary>
    public static BlockFacing RotFor(BlockFacing face, Vec3d? hitPosition)
    {
        // No hit point (a synthesised selection, e.g. a test or a scripted placement) means no "where on the face"
        // information at all. The middle is the honest reading of "somewhere on this face", and the middle means
        // "hug it" — the same answer the real rule gives for the whole centre square.
        if (hitPosition == null) return face.Opposite;

        double dx = Math.Abs(hitPosition.X - 0.5);
        double dy = Math.Abs(hitPosition.Y - 0.5);
        double dz = Math.Abs(hitPosition.Z - 0.5);

        switch (face.Axis)
        {
            case EnumAxis.X:    // clicked an east/west face: the hit varies in Z and Y
                if (dz < CenterRadius && dy < CenterRadius) return face.Opposite;
                if (dz > dy) return hitPosition.Z < 0.5 ? BlockFacing.NORTH : BlockFacing.SOUTH;
                return hitPosition.Y < 0.5 ? BlockFacing.DOWN : BlockFacing.UP;

            case EnumAxis.Y:    // clicked a top/bottom face: the hit varies in Z and X
                if (dz < CenterRadius && dx < CenterRadius) return face.Opposite;
                if (dz > dx) return hitPosition.Z < 0.5 ? BlockFacing.NORTH : BlockFacing.SOUTH;
                return hitPosition.X < 0.5 ? BlockFacing.WEST : BlockFacing.EAST;

            default:            // EnumAxis.Z — clicked a north/south face: the hit varies in X and Y
                if (dx < CenterRadius && dy < CenterRadius) return face.Opposite;
                if (dx > dy) return hitPosition.X < 0.5 ? BlockFacing.WEST : BlockFacing.EAST;
                return hitPosition.Y < 0.5 ? BlockFacing.DOWN : BlockFacing.UP;
        }
    }

    /// <summary>
    /// The axis the log runs along, given the half of the cell it fills: the horizontal axis ACROSS the player's line
    /// of sight when that axis is legal here, else <c>ud</c>.
    ///
    /// <para><b>Legality is one comparison, not a table.</b> A log lying in the half of the cell against face F cannot
    /// also run along F's axis — it would have to pass through the surface it is resting on — so the whole rule is
    /// <c>run.Axis != rot.Axis</c>. That is precisely the asset's six <c>skipVariants</c> patterns
    /// (<c>*-down-ud-*</c>, <c>*-north-ns-*</c>, …), i.e. twelve removed variants, since each pattern spans both
    /// <c>face</c> values — derived rather than restated, which is what
    /// <see cref="BlockHalfLogSlab.CodeWithState"/>'s doc asks callers to do instead of copying the table.
    ///
    /// <para>Only the AXIS of the player's facing is read, never its sign, so this is immune to the standing confusion
    /// about whether <c>HorizontalFromYaw</c> returns the direction the player faces or its opposite (its own doc
    /// comment says "0 degree = north" while the arithmetic answers south at yaw 0, vsapi BlockFacing.cs:619-627) —
    /// north and south are one axis either way. Rounding to the nearest of the four horizontals puts the switch at 45
    /// degrees, the same place every vanilla yaw rule puts it.
    ///
    /// <para>For a floor or ceiling (<c>rot</c> down/up) the across-axis is always horizontal and therefore always
    /// legal, so <c>ud</c> is unreachable there — correct, since it is also illegal there. For the four wall rots the
    /// two branches split cleanly: face the wall and get the horizontal course, look along it and get the upright.
    /// </summary>
    public static string RunFor(BlockFacing rot, float yaw)
    {
        EnumAxis sight = BlockFacing.HorizontalFromYaw(yaw).Axis;
        EnumAxis across = sight == EnumAxis.X ? EnumAxis.Z : EnumAxis.X;

        // When the across-axis is the one the supporting face itself uses, the log cannot lie along it (it would run
        // into the wall), so it stands upright instead.
        return RunCode(across == rot.Axis ? EnumAxis.Y : across);
    }

    // ───────────────────────────── run <-> axis, the one mapping (T05.01) ─────────────────────────────
    //
    // The `run` variant part names an AXIS, and both directions of that naming are needed by more than one caller:
    // this file's <see cref="RunFor"/> builds a run out of an axis, and the wrench (BlockHalfLogSlab.Wrench.cs) needs
    // both directions to answer "which other axis is legal here" and "which axis does the supporting face use". They
    // live here rather than on the block because the placement rule is where the vocabulary was defined and because
    // the wrench's whole legality argument is a restatement of this file's `run.Axis != rot.Axis` (see
    // <see cref="RunFor"/>'s doc) — one file owning both keeps them unable to disagree.
    //
    // The deliberate NON-user is <see cref="SelfCheck"/> below, which keeps its own inline copy of the same mapping.
    // That is not an oversight to tidy up: a machine check assembled out of the code under test agrees with itself no
    // matter what that code does, which is the lesson T03.03 and T04.04 both wrote into the plan. Do not "deduplicate"
    // it into a call to RunAxis.

    /// <summary>The axis a <c>run</c> code names. Unknown codes answer <c>Y</c>, matching <c>ud</c> — an unknown run
    /// cannot resolve to a block anyway (<see cref="BlockHalfLogSlab.Get"/> warns and returns null), so this only has
    /// to be total, not clever.</summary>
    public static EnumAxis RunAxis(string run) => run == "ns" ? EnumAxis.Z : run == "we" ? EnumAxis.X : EnumAxis.Y;

    /// <summary>The <c>run</c> code for an axis: the exact inverse of <see cref="RunAxis"/> over the three real
    /// axes.</summary>
    public static string RunCode(EnumAxis axis) => axis == EnumAxis.Z ? "ns" : axis == EnumAxis.X ? "we" : "ud";

#if IW_DEBUG
    /// <summary>
    /// Sweep the whole input space and prove the three things this function's Definition of Done claims, in one line
    /// at startup. Called from the mod system beside <see cref="BlockHalfLogSlab.SelfCheck"/>, stripped from a
    /// shipped build.
    ///
    /// <para>The claims are (1) every click on every one of the six faces answers a state that is REGISTERED — the
    /// oracle is the block registry, not a second copy of <c>skipVariants</c> in this file, so an edit to the asset's
    /// variant groups or skip list is caught here rather than as a warning under a player's cursor; (2) the six rots
    /// and all twelve legal rot/run pairs are actually reachable by aiming, which is the claim that makes the wrench
    /// optional for orientation and would fail silently if the run rule ever collapsed to one axis; (3) the answer is
    /// a function of its inputs alone, checked by asking twice.
    ///
    /// <para>One species is enough and 57 would only say the same thing 57 times: the variant grid is identical for
    /// every species (one blocktype asset, one injected <c>wood</c> group), and whether a given SPECIES has variants
    /// at all is already <see cref="BlockHalfLogSlab.SelfCheck"/>'s question. Picked lexicographically so two runs on
    /// the same install report the same species.
    /// </summary>
    internal static void SelfCheck(ICoreAPI api)
    {
        // Lexicographically smallest registered species, so the report is stable across runs.
        string? species = null;
        foreach (Block block in api.World.Blocks)
        {
            if (block is not BlockHalfLogSlab slab) continue;
            string? wood = slab.WoodCode;
            if (wood != null && (species == null || string.CompareOrdinal(wood, species) < 0)) species = wood;
        }

        if (species == null)
        {
            api.Logger.Warning("[immersivewoodworking] Half-log placement self-check skipped: no half-log slab variant is registered, so there is nothing to resolve against.");
            return;
        }

        int samples = 0, unregistered = 0, illegalAxis = 0, nonDeterministic = 0;
        System.Collections.Generic.SortedSet<string> pairs = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
        System.Collections.Generic.SortedSet<string> rots = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
        string firstBad = "";

        // Eight yaws (every 45 degrees) rather than four: the interesting values are the BOUNDARIES between
        // horizontal facings, where Math.Round decides, and a four-point sweep would step right over them.
        float[] yaws = new float[8];
        for (int i = 0; i < 8; i++) yaws[i] = i * GameMath.PI / 4;

        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            // A 10x10 grid over the clicked face, offset off the grid lines so no sample lands exactly on the 0.3
            // centre boundary — those are covered by the tie-break, but a sample sitting on one would make the
            // reachability counts depend on floating-point luck rather than on the rule.
            for (int u = 0; u < 10; u++)
            {
                for (int v = 0; v < 10; v++)
                {
                    double a = 0.05 + u * 0.1;
                    double b = 0.05 + v * 0.1;

                    // The coordinate along the face's own axis is pinned to that face's plane, exactly as the engine
                    // reports it: 1 for east/up/south, 0 for west/down/north.
                    double pinned = face.Normali.X + face.Normali.Y + face.Normali.Z > 0 ? 1 : 0;
                    Vec3d hit = face.Axis switch
                    {
                        EnumAxis.X => new Vec3d(pinned, b, a),
                        EnumAxis.Y => new Vec3d(a, pinned, b),
                        _ => new Vec3d(a, b, pinned),
                    };

                    foreach (float yaw in yaws)
                    {
                        samples++;
                        HalfLogState state = Select(face, hit, yaw);
                        HalfLogState again = Select(face, hit, yaw);
                        if (state.ToString() != again.ToString()) nonDeterministic++;

                        rots.Add(state.Rot);
                        pairs.Add(state.Rot + "-" + state.Run);

                        // Second copy of the run -> axis mapping ON PURPOSE; see the note above RunAxis. Calling
                        // RunAxis here would make this assertion agree with the rule it is checking.
                        BlockFacing rotFacing = BlockFacing.FromCode(state.Rot);
                        EnumAxis runAxis = state.Run == "ns" ? EnumAxis.Z : state.Run == "we" ? EnumAxis.X : EnumAxis.Y;
                        if (rotFacing == null || runAxis == rotFacing.Axis) illegalAxis++;

                        if (BlockHalfLogSlab.Get(api.World, species, BlockHalfLogSlab.SkinBarked, state.Rot, state.Run, state.Face) == null)
                        {
                            unregistered++;
                            if (firstBad.Length == 0) firstBad = face.Code + " @ " + hit + " yaw " + yaw + " -> " + state;
                        }
                    }
                }
            }
        }

        // 6 rots x 2 legal runs each. Not "12" as a magic number: it is 6 faces times the 2 axes perpendicular to each.
        const int LegalPairs = 12;

        if (unregistered > 0 || illegalAxis > 0 || nonDeterministic > 0 || rots.Count != 6 || pairs.Count != LegalPairs)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log placement self-check FAILED over {0} clicks on species '{1}': {2} produced an unregistered state (first: {3}), {4} put the log along its own supporting face, {5} were not reproducible; {6} of 6 rots and {7} of {8} rot/run pairs reachable [{9}].",
                samples, species, unregistered, firstBad.Length == 0 ? "none" : firstBad, illegalAxis, nonDeterministic,
                rots.Count, pairs.Count, LegalPairs, string.Join(", ", pairs));
        }
        else
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log placement self-check: {0} clicks over 6 faces x 100 aim points x 8 yaws on species '{1}', all resolve to registered states; every one of the 6 rots and all {2} legal rot/run pairs reachable by aim alone, face defaults to '{3}'.",
                samples, species, LegalPairs, DefaultFace);
        }
    }
#endif
}
