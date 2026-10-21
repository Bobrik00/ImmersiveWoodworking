using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ImmersiveWoodworking;

/// <summary>How a chopper model part behaves at render time.</summary>
public enum ChopperPartKind
{
    /// <summary>Baked into the chunk mesh once (the frame/housing + folded static base); never moves.</summary>
    Static,
    /// <summary>Drawn by <see cref="ChopperRenderer"/> with its own matrix (animated in T07.02; identity in T07.01).</summary>
    Mover,
    /// <summary>Present in the authored monolith but NOT rendered from it — neither chunk-baked (that is
    /// <see cref="Static"/>) nor drawn by the renderer (that is <see cref="Mover"/>): both the static bake
    /// (<c>Kind == Static</c>) and the mover build (<c>Kind == Mover</c>) skip it, so its faces are never tesselated.
    /// The only member is the authored workpiece log (<c>dropmaul-log*</c>, added 2026-07-05) — the billet is drawn
    /// SEPARATELY, species-painted, by T07.03 (from the shared sawhorse_log shape), so the in-model log is a Blender
    /// positioning reference that must be blanked here (else the frame catch-all would bake it as a permanent oak billet).</summary>
    Hidden
}

/// <summary>One movable/static part of the authored chopper model: an ordered set of name-glob rules plus how it moves.</summary>
public sealed class ChopperPartRule
{
    public readonly string Id;
    public readonly ChopperPartKind Kind;
    /// <summary>True only for the axe/maul head (<c>dropmaul-head*</c>): its faces are metal-textured by the installed
    /// <c>chopperhead-{metal}</c> (ITexPositionSource <c>"metal"</c>) — the wear tier made visible, copper..steel.</summary>
    public readonly bool IsHead;
    public readonly Regex[] Patterns;

    public ChopperPartRule(string id, ChopperPartKind kind, bool isHead, Regex[] patterns)
    {
        Id = id; Kind = kind; IsHead = isHead; Patterns = patterns;
    }
}

/// <summary>
/// T07.01 — the element→part membership for the authored chopper model (<c>shapes/block/chopper/chopper.json</c>),
/// grounded on the tuned part layout in <c>Mods/chopper-animator.html</c> (<c>defaultParts()</c>) and the anim spec
/// (T07.00, <c>07-visual-sound/00-anim-spec.md</c> §1). A 1:1 structural mirror of <see cref="SawmillParts"/>: the
/// model is flat-with-groups and carries Blender <c>_NNN</c> suffixes, so membership is decided EXACTLY as the animator
/// does — ordered rules, first-match-wins, each pattern tested against ANY name in the element's ancestor chain (NOT
/// the engine's path-based <c>selectiveElements</c>, which can't reproduce that).
///
/// The mechanism is now a ÷48 REDUCTION gear train (E12/T12.03b, 2026-07-13): the network-driven rotor coupling
/// (<c>rotorDefault/2/3</c> + <c>mainRotorTwoway</c>) → <c>clutch1</c> (÷4) → <c>clutch2</c> (÷16) → the cam pair
/// (<c>gearActive/gearPassive</c>, ÷48) whose teeth lift a pair of pawls (<c>levelerL/R</c>, Trip) that raise the axe
/// beam (<c>arm</c>, Trip translate, one lift-drop per 48 shaft revs) carrying the head (<c>head</c>, Ride). The per-part
/// DRIVERS live in <c>ChopperAnim.cs</c>; here only the membership + Static/Mover/Hidden classification (+ the head flag)
/// is needed. Verified against the authored model (re-modeled 2026-07-13 with the reducer): 15 rules cover all 474
/// elements (rotorDefault[Rotor_default_1] 26 + rotorStatic[Rotor_main_static] 8 + gearActive 64 + gearPassive 32 +
/// levelerL 12 + rotorDefault2[Rotor_default_2] 26 + levelerR 12 + head 4 + arm 75 + mainRotorTwoway 22 + log 9 +
/// rotorDefault3[Rotor_default_3] 10 + clutch1[Rotor_clutch1] 18 + clutch2[Rotor_clutch2] 18 + frame 138). The
/// workpiece log IS a group in the model (<c>dropmaul-log</c>, 9 elements) but is classified <see cref="ChopperPartKind.Hidden"/>
/// — NOT rendered from this model: the billet is drawn separately, species-painted, in T07.03 (from the shared
/// sawhorse_log shape), and the clamp-bed is the hand <c>choppingblock</c> (its own block), not an element of this model.
/// Keep <see cref="Rules"/> in the SAME order as the spec table — priority is positional.
/// </summary>
public static class ChopperParts
{
    public static readonly ChopperPartRule[] Rules =
    {
        // ROTOR / RECTIFIER assembly — re-tuned 2026-07-09 from the user's chopper-animator export (the config the user
        // authored the "looks correct" animation against). The twoway input coupling is SPLIT into per-ring movers
        // (Rotor_default_1/2 + MainRotor_twoway) so each carries its own axis + spin sign; all are SIGNED (input side of
        // the dual-source rectifier, ChopperAnim Src="signed"), and the housing (rotorStatic) hides the signed↔forward
        // junction. ORDER = the animator part order 1:1 (first-match by ancestor chain), so in-game membership == what the
        // stand shows. All MOVERS gated on hasDrive (PartVisible), so the coupling appears only once the drive is in.
        Rule("rotorDefault",    ChopperPartKind.Mover,  false, "Rotor_default_1*"),
        Rule("rotorStatic",     ChopperPartKind.Mover,  false, "Rotor_main_static*"),
        Rule("gearActive",      ChopperPartKind.Mover,  false, "dropmaul-gear-active*"),
        Rule("gearPassive",     ChopperPartKind.Mover,  false, "dropmaul-gear-passive*"),
        Rule("levelerL",        ChopperPartKind.Mover,  false, "dropmaul-leveler-l*"),
        Rule("rotorDefault2",   ChopperPartKind.Mover,  false, "Rotor_default_2*"),
        Rule("levelerR",        ChopperPartKind.Mover,  false, "dropmaul-leveler-r*"),
        Rule("head",            ChopperPartKind.Mover,  true,  "dropmaul-head*"),
        Rule("arm",             ChopperPartKind.Mover,  false, "dropmaul-arm*"),
        // The twoway input rotor (22 elements). The user's 2026-07-09 re-export UN-NESTED the Rotor_default_3 ring in
        // Blender, so this rule no longer captures it — MainRotor_twoway_* now carries only its own geometry (X, −1), and
        // the un-nested ring falls through to the rotorDefault3 rule below for its own Z spin (exactly what the animator shows).
        Rule("mainRotorTwoway", ChopperPartKind.Mover,  false, "MainRotor_twoway*"),
        // The authored workpiece log (monolith reference). HIDDEN — the billet is drawn separately + species-painted in
        // T07.03. Kept ABOVE the frame catch-all so `*` can't bake it as a permanent oak billet. See spec §5.
        Rule("log",             ChopperPartKind.Hidden, false, "dropmaul-log*"),
        // Rotor_default_3_* (15 elements): after the 2026-07-09 un-nest it is an independent ring (no MainRotor_twoway
        // ancestor), so it is captured HERE, below mainRotorTwoway — giving it its own Z spin (driver in ChopperAnim),
        // 1:1 with the animator config.
        Rule("rotorDefault3",   ChopperPartKind.Mover,  false, "Rotor_default_3*"),
        // REDUCER stages (E12/T12.03b, 2026-07-13): the ÷48 gear train the user added in Blender. Rotor_clutch1 = the
        // stage-1 output gear (÷4), Rotor_clutch2 = the stage-2 output gear (÷16); the cam pair (gearActive/gearPassive)
        // is the ÷48 stage. Both MOVERS, gated on hasDrive like the rest of the coupling (PartVisible), prefix-distinct,
        // kept in the animator's export order (right before the frame catch-all) so C# membership == the animator preview.
        Rule("clutch1",         ChopperPartKind.Mover,  false, "Rotor_clutch1*"),
        Rule("clutch2",         ChopperPartKind.Mover,  false, "Rotor_clutch2*"),
        // Catch-all frame LAST (static). Every rotor/gear/pawl rule above is anchored + prefix-distinct, so `*` only
        // sweeps the genuine frame/housing remainder — nothing animated leaks into the bake.
        Rule("frame",           ChopperPartKind.Static, false, "dropmaul-frame*", "*"),
    };

    /// <summary>Index into <see cref="Rules"/> of the first rule any name in <paramref name="chain"/> (root..self)
    /// matches; falls back to the catch-all frame (last rule). Mirrors the animator's <c>partFor</c> /
    /// <see cref="SawmillParts.IndexFor"/>.</summary>
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

    // Case-sensitive full-match glob ('*' => any run), exactly like the animator's globRe and SawmillParts.Glob — names
    // such as "dropmaul-arm_074" are case-significant.
    static Regex Glob(string pattern) =>
        new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);

    static ChopperPartRule Rule(string id, ChopperPartKind kind, bool isHead, params string[] patterns)
        => new ChopperPartRule(id, kind, isHead, Array.ConvertAll(patterns, Glob));
}
