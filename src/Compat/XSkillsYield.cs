using System;

namespace ImmersiveWoodworking;

/// <summary>
/// The ONE place the xSkills yield/wear bonuses turn a fractional percentage into a whole-number unit count, using the
/// SAME chance-based rounding xSkills itself uses (BlockDropItemStack.GetNextItemStack for drops / the carefulminer tool
/// behaviour for durability, XSkillsToolBehavior.cs:70-73): the whole part of <c>amount × factor</c> is applied for
/// certain, and the leftover FRACTION becomes the probability of one extra unit. This keeps a small bonus from vanishing
/// to zero under integer truncation — a +8% on 8 firewood is 0.64, i.e. a 64% chance of +1, not a floored 0 — and makes
/// the bonuses feel like the vanilla xSkills perks they sit alongside. Shared by sawyer/splitter yield (the result is
/// ADDED to the base output) and carefulsawyer wear (the result is SUBTRACTED from the durability cost), so the three
/// sites can never drift on the rounding rule.
/// </summary>
static class XSkillsYield
{
    /// <summary>
    /// The chance-rounded whole quantity of <paramref name="amount"/> × <paramref name="factor"/>: the integer part for
    /// certain, plus 1 more with probability equal to the fractional part (rolled once on <paramref name="rand"/>).
    /// Returns 0 when either input is non-positive. The caller ADDS this to a base yield (sawyer/splitter) or SUBTRACTS
    /// it from a durability cost (carefulsawyer). Mirrors XSkills' own idiom exactly (<c>frac ≥ Rand ⇒ +1</c>).
    /// </summary>
    public static int ChanceScaled(int amount, float factor, Random rand)
    {
        if (amount <= 0 || factor <= 0f) return 0;
        float scaled = amount * factor;
        int whole = (int)scaled;
        float frac = scaled - whole;                     // leftover fraction = probability of one extra unit
        if (frac > 0f && frac >= rand.NextDouble()) whole++;
        return whole;
    }
}
