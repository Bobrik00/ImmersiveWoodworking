using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

public partial class BlockEntitySawhorse
{
    // ──────────────────── Debarking — side action on the pair (E01) ────────────────────
    //
    // Stripping bark is the SIDE counterpart to the end-gated rip: the player stands at a LONG side of the laid log
    // and holds RMB with a debarking tool (a knife, an axe + offhand hammer, or the bark spud), and the bark comes
    // off in passes until log-placed-{wood} becomes debarkedlog-{wood} — the in-world equivalent of the vanilla
    // axe+hammer grid recipe (assets/survival/recipes/grid/debarkedlog.json), which likewise drops NO bark. The
    // machinery mirrors the rip (master routing, serialized progress, co-op by side), but the START gate is the
    // geometric INVERSION of IsPlayerAtAxisEnd: the player must be at a SIDE, not an end or a corner. T01.02 lays
    // the position gate + the CanDebarkHere shim; the tool gate (T01.03), the hold/progress (T01.06) and the
    // log→debarkedlog transform (T01.07) hang off this same gate.

    // The debark "side zone" is the geometric INVERSION of the rip's end zone (IsPlayerAtAxisEnd): the player stands
    // at a LONG side of the pair — across the log past the side face (|perp| at/over the width tolerance) yet WITHIN
    // its length (|along| not past an end face) — rather than at an end or off a corner diagonal. Reusing the end
    // gate's OWN two thresholds as the complement is deliberate: it guarantees the side and end zones can NEVER
    // overlap and that the corner diagonals fall in NEITHER (the same dead-corner rejection the end gate already
    // makes — see its |along|>|perp| note), and it keeps the two gates from drifting apart if a threshold is ever
    // retuned. With the comparisons inverted, the four regions partition cleanly: (|along|>1, |perp|<0.75) = END;
    // (|along|≤1, |perp|≥0.75) = SIDE; (|along|>1, |perp|≥0.75) = corner → neither; (|along|≤1, |perp|<0.75) = the
    // footprint itself (the block — a player can't stand there). No point is ever in both zones.
    const double SideZoneMinPerp = EndZoneMaxPerp;   // end players are capped UNDER this across → a side player is over it
    const double SideZoneMaxAlong = EndZoneMinAlong; // end players are pushed OVER this along → a side player is under it

    /// <summary>
    /// True when <paramref name="byPlayer"/> stands at a LONG SIDE of the pair — the geometric INVERSION of
    /// <see cref="IsPlayerAtAxisEnd"/>: across the log past the side face (<c>|perp| ≥ <see cref="SideZoneMinPerp"/></c>)
    /// yet within its length (<c>|along| ≤ <see cref="SideZoneMaxAlong"/></c>), so a player at an END or off a CORNER
    /// diagonal is rejected (a corner has |along| over the length AND |perp| over the width → in neither zone,
    /// exactly as the end gate already drops it). Shares <see cref="PlayerAxisOffset"/> with the end gate so the two
    /// can't drift. By PHYSICAL position, not view. Master-relative — call on the master (so <see cref="pairAxis"/> /
    /// <see cref="Pos"/> are the laid log's; heldLog ≠ null guarantees inPair &amp; isMaster).
    /// </summary>
    internal bool IsPlayerAtAxisSide(IPlayer byPlayer)
    {
        (double along, double perp) = PlayerAxisOffset(byPlayer);
        // At a long side (past the side face) but not past an end face — a side box, the complement of the end cap.
        return System.Math.Abs(perp) >= SideZoneMinPerp && System.Math.Abs(along) <= SideZoneMaxAlong;
    }

    // ── Tool tiers (T01.03) ──
    // Bark comes off at three TIERS, distinguished by tool TYPE (not metal) and differing only in SPEED: tier 1 a
    // knife (starter, slow), tier 2 an axe in the main hand WITH a hammer in the offhand (copper-age, medium), tier 3
    // the bark spud (a crafted dedicated tool, fast — T02.03). This mirrors the rip's IsSawTool/SawIsEffective/SawSpeedFor
    // split (BlockEntitySawhorse.HeldLog.cs): static TYPE predicates below + an instance effectiveness gate that reads
    // the live Config + an instance speed factor. Keying on EnumTool (not item code) keeps it MOD-INDEPENDENT — a
    // modded axe/hammer/knife qualifies — exactly like IsSawTool.

    /// <summary>Whether <paramref name="stack"/> is an axe — MOD-INDEPENDENT, keyed on the tool TYPE
    /// (<see cref="EnumTool.Axe"/>), so a modded axe with any code qualifies, exactly like <see cref="IsSawTool"/>.
    /// The tier-2 main-hand half (paired with an offhand <see cref="IsHammer"/>).</summary>
    internal static bool IsAxe(ItemStack? stack) => stack?.Item?.Tool == EnumTool.Axe;

    /// <summary>Whether <paramref name="stack"/> is a hammer — MOD-INDEPENDENT (tool TYPE <see cref="EnumTool.Hammer"/>).
    /// The tier-2 offhand half; an axe + offhand hammer together are the in-world echo of the vanilla axe+hammer
    /// debark grid recipe.</summary>
    internal static bool IsHammer(ItemStack? stack) => stack?.Item?.Tool == EnumTool.Hammer;

    /// <summary>Whether <paramref name="stack"/> is a knife — MOD-INDEPENDENT (tool TYPE <see cref="EnumTool.Knife"/>).
    /// The tier-1 starter tool, admitted only when <see cref="ImmersiveWoodworkingConfig.AllowKnifeDebark"/> is on.</summary>
    internal static bool IsKnife(ItemStack? stack) => stack?.Item?.Tool == EnumTool.Knife;

    /// <summary>Whether <paramref name="stack"/> is the bark spud — the mod's purpose-built TIER-3 debarking tool
    /// (<c>barkspud-{metal}</c>, T02.03). Identified by code part (NOT tool TYPE) so the status is OPT-IN and the item
    /// carries NO <see cref="EnumTool"/> that would leak it into the rip gate (<see cref="IsSawTool"/>) or the tier-2
    /// halves (<see cref="IsAxe"/>/<see cref="IsHammer"/>) — exactly like <see cref="IsPitSaw"/>: any mod can mark its
    /// own tool as a bark spud by naming it <c>barkspud-*</c> (domain-agnostic). The single-tool tier-3 branch of
    /// <see cref="BarkToolIsEffective"/> / <see cref="BarkSpeedFor"/>.</summary>
    internal static bool IsBarkSpud(ItemStack? stack) => CodeParts.Is(stack?.Item?.Code, 0, "barkspud");

    /// <summary>Whether the held tools strip bark here — the tool half of <see cref="CanDebarkHere"/>, the debark
    /// counterpart to <see cref="SawIsEffective"/>. True for tier 2 (an axe in <paramref name="mainHand"/> AND a hammer
    /// in <paramref name="offHand"/> together — BOTH required), or tier 1 (any knife in the main hand) only when
    /// <see cref="ImmersiveWoodworkingConfig.AllowKnifeDebark"/> is on (default on — the starter tier a server can
    /// switch off for a harder progression), or tier 3 (the bark spud alone, <see cref="IsBarkSpud"/>). How FAST each
    /// admitted tier strips is the separate <see cref="BarkSpeedFor"/>. Instance method (not static like the TYPE
    /// predicates): reads the live, server-synced <see cref="Config"/>; call on the master, where every debark gesture
    /// routes.</summary>
    internal bool BarkToolIsEffective(ItemStack? mainHand, ItemStack? offHand)
    {
        if (IsBarkSpud(mainHand)) return true;                          // tier 3 — the purpose-built bark spud, alone
        if (IsAxe(mainHand) && IsHammer(offHand)) return true;          // tier 2 — axe (main) + hammer (offhand)
        if (Config.AllowKnifeDebark && IsKnife(mainHand)) return true;  // tier 1 — any knife, toggle-gated starter
        return false;
    }

    /// <summary>The debarking-speed factor for the held tools, scaling the progress accrued per tick against the
    /// spud-referenced <c>DebarkSeconds</c> — the debark counterpart to <see cref="SawSpeedFor"/>. By tool TYPE for the
    /// tier, plus (E02) an added METAL-tier factor on the SPUD ONLY: the bark spud is the tier-3 reference and strips at
    /// its SHARPNESS- and metal-tier-scaled speed
    /// (<see cref="SharpnessSpeedFactor"/> — full speed 1.0 when sharp, down to
    /// <see cref="ImmersiveWoodworkingConfig.SharpnessSpeedFloor"/> when blunt; always 1.0 WITHOUT Toolsmith, so the
    /// base behaviour is unchanged); an axe+hammer strips at <see cref="ImmersiveWoodworkingConfig.AxeHammerDebarkSpeedMultiplier"/>
    /// (default 0.3 → ~5s) and a knife at <see cref="ImmersiveWoodworkingConfig.KnifeDebarkSpeedMultiplier"/>
    /// (default 0.15 → ~10s) — neither is affected by sharpness (user decision: "no difference with an axe"). Only an
    /// EFFECTIVE tool reaches here (callers gate on <see cref="BarkToolIsEffective"/> first), so the 1.0 fallback is the
    /// spud's reference speed. The tier stays TYPE-keyed so a mixed co-op — one sawyer spudding, another with axe+hammer
    /// from the far side — simply sums each side's own speed, like the pit+vanilla saw; each spud's factor is then scaled
    /// by its OWN sharpness AND metal (E02), while the knife and axe+hammer tiers stay flat (their metal ignored — they
    /// carry no wood mining speed). Instance method: reads the live, server-synced <see cref="Config"/>.</summary>
    internal float BarkSpeedFor(ItemStack? mainHand, ItemStack? offHand)
    {
        if (IsBarkSpud(mainHand)) return SharpnessSpeedFactor(mainHand) * ToolTierSpeedFactor(mainHand); // tier 3 spud — sharpness- AND metal-tier-scaled (the reference)
        if (IsAxe(mainHand) && IsHammer(offHand)) return Config.AxeHammerDebarkSpeedMultiplier; // tier 2 — not sharpness-affected, flat across metals
        if (IsKnife(mainHand)) return Config.KnifeDebarkSpeedMultiplier;                        // tier 1 — not sharpness-affected, flat across metals
        return 1f;                                                                              // defensive (the gate admits only the three tiers)
    }

    // ── Bark drop CHANCE by tool tier (T02.05) ──
    // The tool tier also biases WHICH bark a completed strip yields (CompleteDebark samples the multiplier BEFORE its
    // wear pass, T08.02, then hands it down: CompleteDebark -> DropRolledBark -> BarkDropTable.Roll): the bark spud
    // is the REFERENCE (a copper spud = 1.0, so oak's 1.0 tanbark stays a certain 100% — the top clamp),
    // while the cheaper tiers PENALISE the special-bark chance (axe+hammer < spud, knife lower still) — the careful
    // stripper earns richer bark, the careless one more plain bark (user design 2026-07-25). On TOP of the spud's 1.0 its
    // METAL tier adds up to BarkChanceMultSpudSteelBonus at steel (MetalTierBonusFraction: 0 at a copper spud, 1 at steel),
    // which lifts the sub-ceiling woods (the bast and medicinal woods, larch, pine…) — oak and birch both sit at a
    // table 1.0, so a better spud can't push THEM further (AuditFixPlan T08.07 corrected the stale "birch 0.8" here).
    // These are the CHANCE counterpart to the SPEED tiers (BarkSpeedFor) and stay TYPE-keyed the same way; only the
    // spud reads its metal. The four values moved to CONFIG in T08.01 (Config.BarkChanceMult*) and
    // are read LIVE below off the server-synced Config, so a server retunes the tier bonus without a rebuild; Sanitize
    // clamps each to [0, 1] (same defer-to-E08 the burn/dry numbers took at T01.03/T01.04).

    /// <summary>The special-bark CHANCE multiplier for the held tools — the CHANCE counterpart to <see cref="BarkSpeedFor"/>,
    /// fed to <see cref="BarkDropTable.Roll"/> so a better tier rolls the special kinds more often. The bark spud
    /// (<see cref="IsBarkSpud"/>) is the reference <see cref="ImmersiveWoodworkingConfig.BarkChanceMultSpud"/> (1.0) PLUS a metal-tier bonus up to
    /// <see cref="ImmersiveWoodworkingConfig.BarkChanceMultSpudSteelBonus"/> at steel (<see cref="MetalTierBonusFraction"/> — 0 at a copper spud, 1 at
    /// steel), so a steel spud lifts the sub-ceiling woods while oak and birch stay clamped at 100%; an axe+hammer strips at
    /// <see cref="ImmersiveWoodworkingConfig.BarkChanceMultAxeHammer"/> (0.75) and a knife at <see cref="ImmersiveWoodworkingConfig.BarkChanceMultKnife"/> (0.5), both flat across
    /// metals (they carry no wood mining tier, exactly like their speed tiers). Only an effective tool reaches here in
    /// practice (the caller resolves the active debarkers), so the 1.0 fallback is defensive. Instance method: reads the live,
    /// server-synced Config (and the mod system for the spud's metal-tier bonus). The generic-remainder woods are unaffected — a multiplier only
    /// scales EXISTING special chances (<see cref="BarkDropTable.Roll"/>), so a species with no special bark stays
    /// all-generic at any tier.</summary>
    internal float BarkChanceMultFor(ItemStack? main, ItemStack? off)
    {
        if (IsBarkSpud(main)) return Config.BarkChanceMultSpud + Config.BarkChanceMultSpudSteelBonus * MetalTierBonusFraction(main); // tier 3 — reference + metal bonus
        if (IsAxe(main) && IsHammer(off)) return Config.BarkChanceMultAxeHammer;                                            // tier 2 — flat penalty
        if (IsKnife(main)) return Config.BarkChanceMultKnife;                                                               // tier 1 — flat penalty
        return 1f;                                                                                                     // defensive (the spud reference)
    }

    /// <summary>The BEST special-bark chance multiplier (<see cref="BarkChanceMultFor"/>) among the players currently
    /// manning a side of this strip — the co-op rule the plan sets (T02.05): a mixed strip takes the HIGHEST tier that
    /// helped, so pairing a knife with a steel spud rolls at the spud's odds, never dragged down by the weaker tool.
    /// (T02.06) Each participant's own xSkills <c>carefulsawyer</c> perk then folds into THEIR own factor as
    /// <c>×(1 + bonus)</c> BEFORE the max — so the winner is the best (tool tier × personal skill), and a highly-skilled
    /// stripper with a lesser tool can out-roll a novice with a better one, while the two effects never combine
    /// super-additively across players (each player's tool and perk multiply only with each other). The perk fold is
    /// <c>×1.0</c> without xSkills / an unlearned ability, so the distribution is unchanged there, and the resulting
    /// multiplier is clamped only implicitly — <see cref="BarkDropTable.Roll"/>'s cumulative walk saturates a species'
    /// odds at 100%, so even a maxed steel-spud stripper can never overflow past a certain drop. Reads
    /// <see cref="barkSides"/> (the active debarkers, resolved to main-hand + offhand exactly as
    /// <see cref="DamageActiveDebarkTools"/> does) BEFORE any reset clears it, so call it during
    /// <see cref="CompleteDebark"/> — and there BEFORE the wear pass (AuditFixPlan T08.02, F-L9-04), because the
    /// wear can break a tool on this very strip and the engine empties the slot it broke in. Skips a debarker who
    /// switched off a valid tier mid-hold and offline players; returns 1.0 (the spud reference) when none is
    /// resolvable. That fallback is the BEST odds in the game, not a neutral one, so it must stay unreachable: the
    /// pre-wear call site on a server-authoritative, still-held completion is what keeps it so. Server-side,
    /// master-only (its caller is), so the perk read is a pure server-side
    /// roll input — the client predicts no bark kind (T02.06 DoD).</summary>
    double BestActiveDebarkerBarkChanceMult()
    {
        ImmersiveWoodworkingModSystem mod = modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        double best = 1.0;
        bool any = false;
        foreach (SawyerHold held in barkSides.Values)
        {
            IPlayer? player = Api.World.PlayerByUid(held.Uid);
            if (player == null) continue; // offline
            ItemStack? main = player.InventoryManager.ActiveHotbarSlot?.Itemstack;
            ItemStack? off = player.Entity?.LeftHandItemSlot?.Itemstack;
            if (!BarkToolIsEffective(main, off)) continue; // switched away from a valid tier mid-hold
            double m = BarkChanceMultFor(main, off);
            // T02.06: fold in THIS debarker's own carefulsawyer perk (×(1 + bonus)) on their own tool-tier factor,
            // BEFORE the co-op max — so the best (tool × skill) pairing decides the roll. No-op (×1.0) without xSkills.
            if (mod.TryGetCarefulSawyerBarkBonus(player, out float bonus)) m *= 1.0 + bonus;
            if (!any || m > best) { best = m; any = true; }
        }
        return any ? best : 1.0;
    }

    // ── Debark FK animations (E03/T03.02) ──
    // The held-tool gesture played while a genuine strip hold runs, one base code per tier — every tier has its OWN
    // animation: the bark spud (tier 3) draws a two-handed pull (debarking-barkspud, driven T03.02); the axe+hammer
    // (tier 2) gets its own gesture (debarking-axe, driven T03.03 — the in-world echo of tapping the hammer on the
    // axe head wedged under the bark); the knife (tier 1) draws a one-handed scrape (debarking-knife) — all authored
    // + registered by T03.01.
    // Each is registered as metadata on game:entities/humanoid/player + keyframes on seraph-faceless by
    // patches/debarking_animations.json, backfilled onto reskin shapes by EntityBehaviorChopAnim (cross-model), and
    // LOOPS (onAnimationEnd=Repeat) until the hold ends then EaseOut's — exactly like the rip's "sawing". Driven
    // MANUALLY (gated on a real strip start in BlockSawhorse), NOT via GetHeldTpUseAnimation, which fires on ANY
    // RMB-hold (vs-held-use-anim-trigger) and would strip in mid-air. The engine routes the base code to its -fp
    // variant in first person, so we start/stop only the base code (like the saw).
    internal const string DebarkSpudAnim  = "debarking-barkspud";
    internal const string DebarkAxeAnim   = "debarking-axe";
    internal const string DebarkKnifeAnim = "debarking-knife";

    /// <summary>Every debark animation BASE code, so <see cref="BlockSawhorse.OnBlockInteractStop"/> /
    /// <c>OnBlockInteractCancel</c> can stop them ALL on release without tracking which tier started one —
    /// <c>StopAnimation</c> is idempotent. Covers ALL THREE tiers (spud + axe + knife) so any path stops cleanly on
    /// release; the <c>-fp</c> variants stop with their base code, like the saw.</summary>
    internal static readonly string[] DebarkAnimCodes = { DebarkSpudAnim, DebarkAxeAnim, DebarkKnifeAnim };

    /// <summary>The debark FK animation BASE code to play for the held tools, or null when no tier matches — the
    /// debark counterpart to <see cref="BarkSpeedFor"/>, by tool TYPE. Tier 3 (the bark spud,
    /// <see cref="IsBarkSpud"/>) → <see cref="DebarkSpudAnim"/>. Tier 2 (an axe in <paramref name="mainHand"/> + a
    /// hammer in <paramref name="offHand"/>) → <see cref="DebarkAxeAnim"/> (T03.03) — its own two-handed gesture (the
    /// in-world echo of tapping the hammer on the axe head under the bark). Tier 1 (a knife) →
    /// <see cref="DebarkKnifeAnim"/> — its own one-handed scraping gesture. Static + purely cosmetic (tier is tool
    /// TYPE, no <see cref="Config"/> read — the <see cref="ImmersiveWoodworkingConfig.AllowKnifeDebark"/> gate is
    /// already applied upstream by <see cref="BarkToolIsEffective"/>, so a knife only reaches here when its tier is
    /// admitted). The caller (<see cref="BlockSawhorse.OnBlockInteractStart"/>) starts the returned code on a genuine
    /// strip start and stops <see cref="DebarkAnimCodes"/> on release; null only for a non-tier hand the gate would
    /// already have refused.</summary>
    internal static string? DebarkAnimFor(ItemStack? mainHand, ItemStack? offHand)
    {
        if (IsBarkSpud(mainHand)) return DebarkSpudAnim;                // tier 3 — the bark spud's two-handed pull
        if (IsAxe(mainHand) && IsHammer(offHand)) return DebarkAxeAnim; // tier 2 — axe (main) + offhand hammer (T03.03)
        if (IsKnife(mainHand)) return DebarkKnifeAnim;                  // tier 1 — the knife's one-handed scrape
        return null;                                                    // no tier (the gate already refused this hand) → no gesture
    }

    /// <summary>Whether the pair holds a still-BARKED log — a <c>log-placed-{wood}</c> (<see cref="IsSawableLog"/>),
    /// read from the master so either cell answers the same. The "bark still on" half of <see cref="CanDebarkHere"/>:
    /// an empty pair AND a pair holding an already-stripped <c>debarkedlog-{wood}</c> both read false, so a finished
    /// log can't be re-debarked (a no-op, T01.07). <see cref="CompleteDebark"/> drops the bare log OUT (the pair then
    /// empties, or auto-restocks a fresh barked log), which is exactly what ends each strip. Master-relative (uses
    /// <see cref="GetMaster"/>), so it is slave-safe like <see cref="HasLog"/>.</summary>
    internal bool HasBark => IsSawableLog(GetMaster().heldLog);

    /// <summary>Whether a strip has BEGUN on the held log (<see cref="barkProgress"/> &gt; 0) — the debark counterpart
    /// to <see cref="SawStarted"/>, read from the master. Once true the log is LOCKED into debark mode: the rip gate
    /// (<see cref="CanSawHere"/>) refuses it (<c>!BarkStarted</c>), so a log is only ever sawn OR debarked, decided by
    /// whichever began first (the saw at an end → rip, an axe+hammer/knife/spud at a side → strip — T05.03). Resets to
    /// false with the log (<see cref="ResetDebarkState"/>) AND on a completed strip, so the resulting bare log is a
    /// fresh, unlocked billet the rip can then take into beams.</summary>
    internal bool BarkStarted => GetMaster().barkProgress > 0f;

    /// <summary>
    /// The debark start gate: the pair holds a still-BARKED log (<see cref="HasBark"/> — a <c>log-placed-*</c>, NOT an
    /// already-debarked log, so a stripped log is a no-op), NO rip has begun on it (<c>!<see cref="SawStarted"/></c> —
    /// the log is locked to whichever action started first, so a log already being sawn can't also be debarked,
    /// T05.03), the player stands at a long SIDE (<see cref="IsPlayerAtAxisSide"/>), AND the held tools strip bark
    /// (<see cref="BarkToolIsEffective"/> — an axe with an offhand hammer, or a knife when
    /// <see cref="ImmersiveWoodworkingConfig.AllowKnifeDebark"/> is on, or the bark spud alone).
    /// <paramref name="active"/> is the player's main-hand slot; the offhand (hammer) is read from the player entity's
    /// <c>LeftHandItemSlot</c> (the T01.01 spike: a hammer sits there natively). Master-relative: call on the master
    /// (<see cref="IsPlayerAtAxisSide"/> needs the master's geometry; <see cref="HasBark"/> / <see cref="SawStarted"/>
    /// already read it via the master).
    /// </summary>
    internal bool CanDebarkHere(IPlayer byPlayer, ItemSlot? active)
        => HasBark
           && !SawStarted
           && DebarkProductResolves()   // the wood must have a debarkedlog variant — never offer a strip that can't finish (the dead-end gate)
           && IsPlayerAtAxisSide(byPlayer)
           && BarkToolIsEffective(active?.Itemstack, byPlayer?.Entity?.LeftHandItemSlot?.Itemstack);

    /// <summary>Whether the held barked log has a resolvable <c>debarkedlog-{wood}-{orient}</c> product — i.e. the
    /// strip can actually COMPLETE. Mirrors <see cref="CompleteDebark"/>'s resolution (the log's own domain then game,
    /// same orientation). Vanilla ships a debarkedlog for EVERY log wood (incl. aged/veryaged/rotten), and a proper
    /// wood mod ships them alongside its logs, so this is true in the normal case; it is false only for a wood that
    /// adds <c>log-placed-{wood}</c> WITHOUT a matching <c>debarkedlog-{wood}</c> — such a log must NOT be offered a
    /// strip that could never finish (the dead-end this closes, also hidden from the interaction help). Master-relative;
    /// a cheap block lookup.</summary>
    internal bool DebarkProductResolves() => DebarkProductResolves(Api.World, GetMaster().heldLog);

    /// <summary>The same dead-end gate for an ARBITRARY stack rather than the laid log — the form the doors that see a
    /// stack before it is ever laid need: the put (<see cref="CanProcessInput"/>) and the debark auto-restock's accept
    /// predicate (<see cref="PeekDebarkRestockSlot"/>, AuditFixPlan T03.03 / F-L9-27, which used to hand the pair a log
    /// the start gate then refused to strip, silently). Self-contained where the instance form leans on its callers:
    /// it re-checks <see cref="IsSawableLog"/> itself, mirroring <see cref="HasBark"/>, so an already-debarked log
    /// answers false here rather than resolving its own code and claiming it could be stripped again. <c>static</c> and
    /// world-parametrised for the same reason as <see cref="SawProductResolves"/> — one definition, every door.</summary>
    internal static bool DebarkProductResolves(IWorldAccessor world, ItemStack? log)
    {
        if (!IsSawableLog(log)) return false; // only a still-barked log-placed-* can be stripped at all (mirrors HasBark)
        // Kind is already settled by the line above, so the species read skips re-classifying, and both parts come off
        // the code in place (AuditFixPlan T05.03): CanDebarkHere re-asks this gate on every tick of a strip hold.
        string? wood = GetWoodSpecies(log, SawInput.Log);
        if (log!.Block == null || wood == null) return false;
        string? orient = CodeParts.Last(log.Block.Code); // ud / ns / we — debarkedlog carries the same rotation variants
        return ResolveProductBlock(world, log.Block.Code.Domain, "debarkedlog-" + wood + "-" + orient) != null;
    }

    /// <summary>The debark gate MINUS the "a barked log is laid" clause — the player stands at a long SIDE
    /// (<see cref="IsPlayerAtAxisSide"/>) and the held tools strip bark (<see cref="BarkToolIsEffective"/>). Used during
    /// the auto-restock breather (<see cref="barkRestockPending"/>), where the pair is momentarily EMPTY but the held
    /// strip must stay alive until the next barked log is laid — the debark counterpart to
    /// <see cref="CanSawHereIgnoringLog"/>. The dropped HasBark/!SawStarted clauses are both vacuous over the empty pair
    /// (no log → no bark; sawProgress is 0), so this is the same two-clause shape as the rip's empty-pair gate.</summary>
    bool CanDebarkHereIgnoringLog(IPlayer byPlayer, ItemSlot? active)
        => IsPlayerAtAxisSide(byPlayer)
           && BarkToolIsEffective(active?.Itemstack, byPlayer?.Entity?.LeftHandItemSlot?.Itemstack);

    // ──────────────────── Debark hold cycle (T01.06) ────────────────────
    //
    // Stripping bark is the SIDE mirror of the end-gated rip (BlockEntitySawhorse.Sawing.cs): a held RMB accrues
    // barkProgress until the bark is fully off, routed through the master, co-op by SIDE (the rip is co-op by END).
    // The structure deliberately mirrors the rip's so E04 (progressive bark visual) and T01.07 (the log→debarkedlog
    // transform + tool wear) drop onto a familiar skeleton: Try/Continue/Stop + AdvanceDebark + a fire-once stage
    // counter, paced off each holder's OWN honest hold clock (deltas of the real-time-clamped secondsUsed, T02.03)
    // rather than a synced field — the lag-stall lesson from vs-chopanim-restock-easeout. The thin slice (E01)
    // has ONE stage = the full strip; the completion (CompleteDebark) drops the bare debarkedlog out and
    // auto-restocks — the transform landed (T01.07).

    /// <summary>The strip duration of the reference tool (the bark spud), live server-synced config — the debark
    /// counterpart to <see cref="SawSeconds"/>. The slower tiers reach it more slowly via <see cref="BarkSpeedFor"/>.</summary>
    float DebarkSeconds => Config.DebarkSeconds;

    // The strip's stage thresholds as fractions of DebarkSeconds — the debark counterpart to the rip's StageFractions.
    // The thin slice has a SINGLE stage at 1.0 (the whole strip fires once, at completion); E04 will subdivide this
    // (e.g. quarters) to drop bark off in passes for the progressive visual. Kept as an array (not a bare bool) so
    // that subdivision is a one-line change here, exactly like the rip's 4-stage set.
    static readonly double[] BarkStageFractions = { 1.0 };

    /// <summary>How many strip-stage thresholds <paramref name="progress"/> (seconds) has reached, 0..N — the debark
    /// counterpart to <see cref="StagesPassed"/>. Because <see cref="barkProgress"/> only increases, this is the count
    /// of stages already completed; the persisted <see cref="barkStagesFired"/> drives fire-once in
    /// <see cref="AdvanceDebark"/>.</summary>
    static int BarkStagesPassed(float progress, float barkSeconds)
    {
        int n = 0;
        foreach (double f in BarkStageFractions) if (progress >= f * barkSeconds) n++;
        return n;
    }

    /// <summary>Axis-frame sign of the SIDE the player stands at: +1 toward +perp (across the log), -1 toward -perp —
    /// the debark counterpart to <see cref="PlayerEndSign"/>. The across-the-log offset reduced to its sign; the gate
    /// (<see cref="IsPlayerAtAxisSide"/>) already guarantees the player IS at a side (|perp| past the width), so the
    /// sign is well-defined. Two opposite long sides give the two keys of <see cref="barkSides"/>. Master-relative.</summary>
    int PlayerSideSign(IPlayer byPlayer)
    {
        (_, double perp) = PlayerAxisOffset(byPlayer);
        return perp >= 0 ? 1 : -1;
    }

    /// <summary>
    /// Whether a click at world point <paramref name="worldHit"/> (blockSel.FullPosition) fell on the −axis half of the
    /// log ALONG its length — the debark counterpart to <see cref="HitIsReversedSide"/>, but along the LENGTH (the
    /// debarker stands at a SIDE, so the log runs left↔right in front of them) rather than across the width. The bark
    /// bands peel +axis end → −axis end by default (part_1 at the +axis end, see <see cref="DebarkHideSequence"/> /
    /// vs-debark-bark-geometry), so a click PAST the pair centre toward −axis returns true → peel from THERE. Purely
    /// self-consistent (click an end → peel from that end); if the control ever reads inverted in-game, flip the sign.
    /// The centre is the pair centre along the length (the +½ boundary nudge toward the partner is ON the length axis,
    /// unlike the perp-side gate). Master-relative (call on the master; <see cref="pairAxis"/> / <see cref="Pos"/> are
    /// the master's).
    /// </summary>
    bool HitIsBarkReversedSide(Vec3d worldHit)
    {
        double cx = Pos.X + 0.5 + (pairAxis == EnumAxis.X ? 0.5 : 0.0);
        double cz = Pos.Z + 0.5 + (pairAxis == EnumAxis.Z ? 0.5 : 0.0);
        double along = pairAxis == EnumAxis.X ? (worldHit.X - cx) : (worldHit.Z - cz);
        return along < 0.0; // clicked the −axis half → peel from there (default peels +axis→−axis)
    }

    /// <summary>The world perpendicular direction (a horizontal unit vector across the log) to eject the finished
    /// debarked log toward — AWAY from the debarker, per the user's "готовое бревно всегда от игрока". The perp world
    /// axis is X for a Z-pair, Z for an X-pair (<see cref="PlayerAxisOffset"/>'s perp); <see cref="barkSides"/> is keyed
    /// by <see cref="PlayerSideSign"/> (the side the debarker stands on, +1/−1 along that perp axis), so "away" is its
    /// opposite. With two opposite-side co-op debarkers "away" is undefined → default to +perp. ROBUST: derived from the
    /// debarker's real position, so it needs NO in-game sign confirmation (unlike the visual peel). Server-side,
    /// master-only; call while <see cref="barkSides"/> is still populated (before any reset). Fed to
    /// <see cref="SpawnEjectedItem"/>.</summary>
    (double ejectX, double ejectZ) DebarkAwayEjectDir()
    {
        int side = SoleDebarkerSideSign();     // +1/−1 the lone debarker's side, or 0 if none / both sides manned
        double away = side == 0 ? 1.0 : -side; // opposite the debarker along the perp axis (+perp default when ambiguous)
        return (pairAxis == EnumAxis.Z ? away : 0.0, pairAxis == EnumAxis.X ? away : 0.0);
    }

    /// <summary>The perp side (<see cref="PlayerSideSign"/>: +1/−1) the SINGLE active debarker mans, or 0 when none or
    /// BOTH sides are manned (a two-player co-op strip, where "away from the player" is undefined). Server-side; reads
    /// the transient <see cref="barkSides"/> map, whose keys ARE the manned side signs.</summary>
    int SoleDebarkerSideSign()
    {
        if (barkSides.Count != 1) return 0;
        foreach (int side in barkSides.Keys) return side;
        return 0;
    }

    /// <summary>The hold record for the side <paramref name="uid"/> currently mans, or null if none — a player mans at
    /// most one side. The debark counterpart to <see cref="SawyerFor"/>. Server-side.</summary>
    SawyerHold? DebarkerFor(string uid)
    {
        foreach (SawyerHold h in barkSides.Values) if (h.Uid == uid) return h;
        return null;
    }

    /// <summary>Free whatever side <paramref name="uid"/> mans (a player mans at most one); returns true if one was
    /// freed — used to decide whether to persist the progress accrued before the hold ended. The debark counterpart
    /// to <see cref="ReleaseSawyer"/>. Server-side.</summary>
    bool ReleaseDebarker(string uid)
    {
        int? key = null;
        foreach (var kv in barkSides) if (kv.Value.Uid == uid) { key = kv.Key; break; }
        if (key == null) return false;
        barkSides.Remove(key.Value);
        return true;
    }

    /// <summary>Drop any co-op side hold whose player is no longer online. There is no <c>PlayerDisconnect</c> hook, so a
    /// debarker who drops mid-strip would otherwise leave a ghost <see cref="barkSides"/> entry that blocks that side
    /// forever (<see cref="ClaimOrVerifySide"/> refuses a new player on it) and pins a restock breather open
    /// (<see cref="AbandonPendingDebarkRestockIfUnmanned"/> never reaches Count 0). Called before either acts on the
    /// map — and, since AuditFixPlan T03.04, before the START gate reads it too (<see cref="TryStartDebarking"/>,
    /// F-L5-25). Server-side. (If EVERY debarker is gone there is no live tick to run this, but the stale state
    /// self-heals on the next interaction via <see cref="ResetDebarkState"/>, exactly like the serialized
    /// restock-pending reload case, and the breather it pinned open ages out on its own clock —
    /// <see cref="DebarkRestockPauseIsStale"/>.) Mirrors the rip's <see cref="PruneOfflineSawyers"/>.</summary>
    void PruneOfflineDebarkers()
    {
        if (barkSides.Count == 0) return;
        List<int>? dead = null;
        foreach (var kv in barkSides)
            if (Api.World.PlayerByUid(kv.Value.Uid) == null) (dead ??= new List<int>()).Add(kv.Key);
        if (dead != null) foreach (int k in dead) barkSides.Remove(k);
    }

    /// <summary>Ensure <paramref name="uid"/> mans the side at <paramref name="sideSign"/> — confirm the one they
    /// already hold, or claim a free one (giving up any other side first, since a player mans at most one). Returns the
    /// hold, or null when that side is manned by ANOTHER debarker (the caller refuses — no same-side stacking). A
    /// freshly claimed side baselines its timer to <paramref name="secondsUsed"/> — 0 delta this tick, contributing
    /// from the next one. That is the MID-hold case (the player walked round to the other side, or resumed after an
    /// INSTANT restock — the zero-delay path, whose <see cref="ResetDebarkState"/> empties the map; the BREATHER path
    /// keeps <see cref="barkSides"/>, so a hold coming out of one is confirmed here, not re-claimed):
    /// the seconds already elapsed belong to the hold, not to this side. A hold that BEGINS at the
    /// side (<see cref="TryStartDebarking"/>) baselines to 0, because there t=0 really is the press. The debark
    /// counterpart to <see cref="ClaimOrVerifyEnd"/>. Server-side.</summary>
    SawyerHold? ClaimOrVerifySide(string uid, int sideSign, float secondsUsed)
    {
        PruneOfflineDebarkers(); // drop ghost holds from disconnected players first, so a live player can claim/reclaim a side
        if (barkSides.TryGetValue(sideSign, out SawyerHold? held))
        {
            return held.Uid == uid ? held : null; // ours → use it; another debarker's → refuse
        }
        ReleaseDebarker(uid);                       // give up any other side first (one side per player)
        held = new SawyerHold(uid, secondsUsed);    // baseline = now → 0 delta this tick, contributes next
        barkSides[sideSign] = held;
        return held;
    }

    /// <summary>
    /// Plain RMB at a long SIDE of a laid log with a debark tool: begin a strip hold if allowed — the SIDE mirror of
    /// <see cref="TryStartSawing"/>. Gate = <see cref="CanDebarkHere"/> (a log is laid, the player is at a side, and the
    /// held tools strip bark). When the gate fails this returns false so the caller (<see cref="BlockSawhorse.OnBlockInteractStart"/>)
    /// falls through to the rip path — the two never compete because the side and end zones are disjoint. CO-OP: up to
    /// TWO players may strip at once, but ONLY from OPPOSITE sides; on the server this CLAIMS the player's side
    /// (<see cref="PlayerSideSign"/>), and a second player at an ALREADY-manned side is refused (the side map's two keys
    /// are the hard cap of two). Routes to the master. Returns true to begin the timed hold (continued in
    /// <see cref="ContinueDebarking"/>, ended in <see cref="StopDebarking"/>); no gesture animation yet — E03 adds it.
    /// Evaluated on both sides (deterministic from synced state); side-claiming and progress are server-side.
    /// </summary>
    public bool TryStartDebarking(IPlayer byPlayer, ItemSlot? toolSlot, Vec3d hitPos)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) return master.TryStartDebarking(byPlayer, toolSlot, hitPos);

        if (!CanDebarkHere(byPlayer, toolSlot)) return false; // wrong position/tool/state → caller falls through to the rip

        if (Api.Side != EnumAppSide.Server) return true;      // client: the gate drives the animation; the server arbitrates sides

        // Weed the hold map BEFORE reading it, as ClaimOrVerifySide already does mid-hold — this START gate read it raw
        // (AuditFixPlan T03.04, F-L5-25). A debarker who disconnected mid-strip leaves a ghost entry that both reads
        // below believe: the direction lock sees the map as manned, and the ownership check blocks that side for
        // everyone. Mirrors TryStartSawing's prune.
        PruneOfflineDebarkers();

        // Directional debarking: the FIRST debarker on a FRESH strip picks which END the bark peels from, by WHERE THEY
        // CLICKED along the log (HitIsBarkReversedSide). LOCKED for the log (once any progress is on the clock OR any
        // side is already manned, a co-op joiner's / re-grip's click can't flip it). Set BEFORE claiming this player's
        // side below (so barkSides is still empty for the very first debarker). Only the VISUAL peel rides this; the
        // finished-log eject is always AWAY from the player. Mirrors TryStartSawing's direction lock.
        if (barkProgress <= 0f && barkStagesFired == 0 && barkSides.Count == 0)
        {
            bool rev = HitIsBarkReversedSide(hitPos);
            if (rev != barkReversed) { barkReversed = rev; MarkDirty(true); }
        }

        int sideSign = PlayerSideSign(byPlayer);
        string uid = byPlayer.PlayerUID;
        // This side already manned by ANOTHER debarker → refuse (one player per side; two sides = the hard cap of two).
        if (barkSides.TryGetValue(sideSign, out SawyerHold? held) && held.Uid != uid) return false;

        ReleaseDebarker(uid);                          // a player mans at most one side (handles re-grip / side switch)
        // Baseline ZERO, correct for the same reason as TryStartSawing's: the engine stamps the hold's origin
        // (Controls.UsingBeginMS) right after this OnBlockInteractStart returns, so t=0 IS this instant and the first
        // ContinueDebarking tick charges exactly the real seconds since the press. Un-clamped it charged more, because
        // the engine's replayed steps inflate the argument — the strip's share of F-L4-04, and by far the worst-hit
        // of the two: at DebarkSeconds = 1.5 a constant ~0.14 s of over-credit is a tenth of the whole strip. The clamp
        // sits at the seam, in BlockSawhorse.OnBlockInteractStep (T02.03).
        barkSides[sideSign] = new SawyerHold(uid, 0f);
        return true;
    }

    /// <summary>
    /// One tick of an active co-op strip hold, from <see cref="BlockSawhorse.OnBlockInteractStep"/> — the SIDE mirror
    /// of <see cref="ContinueSawing"/>. Re-checks the gate (<see cref="CanDebarkHere"/>) and, if it broke (tool away /
    /// stepped off the side / log gone), frees this player's side and returns false to end their hold. While valid it
    /// adds THIS player's per-tick elapsed DELTA to the master's <see cref="barkProgress"/> via
    /// <see cref="AdvanceDebark"/>, scaled by the tool tier's speed (<see cref="BarkSpeedFor"/>) — so two opposite-side
    /// debarkers each contribute and the strip runs at 2×, while a player whose side is held by ANOTHER contributes
    /// nothing. Routes to the master. Server-authoritative: the client re-gates and keeps the hold alive (returns true)
    /// but never mutates. Returns true to keep holding. A restock breather (<see cref="barkRestockPending"/>, checked
    /// FIRST) keeps the hold alive over the momentarily-empty pair between a finished strip and the next barked log —
    /// the SIDE mirror of <see cref="ContinueSawing"/>'s, since debarking now drops the bare log out and restocks the
    /// next barked one just like the rip.
    /// <para><paramref name="secondsUsed"/> must be the HONEST hold clock — same contract, same caller-side clamp and
    /// same reason as <see cref="ContinueSawing"/> (AuditFixPlan T02.03). It bites hardest here: against a
    /// <see cref="DebarkSeconds"/> of 1.5 the un-clamped over-credit was a tenth of the whole strip.</para>
    /// </summary>
    public bool ContinueDebarking(IPlayer byPlayer, ItemSlot? toolSlot, float secondsUsed)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) return master.ContinueDebarking(byPlayer, toolSlot, secondsUsed);

        string uid = byPlayer.PlayerUID;

        // RESTOCK BREATHER (AutoRestock + DebarkRestockDelaySeconds): the previous log was fully stripped, its bare
        // debarkedlog dropped out, and the pair is paused EMPTY (CompleteDebark set this) before the next barked log is
        // laid. Keep the held strip ALIVE while the debarker still holds a tool at a side, but advance NO progress (no
        // log yet); when the breather elapses the server lays the next barked log and the strip resumes. Checked BEFORE
        // CanDebarkHere, which would fail on the empty pair (HasBark is false). The SIDE mirror of ContinueSawing's
        // restock breather.
        if (barkRestockPending && !DebarkRestockPauseIsStale())
        {
            if (!CanDebarkHereIgnoringLog(byPlayer, toolSlot)) // bailed mid-pause (tool away / stepped off the side)
            {
                if (ReleaseDebarker(uid) && Api.Side == EnumAppSide.Server) { AbandonPendingDebarkRestockIfUnmanned(); MarkDirty(true); }
                return false;
            }
            if (Api.Side != EnumAppSide.Server) return true; // client just keeps the hold alive; the server owns the timing

            // OWNER-ONLY, the SIDE mirror of ContinueSawing's (AuditFixPlan T03.04, F-L5-04): ride a side the player
            // ALREADY mans rather than claiming one, so a passer-by whose plain RMB on the empty pair was swallowed by
            // the put/build path — and who therefore gets Step ticks too — can't slip into someone else's breather and
            // have a barked log pulled out of their own pack.
            SawyerHold? pausing = DebarkerFor(uid);
            if (pausing == null)                       // not one of the debarkers whose hold entered this pause
            {
                DbgRestockPause("strip", "refused-nonowner", byPlayer, Api.World.ElapsedMilliseconds - barkRestockTickedAtMs);
                return false;
            }
            pausing.LastSecondsUsed = secondsUsed;     // consume the elapsed as PAUSE, not stripping → resumed strip starts at ~0 delta
            barkRestockTickedAtMs = Api.World.ElapsedMilliseconds; // freshness stamp: this pause still has a live owner

            if (Api.World.ElapsedMilliseconds >= barkRestockReadyAtMs) FinishPendingDebarkRestock();
            return true;
        }

        // Abandoned pause (nobody ticked it for RestockOwnerTimeoutMs — the disconnect case, which owes us no
        // Stop/Cancel): drop it on the first tick that reaches it and fall through, so the ordinary gate below finds no
        // log and ends this hold cleanly. The SIDE mirror of ContinueSawing's (AuditFixPlan T03.04).
        if (barkRestockPending && Api.Side == EnumAppSide.Server)
        {
            DbgRestockPause("strip", "abandoned-stale", byPlayer, Api.World.ElapsedMilliseconds - barkRestockTickedAtMs);
            AbandonPendingDebarkRestock();
            PruneOfflineDebarkers(); // the same disconnect left a ghost side hold — drop it with the pause it pinned open
            MarkDirty(true);         // the client's synced copy of the flag clears with it
        }

        // Gate broke → this player stops debarking: free their side and persist what they accrued before bowing out
        // (Step only MarkDirty's at a stage crossing, so a partial strip would otherwise not be saved).
        if (!CanDebarkHere(byPlayer, toolSlot))
        {
            if (ReleaseDebarker(uid) && Api.Side == EnumAppSide.Server) MarkDirty(true);
            return false;
        }

        if (Api.Side != EnumAppSide.Server) return true; // client keeps the hold alive; the server owns the progress

        SawyerHold? held = ClaimOrVerifySide(uid, PlayerSideSign(byPlayer), secondsUsed);
        if (held == null)
        {
            // This side is manned by ANOTHER debarker — no same-side stacking. The strip's one refusal point
            // (AuditFixPlan T06.04), reached both by a press at an already-manned side and by a debarker who walked
            // round to the other side mid-hold; the packet drops that client's optimistic hold.
            RefuseStripHold(byPlayer);
            return false;
        }

        float delta = secondsUsed - held.LastSecondsUsed;
        held.LastSecondsUsed = secondsUsed;
        // Scale the advance by the tool tier's speed (spud ×1, axe+hammer ×0.3, knife ×0.15 — the config defaults). The gate above
        // (CanDebarkHere → BarkToolIsEffective) already guaranteed an effective tool; the BarkToolIsEffective re-check
        // is a defensive belt-and-suspenders. Each manned side advances by its OWN scaled elapsed → opposite sides sum.
        ItemStack? main = toolSlot?.Itemstack;
        ItemStack? off = byPlayer.Entity?.LeftHandItemSlot?.Itemstack;
        if (delta > 0f && BarkToolIsEffective(main, off)) AdvanceDebark(delta * BarkSpeedFor(main, off), byPlayer);
        return true;
    }

    /// <summary>End of ONE player's strip hold (release / look-away / slot switch), from
    /// <see cref="BlockSawhorse.OnBlockInteractStop"/> / <c>OnBlockInteractCancel</c> — the SIDE mirror of
    /// <see cref="StopSawing"/>. Finalises that player's last partial tick (charged while their side is still owned),
    /// frees their side, and persists the progress. Partial for real since T02.03 — the caller clamps the engine's
    /// replayed <c>secondsUsed</c> to the honest hold time, so the up-to-0.16 s of frames re-run on the way to Stop
    /// are no longer billed here. No-ops for a non-debarker — the engine also fires Stop after a
    /// rip / put / build / take, none of which mans a side. Routes to the master; server-authoritative. Idempotent.</summary>
    public void StopDebarking(IPlayer byPlayer, float secondsUsed)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.StopDebarking(byPlayer, secondsUsed); return; }
        if (Api.Side != EnumAppSide.Server) return; // the server owns progress; the client has nothing to finalise

        string uid = byPlayer.PlayerUID;
        SawyerHold? held = DebarkerFor(uid);
        if (held == null) return; // not a genuine strip hold (rip / put / build / take, or a refused same-side debarker)

        // Charge the final partial tick, scaled by the tool tier's speed — same factor as ContinueDebarking. Resolve
        // the hands from the player (Stop carries no toolSlot): main = active hotbar, off = left hand (T01.01 spike).
        ItemStack? main = byPlayer.InventoryManager.ActiveHotbarSlot?.Itemstack;
        ItemStack? off = byPlayer.Entity?.LeftHandItemSlot?.Itemstack;
        float delta = secondsUsed - held.LastSecondsUsed;
        if (delta > 0f && heldLog != null && BarkToolIsEffective(main, off)) AdvanceDebark(delta * BarkSpeedFor(main, off), byPlayer);
        ReleaseDebarker(uid);
        AbandonPendingDebarkRestockIfUnmanned(); // last debarker let go mid-breather → end the pause (no next log)
        // Persist + sync the final progress, DATA-ONLY — the side mirror of StopSawing's (T05.04 / F-L10-15): a release
        // lands mid-bucket, the bark visual is the one already drawn, a boundary-crossing release was synced with a
        // redraw by the AdvanceDebark above, and the hints ride RefreshInteractionHelpOnChange off this same resend.
        MarkDirty();
    }

    /// <summary>
    /// Advance the master's <see cref="barkProgress"/> by <paramref name="delta"/> seconds of stripping (clamped to
    /// <see cref="DebarkSeconds"/>, monotonic since delta ≥ 0) and fire each strip stage newly crossed — exactly once
    /// each, using the persisted <see cref="barkStagesFired"/> counter so a live <see cref="DebarkSeconds"/> change can
    /// neither retro-skip nor retro-fire a stage (the same fire-once discipline as <see cref="AdvanceSaw"/>). Each call
    /// adds ONE debarker's per-tick delta, so two opposite-side holds in the same tick sum to a 2× strip. Persists+syncs
    /// (<see cref="MarkDirty"/>) when a stage fires OR the bark VISUAL bucket changes
    /// (<see cref="BarkVisualFromProgress"/>, T04.02) — so the client re-meshes the bark coming off in step with
    /// progress — plus the two hint edges the visual grid would otherwise miss: the strip beginning and the recovery
    /// grace (<see cref="LogRecoverable"/>) flipping. Never every tick (the decimated bucket holds swaps ≥ 0.1s apart;
    /// no per-frame MarkDirty), exactly like <see cref="AdvanceSaw"/>. Server-side, master-only (callers ensure both).
    /// </summary>
    /// <param name="byPlayer">The debarker whose tick this is. Under <see cref="ImmersiveWoodworkingConfig.DropsAtPlayer"/>
    /// the bark and the debarked log of a strip this advance completes drop at their feet, so in a two-man strip the
    /// log goes to whoever's stroke finished it.</param>
    void AdvanceDebark(float delta, IPlayer byPlayer)
    {
        if (delta <= 0f) return;
        float barkSeconds = DebarkSeconds;
        int prevVisual = BarkVisualFromProgress(barkProgress); // bark-strip state shown before this advance (T04.02)
        bool wasStarted = barkProgress > 0f;                   // BarkStarted before this advance (drives the hint-flip sync below)
        bool wasRecoverable = LogRecoverable;                  // recovery grace before this advance (drives the take↔discard hint sync below)
        barkProgress = GameMath.Min(barkProgress + delta, barkSeconds); // delta ≥ 0 → monotonic, clamped to a full strip

        int now = BarkStagesPassed(barkProgress, barkSeconds);
        bool fired = false;
        while (barkStagesFired < now)
        {
            barkStagesFired++;
            fired = true;
        }
        // All strip stages done → the bark is fully off: finish — the bare log DROPS OUT and the pair restocks the next
        // barked log (CompleteDebark, the debark counterpart to ConsumeLog). With the single thin-slice stage this fires
        // exactly when barkProgress reaches DebarkSeconds; the fire-once counter then pins it there (no re-fire). The
        // heldLog != null guard mirrors AdvanceSaw's ConsumeLog call, so a future multi-stage subdivision (E04) can't
        // land CompleteDebark on an already-dropped log.
        if (fired && heldLog != null && barkStagesFired >= BarkStageFractions.Length) CompleteDebark(byPlayer);

        // Sync on a stage fire OR a bark-visual bucket change (T04.02), so the client swaps the bark-strip mesh in step
        // with progress — never every tick (DoD: no per-frame MarkDirty). After CompleteDebark barkProgress is zeroed
        // and the log swapped, so newVisual collapses to 1 and `fired` already forces the sync of the new debarked
        // billet. heldLog null (completed/taken) → no visual.
        // Also sync the instant a strip BEGINS (barkProgress 0→>0): BarkStarted flips the interaction hints (hides the
        // saw hint, turns Ctrl+RMB take→discard), and the help HUD doesn't poll — so without this the flip would wait
        // for the first bark-visual bucket boundary (~0.1s). vs-interaction-help-no-poll.
        //
        // recoverFlip (AuditFixPlan T03.05, F-L4-07) — the same argument at the grace boundary, and on the STRIP it was
        // not hypothetical: the bark syncs land on the 9-band visual grid (1/9 ≈ 11.1% of the strip), so the first
        // bucket at/after the default 25% grace is 33.3%. Between those two the client still held the pre-crossing
        // barkProgress and the hint still read "take", while the server had already committed the log to the scrap
        // branch — the note lying by a third of a strip. Syncing exactly where LogRecoverable flips detaches the hint
        // from the band grid, so it switches on the configured threshold whatever it is. (LogRecoverable folds the rip
        // AND the strip, so this fires on whichever half crosses first; the AdvanceSaw twin carries the same term.)
        //
        // Split by what each term actually moves, the side mirror of AdvanceSaw's (T05.04 / F-L10-15): `fired` and a
        // changed `newVisual` move the drawn bark and keep redrawOnClient; the two hint flips only need the client's
        // BarkStarted / LogRecoverable to be fresh, and RefreshInteractionHelpOnChange turns that resend into a
        // MarkBlockDirty on the looked-at cell by itself.
        int newVisual = heldLog != null ? BarkVisualFromProgress(barkProgress) : 1;
        bool startedFlip = !wasStarted && barkProgress > 0f;
        bool recoverFlip = wasRecoverable != LogRecoverable;
        if (fired || newVisual != prevVisual) MarkDirty(true);
        else if (startedFlip || recoverFlip) MarkDirty();
    }

    /// <summary>
    /// The bark has fully come off (all <see cref="BarkStageFractions"/> stages fired): the bare log DROPS OUT and the
    /// pair empties — the SIDE counterpart to the rip's <see cref="ConsumeLog"/>, which likewise drops its product and
    /// retires the billet. Resolves the held <c>log-placed-{wood}</c>'s debarked counterpart
    /// (<c>debarkedlog-{wood}</c>, same species/orientation/domain — domain-then-game via
    /// <see cref="ResolveProductBlock"/>, like the saw products) — the in-world echo of the vanilla axe+hammer grid
    /// recipe (which also drops NO bark) — wears each active debarker's tools (<see cref="DamageActiveDebarkTools"/>,
    /// read from <see cref="barkSides"/> BEFORE any reset clears it), then EJECTS the bare log beside the construction
    /// (<see cref="SpawnSawnItem"/>, the same drop physics as the boards) and AUTO-RESTOCKS the next BARKED log exactly
    /// like the rip — either after a <see cref="ImmersiveWoodworkingConfig.DebarkRestockDelaySeconds"/> breather (the
    /// pair sits empty while a strip is held) or instantly, or it just empties on the last log. The caller
    /// (<see cref="AdvanceDebark"/>) MarkDirty's, syncing the cleared/restocked state so the client re-meshes. The
    /// bark-item EJECT is the deferred E06 seam (just before the drop). Server-side, master-only.
    /// </summary>
    void CompleteDebark(IPlayer byPlayer)
    {
        // The debarked counterpart of the held log: same species + orientation, the log's own domain first then game
        // (ResolveProductBlock), mirroring how the rip resolves plank-/supportbeam-. The gate (CanDebarkHere → HasBark)
        // guarantees heldLog is a barked log-placed-* here, so the species/block are non-null; only a wood with no
        // debarkedlog variant fails to resolve.
        string? wood = GetWoodSpecies(heldLog);
        if (heldLog?.Block == null || wood == null) return; // not a barked log (the gate should prevent this)

        string domain = heldLog.Block.Code.Domain;
        string orient = heldLog.Block.LastCodePart(); // ud / ns / we — debarkedlog carries the same rotation variants
        Block? debarked = ResolveProductBlock(Api.World, domain, "debarkedlog-" + wood + "-" + orient);
        if (debarked == null)
        {
            // No debarkedlog for this species (a wood mod that ships logs but no debarked variant — NEVER vanilla,
            // which ships a debarkedlog for every log wood incl. aged). The START gate (DebarkProductResolves in
            // CanDebarkHere) normally refuses such a log before any strip begins; reaching here means the variant went
            // away mid-strip (e.g. a block hot-unloaded). Don't DEAD-END the hold: RESET the strip so the next
            // ContinueDebarking re-gates to false and ends it cleanly, leaving the log barked AND untouched
            // (barkProgress 0) so Ctrl+RMB takes it back WHOLE rather than scrapping it. Server-side; the caller
            // (AdvanceDebark) MarkDirty's the cleared state, stopping the chips/sound/animation on the next tick.
            Api.Logger.Warning("[immersivewoodworking] No debarkedlog-{0}-{1} to strip {2} into — leaving the log barked and recoverable.", wood, orient, heldLog.Block.Code);
            ResetDebarkState();
            return;
        }

        // The special-bark chance multiplier is read HERE, from hands that still HOLD their tools (AuditFixPlan T08.02,
        // F-L9-04). DamageActiveDebarkTools below can BREAK a tool on this very strip, and the engine empties the slot
        // it broke in; a multiplier sampled after that finds no tier at all and falls back to 1.0 — the SPUD reference,
        // i.e. the best odds in the game. That handed a premium for wrecking a tool: the last strip of a dying knife
        // rolled oak tanning bark at 100% instead of its 0.5 → 50%, and a tier-2 pair lost its 0.75 to the same 1.0 the
        // moment EITHER the axe or the offhand hammer went. Sampled once, before the wear pass, and carried into
        // DropRolledBark as a finished number, so wear can never feed back into the roll.
        double barkChanceMult = BestActiveDebarkerBarkChanceMult();

        DamageActiveDebarkTools(); // wear each active debarker's tools BEFORE any reset clears barkSides

        // Forestry XP per completed strip (xSkills, E02/T02.02), to every active debarker — read barkSides BEFORE any
        // reset clears it (same window as the wear pass), but kept SEPARATE from that pass so it still accrues on a
        // no-durability server. No-op without xSkills; co-op policy handled in GrantForestryXpToHolders.
        GrantForestryXpToHolders(barkSides.Values, Config.XSkillsDebarkXpPerStrip, "debark");

        // Eject direction for the finished products: AWAY from the debarker (the user's "готовое бревно всегда от
        // игрока"), computed from barkSides BEFORE any reset clears it. The bare log and the ACA bark both use it, so
        // both land on the far side from the player rather than the fixed cut side the planks use.
        (double awayX, double awayZ) = DebarkAwayEjectDir();

        // With the bark chain switched off the strip still makes the debarked log the beam rip needs, and nothing else.
        if (Config.EnableBarkChain)
            DropRolledBark(wood, awayX, awayZ, barkChanceMult, byPlayer); // E02 (T02.03): roll the species' bark kind and eject exactly one, away from the debarker, BEFORE the bare log drops — at the PRE-wear multiplier (T08.02)

        // The strip is DONE: the bare log DROPS OUT beside the construction (the debark counterpart to the rip dropping
        // its planks), then the pair auto-restocks the next BARKED log, exactly like ConsumeLog. Remember the finished
        // BARKED input first (for the same-species restock preference) — the dropped stack is a fresh debarkedlog, a
        // different code. Reusing the eject (SpawnEjectedItem) clears the multiblock collision box so the bare log falls
        // beside the sawhorses instead of resting on them — here toward the far side from the player.
        ItemStack finishedBarked = heldLog;
        SpawnEjectedItem(new ItemStack(debarked), 0.0, awayX, awayZ, byPlayer); // one centred pile, ejected away from the debarker

        // AutoRestock WITH a configured breather (DebarkRestockDelaySeconds): don't snap straight onto the next log.
        // Empty the pair NOW and lay the next BARKED log after a short pause (FinishPendingDebarkRestock, tick-driven
        // from ContinueDebarking), so a held strip reads strip → pause (empty) → place → strip — the saw's
        // ConsumeLog→SawRestockDelaySeconds branch, mirrored. Only enter the pause when a restock is actually available
        // (a peek of the active debarkers' packs), so the strip ends cleanly on the last log with no empty whiff. Keep
        // barkSides (the held strip stays the active debarker) and reset only the per-log progress; barkRestockPending
        // is synced so the client keeps the hold alive over the empty pair. The caller (AdvanceDebark) MarkDirty's,
        // syncing heldLog=null + barkRestockPending=true together.
        if (Config.AutoRestock && Config.DebarkRestockDelaySeconds > 0f && PeekDebarkRestockSlot(finishedBarked) != null)
        {
            heldLog = null;
            barkProgress = 0f;
            barkStagesFired = 0;
            // barkSides kept (the active debarker stays); sawProgress/sawStagesFired/sawEnds are already clear under the
            // per-log mode lock (a log being debarked is never also being sawn), so there is nothing of the rip to reset.
            barkRestockFinished = finishedBarked;
            barkRestockPending = true;
            barkRestockReadyAtMs = Api.World.ElapsedMilliseconds + (long)(Config.DebarkRestockDelaySeconds * 1000f);
            barkRestockTickedAtMs = Api.World.ElapsedMilliseconds; // the hold that stripped the log IS this pause's first tick (AuditFixPlan T03.04)
            return;
        }

        // No breather (delay 0, AutoRestock off, or no barked log left): instant restock exactly as the rip's instant
        // path — read barkSides (via PeekDebarkRestockSlot inside TryAutoRestockBark) HERE, before ResetDebarkState
        // zeroes them; the still-held right-click re-claims its side next tick (ContinueDebarking's "claim a free side
        // mid-hold"), so a held strip carries on after a ≤1-tick gap.
        heldLog = TryAutoRestockBark(finishedBarked);
        ResetDebarkState();
    }

    /// <summary>How far DOWN the log (along the pair axis) the bark pile is nudged from the bare-log pile — a FRACTION in
    /// <see cref="SpawnEjectedItem"/>'s −1..+1 units (scaled there by <c>SawDropAlongSpread</c> = 0.6), so 1.0 ≈ 0.6 block
    /// toward the +axis end. The bark and the bare log both eject AWAY from the debarker at the same across-offset, so
    /// without this nudge they spawn on the SAME spot (only the tiny <c>SawDropAlongJitter</c> separates them — the "one
    /// point" the two piles landed on). A fixed shift ALONG the log is orthogonal to both the away-eject and the
    /// debarker's side, so it never pushes the bark toward the player, and 0.6 stays well within the 2-cell pair.</summary>
    const double BarkDropAlongOffsetFrac = 1.0;

    /// <summary>Eject the species' bark count (<see cref="ImmersiveWoodworkingConfig.BarkPerLogFor"/> — its <c>BarkPerLogBySpecies</c> line, else <c>Config.BarkPerLog</c>, default 3) of the ROLLED kind for a fully-stripped log of <paramref name="wood"/> — the
    /// T02.03 replacement for the old ACA-only oak-bark drop (removed T02.04), called once per completed strip from
    /// <see cref="CompleteDebark"/> (before the bare log drops). Rolls the species' bark drop table
    /// (<see cref="ImmersiveWoodworkingModSystem.BarkDrops"/>) ONCE on the SERVER — <see cref="CompleteDebark"/> is
    /// server-side, master-only — into a special KIND or the generic remainder, biased by
    /// <paramref name="chanceMult"/>: the BEST active debarker's tool tier (T02.05,
    /// <see cref="BestActiveDebarkerBarkChanceMult"/>), so a bark spud rolls the special kinds at full odds and an
    /// axe+hammer/knife less often. That number arrives ALREADY SAMPLED from the caller, taken BEFORE the wear pass
    /// (AuditFixPlan T08.02): re-reading it here would look at hands the wear had already emptied.
    /// <see cref="BarkDropTable.ItemCodeForKind"/> maps the rolled kind to one of our bark
    /// items (fresh = the green state where one exists). Resolved in the mod's OWN domain (all bark items register there)
    /// and ejected AWAY from the debarker (<paramref name="ejectX"/>, <paramref name="ejectZ"/>) via the same
    /// <see cref="SpawnEjectedItem"/> as the bare log — but nudged <see cref="BarkDropAlongOffsetFrac"/> DOWN the log so
    /// the two piles don't land on one spot — so it lands beside the sawhorses rather than on the multiblock collision
    /// box. Unknown / generic-only / foreign woods roll to <c>bark-generic</c>, so EVERY log yields bark — the species' count of it, all one rolled kind.
    /// Server-side, master-only (its caller is).</summary>
    void DropRolledBark(string wood, double ejectX, double ejectZ, double chanceMult, IPlayer byPlayer)
    {
        ImmersiveWoodworkingModSystem mod = modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        string? kind = mod.BarkDrops.Roll(wood, Api.World.Rand, chanceMult);  // one server-side roll; null = the generic remainder
        string code = BarkDropTable.ItemCodeForKind(kind);                    // kind → bark item path (fresh = green where a state exists)
        int count = Config.BarkPerLogFor(wood);                               // per-species config line, else the global BarkPerLog

        Item? bark = Api.World.GetItem(new AssetLocation("immersivewoodworking", code));
        if (bark == null)
        {
            // Our own bark items always register, so this is unreachable in a normal install — guard so a broken asset
            // set drops nothing rather than throwing (the bare log still ejects afterwards).
            Api.Logger.Warning("[immersivewoodworking] Bark item immersivewoodworking:{0} not found — no bark dropped for the {1} strip.", code, wood);
            return;
        }
        // Nudged DOWN the log (BarkDropAlongOffsetFrac) so the bark pile sits BESIDE the bare-log pile instead of on it —
        // the bare log stays centred (alongFrac 0.0 in CompleteDebark), both still eject away from the debarker.
        // ONE stack of `count` (per-species config line, else BarkPerLog, ≥ 1): the KIND was rolled once above, so every bark
        // off this log is that same kind — the count multiplies yield without touching the barkdrops.json kind odds.
        SpawnEjectedItem(new ItemStack(bark, count), BarkDropAlongOffsetFrac, ejectX, ejectZ, byPlayer);
    }

    /// <summary>The auto-restock breather (<see cref="barkRestockPending"/>) has elapsed: lay the next BARKED log now —
    /// pulled from an active debarker's pack (same source as the instant path), preferring the same species as the log
    /// just stripped — and end the pause so the held strip resumes on a fresh barked log. Leaves the pair empty if no
    /// debarker still carries a matching barked log (the strip then ends cleanly next tick, exactly as the instant path
    /// does on the last log). barkProgress/barkStagesFired were already zeroed in <see cref="CompleteDebark"/>;
    /// barkSides was kept, so the still-held strip stays the active debarker. Server-side, master-only; MarkDirty syncs
    /// the laid log (or the now-cleared pending state). The SIDE mirror of <see cref="FinishPendingRestock"/>.</summary>
    void FinishPendingDebarkRestock()
    {
        barkRestockPending = false;
        ItemStack? finished = barkRestockFinished;
        barkRestockFinished = null;
        heldLog = TryAutoRestockBark(finished);
        MarkDirty(true);
    }

    /// <summary>
    /// AutoRestock for debarking, server-side, master-only: the next BARKED log to lay after one is fully stripped,
    /// pulled from an active debarker's own pack — or null when <see cref="ImmersiveWoodworkingConfig.AutoRestock"/> is
    /// off, no debarker is resolvable, or none carries a barked log. Prefers the SAME species as
    /// <paramref name="finishedBarked"/> so a continuing strip doesn't switch wood while a match remains, falling back
    /// to any barked log. Must run BEFORE <see cref="ResetDebarkState"/> (it reads <see cref="barkSides"/>, still
    /// populated). Takes one log out of the first debarker that has one, plays the place sound, and returns it. The
    /// active slot (the debark tool) is never taken from — <see cref="AutoRestockUtil.FindRestockSlot"/> skips it. The
    /// SIDE mirror of <see cref="TryAutoRestockLog"/>.
    /// </summary>
    ItemStack? TryAutoRestockBark(ItemStack? finishedBarked)
    {
        if (Api.Side != EnumAppSide.Server || !Config.AutoRestock || finishedBarked == null) return null;

        ItemSlot? src = PeekDebarkRestockSlot(finishedBarked);
        if (src?.Itemstack == null) return null; // off, no debarker resolvable, or none carries a barked log

        ItemStack pulled = src.TakeOut(1);
        src.MarkDirty();
        Api.World.PlaySoundAt(BuildSound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
        return pulled;
    }

    /// <summary>The first active debarker's inventory slot holding a BARKED log to restock after
    /// <paramref name="finishedBarked"/>, or null if none — a no-take PEEK (the caller owns the take). STRICT: only a
    /// still-barked <c>log-placed-*</c> (<see cref="SawInput.Log"/>), NEVER an already-debarked log (a debarked log
    /// can't be stripped again), preferring the SAME species. Reads <see cref="barkSides"/> (the active debarkers' own
    /// packs; the active tool slot is skipped), so call BEFORE <see cref="ResetDebarkState"/>. Used both to TAKE the
    /// next log (<see cref="TryAutoRestockBark"/>) and to decide whether a restock breather is worth entering
    /// (<see cref="CompleteDebark"/>). The SIDE mirror of <see cref="PeekRestockSlot"/>, narrowed to barked logs only.
    /// Server-side, master-only.</summary>
    ItemSlot? PeekDebarkRestockSlot(ItemStack finishedBarked)
    {
        string? finishedWood = GetWoodSpecies(finishedBarked);

        // The accept predicate repeats the START gate's dead-end clause (DebarkProductResolves, T03.03 / F-L9-27):
        // without it the breather laid a log the very next CanDebarkHere refused to strip — the pair stalled holding
        // someone's log with no message at all. Auto-feed must never admit what the manual path would turn away.
        System.Func<ItemStack, bool> accept = s => GetSawInput(s) == SawInput.Log && DebarkProductResolves(Api.World, s);
        System.Func<ItemStack, bool> preferred = s => accept(s) && GetWoodSpecies(s) == finishedWood;

        foreach (SawyerHold held in barkSides.Values)
        {
            IPlayer? player = Api.World.PlayerByUid(held.Uid);
            if (player == null) continue;

            ItemSlot? src = AutoRestockUtil.FindRestockSlot(player, accept, preferred);
            if (src?.Itemstack != null) return src;
        }
        return null;
    }

    /// <summary>End an in-progress debark auto-restock breather if no debarker is still holding — the held strip that
    /// would have laid the next log is gone, so no further tick would run to lay it or clear the flag. Covers the
    /// "everyone let go DURING the pause" case (the put/take/discard paths are covered by <see cref="ResetDebarkState"/>).
    /// The SIDE mirror of <see cref="AbandonPendingRestockIfUnmanned"/>. Server-side. The dirty exit — a hold ended by
    /// disconnect, leaving a ghost side entry so <see cref="barkSides"/> never reaches 0 — is caught by the staleness
    /// timeout instead (<see cref="DebarkRestockPauseIsStale"/>).</summary>
    void AbandonPendingDebarkRestockIfUnmanned()
    {
        PruneOfflineDebarkers(); // a debarker who disconnected mid-breather leaves a ghost hold — drop it so Count can reach 0
        if (barkRestockPending && barkSides.Count == 0) AbandonPendingDebarkRestock();
    }

    /// <summary>Clear the debark auto-restock breather outright — the flag plus its finished-log memo, always together.
    /// The SIDE mirror of <see cref="AbandonPendingRestock"/>: no <c>MarkDirty</c>, no touching
    /// <see cref="barkSides"/>. Server-side.</summary>
    void AbandonPendingDebarkRestock()
    {
        barkRestockPending = false;
        barkRestockFinished = null;
    }

    /// <summary>Whether the pending debark breather has gone ABANDONED — no owner has ticked it for
    /// <see cref="RestockOwnerTimeoutMs"/> (AuditFixPlan T03.04, F-L5-04). The SIDE mirror of
    /// <see cref="RestockPauseIsStale"/>, including its client clause: false on the client, whose synced flag is what
    /// keeps a held strip alive across the empty pair.</summary>
    bool DebarkRestockPauseIsStale()
        => Api.Side == EnumAppSide.Server && Api.World.ElapsedMilliseconds - barkRestockTickedAtMs > RestockOwnerTimeoutMs;

    /// <summary>
    /// Wear each active debarker's tools for one completed strip — the debark counterpart to
    /// <see cref="DamageActiveSaws"/>. Charges <see cref="ImmersiveWoodworkingConfig.DebarkDurabilityPerLog"/> to every
    /// player currently manning a side (so a two-side co-op strip wears BOTH players' tools), resolving the hands the
    /// same way the gate does: main = active hotbar, off = left hand (the T01.01 spike). Wears exactly the tools that
    /// make up the player's tier — the tier-2 axe+hammer wears BOTH the main-hand axe AND the offhand hammer; the
    /// single-tool tiers (the tier-1 knife and the tier-3 bark spud) wear only the main hand. The
    /// <see cref="BarkToolIsEffective"/> re-check skips a debarker who switched to a non-tool mid-hold (mirrors the
    /// saw's guard) and an offline player; the engine removes a tool whose durability hits zero. No-ops on the 0
    /// config. Reads <see cref="barkSides"/>, so call it BEFORE <see cref="ResetDebarkState"/>. Server-side, master-only.
    /// </summary>
    void DamageActiveDebarkTools()
    {
        int cost = Config.DebarkDurabilityPerLog;
        if (cost <= 0) return; // 0 = no durability cost (negatives are treated as 0 here)

        foreach (SawyerHold held in barkSides.Values)
        {
            IPlayer? player = Api.World.PlayerByUid(held.Uid);
            if (player == null) continue; // offline

            ItemSlot? mainSlot = player.InventoryManager.ActiveHotbarSlot;
            ItemSlot? offSlot = player.Entity?.LeftHandItemSlot;
            ItemStack? main = mainSlot?.Itemstack;
            ItemStack? off = offSlot?.Itemstack;
            if (!BarkToolIsEffective(main, off)) continue; // switched away from a valid tier mid-hold → no wear

            // carefulsawyer (xSkills, T03.04): ONE careful roll per completed strip, applied to EVERY tool the tier wears
            // (so a tier-2 axe+hammer spares BOTH when the roll saves, at PARITY with the pit saw's single tool). Per-player
            // (each debarker's own forestry tier); no-op without xSkills; a full save drops the cost to 0 and nothing wears.
            int effectiveCost = ApplyCarefulSawyer(player, cost);
            if (effectiveCost <= 0) continue;

            DamageDebarkTool(mainSlot, player, effectiveCost);                                  // the main-hand tool always wears
            if (IsAxe(main) && IsHammer(off)) DamageDebarkTool(offSlot, player, effectiveCost); // tier 2: the offhand hammer too
        }
    }

    /// <summary>Charge <paramref name="cost"/> durability to <paramref name="slot"/>'s tool and flag it dirty — the
    /// per-slot half of <see cref="DamageActiveDebarkTools"/>, mirroring <see cref="DamageActiveSaws"/>'s body. No-op
    /// on an empty slot. The engine removes a tool whose durability reaches zero.</summary>
    void DamageDebarkTool(ItemSlot? slot, IPlayer player, int cost)
    {
        if (slot?.Itemstack == null) return;
        slot.Itemstack.Collectible.DamageItem(Api.World, player.Entity, slot, cost);
        slot.MarkDirty();
    }

    /// <summary>Clear the per-log strip state (progress, the fired-stage counter, any in-progress co-op holds) — the
    /// debark counterpart to the bark half of <see cref="ResetSawState"/>, which calls this so a freshly placed / taken
    /// / scrapped / restocked log always starts with full bark and zero progress. barkSides is transient (not
    /// serialized); the scalars serialize out on the next MarkDirty. Server-side callers.</summary>
    void ResetDebarkState()
    {
        barkProgress = 0f;
        barkStagesFired = 0;
        barkSides.Clear(); // any active co-op strip holds end with the log
        // A fresh / placed / taken / scrapped / restocked log also ends any pending debark auto-restock breather, so a
        // stale pause can never hijack the next strip — the debark mirror of how ResetSawState clears restockPending.
        // (The breather path in CompleteDebark deliberately does NOT call ResetDebarkState — it sets barkRestockPending
        // AFTER its own partial reset.)
        AbandonPendingDebarkRestock();
    }

    // ──────────────────── Debark FX — bark chips (T03.04) ────────────────────
    //
    // Cosmetic bark chips thrown off the log's long side while a strip hold runs — the SIDE counterpart to the rip's
    // SpawnSawDust (which throws dust off the END cut). Client-side + purely cosmetic, coloured from the held log's
    // own bark texture so they match the species. TWO sound layers accompany the strip, neither HERE in the chip code:
    // (1) the per-tier HIT sounds are frame-locked to each gesture via the metadata's `animationSounds`
    // (patches/debarking_animations.json — debarking-axehit*/-hammerhit* on the axe+hammer contact frames, -axehit* on
    // the spud's, -knifescrape* on the knife's, like the saw's sawing_ff*/_fb*) with no code timer
    // (vs-held-use-anim-trigger); (2) the generic continuous "bark coming off" loop (debarking1/2/3) is the code-driven
    // rotation in UpdateDebarkSound / StartDebarkClip below — the separate trigger the ps1's New-DebarkSounds block
    // reserves those clips for. See tools/apply-debarking-animation.ps1.

    // Bark-chip RATE and LIFE, both authored in REAL units (AuditFixPlan T06.02 / F-L10-07), the strip twin of the
    // rip's SawDustPerEmitterPerSecond / SawDustSeconds. RATE is per emitter per SECOND across THREE faces, so a
    // strip throws 108 chips a second whatever the frame rate; as with the rip it is the TOP of the range the old
    // per-step template produced (0.75 chips × 3 faces × ~48 steps/s on a 144 fps client), so nobody's picture gets
    // thinner. LIFE was five times what this template declared — the engine multiplies LifeLength by 5 at spawn
    // (see ParticleLife), making the old "1.0" five seconds on screen — and is cut to a third by author decision
    // 2026-08-28. Chips stay chunkier and longer-lived than the saw's fine flakes: bark peels off in flakes, not
    // powder.
    const float BarkChipPerEmitterPerSecond = 36f;
    const float BarkChipSeconds = 1.7f;

    // Bark-chip template, reused per spawn (Color / MinPos / AddPos / velocity / quantity set each time, like
    // SawDust). Falls under gravity.
    static readonly SimpleParticleProperties BarkChips = new SimpleParticleProperties(
        0, 0,                               // quantity is set per spawn from the rate accumulator — ctor args unused
        ColorUtil.ToRgba(255, 120, 90, 60), // overwritten per spawn from the log's bark texture (species colour)
        new Vec3d(), new Vec3d(),
        new Vec3f(), new Vec3f(),           // velocity set per spawn, per face (the left/right sides pop outward, the top pops up)
        ParticleLife.Seconds(BarkChipSeconds), // life (real seconds — ParticleLife undoes the engine's ×5)
        1f,                                  // gravity — chips drop off the log
        0.15f, 0.4f,                         // min / max size — chunkier than sawdust
        EnumParticleModel.Quad)
    {
        WithTerrainCollision = true,
        WindAffected = true,
        WindAffectednes = 0.3f,
    };

    // The OBSERVER's copy of the same chips (AuditFixPlan T06.01, F-L4-13) — the debark twin of SawDustNet, and
    // separate from the template above for the same three reasons spelled out there: ColorByBlock (a BlockId the
    // RECEIVING client resolves against its own atlas) would blank the local Color if they shared one object; in
    // singleplayer the server tick writes this one while the client tick writes that one; and the broadcast fires
    // roughly once per hold packet rather than once per frame, so each burst carries more chips.
    // Same arithmetic as SawDustNet: the local template now emits a FIXED BarkChipPerEmitterPerSecond (36) per
    // emitter per second (T06.02) against roughly 7 bursts a second here, so parity is ≈5.1 per burst and 4+5
    // averages 4.5, just under it. LIFE is the local copy's constant, so the two read identically.
    static readonly SimpleParticleProperties BarkChipsNet = new SimpleParticleProperties(
        4f, 5f,                             // per emitter; ×3 faces per burst, ~1 burst per hold packet
        ColorUtil.ToRgba(255, 120, 90, 60), // replaced per spawn by ColorByBlock, resolved on the receiving client
        new Vec3d(), new Vec3d(),
        new Vec3f(), new Vec3f(),           // velocity set per spawn, per face (the left/right sides pop outward, the top pops up)
        ParticleLife.Seconds(BarkChipSeconds), // life (real seconds — ParticleLife undoes the engine's ×5)
        1f,                                  // gravity — chips drop off the log
        0.15f, 0.4f,                         // min / max size — chunkier than sawdust
        EnumParticleModel.Quad);

    // Wall-clock stamps of the last observer broadcast of each strip effect. Station-wide, NOT per debarker (unlike
    // the rip's SawyerHold.LastFxMs): the peel line and the loop position are both global to the log, so two
    // opposite-side debarkers should drive ONE stream, not two stacked ones. Server-side, transient.
    long lastBarkChipsNetMs;
    long lastDebarkClipNetMs;

    // Bark-chip emission geometry (axis frame). Bark peels off the log along its LENGTH: the dedicated debark model
    // hides its nine bark bands (part_1..part_9) in order from the +axis end toward the −axis end as the strip runs
    // (BlockEntitySawhorse.Render.cs / DebarkHideSequence), so the EXPOSED edge of the remaining bark TRAVELS down the
    // log. We throw chips from that moving edge, off the log's THREE visible faces at once — the two long SIDES (the
    // diamond's left/right corners) and the TOP (its crown) — each kept on the real cross-section so the chips read as
    // bark flaking off the billet. Independent of which side the debarker stands at (the peel line is global to the
    // log), unlike the former single player-side emitter. See vs-debark-bark-geometry.

    // The bark edge's along-offset from the pair centre runs +axis end (part_1, still full bark) → −axis end (bare) as
    // the strip fraction goes 0→1. The nine bands span local Z ≈ ±14.2px ⇒ ±0.89 block (14.25/16). local +Z maps to
    // +axis for BOTH orientations (+Z for an N–S pair; +X for an E–W pair under PlaceLogMesh's +90° turn — Mat4f.RotateXYZ
    // sends local +Z to world +X), so +along is the still-barked end either way. If the peel ever runs the wrong way
    // DOWN the log, negate this.
    const double BarkEdgeAlong   = 0.89;

    // Cross-section: the laid log is a 45°-rolled square — a diamond standing on a corner. Corners computed from the
    // model (square corners rotated −45° about rotationOrigin): BOTTOM Y ≈ 0.58, SIDE corners at perp ≈ ±0.43 / Y ≈ 1.02,
    // TOP corner (the crown) at perp 0 / Y ≈ 1.46. The TOP emitter MUST sit ABOVE 1.46 — at perp 0 the diamond is solid
    // wood all the way from 0.58 to 1.46, so a lower top emitter spawns its chips INSIDE the log and they never show.
    const double BarkSideOffset  = 0.38; // perp: out at the side bark flank (corner ≈ ±0.43) — the left/right emitters
    const double BarkSideMinY    = 0.75; // up: low edge of the side emission band …
    const double BarkSideAddY    = 0.35; // … band height → side chips come off ≈ 0.75..1.10, the lower/front bark flank
    const double BarkTopMinY     = 1.45; // up: the top emitter sits just above the crown (top corner ≈ 1.46) …
    const double BarkTopAddY     = 0.08; // … ≈ 1.45..1.53, so the chips clear the wood instead of spawning inside it
    const double BarkTopPerpHalf = 0.16; // perp half-spread of the top emitter (chips fan off the crown sideways)
    const double BarkAlongJitter = 0.15; // along: small random spread so a held strip doesn't stack chips on one cross-section
    // Launch (blocks/tick). The two SIDE emitters pop OUTWARD off their corner (±perp) at [Out,Out] and fall ([Down,Down]);
    // the TOP emitter pops UP ([Up,Up]) with a small sideways fan. A little ±scatter ALONG the log spreads each burst over
    // the peel line.
    const double BarkChipOutMin  =  0.20;
    const double BarkChipOutMax  =  0.50;
    const double BarkChipScatter =  0.15;
    const double BarkChipDownMin = -0.6;
    const double BarkChipDownMax = -0.2;
    const double BarkChipUpMin   =  0.10;
    const double BarkChipUpMax   =  0.35;

    /// <summary>
    /// Throw cosmetic bark chips off the log while stripping (T03.04), coloured from the held log's own bark texture
    /// (<see cref="Block.GetRandomColor"/>, as <see cref="SpawnSawDust"/> does for the species colour). This is the
    /// DEBARKER'S OWN copy, drawn locally; everyone else gets <see cref="BroadcastDebarkFx"/> from the
    /// server. Purely cosmetic: <see cref="BlockSawhorse.OnBlockInteractStep"/> calls this only while a GENUINE strip
    /// is in progress (<see cref="ContinueDebarking"/> returned true), so it inherits the debark gates (no log / not at
    /// a side / wrong tool → no chips) for free, exactly like the rip's sawdust. Routes to the master — only it holds
    /// the log, the pair geometry and the synced <see cref="barkProgress"/>; a slave proxies.
    ///
    /// THREE emitters per call, all on the log's visible faces at the MOVING strip edge: the two long SIDES (the
    /// diamond's left/right corners) popping outward + down, and the TOP (its crown) popping up + a sideways fan. The
    /// edge's along-position is read from <see cref="barkProgress"/> (the peel travels +axis end → −axis end as the
    /// strip runs, <see cref="BarkEdgeAlong"/>), so the chips stay on the OUTER edge of the bark still coming off and
    /// march down the log with it — matching the bands the model hides (<see cref="DebarkHideSequence"/>). Independent
    /// of which side the debarker stands at (the peel line is global), so two opposite-side co-op debarkers see the
    /// same three streams; <paramref name="byPlayer"/> is not needed for placement (kept for the call-site shape, like
    /// <see cref="UpdateDebarkSound"/>). No-op on a slave whose master can't be resolved (heldLog null).
    ///
    /// <para>Emitted at a RATE, not per call (AuditFixPlan T06.02 / F-L10-07), exactly as the rip's
    /// <see cref="SpawnSawDust"/>: <see cref="FxRateBudget"/> turns this frame's slice of the hold clock into a
    /// whole-chip count at <see cref="BarkChipPerEmitterPerSecond"/>, so the strip throws the same chips per second
    /// at 30 fps as at 144. The budget runs BEFORE the log and progress gates so the frames they suppress still
    /// advance the clock instead of banking a clump for the first frame that passes.</para>
    /// </summary>
    public void SpawnBarkChips(IPlayer byPlayer, float secondsUsed)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.SpawnBarkChips(byPlayer, secondsUsed); return; }
        if (capi == null) return; // client only

        int perEmitter = FxRateBudget(ref barkChipAccum, ref barkChipLastSeconds, secondsUsed, BarkChipPerEmitterPerSecond);
        if (perEmitter <= 0) return;        // nothing due this frame
        if (heldLog?.Block == null) return; // the pair holds no log
        if (barkProgress <= 0f) return;     // see EmitBarkChips for why nothing may fire at 0

        int color = heldLog.Block.GetRandomColor(capi, Pos, BlockFacing.UP);
        BarkChips.Color = color | (255 << 24); // GetRandomColor returns no alpha byte — force opaque (mirrors SawDust)
        BarkChips.MinQuantity = perEmitter;    // this frame's exact count per face …
        BarkChips.AddQuantity = 0f;            // … and no second roll on top — the accumulator owns the randomness
        EmitBarkChips(BarkChips, null);        // local: the client overload ignores dualCallByPlayer, so it stays null
    }

    // This client's own bark-chip bank — the strip twin of sawDustAccum / sawDustLastSeconds, and master-owned for
    // the same reason (AuditFixPlan T06.02). Client-side and transient.
    float barkChipAccum;
    float barkChipLastSeconds;

    /// <summary>
    /// The strip's observer half (AuditFixPlan T06.01, F-L4-13): the same three chip emitters AND the generic strip
    /// loop, sent from the SERVER to every nearby player EXCEPT <paramref name="byPlayer"/>, whose own client already
    /// runs both locally. Before this, a bystander watched a debarker scrape a log with no chips and no continuous
    /// sound — only the frame-locked per-tier hit clips from the animation metadata carried, because those ride the
    /// replicated animation rather than client-local code.
    ///
    /// <para>The two halves keep SEPARATE stamps, both station-wide: the chips ride the particle spacing
    /// (<see cref="FxBroadcastDue"/>'s default), the loop rides <see cref="DebarkClipNetIntervalMs"/>, which is the
    /// length of a clip rather than a frame budget. Station-wide and not per debarker because the peel line and the
    /// loop position are both global to the log — two opposite-side debarkers must drive ONE stream, not two stacked
    /// ones. The known edge of that choice: with two debarkers <c>dualCallByPlayer</c> can exclude only the one whose
    /// tick sent it, so the OTHER occasionally hears a broadcast clip over their own loop. It is a work noise and the
    /// case needs both sides of one log manned, so it is left rather than paid for with an addressed per-player send.</para>
    /// </summary>
    public void BroadcastDebarkFx(IPlayer byPlayer)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.BroadcastDebarkFx(byPlayer); return; }
        if (Api.Side != EnumAppSide.Server || heldLog?.Block == null) return; // server only; the restock breather holds no log

        if (barkProgress > 0f && FxBroadcastDue(ref lastBarkChipsNetMs))
        {
            BarkChipsNet.ColorByBlock = heldLog.Block; // travels as a BlockId; the RECEIVER resolves it against its own atlas
            EmitBarkChips(BarkChipsNet, byPlayer);
        }

        // The generic loop, as a rotation of the SAME one-shots the local handle rotates through. An ILoadedSound is a
        // client-owned handle and cannot be broadcast at all, so the observer's copy is re-triggered on a timer instead:
        // one clip every DebarkClipNetIntervalMs, pitch unrandomised so it matches the local loop's plain playback. The
        // worker keeps their own handle, and with it the fade-out on release that a fire-and-forget clip cannot have.
        if (FxBroadcastDue(ref lastDebarkClipNetMs, DebarkClipNetIntervalMs))
        {
            AssetLocation loc = DebarkLoopClips[Api.World.Rand.Next(DebarkLoopClips.Length)];
            Vec3f at = DebarkSoundPos();
            Api.World.PlaySoundAt(loc, at.X, at.Y, at.Z, byPlayer, false, DebarkSoundRange, DebarkSoundVolume);
        }
    }

    /// <summary>Place and fire this call's three chip emitters into <paramref name="props"/> — the shared body of the
    /// local spawn and the observer broadcast, so the two can never drift in geometry. Caller owns the template and its
    /// colour; <paramref name="dualCallByPlayer"/> is null for a local spawn and the worker for a broadcast. Master-only
    /// (both callers routed already).</summary>
    void EmitBarkChips(SimpleParticleProperties props, IPlayer? dualCallByPlayer)
    {
        // Nothing fires until a strip has actually advanced (barkProgress > 0; both callers gate on it). Beyond being
        // physically right (nothing has peeled yet at 0), this closes a cosmetic multiplayer glitch: barkReversed is
        // server-authoritative and only syncs with the FIRST progress tick, but the client runs SpawnBarkChips from
        // frame 1 of the hold (client-side ContinueDebarking returns true without advancing progress). Emitting at
        // barkProgress 0 would use the STALE client barkReversed and, at frac 0, spray all chips off the +axis tip
        // regardless of the clicked end for ~1 RTT. Gating means the first chips only appear once the synced progress
        // (which carries barkReversed) has landed, so they always come off the correct end.

        // Pair centre (world, horizontal), exactly as PlayerAxisOffset reads it.
        double cx = Pos.X + 0.5 + (pairAxis == EnumAxis.X ? 0.5 : 0.0);
        double cz = Pos.Z + 0.5 + (pairAxis == EnumAxis.Z ? 0.5 : 0.0);

        // Axis-frame → world units in PlayerAxisOffset's OWN frame (NOT AxisToWorld's mirrored perp). X-pair: along=+X,
        // perp=+Z; Z-pair: along=+Z, perp=+X. local +Z (the model's part_1 end) maps to +along for both (see the const
        // block), so the still-barked end is +along either way.
        double alongX = pairAxis == EnumAxis.X ? 1.0 : 0.0;
        double alongZ = pairAxis == EnumAxis.X ? 0.0 : 1.0;
        double perpX  = pairAxis == EnumAxis.X ? 0.0 : 1.0;
        double perpZ  = pairAxis == EnumAxis.X ? 1.0 : 0.0;

        // The exposed bark edge travels +axis end (full bark) → −axis end (bare) as the strip fraction runs 0→1; chips
        // come off THERE. Read from the master's synced barkProgress (this == master here), with a little along jitter so
        // a held strip doesn't stack chips on one cross-section. Api.World.Rand, not capi's: this body runs on both sides.
        double frac = DebarkSeconds > 0f ? GameMath.Clamp(barkProgress / DebarkSeconds, 0f, 1f) : 0f;
        // Directional debarking: the exposed edge normally travels +axis end (full bark) → −axis end (bare); a reversed
        // peel (barkReversed, chosen from the click) runs the other way, so negate the edge term in lockstep with the
        // reversed band order (DebarkHideSequenceReversed). The chip edge and the model bands then always come off the
        // SAME end (jitter unchanged).
        double edgeSign = barkReversed ? -1.0 : 1.0;
        double alongEdge = GameMath.Clamp(
            BarkEdgeAlong * (1.0 - 2.0 * frac) * edgeSign + (Api.World.Rand.NextDouble() * 2 - 1) * BarkAlongJitter,
            -BarkEdgeAlong, BarkEdgeAlong); // keep the jitter at the end caps from spilling chips past the bark tips
        double ex = cx + alongX * alongEdge; // edge cross-section centre (world), before each face's perp / height offset
        double ez = cz + alongZ * alongEdge;

        // Three faces of the diamond at the moving edge: left side, right side, top.
        SpawnBarkSide(props, dualCallByPlayer, ex, ez, perpX, perpZ, alongX, alongZ, -1); // left  (−perp corner)
        SpawnBarkSide(props, dualCallByPlayer, ex, ez, perpX, perpZ, alongX, alongZ, +1); // right (+perp corner)
        SpawnBarkTop (props, dualCallByPlayer, ex, ez, perpX, perpZ, alongX, alongZ);     // top   (crown)
    }

    /// <summary>Throw one bark-chip burst off a long SIDE of the log at the moving strip edge — at the diamond's side
    /// corner (<paramref name="sideSign"/> = −1 left / +1 right of the log centre-line), over the visible bark flank,
    /// popping OUTWARD off that side and falling. <paramref name="ex"/>/<paramref name="ez"/> is the edge cross-section
    /// centre; perp/along are the axis-frame world units. The two velocity corners keep AddVelocity non-negative on
    /// either side (outward-slow −scatter vs outward-fast +scatter, both downward), mirroring the old single emitter.
    /// The caller owns <paramref name="props"/> (which template, which colour) and
    /// <paramref name="dualCallByPlayer"/> (null local, the worker for a broadcast).</summary>
    void SpawnBarkSide(SimpleParticleProperties props, IPlayer? dualCallByPlayer, double ex, double ez, double perpX, double perpZ, double alongX, double alongZ, int sideSign)
    {
        double sx = ex + perpX * sideSign * BarkSideOffset;
        double sz = ez + perpZ * sideSign * BarkSideOffset;
        props.MinPos.Set(sx - 0.05, Pos.Y + BarkSideMinY, sz - 0.05);
        props.AddPos.Set(0.10, BarkSideAddY, 0.10);

        double oux = perpX * sideSign, ouz = perpZ * sideSign; // outward unit (away from the log on this side)
        double aX = oux * BarkChipOutMin + alongX * -BarkChipScatter, bX = oux * BarkChipOutMax + alongX * BarkChipScatter;
        double aZ = ouz * BarkChipOutMin + alongZ * -BarkChipScatter, bZ = ouz * BarkChipOutMax + alongZ * BarkChipScatter;
        props.MinVelocity.Set((float)System.Math.Min(aX, bX), (float)BarkChipDownMin, (float)System.Math.Min(aZ, bZ));
        props.AddVelocity.Set((float)System.Math.Abs(bX - aX), (float)(BarkChipDownMax - BarkChipDownMin), (float)System.Math.Abs(bZ - aZ));
        Api.World.SpawnParticles(props, dualCallByPlayer);
    }

    /// <summary>Throw one bark-chip burst off the TOP (crown) of the log at the moving strip edge — at the diamond's top
    /// corner, popping UP with a small sideways fan across the log, then falling under gravity. Axis-frame world units as
    /// in <see cref="SpawnBarkSide"/>; perp and along map to disjoint world axes, so each world axis takes whichever
    /// half-extent applies (the other factor is 0). Template + dual-call target owned by the caller, as above.</summary>
    void SpawnBarkTop(SimpleParticleProperties props, IPlayer? dualCallByPlayer, double ex, double ez, double perpX, double perpZ, double alongX, double alongZ)
    {
        props.MinPos.Set(ex - 0.05, Pos.Y + BarkTopMinY, ez - 0.05);
        props.AddPos.Set(0.10, BarkTopAddY, 0.10);

        double halfX = perpX * BarkTopPerpHalf + alongX * BarkChipScatter; // sideways fan (perp) + spread over the peel line (along)
        double halfZ = perpZ * BarkTopPerpHalf + alongZ * BarkChipScatter;
        props.MinVelocity.Set((float)-halfX, (float)BarkChipUpMin, (float)-halfZ);
        props.AddVelocity.Set((float)(2.0 * halfX), (float)(BarkChipUpMax - BarkChipUpMin), (float)(2.0 * halfZ));
        Api.World.SpawnParticles(props, dualCallByPlayer);
    }

    // ──────────────────── Debark work-sound loop — generic bark-stripping (client) ────────────────────
    //
    // The continuous "bark coming off" sound that plays for as long as a strip hold runs — the GENERIC layer the
    // metadata per-tier HIT sounds sit on top of. patches/debarking_animations.json frame-locks the tool-contact clips
    // (debarking-axehit*/-hammerhit* on the axe+hammer frames, -axehit* on the spud's, -knifescrape* on the knife's);
    // THIS is the separate, code-driven generic loop the ps1's New-DebarkSounds block deliberately reserves debarking1/
    // 2/3 for. It is NOT a single ShouldLoop buffer (that repeats ONE clip): each clip is a one-shot and we ROTATE —
    // BlockSawhorse's Step relay starts another random one whenever the current ends, so the three variants cycle while
    // the player works. Code-managed (an ILoadedSound handle) precisely so the release can FADE it out over
    // DebarkSoundFadeOut seconds — a fire-and-forget PlaySoundAt has no handle to stop. Positioned at the log
    // (RelativePosition = false → spatialized at the billet, never "in-head"; the clips are MONO so OpenAL spatializes
    // them, vs-positional-audio-mono). The HANDLE below is the worker's own — client-local, on the MASTER (it holds the
    // log + geometry), gated the SAME way as SpawnBarkChips: no log (incl. the restock breather's empty pair) → silent.
    // Everyone ELSE gets the same three clips re-triggered on a server timer from BroadcastDebarkFx, because a
    // ILoadedSound handle exists only in the process that loaded it and cannot be replicated (AuditFixPlan T06.01,
    // F-L4-13). Only the worker gets the fade-out, which is the whole reason the handle exists.

    // The three generic strip clips, picked at random per rotation. Explicit (NOT a "debarking*" wildcard) — that glob
    // would also catch the per-tier hit clips (debarking-axehit*/-hammerhit*/-knifescrape*), which share the "debarking"
    // prefix (see the apply-debarking-animation.ps1 warning). All MONO so they spatialize.
    static readonly AssetLocation[] DebarkLoopClips =
    {
        new AssetLocation("immersivewoodworking", "sounds/debark/debarking1"),
        new AssetLocation("immersivewoodworking", "sounds/debark/debarking2"),
        new AssetLocation("immersivewoodworking", "sounds/debark/debarking3"),
    };
    const float DebarkSoundVolume  = 1f;   // tuned in-game
    const float DebarkSoundRange   = 16f;  // matches the per-tier hit sounds' range
    const float DebarkSoundFadeOut = 0.3f; // seconds to fade out on release / strip end

    /// <summary>Wall-clock spacing between two OBSERVER clips of the generic strip loop (AuditFixPlan T06.01,
    /// F-L4-13). The worker's local handle rotates on the real end of each clip (<see cref="ILoadedSound.HasStopped"/>),
    /// which a fire-and-forget broadcast cannot know, so the observer's copy runs on a timer instead — set just under
    /// the shortest of the three (measured 0.83 / 0.95 / 1.28 s) so the stream reads as continuous rather than as
    /// separated knocks. Longer than the particle spacing on purpose: this one is a clip length, not a frame budget.</summary>
    const long DebarkClipNetIntervalMs = 800;

    // The currently-rotating strip clip, or null when not stripping. Client-only, transient (never serialized),
    // master-held. DisposeOnFinish is false (we own it → we free it on rotation / fade / teardown).
    ILoadedSound? debarkSound;

    /// <summary>
    /// Drive the generic strip loop one Step while a genuine strip runs — called from
    /// <see cref="BlockSawhorse.OnBlockInteractStep"/> only when <see cref="ContinueDebarking"/> returned true, so it
    /// inherits the debark gates, then re-checks <see cref="heldLog"/> so the restock breather's empty pair stays silent
    /// (mirrors <see cref="SpawnBarkChips"/>). ROTATION: each clip is a one-shot; once the current one has ended
    /// (<see cref="ILoadedSound.HasStopped"/>) we start another random one, so the three variants cycle for as long as
    /// the hold lasts. A no-op while a clip is still loading or playing. Routes to the master; client-only.
    /// </summary>
    public void UpdateDebarkSound(IPlayer byPlayer)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.UpdateDebarkSound(byPlayer); return; }
        if (capi == null) return;                                 // client only
        if (heldLog?.Block == null) { StopDebarkSound(); return; } // empty pair (restock breather): fade any in-flight clip instead of letting its tail bleed over the empty pair

        if (debarkSound != null && !debarkSound.IsDisposed && !debarkSound.HasStopped) return; // loading / playing → wait
        if (debarkSound != null && !debarkSound.IsDisposed) debarkSound.Dispose();             // free the finished handle
        debarkSound = StartDebarkClip();                                                        // next random clip
    }

    /// <summary>Load + start one random generic strip clip at the log, returning the handle (or null if the asset is
    /// missing / on the server). A one-shot (NOT ShouldLoop — the rotation in <see cref="UpdateDebarkSound"/> starts the
    /// next), positioned in the WORLD so OpenAL spatializes it. Master-only caller.</summary>
    ILoadedSound? StartDebarkClip()
    {
        if (capi == null) return null;
        AssetLocation loc = DebarkLoopClips[capi.World.Rand.Next(DebarkLoopClips.Length)];
        ILoadedSound? snd = capi.World.LoadSound(new SoundParams
        {
            Location         = loc,
            ShouldLoop       = false, // ROTATION, not one repeated buffer
            DisposeOnFinish  = false, // we own the handle so the release can fade it; we dispose it ourselves
            RelativePosition = false, // a WORLD position → spatialized at the log, never "in-head"
            Position         = DebarkSoundPos(),
            Volume           = DebarkSoundVolume,
            Range            = DebarkSoundRange,
        });
        snd?.Start();
        return snd;
    }

    /// <summary>Fade the strip loop out over <see cref="DebarkSoundFadeOut"/> seconds and drop our handle — called on
    /// release / look-away (<see cref="BlockSawhorse.OnBlockInteractStop"/> / <c>OnBlockInteractCancel</c>) and the
    /// instant a strip ends mid-hold (the gate broke or the bark fully came off → <see cref="ContinueDebarking"/> false).
    /// The fade runs to completion on the DETACHED handle (the engine drives it; the callback disposes it), so a quick
    /// re-press starts a fresh clip while the old one tails off. Idempotent (null handle = no-op), so a non-debarker's
    /// Step / Stop is harmless. Routes to the master; client-only in effect (the server never loaded a sound).</summary>
    public void StopDebarkSound()
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.StopDebarkSound(); return; }

        ILoadedSound? snd = debarkSound;
        debarkSound = null; // detach first → an immediate re-press starts a fresh clip, not this fading one
        if (snd == null || snd.IsDisposed) return;
        if (snd.IsPlaying) snd.FadeOut(DebarkSoundFadeOut, s => s.Dispose());
        else snd.Dispose();
    }

    /// <summary>World position the strip loop plays from: the pair centre (as <see cref="SpawnBarkChips"/> reads it), at
    /// roughly the laid log's mid-height. Master-relative.</summary>
    Vec3f DebarkSoundPos()
    {
        double cx = Pos.X + 0.5 + (pairAxis == EnumAxis.X ? 0.5 : 0.0);
        double cz = Pos.Z + 0.5 + (pairAxis == EnumAxis.Z ? 0.5 : 0.0);
        return new Vec3f((float)cx, Pos.Y + 0.9f, (float)cz); // log spans ≈ 0.58..1.29 → ~0.9 is mid-bark
    }

    /// <summary>Hard-stop + free the strip loop on teardown (no fade — the block is going away). Client-only resource;
    /// a no-op when none is active or on a slave / the server.</summary>
    void DisposeDebarkSound()
    {
        if (debarkSound == null) return;
        if (!debarkSound.IsDisposed) { debarkSound.Stop(); debarkSound.Dispose(); }
        debarkSound = null;
    }

    // Both teardown seams also retire this BE for anyone holding a cached reference to it (MarkGone, T05.05): the
    // partner memoizes us for its box-cache state read, and a chunk reload hands it a NEW BE object while the old one
    // would keep answering field reads.
    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        MarkGone();
        DisposeDebarkSound();
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        MarkGone();
        DisposeDebarkSound();
    }
}
