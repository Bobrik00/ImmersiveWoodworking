using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ImmersiveWoodworking;

/// <summary>How a sawmill model part behaves at render time.</summary>
public enum SawmillPartKind
{
    /// <summary>Baked into the chunk mesh once (the frame/housing); never moves.</summary>
    Static,
    /// <summary>Drawn by <see cref="SawmillRenderer"/> with its own matrix (animated in T07.02/T07.03; identity in T07.01).</summary>
    Mover,
    /// <summary>The workpiece log — drawn species-textured only when a log is loaded (T07.03); not rendered in T07.01.</summary>
    Log
}

/// <summary>One movable/static part of the authored sawmill model: an ordered set of name-glob rules plus how it moves.</summary>
public sealed class SawmillPartRule
{
    public readonly string Id;
    public readonly SawmillPartKind Kind;
    /// <summary>True only for the blade kit (saw_*): its faces are metal-textured by the installed kit (ITexPositionSource "metal").</summary>
    public readonly bool IsSaw;
    public readonly Regex[] Patterns;

    public SawmillPartRule(string id, SawmillPartKind kind, bool isSaw, Regex[] patterns)
    {
        Id = id; Kind = kind; IsSaw = isSaw; Patterns = patterns;
    }
}

/// <summary>
/// T07.01 — the element→part membership for the authored sawmill model (<c>shapes/block/sawmill/sawmill.json</c>),
/// a 1:1 C# mirror of the user's tuned part layout in <c>sawmill-animator.html</c> (saved to
/// <c>AutomatedSawmillPlan/sawmill_parts.json</c>). The model is flat-with-a-few-groups and carries Blender
/// <c>.NNN</c>/<c>_NNN</c> suffixes, so membership is decided EXACTLY as the animator does — ordered rules,
/// first-match-wins, each pattern tested against ANY name in the element's ancestor chain (NOT the engine's
/// path-based <c>selectiveElements</c>, which can't reproduce that). Verified against <c>verify_anim.py</c>.
///
/// Only the membership + Static/Mover/Log classification is needed here; the per-part drivers (rotate / rod /
/// follow / feed / ride — the slider-crank + ratchet kinematics) are ported into the renderer in T07.02/T07.03.
/// Keep <see cref="Rules"/> in the SAME order as sawmill_parts.json — priority is positional.
/// </summary>
public static class SawmillParts
{
    public static readonly SawmillPartRule[] Rules =
    {
        // Drive-coupling rotor discs Rotor_default_1..4 — the visible mechanical-power connection gear (the MP node's own
        // mechPartShape is null, so the engine builds no second gear here — ghostpower.json, T07.08). These were the
        // old lowercase `rotor_default`
        // disc the now-removed `input` Mover rule matched; the model rename to capitalised `Rotor_default_N` silently
        // reclassified them under the STATIC catch `rotorDefault` → they got baked into the chunk mesh and STOPPED
        // spinning in-game (the frozen rotors the user reported 2026-07-09). Restored as four MOVERS, one per disc, each
        // Rotate about its own axis/direction per the sawmill-animator export (SawmillAnim rotorDefault1..4; auto
        // mesh-bbox pivot). Gated on hasCrankshaft (the drive train they head), like the crank/rod below. Patterns are
        // mutually exclusive; order among them is free.
        Rule("rotorDefault4",   SawmillPartKind.Mover,  false, "Rotor_default_4*"),
        Rule("rotorDefault3",   SawmillPartKind.Mover,  false, "Rotor_default_3*"),
        Rule("rotorDefault2",   SawmillPartKind.Mover,  false, "Rotor_default_2*"),
        Rule("rotorDefault1",   SawmillPartKind.Mover,  false, "Rotor_default_1*"),
        // DRIVE / rectifier assembly (re-named/re-modeled 2026-07-09, re-wired 2026-07-14): rotor discs + a separate
        // twoway coupling disc + static housing. `mainRotorTwoway` is the twoway mechanical-power COUPLING (the old
        // `MainRotor` disc, renamed `MainRotor_twoway`) — a MOVER riding the SIGNED drive angle (animAngle × FacingDriveSign,
        // SawmillAnim Src="theta"), so it visibly co-turns with / reverses against the network gear (reflecting input
        // direction). It is the ONLY signed part. The CRANK is now the fixed-direction disc `rotorDefault4` (Rotor_default_4,
        // above): the connecting rod `crankpit` pins THERE (user bench 2026-07-14, pinFrom=part1), so the slider-crank +
        // feed-ratchet chain (crank/sash/blades/levers/wheel/spring) rides the RECTIFIED accumulator (animAngleFwd, Src="fwd")
        // and runs one fixed direction regardless of input-shaft direction — the fix for the "everything inverts when the
        // shaft reverses" report. Nothing pins to `mainRotorTwoway` anymore. (The 2026-07-09 rename removed every bare
        // `MainRotor`, so the old `mainrotor`/`MainRotor*` rule + driver are DELETED.)
        Rule("mainRotorTwoway", SawmillPartKind.Mover,  false, "MainRotor_twoway*"),
        Rule("rotorStatic",     SawmillPartKind.Static, false, "Rotor_main_static*"),
        Rule("crankpit",        SawmillPartKind.Mover,  false, "crankpit*"),
        Rule("sash",            SawmillPartKind.Mover,  false, "sash*"),
        Rule("saw",             SawmillPartKind.Mover,  true,  "saw_*", "saw*"),
        Rule("lever_top",       SawmillPartKind.Mover,  false, "leveler_top*"),
        Rule("lever_connector", SawmillPartKind.Mover,  false, "leveler_connector*", "connector*"),
        Rule("lever_bottom",    SawmillPartKind.Mover,  false, "leveler_bottom*"),
        Rule("lever_active",    SawmillPartKind.Mover,  false, "rotor_metal_leveler_active*", "rotor_metal_leveleractive*"),
        Rule("spring_rotor",    SawmillPartKind.Mover,  false, "spring_rotor*"),
        // spring* split out of lever_static so conditional render (PartVisible) can gate it on the CARRIAGE — the
        // authored carriage mesh (sawmill_carriage_parts) groups the spring with the cart, not the levers. Static
        // either way, so this changes only render membership, never animation. (Must stay AFTER spring_rotor* so the
        // ratchet rotor wins first-match.) sawmill_parts.json mirrors this split.
        Rule("spring",          SawmillPartKind.Static, false, "spring*"),
        Rule("lever_static",    SawmillPartKind.Static, false, "leveler_metal_static*"),
        Rule("passive",         SawmillPartKind.Static, false, "rotor_metal_leveler_passive*"),
        Rule("wheel",           SawmillPartKind.Mover,  false, "rotor_metal*"),
        Rule("carriage",        SawmillPartKind.Mover,  false, "carriage*"),
        Rule("log",             SawmillPartKind.Log,    false, "log*"),
        Rule("frame",           SawmillPartKind.Static, false, "*"),
    };

    /// <summary>Index into <see cref="Rules"/> of the first rule any name in <paramref name="chain"/> (root..self)
    /// matches; falls back to the catch-all frame (last rule). Mirrors the animator's <c>partFor</c>.</summary>
    public static int IndexFor(IReadOnlyList<string> chain)
    {
        for (int i = 0; i < Rules.Length; i++)
        {
            Regex[] pats = Rules[i].Patterns;
            for (int p = 0; p < pats.Length; p++)
            {
                for (int c = 0; c < chain.Count; c++)
                {
                    if (pats[p].IsMatch(chain[c])) return i;
                }
            }
        }
        return Rules.Length - 1; // catch-all frame
    }

    // Case-sensitive full-match glob ('*' => any run), exactly like the animator's globRe — names such as
    // "MainRotor"/"Frame"/"sash_001" are case-significant.
    static Regex Glob(string pattern) =>
        new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);

    static SawmillPartRule Rule(string id, SawmillPartKind kind, bool isSaw, params string[] patterns)
        => new SawmillPartRule(id, kind, isSaw, Array.ConvertAll(patterns, Glob));
}
