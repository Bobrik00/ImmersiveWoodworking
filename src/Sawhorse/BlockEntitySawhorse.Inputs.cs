using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

public partial class BlockEntitySawhorse
{
    // ───────────────────────────── E05: accepted inputs (T05.01) ─────────────────────────────
    //
    // Single source of truth for what a finished sawhorse pair accepts and how its wood species is read,
    // mirroring BlockEntityChoppingBlock's LogCodeParts idiom: the accept filter here and the saw-result mapping
    // in E06 read the SAME codes, so "what can I lay on the pair" and "what it saws into" can never drift apart.
    // The pair is deliberately NARROWER than the chopping block (Q5): ONLY player-placed logs, debarked logs and
    // log sections — explicitly NOT firewood, NOT flotsam/driftwood.
    //
    //   log-placed-{wood}-{rot}              → planks  — part(0) "log", part(1) "placed", wood = part(2)
    //   logsection-placed-{wood}-{seg}-{rot} → planks  — part(0) "logsection", part(1) "placed", wood = part(2)  (E08)
    //   debarkedlog-{wood}-{rot}             → beams   — part(0) "debarkedlog",                   wood = part(1)
    //
    // Verified against the vanilla recipe inputs this pair replaces: plank.json's "log-placed-*-ud" AND
    // "logsection-placed-*-ne-ud" (both → 12 planks, i.e. a section == a log for planks, so E08 gives it the same
    // PlanksPerLog), and supportbeams.json's "debarkedlog-{wood}-ud". A log section carries an EXTRA {segment}
    // (ne/se/sw/nw) part before the orientation, but its wood still sits at part 2 (the "placed" part shifts it one
    // slot right, exactly like a log) so it shares the log's index. The orientation (ud/we/ns) is always the LAST
    // code part, so matching stays orientation-agnostic and wood extraction is unaffected by it. Log sections exist
    // only for redwood in vanilla (a quarter of a giant 2×2 trunk); the code stays species-agnostic for wood mods.
    //
    // The parts are read through CodeParts, not Block.FirstCodePart (AuditFixPlan T05.03 / F-L10-01): the engine's
    // accessor splits the whole code on every call, and these predicates sit on the sawmill's 25 ms heartbeat, on the
    // client frame and on every tick of a manual rip hold. Same questions, same answers, no per-call garbage — see
    // CodeParts for the equivalence with FirstCodePart, edge cases included.

    /// <summary>A player-placed log (<c>log-placed-{wood}-{rot}</c>) → saws into planks. Narrower than the
    /// chopping block's "any log": the <c>placed</c> second part excludes grown/natural logs, and the distinct
    /// first part excludes log sections — matching the recipe input the pair replaces (<c>log-placed-*-ud</c>).</summary>
    internal static bool IsSawableLog(ItemStack? stack)
    {
        AssetLocation? code = stack?.Block?.Code;
        return CodeParts.Is(code, 0, "log") && CodeParts.Is(code, 1, "placed");
    }

    /// <summary>A debarked log (<c>debarkedlog-{wood}-{rot}</c>) → saws into support beams.</summary>
    internal static bool IsSawableDebarked(ItemStack? stack)
        => CodeParts.Is(stack?.Block?.Code, 0, "debarkedlog");

    /// <summary>A player-placed log section (<c>logsection-placed-{wood}-{seg}-{rot}</c>) → saws into planks, just
    /// like a whole log (E08). The <c>placed</c> second part excludes the grown/natural section that is still part of
    /// a standing tree (<c>logsection-grown-*</c>), matching the recipe input this replaces
    /// (<c>logsection-placed-*-ne-ud</c>) and the log's own <c>log-placed-*</c> rule.</summary>
    internal static bool IsSawableLogsection(ItemStack? stack)
    {
        AssetLocation? code = stack?.Block?.Code;
        return CodeParts.Is(code, 0, "logsection") && CodeParts.Is(code, 1, "placed");
    }

    /// <summary>Any accepted input. Everything else — firewood / flotsam / a grown log or section / a plain item —
    /// falls through all predicates and is rejected; this is the gate the place action (T05.02) checks.</summary>
    internal static bool IsSawableInput(ItemStack? stack)
        => IsSawableLog(stack) || IsSawableDebarked(stack) || IsSawableLogsection(stack);

    /// <summary>Classify a stack into the accepted-input kind (<see cref="SawInput.None"/> if not sawable).
    /// E06 maps this to the saw result: <see cref="SawInput.Log"/> and <see cref="SawInput.LogSection"/> →
    /// <c>plank-{wood}</c>, <see cref="SawInput.DebarkedLog"/> → <c>supportbeam-{wood}</c>
    /// (see <see cref="SawsIntoPlanks"/>).</summary>
    internal static SawInput GetSawInput(ItemStack? stack)
    {
        if (IsSawableLog(stack)) return SawInput.Log;
        if (IsSawableDebarked(stack)) return SawInput.DebarkedLog;
        if (IsSawableLogsection(stack)) return SawInput.LogSection;
        return SawInput.None;
    }

    /// <summary>Whether a sawn <paramref name="kind"/> yields PLANKS (a log or a log section) rather than support
    /// beams (a debarked log). The ONE place the plank-vs-beam split is decided, shared by every saw-result site
    /// (<see cref="GetSawProduct"/>, <see cref="SawProductResolves"/>, the sawmill's <c>GetPassBatch</c>) so adding a third
    /// plank-yielding input never has to be threaded through each <c>if (kind == Log) … else beam</c> by hand — the
    /// old two-way shape where "not a log" implicitly meant "a debarked log → beam" would silently misroute a
    /// section to beams.</summary>
    internal static bool SawsIntoPlanks(SawInput kind) => kind == SawInput.Log || kind == SawInput.LogSection;

    /// <summary>
    /// The mod-independence "the product must resolve" gate — the rule spelled out in <c>source-conventions.md</c> 2:
    /// for this input's kind and species, does the sawn output actually EXIST (<c>plank-{wood}</c> for a log or a log
    /// section, <c>supportbeam-{wood}</c> for a debarked log), in the log's OWN domain or in game? A station that
    /// cannot make the product refuses the input AT THE DOOR, instead of taking it, working it through every stage and
    /// dropping nothing.
    ///
    /// ONE definition, every door (AuditFixPlan T03.03). The hand pair's put (<see cref="TryPutLog"/>), its rip start
    /// gate (<see cref="CanSawHere"/>, re-checked each tick) and its auto-restock all call this, as do the sawmill's
    /// put (<c>BlockEntitySawmill.IsValidInput</c>) and its hopper suitability. It lives HERE, next to the classifiers
    /// it reads and the one domain-then-game resolver it calls (<see cref="ResolveProductItem"/> /
    /// <see cref="ResolveProductBlock"/>), rather than on the sawmill where it first landed: a second copy per station
    /// is precisely the drift this rule exists to prevent. <c>static</c> and world-parametrised, so every door asks the
    /// identical question and both sides answer alike.
    ///
    /// The beam branch carries one clause the plank branch has no counterpart for: a wood the game's own
    /// <c>debarkedlog -> supportbeam</c> recipe listed in <c>skipVariants</c> is refused even though its beam block
    /// exists, because the mod removed that recipe and must not become the way around it
    /// (<see cref="ImmersiveWoodworkingModSystem.BeamSkippedWood"/>, F-L9-10).
    /// </summary>
    internal static bool SawProductResolves(IWorldAccessor world, ItemStack? stack, SawMode mode)
    {
        // Classify ONCE and hand the kind to the species read (AuditFixPlan T05.03): the old pair of calls ran the
        // same three predicates twice over, which on the sawmill's 25 ms gate was the larger half of the cost.
        SawInput kind = GetSawInput(stack);
        if (kind == SawInput.None) return false;
        string? wood = GetWoodSpecies(stack, kind);
        if (wood == null) return false;

        string domain = stack!.Block!.Code.Domain; // Block non-null once kind != None (GetSawInput requires it)
        // The chosen mode picks which product must exist; Auto reproduces the pre-SawModePlan input-driven gate exactly.
        return ProductIsPlanks(kind, mode) ? PlankResolves(world, domain, wood) : BeamResolves(world, domain, wood);
    }

    /// <summary>Whether a sawn <paramref name="kind"/> yields PLANKS under <paramref name="mode"/>: a chosen
    /// <see cref="SawMode.Planks"/>/<see cref="SawMode.Beams"/> forces it, <see cref="SawMode.Auto"/> defers to the
    /// input (<see cref="SawsIntoPlanks"/>). The ONE place kind+mode collapses to plank-vs-beam, shared by
    /// <see cref="GetSawProduct"/>, <see cref="SawProductResolves(IWorldAccessor, ItemStack, SawMode)"/> and the
    /// sawmill's <c>GetPassBatch</c> (SawModePlan), so a new mode never has to be threaded through each site by hand.</summary>
    internal static bool ProductIsPlanks(SawInput kind, SawMode mode) => mode switch
    {
        SawMode.Planks => true,
        SawMode.Beams  => false,
        _              => SawsIntoPlanks(kind),
    };

    /// <summary>Does <c>plank-{wood}</c> exist for this wood, in the log's own domain then game (the mod-independence
    /// resolver, source-conventions #2)?</summary>
    static bool PlankResolves(IWorldAccessor world, string domain, string wood)
        => ResolveProductItem(world, domain, "plank-" + wood) != null;

    /// <summary>Does <c>supportbeam-{wood}</c> exist AND is this wood NOT a removed-recipe species
    /// (<see cref="ImmersiveWoodworkingModSystem.BeamSkippedWood"/>, F-L9-10)? The beam clause the plank one has no
    /// counterpart for, kept wherever a gate reaches beams.</summary>
    static bool BeamResolves(IWorldAccessor world, string domain, string wood)
        => !ImmersiveWoodworkingModSystem.BeamSkippedWood(world, wood)
           && ResolveProductBlock(world, domain, "supportbeam-" + wood) != null;

    /// <summary>The MODE-AGNOSTIC door gate (SawModePlan): can this input become EITHER product — plank OR beam? The
    /// hand pair's put (<see cref="TryPutLog"/> via <see cref="CanProcessInput"/>), the sawmill's put/hopper and the
    /// interaction-icon list use this, so a wood that only one cut mode can turn into a product is still ACCEPTED at the
    /// door; the per-action gate (<see cref="SawProductResolves(IWorldAccessor, ItemStack, SawMode)"/> with the chosen
    /// mode) is what refuses a specific mode with a nudge. Broadens the old input-driven door, which would have turned a
    /// barked, plank-less-but-beam-yielding wood away even though a beams-mode rip could finish it.</summary>
    internal static bool CanProduceAnySaw(IWorldAccessor world, ItemStack? stack)
    {
        SawInput kind = GetSawInput(stack);
        if (kind == SawInput.None) return false;
        string? wood = GetWoodSpecies(stack, kind);
        if (wood == null) return false;
        string domain = stack!.Block!.Code.Domain; // Block non-null once kind != None (GetSawInput requires it)
        return PlankResolves(world, domain, wood) || BeamResolves(world, domain, wood);
    }

    /// <summary>Can the pair do ANYTHING with <paramref name="stack"/> — rip it into planks/beams
    /// (<see cref="SawProductResolves"/>) or strip its bark (<see cref="DebarkProductResolves"/>)? The gate on the PUT
    /// (<see cref="TryPutLog"/>, AuditFixPlan T03.03): only a total dead end is turned away at the trestle, so a wood
    /// that has a <c>debarkedlog</c> but no <c>plank</c> can still be laid and stripped — a complete, useful action in
    /// its own right, which yields bark and a bare log. The per-ACTION gates then decide which of the two a given
    /// player, tool and position may actually start; this one only answers "is there any point laying it at all".</summary>
    internal static bool CanProcessInput(IWorldAccessor world, ItemStack? stack)
        => CanProduceAnySaw(world, stack) || DebarkProductResolves(world, stack);

    /// <summary>The mode-agnostic <see cref="CanProduceAnySaw"/> door gate for the LAID log — the master-relative
    /// instance form, the twin of <see cref="DebarkProductResolves()"/>. Used where a caller holds the block entity
    /// rather than a stack: the interaction-help builder (<c>BlockSawhorse</c>, which must not OFFER a rip that could
    /// never finish in EITHER mode, exactly as it already withholds an impossible strip). The per-mode refusal is the
    /// nudge's job (<see cref="NotifyInteractBlocked"/>), not the help's. False on an empty pair.</summary>
    internal bool CanProduceAnySaw() => CanProduceAnySaw(Api.World, GetMaster().heldLog);

    /// <summary>The wood species encoded in an accepted input's block code (oak, birch, …, redwood, or "aged"), or
    /// null if the stack isn't a sawable input. Binds the saw result to the parent tree (E06) and the log mesh to the
    /// right textures (T05.03). The extra <c>placed</c> part shifts wood one slot right for BOTH a log
    /// (<c>log-placed-{wood}</c> → part 2) and a log section (<c>logsection-placed-{wood}</c> → part 2), versus a
    /// debarked log (<c>debarkedlog-{wood}</c> → part 1).</summary>
    internal static string? GetWoodSpecies(ItemStack? stack) => GetWoodSpecies(stack, GetSawInput(stack));

    /// <summary>The species read for a stack whose kind the caller ALREADY classified — the form
    /// <see cref="SawProductResolves"/> uses so the gate classifies once instead of twice (AuditFixPlan T05.03). The
    /// offset rule is stated as "the debarked log is the one WITHOUT the extra kind part", so a future placed-style
    /// input inherits part 2 by default rather than silently reading the wrong slot. One substring, no split
    /// (<see cref="CodeParts.At"/>); the species is genuinely kept, unlike the parts the predicates only compare.</summary>
    internal static string? GetWoodSpecies(ItemStack? stack, SawInput kind)
        => kind == SawInput.None ? null : CodeParts.At(stack!.Block!.Code, kind == SawInput.DebarkedLog ? 1 : 2);

    /// <summary>Cheap collectible-level gate (code part only, no handbook-stack resolution): could this object
    /// be a sawable input? The E07 interaction-help builder filters with this FIRST — the vanilla idiom
    /// (<c>BlockEntityChoppingBlock.CouldBeChoppable</c>, BlockForge/BlockBloomery) — so the precise per-stack
    /// <see cref="IsSawableInput"/> only ever runs on plausible candidates and we never call a stranger's
    /// GetHandBookStacks during the fragile block-load phase. Looser than the per-stack check (a bare
    /// FirstCodePart "log"/"logsection" also admits grown logs / sections) — fine for a pre-filter the per-stack
    /// check then tightens.</summary>
    internal static bool CouldBeSawable(CollectibleObject obj)
    {
        if (obj?.Code == null) return false;
        string part = obj.FirstCodePart();
        return part == "log" || part == "debarkedlog" || part == "logsection";
    }

    /// <summary>The accepted-input handbook stacks (the interaction-help icons), resolved once and cached process-wide
    /// under the SAME <c>iwSawhorseSawableStacks</c> key the sawhorse and the sawmill share — the accepted-input list
    /// is identical, so whichever station loads first builds it and the other reuses it. ONE builder body (was
    /// byte-duplicated in <c>BlockSawhorse.OnLoaded</c> and <c>BlockSawmill.OnLoaded</c>, F-S12-02): the cheap
    /// <see cref="CouldBeSawable"/> code-gate FIRST so handbook stacks are only resolved for plausible candidates, then
    /// tighten per stack with <see cref="IsSawableInput"/>. GetHandBookStacks is wrapped in try/catch because some
    /// (modded) collectibles throw inside it during the block-load phase, which would otherwise crash mod load — the
    /// same vanilla idiom BlockChoppingBlock uses for its choppable list.
    ///
    /// The per-stack tightening is <see cref="CanProcessInput"/>, not the bare shape check (AuditFixPlan T03.03): the
    /// icons say "lay one of these here", so a wood the pair would turn away at the door has no business among them.
    /// ONE known limit, accepted rather than papered over: the list is built once at block load and the beam
    /// <c>skipVariants</c> half of the gate only reaches a CLIENT with the config packet, later. So on a client this
    /// list can still show <c>debarkedlog-veryaged/rotten/veryrotten</c>, which the put then refuses with a message.
    /// Nothing else drifts — the plank/beam existence half is a plain registry lookup, already true on both sides at
    /// load — and those three states are creative/worldgen-only, so the residue is cosmetic. Refreshing it would mean
    /// rebuilding the WorldInteractions the callers bake at load, which is not worth a stale icon.</summary>
    internal static ItemStack[] GetOrCreateSawableStacks(ICoreAPI api, ICoreClientAPI capi)
        => ObjectCacheUtil.GetOrCreate(api, "iwSawhorseSawableStacks", () =>
        {
            List<ItemStack> list = new List<ItemStack>();
            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                if (!CouldBeSawable(obj)) continue;
                List<ItemStack>? stacks;
                try { stacks = obj.GetHandBookStacks(capi); }
                catch { continue; } // stay resilient if a (modded) sawable still throws in GetHandBookStacks
                if (stacks == null) continue;
                foreach (ItemStack stack in stacks)
                {
                    if (CanProcessInput(api.World, stack)) list.Add(stack); // shape AND "the pair can do something with it"
                }
            }
            return list.ToArray();
        });
}
