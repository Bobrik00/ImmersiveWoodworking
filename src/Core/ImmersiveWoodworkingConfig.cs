using System.Collections.Generic;
using Newtonsoft.Json;
using ProtoBuf;
using Vintagestory.API.Common;   // ILogger, so Sanitize can log a band-clamp correction (SpeedDecouplingPlan T01.03)
using Vintagestory.API.MathTools; // GameMath.TWOPI for the derived speed constants (SpeedDecouplingPlan T01.02)

namespace ImmersiveWoodworking;

// PLAIN DATA — the on-disk config (Newtonsoft, via StoreModConfig/LoadModConfig) and the in-memory object both sides
// read. It is NOT itself the network packet: the server pushes it to clients as JSON inside ConfigSyncPacket (below),
// wired in ImmersiveWoodworkingModSystem.Start / StartServerSide / StartClientSide.
//
// AuditFixPlan T02.05 (F-L4-02) — why the packet is no longer this class. It used to carry
// [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)] and be sent directly. ProtoBuf then dropped from the wire
// EVERY field whose server value equalled the CLR zero of its type (false / 0 / 0f): protobuf-net treats an implicit
// member's zero as its default and skips it, and UseImplicitZeroDefaults cannot be disabled on the model Serializer
// uses. The client, meanwhile, builds the object through its normal constructor, so each missing field kept its
// initializer. Net effect: 16 `true`-by-default toggles and 29 numbers never reached the client at all, and a mechanic
// SWITCHED OFF on the server stayed ON for every client — in single-player too, since the client never reads the file.
//
// KEEP THIS CLASS FREE OF PROTOBUF ATTRIBUTES. The wire form is JSON precisely because JSON writes every field,
// `false` and 0 included, and because a field added later travels correctly with no attribute to remember. The
// startup self-check VerifyConfigSyncRoundTrip enforces it field by field.
public class ImmersiveWoodworkingConfig
{
    // ── General (feature toggles) ───────────────────────────────────────────────────────────────────────
    // The mod-wide on/off switches that span both manual stations (auto-feed, the vanilla-recipe strips, the
    // pit-saw / sawhorse acquisition tiers). Grouped first so a server can find every behaviour gate in one
    // place; the per-station NUMBERS (yields, timings, drop counts) live in their own sections below.

    // The three subsystem switches come first: each takes a whole part of the mod out of a world. Off means nothing
    // NEW of that part can be obtained or found (its recipes are not loaded, its blocks and items leave the creative
    // inventory and the handbook), while what a world already holds keeps working, because every block and item stays
    // registered: dropping a code would turn each placed machine and each carried stack into an unknown block or item.
    // The asset side lives in FeatureGate.cs, the per-domain machine recipes and the tannin strip in
    // ImmersiveWoodworkingModSystem, the bark drop in BlockEntitySawhorse.Debarking.cs, the prose in
    // WoodworkingHandbookInfo. All three are server-owned like the rest of the config and read by the client only for
    // the handbook text, after the sync.

    /// <summary>When false, the automated SAWMILL is switched off for new builds: the grid recipes for its frame, its
    /// four mechanism parts and its blade kit are not registered, and the frame, the parts and the blade kits leave the
    /// creative inventory and the handbook, along with every handbook line that names the sawmill. A sawmill already
    /// built in the world keeps running on the blade kits that already exist; no new kit can be crafted. The
    /// automation.json and drops.json sawmill fields stay in the files and keep applying to such a machine. The hand
    /// sawhorses are not affected. Default true. Plain bool, nothing to clamp.</summary>
    public bool EnableSawmill = true;

    /// <summary>When false, the automated CHOPPER is switched off for new builds: the grid recipes for its frame, drive,
    /// arm and head and the anvil recipe for the head half are not registered, and those blocks and items leave the
    /// creative inventory and the handbook, along with every handbook line that names the chopper. A chopper already
    /// built in the world keeps running on the heads that already exist; no new head can be made. The hand chopping
    /// block, which the chopper takes as its bed, is not affected. Default true. Plain bool, nothing to clamp.</summary>
    public bool EnableChopper = true;

    /// <summary>When false, the BARK CHAIN is switched off: a log stripped on the sawhorses drops only the debarked log
    /// and no bark. Debarking itself stays, with every tool, because the debarked log is the input of the beam rip. The
    /// bark items, birch tar, the bark decoction, the perforated pot and the tar pot leave the creative inventory and
    /// the handbook, and the recipes that work them are not loaded: bark tannin and bark compost in the barrel, the
    /// perforated pot on the clay-forming table, the decoction in the cooking pot, the bark and bast poultices, bast
    /// rope and ACA's boiled bark. So are the patches that only change other recipes (tar as resin, tar glue, the tags
    /// that feed ACA's oak bark into the tannin barrel). The bast and bark-decoction poultice variants stay registered
    /// and are only hidden. Patches that hold up something already PLACED stay on, inert without a bark source: bark
    /// as pit-kiln kindling, the birch-bark roof for VSRoofing, bark in Food Shelves jars. Overrides
    /// <see cref="DisableVanillaLogTannin"/>: with the chain off the vanilla whole-log tannin recipes are always kept,
    /// since nothing else would make tannin. Bark and placed tar rigs already in the world keep working. Default true.
    /// Plain bool, nothing to clamp.</summary>
    public bool EnableBarkChain = true;

    /// <summary>When true, the manual stations auto-feed themselves from the working player's inventory: after a
    /// log is consumed — chopped on the chopping block, or fully ripped on the sawhorses — the station pulls the
    /// next matching log out of that player's pack (NOT the active slot, which holds the axe / saw) so a whole
    /// stack can be processed without re-placing each piece by hand. It prefers a log of the SAME kind/species as
    /// the one just processed, falling back to any acceptable input. On the sawhorses this is seamless — a held
    /// rip simply continues on the next log; on the chopping block a held right-click keeps chopping the whole stack
    /// (a configurable <see cref="ChopRestockDelaySeconds"/> pause between strikes). Default ON — the stations
    /// restock themselves so a whole stack flows through hands-free; set it false to restore the original
    /// behaviour, where the stand/pair empties after each log and you re-place the next one yourself. (Shared by
    /// both manual stations; the sawhorse counterpart pause is <see cref="SawRestockDelaySeconds"/>, listed under
    /// Sawing.)</summary>
    public bool AutoRestock = true;

    /// <summary>When true, everything the MANUAL stations produce drops at the feet of the player who did the work,
    /// instead of scattering around the station: the chopping block's firewood / half-logs / sticks, and on the
    /// sawhorses both the rip (planks or beams, plus Wear &amp; Tear sawdust) and the bark strip (the bark and the
    /// debarked log). The drop lands with no velocity, so the game's own item pickup collects it on the next tick,
    /// which keeps the player's "collect only while sneaking" setting working, plays the pickup sound, and leaves what
    /// does not fit lying at their feet instead of losing it. In a two-man rip or strip each batch goes to the player
    /// whose stroke completed that section. The automated sawmill and chopper are NOT affected: they have no worker to
    /// deliver to, and eject their batch from the machine as before. Default OFF: the stations keep their own drop
    /// physics (the block's scatter, the sawhorse's sideways eject). Plain bool, nothing to clamp.</summary>
    public bool DropsAtPlayer = false;

    /// <summary>When true (the default), the SPEED of the MANUAL woodworking operations scales with the METAL/tier of the
    /// tool you use — a copper tool works proportionally slower than a steel one — with NO per-metal config keys to tune.
    /// The factor is read from the tool's OWN vanilla wood mining speed (<c>MiningSpeed[Wood]</c>) relative to the STEEL
    /// tool of its family — STEEL is the CEILING: a steel tool works at full speed and nothing is faster (a better-than-
    /// steel tool, e.g. a meteoric-iron axe, is capped to steel's speed, working exactly like steel). It applies to the
    /// three tier-scaled hand operations: CHOPPING with the AXE, RIPPING with the SAW / pit saw, and DEBARKING with the
    /// bark SPUD. (The knife and the axe+hammer debark tiers stay FLAT — they carry no wood mining speed, so their metal
    /// never matters, which also keeps the debark tiers from inverting.) IMPORTANT — because of this, the base-effort
    /// knobs are calibrated for the BEST (steel) tool and a lesser metal is slower AUTOMATICALLY, from the item itself,
    /// with no per-metal number to set anywhere: <see cref="SawSeconds"/> / <see cref="DebarkSeconds"/> are the time with a
    /// steel saw / spud, and with <see cref="ChopSpeedMultiplier"/> at its default a log takes a single steel-axe strike
    /// (a copper axe needs proportionally MORE strikes — the per-swing <see cref="ChopSeconds"/> itself is unchanged by
    /// the metal). Set this false to make every allowed tool work at the full base speed regardless of its metal (the
    /// original behaviour). The automated sawmill / chopper are NOT affected: their pace comes from the drive RPM, and
    /// their blade/head metal stays a WEAR tier, not a speed tier (documented in those sections). Plain bool — nothing to
    /// clamp.</summary>
    public bool ScaleWorkSpeedByToolTier = true;

    /// <summary>When true, removes the vanilla "axe + log in the crafting grid → firewood" recipes,
    /// so firewood can only be produced on the chopping block. Set to false to keep
    /// vanilla hand-crafting alongside the chopping block.
    /// <para>"Firewood" here includes a species-specific one: vanilla's separate
    /// <c>log-placed-aged-ud → agedfirewood</c> recipe goes with the others, because the stand makes that item itself
    /// (AuditFixPlan T04.05). It used to be spared, correctly, back when an aged log chopped into ORDINARY
    /// firewood.</para></summary>
    public bool DisableVanillaFirewoodCrafting = true;

    /// <summary>When true, removes the vanilla "saw + log/debarked log in the crafting grid → planks/beams"
    /// recipes, so planks and support beams can only be produced by ripping on the sawhorses. Set to false to
    /// keep vanilla hand-crafting alongside the sawhorses. Recycling recipes (plank block/stairs/slab → planks)
    /// and other saw recipes (cabinet, crate, table…) are never touched.</summary>
    public bool DisableVanillaPlankCrafting = true;

    /// <summary>When true, removes the vanilla "axe + hammer + log in the crafting grid → debarked log" recipe, so a
    /// log can only be stripped in-world on the sawhorses (the SIDE action — stand at a long side, hold RMB with a
    /// debarking tool). Mirrors <see cref="DisableVanillaPlankCrafting"/>: the grid recipe and the in-world station give
    /// the SAME result (a bark-less <c>debarkedlog</c> — neither drops bark), so this just funnels that hand path onto
    /// the station. The match is STRUCTURAL (output <c>debarkedlog-*</c> + a <c>log-placed-*</c> input), so it covers
    /// vanilla AND any wood mod's own debark recipe (e.g. wildcrafttree's) with no per-mod patch. Set false to keep the
    /// vanilla grid recipe alongside the sawhorses. Default true, matching the firewood/plank strips.</summary>
    public bool DisableVanillaDebarkCrafting = true;

    /// <summary>When true (the default), removes the vanilla "whole log + water in a barrel → weak/strong tannin"
    /// recipes, so the tanning agent comes from BARK instead: strip a log's bark on the sawhorses (BarkPlan/E02,
    /// <see cref="BarkPerLog"/> bark per log) and steep THAT (the mod's own bark → tannin barrel recipes, E03/T03.02) — the debarked log survives for
    /// lumber, which is the whole point (vanilla burns the log for tannin). The match is STRUCTURAL (output
    /// <c>weaktanninportion</c>/<c>strongtanninportion</c> + a <c>log-placed-*</c> or <c>debarkedlog-*</c> input), so it
    /// covers vanilla (oak/acacia) AND any wood mod's own whole-log tannin recipe (e.g. wildcrafttree's walnut/willow/
    /// beech) with no per-mod patch. Narrow-trunk logs (<c>lognarrow-placed-*</c>, which the sawhorses can't debark →
    /// no bark path) and non-log routes (e.g. wildcrafttree's leaf tannin) are deliberately SPARED so they aren't
    /// orphaned — the same discipline the firewood strip uses for <c>lognarrow</c>. Removing the biome gate "leather only
    /// from oak/acacia" is intended: conifers tan too now, via their bark, at a lower yield; a server that wants the
    /// vanilla gate back sets those species to zero bark in <c>barkdrops.json</c>. Set false to keep the vanilla
    /// whole-log tannin recipes alongside the bark chain. Mirrors <see cref="DisableVanillaPlankCrafting"/> /
    /// <see cref="DisableVanillaDebarkCrafting"/> (structural, config-gated, server-side asset edit before RecipeLoader) —
    /// applied to BARREL recipes rather than grid ones. Ignored while <see cref="EnableBarkChain"/> is off: there is no
    /// bark then, and the strip would leave the game without tannin.</summary>
    public bool DisableVanillaLogTannin = true;

    /// <summary>When true (the default), the grid strips above (<see cref="DisableVanillaFirewoodCrafting"/>,
    /// <see cref="DisableVanillaPlankCrafting"/>, <see cref="DisableVanillaDebarkCrafting"/>) skip any recipe that carries
    /// <c>requiresTrait</c>. Such a recipe is a class reward from a class mod, not the everyday hand path the stations
    /// replace. Sacred Classes' Carpenter trait is ten grid recipes; three of them fall into these families ("2 logs +
    /// saw → 32 planks", "2 debarked logs + saw → 12 beams", "2 logs + axe → 12 firewood"), and stripping them would
    /// leave the trait only its stick and log-quad recipes. Vanilla has no trait-locked recipe in these families, so
    /// without a class mod nothing changes. Applies only while the world keeps class-exclusive recipes on (the
    /// <c>classExclusiveRecipes</c> world option, off in the Exploration preset): with it off the engine drops every
    /// trait lock, so the recipes are stripped as before rather than handed to every player. Set false to strip
    /// trait-locked recipes too, so that every player, whatever the class, works logs on the stations. Plain bool.</summary>
    public bool KeepTraitRecipes = true;

    /// <summary>When true (the default) an ORDINARY saw rips logs on the sawhorses too, at the speed set by
    /// <see cref="VanillaSawSpeedMultiplier"/> (default half the pit saw — the pit saw stays the faster tool).
    /// "Ordinary saw" is mod-independent: any tool the game classes as a saw (<c>tool: "saw"</c>) that isn't the pit
    /// saw — the vanilla <c>game:saw-{metal}</c> AND any saw added by another mod. When false an ordinary saw does
    /// NOT work here at all: the attempt is refused with an on-screen "need a pit saw" nudge and produces no sawing
    /// animation, particles, sound or progress — only the pit saw can rip. The pit saw always works, regardless of
    /// this flag.</summary>
    public bool AllowVanillaSaw = true;

    /// <summary>When true, REMOVES the pit saw: its crafting recipe is disabled (it can no longer be crafted — the
    /// strip in <see cref="ImmersiveWoodworkingModSystem.AssetsLoaded"/>) AND it stops working as a saw on the
    /// sawhorses — so only ORDINARY saws rip logs there (the vanilla <c>game:saw-{metal}</c> and any saw another mod
    /// adds). The inverse of the normal tier: instead of the pit saw being the privileged fast tool, it is gone and
    /// ordinary saws are the only rip tool. While this is on, an ordinary saw works on the sawhorses REGARDLESS of
    /// <see cref="AllowVanillaSaw"/> (otherwise the station would have no valid tool at all), still ripping at
    /// <see cref="VanillaSawSpeedMultiplier"/> (the pit-saw speed parity is gone with the pit saw). A pit saw already
    /// in a world stays as an item but no longer rips. Does NOT touch the automated sawmill (a separate
    /// <c>sawmillblade</c> item). Default OFF — the pit saw is the intended tool.</summary>
    public bool RemovePitSaw = false;

    /// <summary>When true, the sawhorse trestle is obtained from a HAND CRAFT — stick + rope + stick in a single
    /// row (matchable in ANY row of the grid) → one finished trestle — and placed like an ordinary block, AND the
    /// in-world "plain right-click a stick on the ground to lay a trestle" gesture is DISABLED. Default false (the
    /// original in-world stick→stick→rope build). Flip it to true to resolve a conflict with another mod that also
    /// claims a plain right-click with a stick: the trestle then never touches the stick's right-click at all. The
    /// crafted block places straight to the finished (paired-capable) state, and breaking it still returns its
    /// 2 sticks + 1 rope — the same materials the craft cost — so the two acquisition paths stay balance-neutral.</summary>
    public bool CraftableSawhorse = false;

    /// <summary>The fraction of a station's work — a rip on the sawhorses or the automated sawmill, or a bark strip on
    /// the sawhorses — BELOW which a REMOVED or BROKEN log is handed back WHOLE instead of being forfeit: the
    /// "misclick / barely-started" grace, shared by every station and both processes so they can't drift. 0.25 = 25%
    /// (the default): a log under a quarter of the way through comes back intact on a Ctrl+RMB remove OR when the
    /// station is broken / its pair dissolves; at or past this fraction the original "begun → no free hand-back" rule
    /// stands (the partly-worked log is scrapped with no drop). An UNTOUCHED log (0% progress) is ALWAYS returned whole
    /// regardless of this value, so a freshly-loaded log can never be destroyed on break. Set 0 to return only untouched
    /// logs (strictest), up to 1 to always return the whole log (never forfeit). Clamped to [0, 1]. Read live/synced by
    /// <see cref="ImmersiveWoodworking.BlockEntitySawhorse.LogRecoverable"/> and
    /// <see cref="ImmersiveWoodworking.BlockEntitySawmill.LogRecoverable"/>.</summary>
    public float LogRecoverThreshold = 0.25f;

    // ── Chopping (chopping block) ───────────────────────────────────────────────────────────────────────

    /// <summary>When true (the default), chopping a WHOLE log by HAND on the block first splits it LENGTHWISE into two
    /// <c>halflog</c> intermediates (ManualOpsBalancePlan E04/T04.02) — carryable half-logs that each then chop into
    /// firewood, so a log → 2 half-logs → firewood over three chops. Set false to SKIP that stage entirely: a hand-laid
    /// whole log chops STRAIGHT to firewood in one pass, exactly like the automated chopper (which never made
    /// half-logs). The axe WEAR per log is the same either way at ANY reachable
    /// <see cref="AxeDurabilityPerLog"/>, not just on the shipped default: since AuditFixPlan T03.01 the target is
    /// divided across the chain's paid steps and the split takes what the halves leave, so the split chain sums back to
    /// the target by construction (the old formula only balanced on an even default — and left the split free on the
    /// old default of 2). The FIREWOOD total can still differ, by at most 1 and always in the PLAYER'S FAVOUR: a direct
    /// chop yields the FULL FirewoodPerLog, while the half-log chain drops the FirewoodPerLog/2 integer-division
    /// remainder on an ODD, hand-edited config — the accepted edge in
    /// <see cref="ImmersiveWoodworking.BlockEntityChoppingBlock.GetChopResult"/>. Only the intermediate item and the
    /// NUMBER of chop workpieces differ otherwise. Because a log is then ONE completed workpiece instead of three, with
    /// xSkills it grants forestry XP (<see cref="XSkillsChopXpPerWorkpiece"/>) ONCE per log instead of three times.
    /// Only the HAND stand's WHOLE-LOG split is gated: a half-log already in your pack still chops into firewood, log
    /// sections / a laid chopping block still go straight to firewood, and the automated chopper is untouched. Plain
    /// bool — nothing to clamp.</summary>
    public bool HalfLogChopStage = true;

    /// <summary>When true (the default), FIREWOOD can be chopped into sticks on the hand chopping block AND the automated
    /// chopper (firewood → <see cref="SticksPerFirewood"/> sticks). Set false to REMOVE that second-order step entirely:
    /// firewood can no longer be laid on the block or fed into the chopper (both stations' input gates refuse it — manual
    /// put, hopper/chute push, and the machine's start gate) and it yields NO drop, so sticks then come only from vanilla
    /// sources (the firewood → sticks conversion is IW's own addition — vanilla has no such recipe). This gates ONLY the
    /// firewood → sticks step; chopping LOGS still produces firewood exactly as before (that is the primary output, not
    /// this step) — a log chopped with this off still drops its firewood, you just can't process that firewood further
    /// here. A firewood workpiece left on a stand from BEFORE this was turned off simply won't chop (no drop) — take it
    /// back with Ctrl+RMB. Applies to both chopping stations; the sawing/debarking stations are unrelated. Plain bool —
    /// nothing to clamp.</summary>
    public bool AllowFirewoodToSticks = true;

    /// <summary>Total firewood produced from one log. 8 = ×2 the vanilla axe-in-grid yield (4 firewood per
    /// log), matching the sawing station's ×2-vanilla outputs (<see cref="PlanksPerLog"/> 24 = 2×12,
    /// <see cref="BeamsPerDebarkedLog"/> 6 = 2×3) so every station's wood output is a consistent ×2 vanilla —
    /// no ×2/×3 spread across chopping vs sawing.</summary>
    public int FirewoodPerLog = 8;

    /// <summary>Total sticks produced from one firewood. Lowered 4→2 alongside <see cref="FirewoodPerLog"/>
    /// so the firewood→sticks step stays in step with the rebalanced log→firewood chain. (Vanilla has no
    /// firewood→stick recipe — this is IW's own conversion, kept deliberately modest.)</summary>
    public int SticksPerFirewood = 2;

    /// <summary>How many separate item entities the firewood is split across when dropped.
    /// Capped to the total amount (never more objects than resources).</summary>
    public int FirewoodDropCount = 4;

    /// <summary>How many separate item entities the sticks are split across when dropped.
    /// Capped to the total amount (never more objects than resources) — with <see cref="SticksPerFirewood"/>
    /// now 2 this yields 2 single-stick piles.</summary>
    public int SticksDropCount = 2;

    /// <summary>How long (seconds) ONE axe swing takes on the stand — the impact frame is timed to land here, and this
    /// also drives the swing-animation speed. A laid workpiece is chopped over several such strikes
    /// (ManualOpsBalancePlan E03 discrete progress) — how many depends on <see cref="ChopSpeedMultiplier"/> and the
    /// axe's metal — so the wall time to chop a whole log is roughly that many swings plus the
    /// <see cref="ChopRestockDelaySeconds"/> breather between them. This per-swing duration is NOT affected by the
    /// tool's metal (the metal changes the strike COUNT — see <see cref="ChopSpeedMultiplier"/> — not the swing).
    /// Clamped to [0.1, 2].</summary>
    public float ChopSeconds = 0.75f;

    /// <summary>Master SPEED multiplier for hand chopping on the block — how fast the discrete chop bar fills per axe
    /// strike (a log → firewood, or firewood → sticks). The bar advances by this × the axe's metal-tier factor per
    /// strike (× the billet boost for firewood / half-logs, <see cref="FirewoodChopSpeedMultiplier"/> /
    /// <see cref="HalfLogChopSpeedMultiplier"/>) × the axe's Toolsmith sharpness, and the drop
    /// lands on the strike that reaches 100% (E03). Default 1.0 — with a STEEL axe (the tier reference, factor 1.0) a
    /// whole log splits in a SINGLE strike, so 1.0 reproduces the original one-strike-steel feel. RAISE it above 1
    /// (e.g. 2-3×) to chop FASTER — every axe advances more per strike, so weaker metals one-shot a log sooner (2 ≈
    /// halves the strikes a copper axe needs); LOWER it below 1 to chop SLOWER, spreading even a steel axe over several
    /// strikes (0.5 ≈ two steel strikes — the old "ChopStrikesPerLog = 2"). This is a FLOATING knob layered ON TOP of
    /// the per-tool metal factor, and — unlike that factor — it is NOT capped at the steel reference, so you can tune
    /// chopping PAST the one-strike-steel floor here even though a better-than-steel AXE alone (see
    /// <see cref="ScaleWorkSpeedByToolTier"/>) still tops out at steel. With <see cref="ScaleWorkSpeedByToolTier"/> off,
    /// every axe advances by exactly this per strike. Affects ONLY hand chopping — not the saw / bark spud or the
    /// automated stations. Each strike is one <see cref="ChopSeconds"/> swing. Clamped to [0.05, 10].</summary>
    public float ChopSpeedMultiplier = 1f;

    /// <summary>How many times FASTER FIREWOOD (→ sticks) is chopped than a whole log — the discrete chop bar advances by
    /// this multiple per strike when the workpiece is FIREWOOD, already-split billet wood far easier to break up than a
    /// whole log. Default 2.25: at that rate a SHARP copper axe (tool-tier factor ~0.47 × sharpness 1.0 × 2.25 ≈ 1.06)
    /// splits firewood in a SINGLE strike, while a blunt copper one (down toward the <see cref="SharpnessSpeedFloor"/>)
    /// needs two, and a whole log still needs several from the same axe. HALF-LOGS (→ firewood) have their OWN multiplier
    /// (<see cref="HalfLogChopSpeedMultiplier"/>), so the two can be tuned independently. 1 = firewood chops at the whole-
    /// log rate; raise it to let blunter/weaker axes one-shot firewood too. Applies on TOP of the tool-tier factor AND the
    /// sharpness factor (<see cref="ScaleWorkSpeedByToolTier"/> / Toolsmith). Clamped to [1, 10].</summary>
    public float FirewoodChopSpeedMultiplier = 2.25f;

    /// <summary>How many times FASTER a HALF-LOG (→ firewood) is chopped than a whole log — the discrete chop bar advances
    /// by this multiple per strike when the workpiece is a HALF-LOG, already-split billet wood easier than a whole log but
    /// kept SEPARATE from firewood (<see cref="FirewoodChopSpeedMultiplier"/>) so the two tune independently. Default 2
    /// (unchanged from when firewood and half-logs shared one multiplier): a half-log chops ~twice as fast as a whole log,
    /// so a sharp copper axe still needs two strikes (~0.47 × 2 ≈ 0.95) while steel one-shots it. 1 = half-logs chop at the
    /// whole-log rate. Applies on TOP of the tool-tier and sharpness factors. Clamped to [1, 10].</summary>
    public float HalfLogChopSpeedMultiplier = 2f;

    /// <summary>Random SPREAD applied to each axe strike's chop-progress notch on the block, so the number of strikes a
    /// workpiece takes stops being perfectly predictable. Each strike's notch (<see cref="ChopSpeedMultiplier"/> ×
    /// metal-tier factor × Toolsmith sharpness × the billet boost) is multiplied by a value drawn uniformly from
    /// [1 - this, 1 + this]: at the default 0.2 a strike lands anywhere from 80% to 120% of its nominal notch. This is
    /// a SPREAD, not a speed-up — it is symmetric about 1.0, so over many logs the AVERAGE strike count is unchanged for
    /// any tool that already takes several strikes. The ONE exception is a tool sitting EXACTLY on the one-strike line (a
    /// steel AXE splitting a whole log, notch ≈ 1.0): there the spread can only push the count UP — you cannot take less
    /// than a whole strike — so it one-shots about half the time and needs a second strike the rest, averaging ~1.5. (The
    /// steel MAUL clears that line by default, see <see cref="MaulChopSpeedMultiplier"/>, so it is not affected.) It does
    /// NOT make chopping non-deterministic between server and client: the spread is derived from values BOTH sides
    /// already share at the moment of the strike (block position, the chopper's UID, the pre-strike progress), so both
    /// finish the workpiece on the SAME strike — no desync, no phantom swing, and the total axe wear per workpiece is
    /// unchanged. 0 turns the spread off entirely and restores the old strictly predictable behaviour, strike for
    /// strike. Affects ONLY hand chopping with the axe on the block — not sawing, debarking, or the automated sawmill /
    /// chopper (their pace is set by the drive RPM, where a spread would read as machinery jitter). Clamped to [0, 0.5];
    /// above 0.5 the strike count would lurch by more than half.</summary>
    public float ChopProgressJitter = 0.2f;

    /// <summary>Per-strike SPEED multiplier for a MAUL (колун) chopping a whole log straight to firewood (it skips the
    /// half-log stage — a maul does the work of THREE workpieces, log → 2 half-logs → firewood, in one). The default is
    /// 1.5: a STEEL maul (tool-tier factor 1.0) then advances 1.5 per strike, which clears a whole log to firewood in a
    /// SINGLE strike even at the BOTTOM of the <see cref="ChopProgressJitter"/> spread (1.5 × (1 − 0.2) = 1.2 ≥ 1), so it
    /// reliably one-shots a log — strictly better than an axe, which needs the split chain (1 strike to halve + 1 per
    /// half). This 1.5 is deliberately above 1.0: at exactly 1.0 a steel maul sits ON the completion line, so the
    /// symmetric jitter leaves it a strike short about half the time and it takes 2 strikes then — 1.5 buys the margin to
    /// clear the floor every swing. Lower it below 1 to NERF the maul (0.5 ≈ twice the strikes). Weaker metals still scale
    /// by their tool-tier factor, so a copper maul (~0.47) takes about two strikes at 1.5; raise the knob further to speed
    /// those up too. Applies ONLY to a maul with the half-log stage ON, on a ChopKind.Log workpiece — a whole log, a log
    /// SECTION, or a laid chopping block, all of which a maul clears straight to firewood. A plain axe, a billet workpiece
    /// (half-log / firewood), and the whole HalfLogChopStage-off case are unaffected (1.0). The section / laid-block case
    /// is rare misuse but stays in the penalised family so the both-sides speed gate stays deterministic and an
    /// AutoRestock hold that started on a section can't dodge the knob (see ChopStrike,
    /// ImprovementsPlan T06.05/T06.07). Multiplies alongside the tool-tier, sharpness and billet factors, BEFORE the
    /// ImprovementsPlan E05 jitter. Clamped to [0.05, 10] like <see cref="ChopSpeedMultiplier"/>.</summary>
    public float MaulChopSpeedMultiplier = 1.5f;

    /// <summary>How much axe durability it costs to fully process ONE whole log into firewood — the direct
    /// "durability per log" knob (renamed 2026-07-24 from the old inverse <c>AxeItemsPerDurability</c>). The whole-log
    /// cost is spread across the chop so the total is exactly this whether the log is SPLIT by an axe (the split plus
    /// each half-log → firewood) or cleared in ONE pass by a maul. The target divides across the axe chain's PAID
    /// steps — the split itself plus one chop per half-log (<c>ChopChainSteps</c>) — so a half-log costs
    /// <c>this / ChopChainSteps</c> (floor, min 1) and the SPLIT charges what the halves leave. That remainder is never
    /// zero and never below one half's share, at any reachable target (AuditFixPlan T03.01; the arithmetic lives on
    /// <c>BlockEntityChoppingBlock.PerHalfLogWear</c> / <c>SplitWearCost</c>). Chopping firewood into sticks (a separate
    /// step, not part of a log) costs a flat 1.
    ///
    /// Default 3, raised from 2 in 1.3.1: at 2 the two half-log chops already spent the whole target and the split came
    /// out FREE — a free upgrade into a better-burning intermediate. 3 is the lowest target at which the split still
    /// costs something, so the change makes an axe last about a third fewer logs. A HIGHER value wears the axe FASTER;
    /// LOWER wears it slower; exactly 0 = the axe takes no durability at all. <see cref="Sanitize"/> admits 0 or ≥ 3
    /// and RAISES 1 or 2 to 3 — including in a world whose config file still carries the old default, since Sanitize
    /// runs before the folder is written back. Sibling of <see cref="DebarkDurabilityPerLog"/> for the sawhorse
    /// debark.</summary>
    public int AxeDurabilityPerLog = 3;

    /// <summary>Pause (seconds) the chopping block waits between axe strikes while a right-click is held — a short
    /// breather AFTER each strike (the axe rests on its impact pose) before the next swing relaunches, so a held chop
    /// isn't a relentless machine-gun. This paces BOTH the strikes WITHIN a single log (ManualOpsBalancePlan E03
    /// discrete progress) AND, with <see cref="AutoRestock"/> on, the handover to the next auto-fed log — so it applies
    /// even with AutoRestock off (a single log still takes several paced strikes, per <see cref="ChopSpeedMultiplier"/>
    /// and the axe metal). 0 = no pause (back-to-back swings, the original feel). The sawhorse rip is ONE continuous
    /// hold with no per-swing relaunch, so this does not apply there. Clamped to [0, 10].</summary>
    public float ChopRestockDelaySeconds = 0.3f;

    // ── Flotsam (driftwood, broken by hand) ─────────────────────────────────────────────────────────────

    /// <summary>How long (seconds) you must hold right-click to snap a piece of beach driftwood
    /// (<c>game:flotsam-driftwood-*</c>) apart into firewood and sticks — either with the ITEM in hand or empty-handed on
    /// a placed block. The in-world replacement for the removed chopping-block/crafting path (ManualOpsBalancePlan E07:
    /// driftwood is no longer chopped on the block). A single continuous hold, like breaking a block by mining;
    /// releasing early resets the progress. ONE duration for ALL sizes (only the drop COUNTS differ by size, below) —
    /// deliberately, so the break is tied to a single gesture.
    ///
    /// Default is TIED to the <c>driftwood-break</c> animation so the piece snaps apart exactly on the animation's last
    /// frame: 21 frames at <c>animationSpeed 0.6</c>, and VS advances shape animations at 30·speed fps
    /// (RunningAnimation.cs), so the last frame (index <c>QuantityFrames-1</c> = 20) lands at 20 / (30·0.6) ≈ 1.111 s.
    /// Re-derive this default if the animation's frame count or speed changes. Clamped to [0.1, 30]s.</summary>
    public float FlotsamBreakSeconds = 20f / 18f; // = (21-1) / (30 * 0.6) ≈ 1.111s — the driftwood-break animation's length

    /// <summary>Firewood dropped when a LARGE piece of driftwood (<c>flotsam-driftwood-3</c>, the ~72s-burn variant) is
    /// broken by hand. See <see cref="FlotsamSticksLarge"/> for its sticks.</summary>
    public int FlotsamFirewoodLarge = 2;

    /// <summary>Sticks dropped when a LARGE piece of driftwood (<c>flotsam-driftwood-3</c>) is broken by hand.</summary>
    public int FlotsamSticksLarge = 2;

    /// <summary>Firewood dropped when a NORMAL piece of driftwood (<c>flotsam-driftwood-1</c>/<c>-2</c>, the two ~24s-burn
    /// variants) is broken by hand.</summary>
    public int FlotsamFirewoodNormal = 1;

    /// <summary>Sticks dropped when a NORMAL piece of driftwood (<c>flotsam-driftwood-1</c>/<c>-2</c>) is broken by hand.</summary>
    public int FlotsamSticksNormal = 1;

    /// <summary>Firewood dropped when a SMALL piece of driftwood (<c>flotsam-driftwood-4</c>, the ~10s-burn variant) is
    /// broken by hand. Default 0 — a small chunk gives only sticks.</summary>
    public int FlotsamFirewoodSmall = 0;

    /// <summary>Sticks dropped when a SMALL piece of driftwood (<c>flotsam-driftwood-4</c>) is broken by hand.</summary>
    public int FlotsamSticksSmall = 2;

    // ── Sawing (sawhorses + pit saw) ────────────────────────────────────────────────────────────────────

    /// <summary>Total planks produced from one log ripped lengthways on the sawhorses (input
    /// <c>log-placed-{wood}</c> → <c>plank-{wood}</c>). Spread across the 4 sawing stages
    /// (per stage ≈ this / 4). Must be ≥ 1. Also the yield for a log SECTION
    /// (<c>logsection-placed-{wood}</c>, redwood; ManualOpsBalancePlan E08): vanilla rips a section and a whole log to
    /// the same 12 planks, so the mod keeps them equal here rather than adding a separate knob.</summary>
    public int PlanksPerLog = 24;

    /// <summary>Total support beams produced from one debarked log ripped on the sawhorses (input
    /// <c>debarkedlog-{wood}</c> → <c>supportbeam-{wood}</c>). Spread across the 4 sawing stages
    /// (per stage ≈ this / 4). Must be ≥ 1.</summary>
    public int BeamsPerDebarkedLog = 6;

    /// <summary>How long (seconds) the pit saw must be held to fully rip a log on the sawhorses — the sum of
    /// all stages. Progress is a single scalar that accumulates across separate right-click holds. Clamped
    /// to [0.1, 30]. The number of stages is always 4 (a code constant, not configurable), with the batches
    /// firing at the 20-model transitions 5→6, 14→15, 19→20, 20→∅ rather than at equal time slices. With
    /// <see cref="ScaleWorkSpeedByToolTier"/> on (the default) this is the time with the BEST (steel) saw; a lesser
    /// metal rips proportionally slower, automatically from the tool — there is no per-metal knob.</summary>
    public float SawSeconds = 6f;

    /// <summary>How many separate item entities each stage's batch of PLANKS is split across when it drops
    /// (support beams use <see cref="BeamDropCount"/>). The over-cap (never more piles than items in that batch)
    /// can't be applied here because the per-stage amount is total/4 and the output item isn't loaded at config
    /// time — it's enforced at saw time. Must be ≥ 1.</summary>
    public int PlankDropCount = 4;

    /// <summary>How many separate item entities each stage's batch of SUPPORT BEAMS is split across when it drops
    /// (planks use <see cref="PlankDropCount"/>; beams default to fewer, larger piles). Same over-cap caveat as
    /// <see cref="PlankDropCount"/> — enforced at saw time, not here. Must be ≥ 1.</summary>
    public int BeamDropCount = 2;

    /// <summary>Pit-saw durability spent per completed sawing section/stage (4 stages per log = 4× this over a
    /// full rip). 0 = the saw takes no durability at all.</summary>
    public int SawDurabilityPerStage = 1;

    /// <summary>Sawing-SPEED multiplier for an ordinary saw relative to the pit saw, when
    /// <see cref="AllowVanillaSaw"/> is on (it has no effect while that flag is off, since an ordinary saw makes no
    /// progress at all then). 1.0 = the same speed as the pit saw; the default 0.5 = half speed, so an ordinary
    /// saw takes twice as long to rip the same log (the pit saw stays the better tool). The pit saw itself is
    /// always ×1.0. Applied to the progress accrued per tick, so it scales only the TIME — the yield, stages and
    /// durability cost per log are unchanged. Clamped to [0.05, 1.0] (≤ 1 keeps it "no better than the pit saw";
    /// the floor stops a 0 from making an allowed saw silently never finish).</summary>
    public float VanillaSawSpeedMultiplier = 0.5f;

    /// <summary>Pause (seconds) the sawhorses wait before auto-restocking the NEXT log after one is fully ripped,
    /// while a rip is held — the saw's counterpart to <see cref="ChopRestockDelaySeconds"/>. With
    /// <see cref="AutoRestock"/> on, a held rip normally flows straight onto the next log; this inserts a short
    /// breather with the pair sitting visibly EMPTY (rip → pause → place → rip) so it isn't a seamless snap. Only
    /// meaningful with <see cref="AutoRestock"/> on; 0 = no pause (the next log is laid the instant the previous one
    /// finishes — the original seamless feel). The sawhorse rip is ONE continuous hold, so unlike the chopping block
    /// there is no per-swing relaunch — this only paces the log-to-log handover. Clamped to [0, 10].</summary>
    public float SawRestockDelaySeconds = 0.3f;

    // ── Debarking (sawhorse side action) ───────────────────────────────────────────────────────────────
    // Stripping bark is the SIDE counterpart to the end-gated rip on the SAME sawhorses (BlockEntitySawhorse.
    // Debarking.cs): stand at a long side of a laid log, hold RMB with a debarking tool, and log-placed-{wood}
    // becomes debarkedlog-{wood} — the in-world echo of the vanilla axe+hammer grid recipe (which drops no bark
    // either). Three TIERS by tool TYPE, differing only in SPEED: knife (starter, slow) → axe+hammer (copper,
    // medium) → bark spud (a dedicated crafted tool, any tool metal copper…steel, fast — DebarkingPlan E02). Speeds
    // mirror the saw's SawSeconds / VanillaSawSpeedMultiplier shape, with the BEST tool (the spud) as the 1.0 reference.
    // DebarkSeconds (the strip DURATION, the spud reference) was added by the strip-cycle task (DebarkingPlan T01.06),
    // which needs the target duration, and DebarkDurabilityPerLog by the wear loop (T01.07); T01.08 then added the full
    // Sanitize clamps (below) and the JSON "// Debarking" section header. AllowKnifeDebark + the per-tier speed
    // multipliers are the gate's toggle/speeds — see BlockEntitySawhorse.Debarking.cs BarkToolIsEffective / BarkSpeedFor.

    /// <summary>How long (seconds) the BEST debarking tool — the bark spud, the 1.0 speed reference — must be held at a
    /// long side of a laid log to strip its bark fully (<c>log-placed-{wood}</c> → <c>debarkedlog-{wood}</c>). The
    /// slower tiers scale off this: an axe+hammer at <see cref="AxeHammerDebarkSpeedMultiplier"/> (~5s at the default),
    /// a knife at <see cref="KnifeDebarkSpeedMultiplier"/> (~10s). Progress is a single scalar that accumulates across
    /// separate right-click holds (and across co-op sides). Clamped to [0.1, 30] (T01.08, like <see cref="SawSeconds"/>);
    /// the field itself lives here because the strip cycle (T01.06) needs the target duration. With
    /// <see cref="ScaleWorkSpeedByToolTier"/> on (the default) the spud reference is specifically the STEEL spud; a
    /// lesser-metal spud strips proportionally slower, automatically from the tool (the knife and axe+hammer tiers stay
    /// flat — their metal never matters). There is no per-metal knob.</summary>
    public float DebarkSeconds = 1.5f;

    /// <summary>When true (the default), a KNIFE is the tier-1 starter debarking tool on the sawhorses: held at a
    /// long side of a laid log it strips bark, slowly (<see cref="KnifeDebarkSpeedMultiplier"/>). "Knife" is
    /// mod-independent — any tool the game classes as a knife (<c>tool: "knife"</c>). Set false to remove the knife
    /// tier for a harder progression (then bark comes off only with an axe+hammer or a bark spud). The tiers above
    /// the knife — axe+hammer and the bark spud — are unaffected by this flag.</summary>
    public bool AllowKnifeDebark = true;

    /// <summary>Debarking-SPEED multiplier for a tier-1 KNIFE relative to the bark spud (the 1.0 reference): the
    /// default 0.15 = 0.15× the spud's speed, so with <c>DebarkSeconds</c> at its default 1.5s a knife takes ~10s to
    /// strip a log. Applied to the progress accrued per tick, so it scales only the TIME. Speed is keyed by tool
    /// TYPE, not metal. Only used when <see cref="AllowKnifeDebark"/> admits the knife. Clamped to [0.05, 1] (T01.08) —
    /// the spud-relative range, like <see cref="VanillaSawSpeedMultiplier"/>.</summary>
    public float KnifeDebarkSpeedMultiplier = 0.15f;

    /// <summary>Debarking-SPEED multiplier for the tier-2 AXE+HAMMER relative to the bark spud (the 1.0 reference):
    /// the default 0.3 = 0.3× the spud's speed, so with <c>DebarkSeconds</c> at its default 1.5s an axe+hammer takes
    /// ~5s to strip a log (faster than the knife, slower than the spud). Applied to the progress accrued per tick, so
    /// it scales only the TIME. Speed is keyed by tool TYPE, not metal. Clamped to [0.05, 1] (T01.08) — the
    /// spud-relative range, like <see cref="VanillaSawSpeedMultiplier"/>.</summary>
    public float AxeHammerDebarkSpeedMultiplier = 0.3f;

    /// <summary>Durability spent on each debarking tool per fully-stripped log — the debark counterpart to the saw's
    /// <see cref="SawDurabilityPerStage"/>. Charged once per completed strip to every tool that makes up the tier: the
    /// tier-2 axe+hammer loses this from BOTH the axe and the hammer; the single-tool tiers (knife, bark spud) from the
    /// one tool. In a co-op strip every active side's tools wear. 0 = the tools take no durability at all. The field
    /// lives here, with the strip cycle that first wears the tools (T01.07), rather than in the T01.08 config-section
    /// task — the same early-placement as <see cref="DebarkSeconds"/>. Its Sanitize clamp (≥ 0) landed in T01.08 with the
    /// rest of the Debarking clamps; the wear loop independently treats any value ≤ 0 as "no wear".</summary>
    public int DebarkDurabilityPerLog = 1;

    /// <summary>Pause (seconds) the sawhorses wait before auto-restocking the NEXT barked log after one is fully
    /// DEBARKED, while a strip is held — the debark counterpart to <see cref="SawRestockDelaySeconds"/>. When a strip
    /// finishes, the bare <c>debarkedlog</c> drops out beside the construction (the same way the rip drops its planks)
    /// and, with <see cref="AutoRestock"/> on, the next barked <c>log-placed</c> is pulled from the debarker's pack;
    /// this inserts a short breather with the pair sitting visibly EMPTY (strip → pause → place → strip) so it isn't a
    /// seamless snap. Only meaningful with <see cref="AutoRestock"/> on; 0 = no pause (the next log is laid the instant
    /// the previous one is stripped — the seamless feel). Like the rip, a strip is ONE continuous hold, so this only
    /// paces the log-to-log handover. Clamped to [0, 10].</summary>
    public float DebarkRestockDelaySeconds = 0.3f;

    // ── Automated sawmill (frame saw on a mechanical axle) ─────────────────────────────────────────────
    // Tech-tier upgrade over the hand sawhorses: a mechanical-power multiblock that rips the SAME inputs to
    // the SAME outputs (log→plank, debarked log→support beam), reusing PlanksPerLog/BeamsPerDebarkedLog above
    // — but UNATTENDED and emitting the whole batch in one pass instead of 4 hand stages. The cut pace is set
    // ENTIRELY by the drive RPM; SawmillSpeed scales log throughput (work per log = SawmillDefaultWork / speed)
    // (SpeedDecouplingPlan). These fields only describe the DRIVE (torque/network load), the blade kit and its wear.
    // The output amounts are deliberately NOT duplicated
    // here so the two tiers can never balance-drift apart.

    /// <summary>The baked-in reference WORK per log for the automated sawmill: 50 shaft revolutions to rip one log at
    /// the default multiplier. It is the ratchet-tooth count and the carriage step count — the ratchet turns one tooth
    /// and the carriage takes one step per internal cycle, 50 cycles to a log — so at the shipped default all three are
    /// 50. It is the ONE source of that number (SpeedDecouplingPlan, F-L2-07): the procedural animation
    /// (<c>SawmillAnim</c>) reads THIS constant for its ratchet teeth and carriage-step count — T02.01 removed the old
    /// private <c>FeedCycles</c> literal that merely coincided at 50. The player scales the SPEED with
    /// <see cref="SawmillSpeed"/> (a log costs <c>50 / speed</c> revolutions, i.e. <c>50 ×</c> the internal work
    /// factor <c>1/speed</c>); the step COUNT stays 50, compressed into those revolutions by <see cref="SawmillWorkScale"/>.
    /// The wrap-period is <see cref="SawmillFwdWrapBound"/> = <c>2π × this / speed</c>, equal to <c>2π × this</c> only at
    /// speed=1 — not a flat multiple of this constant. A code constant, not config.</summary>
    public const int SawmillDefaultWork = 50;

    /// <summary>SPEED multiplier for the automated sawmill's log THROUGHPUT — higher = FASTER. Default 1.0 = the shipped
    /// pace. The mechanism's rotational SPEED still comes ENTIRELY from the network RPM (invariant I1); this scales how
    /// fast one log is PROCESSED at a given RPM, by scaling — inversely — how much shaft rotation a log costs: a log costs
    /// <c>SawmillDefaultWork / this</c> revolutions, so HIGHER = less work per log = a faster, coarser feed, and LOWER =
    /// more work = slower. (The internal WORK factor the rest of the code gears by is <c>1/this</c>; the public knob is the
    /// intuitive one — bigger number, faster machine.) The rectified mechanism (carriage, wheel, ratchet, crank) is geared
    /// by <see cref="SawmillWorkScale"/> = this, so the visible reciprocation tracks the log rate at any multiplier (memory
    /// <c>vs-sawmill-procedural-anim</c>) and the carriage↔ratchet drift (F1) closes by construction. Replaces the old
    /// absolute <c>SawmillStepsPerLog</c> AND the intermediate <c>SawmillWorkMultiplier</c> (inverted-direction work knob);
    /// neither saved key binds any more (mod in development, no key migration — AuditPlan DECISIONS #6) and each resets to
    /// this default (logged once on the first launch after the upgrade, before the folder is rewritten without the retired
    /// key). <see cref="Sanitize"/> band-clamps this into [<see cref="SawmillSpeedMin"/>,
    /// <see cref="SawmillSpeedMax"/>] and logs any correction. At 1.0 the machine behaves bit-for-bit as before
    /// the feature.</summary>
    public float SawmillSpeed = 1f;

    /// <summary>Plausibility band for <see cref="SawmillSpeed"/> (SpeedDecouplingPlan T01.03, widened
    /// 2026-09-15 on the author's call — freedom over smoothness). The band is now FUNCTIONAL-safety, not smoothness:
    /// past ~3× the fine 50-tooth ratchet visibly aliases against the shaft on a fast network and past ~1/3 it reads as a
    /// near-stall, but the author accepts that for the tuning range. The hard ceiling 100 keeps the derived wrap-bound
    /// <see cref="SawmillFwdWrapBound"/> = 2π·SawmillDefaultWork / this ≥ π &gt; 0 (so <c>FeedAdvance</c> never divides by
    /// ~0 and the render fold stays bounded); the floor 0.01 caps a log at 100× the baked work (5000 revolutions) before
    /// it is a pointless crawl. A non-positive / NaN value is garbage and resets to 1.0. <see cref="Sanitize"/> pins an
    /// out-of-band value to the nearer edge and logs it; the config file is rewritten, so the shift is never silent.</summary>
    public const float SawmillSpeedMin = 0.01f;
    /// <summary>Upper edge of the <see cref="SawmillSpeed"/> plausibility band; see
    /// <see cref="SawmillSpeedMin"/> for the reasoning.</summary>
    public const float SawmillSpeedMax = 100f;

    // The dev knob SawmillGearConnectionDiscs (whether the visible connection discs gear with the mechanism or hold 1:1
    // with the shaft) was BAKED OUT to false after the author's live judgment (SpeedDecouplingPlan T04.03, 2026-09-16):
    // the discs hold 1:1 with the shaft — the honest "the shaft turns the clutch, the reduction is inside" look, and the
    // rate that matches the connected vanilla gear. The client render (GetPartMatrices) now hardcodes that. The former
    // config key is dropped silently on the next save (never shipped; no [JsonExtensionData] to resurrect it).

    /// <summary>Minimum effective network speed (TrueSpeed = |network.Speed × GearedRatio|) below which the
    /// saw simply won't turn — a barely-spinning network can't drive it, regardless of which blade is installed.
    /// Clamped to ≥ 0 (0 = no minimum, runs at any non-zero speed).</summary>
    public float SawmillMinTorque = 0.05f;

    /// <summary>How much resistance/load the running sawmill adds to its mechanical network (returned from the
    /// MP behaviour's GetResistance). Default 0.085 mirrors vanilla's pulverizer (BEBehaviorPulverizer with an
    /// axle). Clamped to ≥ 0.</summary>
    public float SawmillResistance = 0.085f;

    /// <summary>Durability points the blade kit spends per completed log (AutomatedSawmillPlan T06.06). The kit carries
    /// NATIVE VS durability now (<c>durabilitybytype</c> in <c>sawmillblade.json</c>, per-metal = the pit-saw table,
    /// copper 500 … steel 4500), so this is just the per-log wear charge: 1 = one whole log costs 1 point (copper lasts
    /// 500 logs), 2 = wears twice as fast, <b>0 = blades never wear</b> (a permanent capital part). When the kit hits 0
    /// it breaks and the station stops until a new kit is installed (<see cref="BlockEntitySawmill"/>, E06). Integer
    /// durability has no sub-1 granularity: any rate &gt; 0 costs at least 1/log — to make a kit last LONGER raise its
    /// <c>durabilitybytype</c>, not this. Clamped to ≥ 0. (Default flipped 0→1 in T06.06: wear is now ON by default,
    /// matching the pit saw's <see cref="SawDurabilityPerStage"/>.)</summary>
    public float SawmillBladeWearMultiplier = 1f;

    // NOTE: the optional-flywheel multipliers (torque relief / network-load relief) and the sawdust-WAVE knobs
    // (spread / along / height / velocity / quantity / lifetime / feed-window) were dev TUNING dials, FINALIZED
    // in-game and BAKED into constants in the drive code (BlockEntitySawmill.Power.cs / BEBehaviorSawmillMP.cs)
    // and the cut effects (BlockEntitySawmill.Effects.cs) on 2026-06-21 — they are no longer player config (same
    // treatment as the saw-sound shaping knobs noted below).

    // NOTE: the completed-pass product is EJECTED forward out of the machine (pulverizer-style — never buffered in an
    // output slot), so there is no output-full pause and no opt-in ground-drop fallback to configure: the former
    // SawmillGroundFallback flag was removed when the output inventory was dropped.

    // NOTE: the saw-sound shaping knobs (speed bands / onset delay / volume-follow) were FINALIZED in-game and BAKED
    // into constants in BlockEntitySawmill.Effects.cs (2026-06-19) — they are no longer live config.

    // ── Automated chopper (axe on a cam-driven arm, mechanical axle) ────────────────────────────────────
    // Tech-tier upgrade over the hand chopping block: a mechanical-power multiblock that splits the SAME inputs to
    // the SAME outputs as the block (log → game:firewood, firewood → sticks), reusing
    // FirewoodPerLog/FirewoodDropCount + SticksPerFirewood/SticksDropCount above — but UNATTENDED, one axe strike per
    // lift-cycle. The chop pace is set ENTIRELY by the drive RPM; ChopperSpeed scales log throughput (work per
    // log = ChopperCycleCap / speed) (SpeedDecouplingPlan). These fields only describe the DRIVE (torque / network load) and the
    // head's wear. The output amounts are
    // deliberately NOT duplicated here so the block and the machine can never balance-drift apart. (The optional
    // flywheel was cut 2026-07-04, so its two torque/resistance multipliers are gone with it.)
    // The head's metal is a WEAR tier (native durabilitybytype in head.json), NOT a speed tier — pace comes from RPM.

    /// <summary>The baked-in cap on shaft revolutions per axe lift-cycle: 48 revolutions raise and drop the axe once at
    /// the default speed, and this is also the default WORK per log for one strike (SpeedDecouplingPlan). It is the SAME
    /// nominal as the modeled reduction gear train (<c>ChopperAnim.ReducerRevs</c>, which aliases this) — the "без
    /// ремодела" revision (2026-09-15) kept the ÷48 mechanism and gears only the animation clock, so the two are ONE
    /// constant (48 = LCM of the mover periods 1/4/16/48, so the raw accumulator's fold lands on whole gear revolutions).
    /// The parametric drive (E03/T03.03) reads it as the reference cycle length. A code constant, not config.</summary>
    public const int ChopperCycleCap = 48;

    /// <summary>SPEED multiplier for the automated chopper's log THROUGHPUT — higher = FASTER. Default 1.0 = one slow
    /// lift-and-slam per log (the "one majestic blow"). The chop SPEED still comes ENTIRELY from the network RPM
    /// (invariant I1); this scales how fast a log is felled at a given RPM, by scaling — inversely — the shaft work a log
    /// costs: a log costs <c>R = ChopperCycleCap / this</c> revolutions (= <c>48 ×</c> the internal work factor
    /// <c>1/this</c>), split into <see cref="ChopperStrikes"/> = <c>⌈1/this⌉</c> strikes of <see cref="ChopperCycleRevs"/> =
    /// R/N revolutions each (a lift-cycle never runs longer than the 48-rev cap — the excess becomes MORE strikes, not a
    /// longer one). So HIGHER = fewer/shorter strikes per log = a faster chop; the default and anything faster stay at one
    /// blow, only SLOWER-than-default settings add strikes (speed 0.5 → 2 blows, 0.25 → 4). Replaces the old absolute
    /// <c>ChopperStepsPerLog</c> AND the intermediate <c>ChopperWorkMultiplier</c> (inverted-direction work knob); neither
    /// saved key binds any more (mod in development, no key migration — AuditPlan DECISIONS #6) and each resets to this
    /// default (logged once on the first launch after the upgrade), which also retires the 2026-07-12 stopgap of 50.
    /// <see cref="Sanitize"/> band-clamps this into [<see cref="ChopperSpeedMin"/>,
    /// <see cref="ChopperSpeedMax"/>] and logs any correction. At 1.0 the chopper behaves bit-for-bit as before
    /// the feature.</summary>
    public float ChopperSpeed = 1f;

    /// <summary>Plausibility band for <see cref="ChopperSpeed"/> (SpeedDecouplingPlan T01.03, widened
    /// 2026-09-15 with the sawmill's on the author's call — freedom over smoothness). Unified with
    /// <see cref="SawmillSpeedMin"/>/<see cref="SawmillSpeedMax"/> at [0.01, 100]: the earlier
    /// per-machine split (chopper 1/4..4, sawmill 1/3..3) is gone. The band is now FUNCTIONAL-safety, not smoothness —
    /// past a few × the short lift-cycle visibly flickers, but that is accepted for the tuning range. The ceiling 100
    /// keeps a log from costing under half a revolution of nonsense; the floor 0.01 caps it at 100 strikes (100× the baked
    /// work). A non-positive / NaN value resets to 1.0. <see cref="Sanitize"/> pins an out-of-band value to the nearer
    /// edge and logs it.</summary>
    public const float ChopperSpeedMin = 0.01f;
    /// <summary>Upper edge of the <see cref="ChopperSpeed"/> plausibility band; see
    /// <see cref="ChopperSpeedMin"/> for the reasoning.</summary>
    public const float ChopperSpeedMax = 100f;

    // The dev knob ChopperGearConnectionDiscs (the chopper twin of the sawmill's) was BAKED OUT to false after the
    // author's live judgment (SpeedDecouplingPlan T04.03, 2026-09-16): the visible connection discs rotorDefault1/2/3
    // hold 1:1 with the shaft as the input coupling. The client render (GetPartMatrices) hardcodes that; the former
    // config key is dropped silently on the next save.

    /// <summary>Minimum effective network speed (TrueSpeed = |network.Speed × GearedRatio|) below which the chopper
    /// simply won't turn — a barely-spinning network can't drive it, regardless of which head is installed. Twin of
    /// <see cref="SawmillMinTorque"/>. Clamped to ≥ 0 (0 = no minimum, runs at any non-zero speed).</summary>
    public float ChopperMinTorque = 0.05f;

    /// <summary>How much resistance/load the running chopper adds to its mechanical network (returned from the MP
    /// behaviour's GetResistance). Default 0.085 mirrors vanilla's pulverizer and the automated sawmill
    /// (<see cref="SawmillResistance"/>). Clamped to ≥ 0.</summary>
    public float ChopperResistance = 0.085f;

    /// <summary>Durability points the axe head (<c>chopperhead-{metal}</c>) spends per completed log — the chopper's
    /// twin of <see cref="SawmillBladeWearMultiplier"/>. The head carries NATIVE VS durability
    /// (<c>durabilitybytype</c> in <c>head.json</c>, per-metal copper … steel), so this is just the per-log wear
    /// charge: 1 = one log costs 1 point, 2 = wears twice as fast, <b>0 = the head never wears</b> (a permanent
    /// capital part). When the head hits 0 it breaks and the station stops until a new head is installed
    /// (<c>BlockEntityChopper</c>, AutoChopperPlan E06). Integer durability has no sub-1 granularity: any rate &gt; 0
    /// costs at least 1/log — to make a head last LONGER raise its <c>durabilitybytype</c>, not this. Clamped to [0,
    /// 100].</summary>
    public float ChopperHeadWearMultiplier = 1f;

    // ── Derived speed constants (SpeedDecouplingPlan T01.02) ─────────────────────────────────────────────
    // Precompute for the machine animations + feed, filled ONCE at the end of Sanitize from the two *Speed
    // fields. The public knob is SPEED (higher = faster); internally every derived value below is computed from the WORK
    // factor = 1/speed, so all the render/anim/feed math (and the "M" in their comments) keeps its original work-factor
    // form — only the boundary flips. The tick and render read these STRAIGHT — no ceil/division from the multiplier in
    // any hot path (invariant I5). [JsonIgnore] so they neither serialize to the config folder nor ride the sync packet:
    // the client rebuilds them by running the SAME Sanitize on the config it receives (invariant I9), so both sides hold
    // identical values. Do NOT assign these anywhere but Sanitize, and do NOT read a *Speed directly in a hot
    // path — read the derived value here. The initializers give the speed=1 default so an un-sanitized object is still sane.

    /// <summary>Pilorama gearing factor = <see cref="SawmillSpeed"/> (equivalently 1/(work factor), work =
    /// 1/speed): the rectified mechanism reads <c>animAngleFwd × this</c> so it runs at <c>speed×</c> the shaft (speed=1 →
    /// 1.0, the bit-identical regress). The render/anim comments call the internal work factor "M", so there this reads as
    /// <c>1/M</c> — the same number. Consumed by the render in E02.</summary>
    [JsonIgnore]
    public float SawmillWorkScale = 1f;

    /// <summary>Wrap period of the pilorama's rectified clock, <c>2π × SawmillDefaultWork / SawmillSpeed</c> (=
    /// 2π·SawmillDefaultWork × the work factor = 2π·R). The forward accumulator folds here, so
    /// <c>animAngleFwd × SawmillWorkScale</c> lands on exactly one whole ratchet revolution at the fold (invisible, no
    /// float drift). It is ALSO the FeedAdvance divisor: one pass is R = SawmillDefaultWork/speed revolutions of work.
    /// speed=1 → 2π·50, the pre-feature bound. The band ceiling (speed ≤ 100) keeps this ≥ π &gt; 0.</summary>
    [JsonIgnore]
    public float SawmillFwdWrapBound = GameMath.TWOPI * SawmillDefaultWork;

    /// <summary>Drovokol strikes per log, N = <c>⌈1/<see cref="ChopperSpeed"/>⌉</c> = ⌈work factor⌉ (speed=1 →
    /// 1; slower speeds add strikes, speed 0.5 → 2). Each strike advances chopProgress by
    /// <see cref="ChopperSplitPerStrike"/> and splits one stage. Consumed by the server strike detector in E03.</summary>
    [JsonIgnore]
    public int ChopperStrikes = 1;

    /// <summary>Revolutions per lift-cycle for the drovokol's parametric drive, R/N = <c>ChopperCycleCap × (work factor) /
    /// ChopperStrikes</c> = <c>ChopperCycleCap / (ChopperSpeed × ChopperStrikes)</c> (≤ 48 by construction;
    /// speed=1 → 48). The lift-cycle length the E03 geared clock folds on: the raw accumulator wraps at
    /// <c>ChopperCycleRevs·2π</c> on BOTH sides (render + server), and the geared read
    /// <c>animAngle × <see cref="ChopperWorkScale"/></c> then lands on exactly one whole arm cycle at the fold.</summary>
    [JsonIgnore]
    public float ChopperCycleRevs = ChopperCycleCap;

    /// <summary>Split fraction per drovokol strike, 1/<see cref="ChopperStrikes"/> (speed=1 → 1.0, one blow per log). The
    /// server strike detector adds this to chopProgress on each strike.</summary>
    [JsonIgnore]
    public float ChopperSplitPerStrike = 1f;

    /// <summary>Drovokol gearing factor = <see cref="ChopperCycleCap"/> / <see cref="ChopperCycleRevs"/> = N/M (work factor
    /// M = 1/speed): the rectified mechanism (gear train / pawls / axe beam, <c>ChopperDriver.Src="fwd"</c>) reads
    /// <c>animAngle × this</c> so the geared clock completes exactly one full arm lift-cycle per
    /// <see cref="ChopperCycleRevs"/> shaft revolutions — the twin of <see cref="SawmillWorkScale"/> (E03/T03.03). speed=1
    /// → 48/48 = 1.0 exact (bit-identical regress, invariant I8). Consumed by the render and the server strike detector
    /// (E03).</summary>
    [JsonIgnore]
    public float ChopperWorkScale = 1f;

    // ── Automated-station drop overrides (ImprovementsPlan E04/T04.04) ───────────────────────────────────
    // Per-station YIELD knobs for the POWERED tiers (automated chopper + automated sawmill), so a server can tune
    // what the machines drop SEPARATELY from the hand stations they mirror — without reopening the "amounts are
    // shared so the tiers can never balance-drift" promise for anyone who leaves them alone. Each defaults to the
    // sentinel -1 = "inherit the manual station's value": OUT OF THE BOX every powered tier drops EXACTLY what its
    // hand tier does (identical balance), and the two sets of numbers physically cannot diverge until a player
    // deliberately writes one in. Set a field to 0+ to give that one machine its own amount. Read ONLY through the
    // *Effective accessors below — the bare field is the serialized/synced storage, the accessor resolves the
    // inherit (field < 0 -> the manual field, else the field). The accessors carry [JsonIgnore] so they stay out of
    // BOTH the on-disk config files and the JSON the sync packet carries (they are the same serializer since T02.05):
    // only the bare -1 field travels the wire, and each app-side computes Effective locally off the synced value. On disk these live in drops.json beside their manual counterparts so the
    // inherit relationship reads at a glance (ImmersiveWoodworkingModSystem.ConfigGroups). The station read sites are
    // repointed to the *Effective accessors in T04.05; the Sanitize clamps (preserve -1, floor an override at its
    // manual field's own minimum) land in T04.06. Chopper mirrors FirewoodPerLog/FirewoodDropCount/SticksPerFirewood/
    // SticksDropCount; sawmill mirrors PlanksPerLog/PlankDropCount/BeamsPerDebarkedLog/BeamDropCount (the sawmill DOES
    // read the drop-count fields — it splits its pass into piles in BlockEntitySawmill.Sawing.EjectBatch, so none of
    // these is a dead key). The hand chopping block and sawhorses are untouched; they keep reading the manual fields.

    /// <summary>Automated CHOPPER override for the total FIREWOOD produced per log. -1 (the default) = inherit
    /// <see cref="FirewoodPerLog"/>, so the powered chopper drops exactly what the hand block does; set 0+ to give the
    /// chopper its own firewood yield. Affects ONLY the automated chopper, never the hand chopping block. Read via
    /// <see cref="ChopperFirewoodPerLogEffective"/>.</summary>
    public int ChopperFirewoodPerLog = -1;

    [JsonIgnore]
    public int ChopperFirewoodPerLogEffective => ChopperFirewoodPerLog < 0 ? FirewoodPerLog : ChopperFirewoodPerLog;

    /// <summary>Automated CHOPPER override for how many separate item piles the firewood is split across. -1 (the
    /// default) = inherit <see cref="FirewoodDropCount"/>; set 0+ for the chopper's own pile count. Visual pile count
    /// only — it never changes the total amount (that is <see cref="ChopperFirewoodPerLog"/>). Automated chopper only.
    /// Read via <see cref="ChopperFirewoodDropCountEffective"/>.</summary>
    public int ChopperFirewoodDropCount = -1;

    [JsonIgnore]
    public int ChopperFirewoodDropCountEffective => ChopperFirewoodDropCount < 0 ? FirewoodDropCount : ChopperFirewoodDropCount;

    /// <summary>Automated CHOPPER override for the total STICKS produced per firewood (the firewood -> sticks step). -1
    /// (the default) = inherit <see cref="SticksPerFirewood"/>; set 0+ for the chopper's own stick yield. Automated
    /// chopper only. Read via <see cref="ChopperSticksPerFirewoodEffective"/>.</summary>
    public int ChopperSticksPerFirewood = -1;

    [JsonIgnore]
    public int ChopperSticksPerFirewoodEffective => ChopperSticksPerFirewood < 0 ? SticksPerFirewood : ChopperSticksPerFirewood;

    /// <summary>Automated CHOPPER override for how many separate item piles the sticks are split across. -1 (the
    /// default) = inherit <see cref="SticksDropCount"/>; set 0+ for the chopper's own pile count. Visual pile count only.
    /// Automated chopper only. Read via <see cref="ChopperSticksDropCountEffective"/>.</summary>
    public int ChopperSticksDropCount = -1;

    [JsonIgnore]
    public int ChopperSticksDropCountEffective => ChopperSticksDropCount < 0 ? SticksDropCount : ChopperSticksDropCount;

    /// <summary>Automated SAWMILL override for the total PLANKS produced per log. -1 (the default) = inherit
    /// <see cref="PlanksPerLog"/>, so the powered sawmill rips exactly what the hand sawhorses do; set 0+ for the
    /// sawmill's own plank yield. Affects ONLY the automated sawmill, never the hand sawhorses. Read via
    /// <see cref="SawmillPlanksPerLogEffective"/>.</summary>
    public int SawmillPlanksPerLog = -1;

    [JsonIgnore]
    public int SawmillPlanksPerLogEffective => SawmillPlanksPerLog < 0 ? PlanksPerLog : SawmillPlanksPerLog;

    /// <summary>Automated SAWMILL override for how many separate item piles the planks are split across. -1 (the
    /// default) = inherit <see cref="PlankDropCount"/>; set 0+ for the sawmill's own pile count. The sawmill DOES read
    /// this — it splits its pass into piles in <c>BlockEntitySawmill.Sawing.EjectBatch</c> (not a dead key). Visual pile
    /// count only. Automated sawmill only. Read via <see cref="SawmillPlankDropCountEffective"/>.</summary>
    public int SawmillPlankDropCount = -1;

    [JsonIgnore]
    public int SawmillPlankDropCountEffective => SawmillPlankDropCount < 0 ? PlankDropCount : SawmillPlankDropCount;

    /// <summary>Automated SAWMILL override for the total SUPPORT BEAMS produced per debarked log. -1 (the default) =
    /// inherit <see cref="BeamsPerDebarkedLog"/>; set 0+ for the sawmill's own beam yield. Affects ONLY the automated
    /// sawmill, never the hand sawhorses. Read via <see cref="SawmillBeamsPerDebarkedLogEffective"/>.</summary>
    public int SawmillBeamsPerDebarkedLog = -1;

    [JsonIgnore]
    public int SawmillBeamsPerDebarkedLogEffective => SawmillBeamsPerDebarkedLog < 0 ? BeamsPerDebarkedLog : SawmillBeamsPerDebarkedLog;

    /// <summary>Automated SAWMILL override for how many separate item piles the support beams are split across. -1 (the
    /// default) = inherit <see cref="BeamDropCount"/>; set 0+ for the sawmill's own pile count. The sawmill DOES read
    /// this (same <c>EjectBatch</c> pile split as planks — not a dead key). Visual pile count only. Automated sawmill
    /// only. Read via <see cref="SawmillBeamDropCountEffective"/>.</summary>
    public int SawmillBeamDropCount = -1;

    [JsonIgnore]
    public int SawmillBeamDropCountEffective => SawmillBeamDropCount < 0 ? BeamDropCount : SawmillBeamDropCount;

    // ── Sharpness (Toolsmith integration) ─────────────────────────────────────────────────────────────
    // Cross-cutting knob that only does anything with the Toolsmith mod installed: a tinkered pit saw / bark spud
    // carries a sharpness edge that dulls with use, and IW scales the rip (BlockEntitySawhorse.HeldLog.cs
    // SawSpeedFor) and strip (.Debarking.cs BarkSpeedFor) speed by it — linearly, sharp = full speed, blunt = the
    // floor below. WITHOUT Toolsmith the tools carry no sharpness attributes, so the factor is always 1.0 and this
    // field is inert. The axe+hammer, knife and ordinary saw are deliberately NOT sharpness-scaled (user decision).

    /// <summary>Speed multiplier a FULLY BLUNT Toolsmith-tinkered pit saw / bark spud rips or strips at, relative to a
    /// fully sharp one (1.0). The default 0.5 = a blunt tool runs at half speed; sharpness scales the speed LINEARLY
    /// between this floor (0% sharp) and 1.0 (100% sharp). Set to 1.0 to make sharpness NOT affect speed at all (the
    /// tool always runs full speed regardless of edge). Only the pit saw (rip) and bark spud (strip) are affected — the
    /// axe+hammer, knife and ordinary saw never are. Has NO effect without the Toolsmith mod: the tools then carry no
    /// sharpness attributes and always run at 1.0 (read raw, no Toolsmith.dll dependency). Clamped to [0.05, 1] (a
    /// 0/negative would freeze a blunt tool at ~0 progress; > 1 — "faster than sharp" — is nonsensical for the linear
    /// floor…1 model).</summary>
    public float SharpnessSpeedFloor = 0.5f;

    /// <summary>Speed multiplier a RAZOR-SHARP (100%) Toolsmith-tinkered axe CHOPS at, relative to a mid-sharpness one.
    /// The chopping sharpness curve is a PLATEAU: the whole 35–65% band chops at exactly 1.0 (the tool's plain metal-tier
    /// speed = vanilla feel), ABOVE 65% it rises linearly toward this ceiling, and BELOW 35% it falls toward
    /// <see cref="SharpnessSpeedFloor"/>. Default 1.4 = a freshly honed axe chops a touch faster than vanilla (a sharp
    /// copper axe drops a whole log from ~3 strikes to ~2). This is the ONE place a tool may beat its metal-tier baseline;
    /// a super-sharp copper still can't out-chop a STEEL axe on a whole log (steel already one-shots it). Only the CHOPPING
    /// axe uses this curve — the pit saw / bark spud keep the plain <see cref="SharpnessSpeedFloor"/>…1.0 model. No effect
    /// without Toolsmith (no sharpness attributes → factor 1.0). Set to 1.0 to disable the sharp-side bonus (sharpness then
    /// only ever penalises a blunt edge, never speeds a sharp one). Clamped to [1, 5].</summary>
    public float ChopSharpnessCeiling = 1.4f;

    // ── xSkills integration (forestry skill, optional soft-dependency) ───────────────────────────────────
    // Reflection-only bridge to the xSkills fork (no DLL reference — see XSkillsCompat.cs, gated on
    // IsModEnabled). With xSkills installed, the three MANUAL operations grant forestry XP and four abilities the
    // mod registers INTO the existing forestry skill act on them: sawyer (+plank/beam yield), splitter (+firewood),
    // carefulsawyer (−saw/spud wear), swiftsaw (chop/rip speed). Without xSkills every field here is inert (the
    // facade reports "off"). IMPORTANT: the per-tier ABILITY VALUES live in the auto-generated
    // ModConfig/XLeveling/forestry.json (xSkills owns that file), NOT here — these are only the master enable, the
    // Tier-1 XP rates, the per-ability apply toggles and the swiftsaw speed caps. The automated sawmill / chopper
    // are NOT covered (no player owner at yield time — out of scope).

    /// <summary>Master switch for the whole xSkills integration. When false the mod behaves exactly as if xSkills
    /// were absent even when it is installed: no forestry XP is granted, no abilities are registered, and no
    /// yield/wear/speed bonus is applied — every other field in this section is then moot. Default true. Independent
    /// of whether xSkills is actually present: the integration ALSO self-disables (silently) whenever the xSkills
    /// fork is absent or its reflection surface can't be resolved (see <c>XSkillsCompat</c>). Plain bool — nothing
    /// to clamp.</summary>
    public bool XSkillsIntegration = true;

    // The four XP rates below were DOUBLED on 2026-10-20 at the author's request (0.075 / 0.15 / 0.1 / 0.025 before).
    // Their ratios are unchanged. The new defaults reach only a config folder written from now on: an existing
    // integration.json keeps the numbers it already holds, because the folder is rewritten from the values it was read
    // with, not from the initializers (the mod does not migrate defaults, AuditPlan DECISIONS #6).

    /// <summary>Forestry XP granted to each active sawyer per completed rip SECTION on the sawhorses (4 sections per
    /// log, so ×4 ≈ this per whole log). Default 0.15 → ~0.6 XP/log. Granted server-side only (a client-side grant is a
    /// no-op). Floored at 0 in <see cref="Sanitize"/> (0 = ripping grants no XP; a negative would drain the skill).
    /// Inert without xSkills.</summary>
    public float XSkillsSawXpPerSection = 0.15f;

    /// <summary>Forestry XP granted to each active debarker per fully-stripped log on the sawhorses. Default 0.3 (a
    /// strip ≈ two rip sections' worth), so debarking a log advances forestry a little faster than one rip stage.
    /// Server-side only. Floored at 0 in <see cref="Sanitize"/> (0 = debarking grants no XP). Inert without xSkills.</summary>
    public float XSkillsDebarkXpPerStrip = 0.3f;

    /// <summary>Forestry XP granted per chopped WORKPIECE on the chopping block. With <see cref="HalfLogChopStage"/> on
    /// (the default) a whole log → two half-logs pays this, and each half-log → firewood pays half of it
    /// (<c>BlockEntityChoppingBlock</c>), so a fully processed log ≈ 2× this — default 0.2 → ~0.4 XP/log. A maul that
    /// takes the full log straight to firewood pays the same 2×. With <see cref="HalfLogChopStage"/> off a log is ONE
    /// completion (straight to firewood), so it grants just 1× this per log. Server-side only. Floored at 0 in
    /// <see cref="Sanitize"/> (0 = chopping grants no XP). Inert without xSkills.</summary>
    public float XSkillsChopXpPerWorkpiece = 0.2f;

    /// <summary>
    /// Forestry XP granted for chopping FIREWOOD into sticks — the one chop that is NOT a step of the log chain, so it
    /// gets its own absolute rate instead of riding <see cref="XSkillsChopXpPerWorkpiece"/> (which the log and half-log
    /// steps scale off). Default 0.05, deliberately a quarter of the log rate: sticks are a low-value byproduct, and
    /// unlike a log the input is stockpileable and obtainable outside the chopping block, so this is the one grant that
    /// could be farmed in bulk. Keeping it a separate knob is exactly what lets a server zero it — <b>set 0 to restore
    /// the original no-XP-for-sticks rule</b> — without also flattening the log/half-log grants, and stops a raised
    /// log rate from silently raising the farm rate with it. Server-side only; floored at 0 in <see cref="Sanitize"/>
    /// (a negative would drain the skill). Inert without xSkills, and unreachable with
    /// <see cref="AllowFirewoodToSticks"/> off (that chop is refused outright). User decision 2026-07-31.
    /// </summary>
    public float XSkillsFirewoodChopXp = 0.05f;

    /// <summary>When true (the default), in a CO-OP operation (two players ripping or debarking the same log on a
    /// paired sawhorse) BOTH active participants receive the FULL per-operation XP rather than splitting one share —
    /// matching the mod's co-op-friendly stance elsewhere. Set false to award the XP to only the master side of the
    /// pair. No effect on solo operations. Plain bool — nothing to clamp.</summary>
    public bool XSkillsCoopXpFullToBoth = true;

    /// <summary>Apply toggle for the <c>sawyer</c> ability (+plank/beam yield when ripping on the sawhorses). When
    /// false the ability may still show and level in the forestry tree but grants no extra output here. Default true.
    /// Also gated by xSkills being present and the sawyer tier being &gt; 0. Plain bool — nothing to clamp.</summary>
    public bool XSkillsSawyerYield = true;

    /// <summary>Apply toggle for the <c>splitter</c> ability (+FIREWOOD yield when chopping on the block — sticks are
    /// never boosted, and the automated chopper is untouched). When false the ability grants no extra firewood.
    /// Default true. Also gated by xSkills being present and the splitter tier being &gt; 0. Plain bool — nothing to
    /// clamp.</summary>
    public bool XSkillsSplitterYield = true;

    /// <summary>Apply toggle for the <c>carefulsawyer</c> ability (a chance to spend NO durability). Gates BOTH wear
    /// hooks at once — the pit saw / ordinary saw when ripping AND the bark spud when debarking. When false both tools
    /// wear normally. Default true. Also gated by xSkills being present and the carefulsawyer tier being &gt; 0. Plain
    /// bool — nothing to clamp.</summary>
    public bool XSkillsCarefulSawyer = true;

    /// <summary>Apply toggle for the <c>swiftsaw</c> ability (faster hand CHOPPING and RIPPING). When false neither
    /// speeds up. Default true. Also gated by xSkills being present and the swiftsaw tier being &gt; 0, and hard-capped
    /// by <see cref="XSkillsSwiftsawMaxChopSpeedBonus"/> / <see cref="XSkillsSwiftsawMaxSawSpeedBonus"/>. Plain
    /// bool — nothing to clamp.</summary>
    public bool XSkillsSwiftsawSpeed = true;

    /// <summary>Hard cap on the <c>swiftsaw</c> ability's axe-chopping speed-up, as a FRACTION of the swing time:
    /// EffectiveChopSeconds = <see cref="ChopSeconds"/> × (1 − min(abilityBonus, this)). Default 0.35 = at most 35% faster
    /// (a 0.75s swing drops to ~0.49s), symmetric with <see cref="XSkillsSwiftsawMaxSawSpeedBonus"/> so chopping and
    /// rip-sawing share one ceiling and speed up by the same percentage. A separate hard floor in EffectiveChopSeconds keeps
    /// the swing at ≥ half the base regardless, so it can never reach 0s (which would divide-by-zero the chop animation).
    /// Clamped in <see cref="Sanitize"/> to [0, 0.5] (0.5 is that floor's limit; higher would just be clipped by it). 0 =
    /// swiftsaw never speeds chopping. Inert without xSkills / with the ability off.</summary>
    public float XSkillsSwiftsawMaxChopSpeedBonus = 0.35f;

    /// <summary>Hard cap on the <c>swiftsaw</c> ability's ripping-speed bonus — the rip TEMPO (<c>SawSpeedFor</c>) is
    /// scaled by at most ×(1 + this). Default 0.35 = at most ×1.35 (35% faster; a full rip drops from 6.0s to ~4.4s).
    /// Only the rip tempo is scaled, never the saw animation/sound (its loop bakes the sound into fixed frames —
    /// changing its speed would desync). Floored at 0 in <see cref="Sanitize"/> (0 = swiftsaw never speeds ripping; a
    /// negative would slow it). Inert without xSkills / with the ability off. The CHOP side shares the SAME ceiling via
    /// <see cref="XSkillsSwiftsawMaxChopSpeedBonus"/> (also 0.35, expressed as a fraction of the swing time), so chopping
    /// and ripping speed up together and by the same percentage even though one ability drives both.</summary>
    public float XSkillsSwiftsawMaxSawSpeedBonus = 0.35f;

    // ── Seraph Leveling ReClassed (optional soft-dependency) ────────────────────────────────────────────
    // Reflection-only bridge (no DLL reference, see SeraphLevelingCompat.cs). Seraph unlocks some class traits by
    // counting grid crafts; the rip replaces the grid plank recipe, so the sawhorses report their boards instead.

    /// <summary>When true (the default) and Seraph Leveling ReClassed is installed, the planks and support beams ripped
    /// on the sawhorses are reported to Seraph as crafted, the same way it counts the grid recipe the rip replaces. That
    /// is what lets the Carpenter trait (Sacred Classes' Woodsman, 120 boards) progress while
    /// <see cref="DisableVanillaPlankCrafting"/> is on. Each sawyer on the log is credited with the full batch as it
    /// drops, xSkills sawyer bonus included. The automated sawmill reports nothing: no player owns its output. Set false
    /// if a later Seraph version starts counting the sawhorses itself, so boards are not credited twice. Inert without
    /// Seraph. Plain bool.</summary>
    public bool SeraphLevelingIntegration = true;

    // ── Bark chain (BarkPlan/E08, T08.01) ───────────────────────────────────────────────────────────────
    // The bark-system numbers that lived in CODE (a C# constant until now), gathered here so a server can retune them
    // without a rebuild: how many bark a stripped log drops, globally and per species (E02), the debark tool-tier bonus to the special-bark roll (E02/T02.05), the birch-tar rig's pot
    // capacity + per-birch fill (E04/T04.09) and its opening yield (E04/T04.05), and the bark drying time (E01/T01.04 —
    // applied by a runtime rewrite in AssetsFinalize, T08.02, since it lives in the item asset). NOT here (a deliberate
    // scope line, T08.01 user decision "код-константы + сушка"): the tannin barrel litres/seal-hours and the poultice/
    // decoction numbers stay in their own editable JSON assets (recipes/barrel/*, recipes/cooking/barkbrew.json,
    // recipes/grid/poultice-*.json) — a server edits those directly; the quern grind ratio is fixed 1:1 by the engine.

    /// <summary>How many bark items a fully-stripped log drops on the sawhorses (BarkPlan/E02). Default 3. The KIND of
    /// bark is still rolled ONCE per log from <c>config/barkdrops.json</c> (biased by the debark tool tier), then dropped
    /// as a single stack of this size — so raising it multiplies bark yield WITHOUT touching the odds of which kind a log
    /// gives, and every bark off one log is that one rolled kind. Applies to every species whose
    /// <see cref="BarkPerLogBySpecies"/> line is -1 or absent. Floored at 1 in <see cref="Sanitize"/> like the other
    /// per-log yields (0 would silently starve the whole bark chain); a value above the bark item's max stack size just
    /// splits across stacks on pickup. Read through <see cref="BarkPerLogFor"/>.</summary>
    public int BarkPerLog = 3;

    /// <summary>Per-species override of <see cref="BarkPerLog"/>: wood species code (the short code the game uses:
    /// <c>oak</c>, <c>birch</c>, <c>willow</c>) → how many bark a fully-stripped log of it drops. -1 = inherit
    /// <see cref="BarkPerLog"/>, the sentinel the automated drop overrides use; a species with no line (every foreign wood)
    /// inherits too. It replaces the <c>countBySpecies</c> map of <c>config/barkdrops.json</c>, which listed every vanilla
    /// species at an explicit 3: that line beat the global value, so a server that set <see cref="BarkPerLog"/> to 16
    /// still got 3 bark off oak (ModDB reports, moved here 2026-10-20).
    ///
    /// <para>The shipped species sit at -1 and not at a number ON PURPOSE. The folder loader's
    /// <c>JsonConvert.PopulateObject</c> and the client's <c>DeserializeObject</c> both MERGE the file into this
    /// initializer instead of replacing it (checked on the game's Newtonsoft 2026-10-20), so a line an admin deletes
    /// comes back on the next write at its default: at -1 that changes nothing, at 3 it would rebuild the same trap.
    /// Keys are case-insensitive; <see cref="Sanitize"/> lowercases them and maps each value like an automated override
    /// (-1 stays, any other negative → -1, 0 → 1). No upper cap, like <see cref="BarkPerLog"/>.</para></summary>
    public Dictionary<string, int> BarkPerLogBySpecies = DefaultBarkPerLogBySpecies();

    /// <summary>How many bark a fully-stripped log of <paramref name="species"/> drops: its
    /// <see cref="BarkPerLogBySpecies"/> line when that is ≥ 1, else <see cref="BarkPerLog"/>. Always ≥ 1 once
    /// <see cref="Sanitize"/> has run. Read by <see cref="ImmersiveWoodworking.BlockEntitySawhorse.DropRolledBark"/>.</summary>
    public int BarkPerLogFor(string? species)
    {
        if (species != null && BarkPerLogBySpecies != null
            && BarkPerLogBySpecies.TryGetValue(species, out int count) && count >= 1) return count;
        return BarkPerLog;
    }

    /// <summary>Special-bark chance multiplier when a log is stripped with a tier-1 KNIFE (BarkPlan/E02, T02.05): the
    /// careless starter tool, so it SCALES DOWN the odds a stripped log yields a SPECIAL bark (the odds in
    /// <c>config/barkdrops.json</c>). Default 0.5 = half the table's special chance; the remainder becomes ordinary
    /// <c>bark-generic</c>. It scales EXISTING special chances only, so a species with no special entry (or oak's
    /// certain 100% tan) is unaffected. Clamped to [0, 1] — a knife never beats the spud reference (1.0), a negative is
    /// nonsense. Read live by <see cref="ImmersiveWoodworking.BlockEntitySawhorse.BarkChanceMultFor"/>.</summary>
    public float BarkChanceMultKnife = 0.5f;

    /// <summary>Special-bark chance multiplier for the tier-2 AXE+HAMMER strip (T02.05): below the spud reference.
    /// Default 0.75. Same [0, 1] clamp and same "scales existing special chances only" rule as
    /// <see cref="BarkChanceMultKnife"/>.</summary>
    public float BarkChanceMultAxeHammer = 0.75f;

    /// <summary>Special-bark chance multiplier for the tier-3 BARK SPUD (T02.05) — the REFERENCE tool: a copper spud
    /// rolls the table's special chances at exactly this. Default 1.0 (the table's own odds). A steel spud adds up to
    /// <see cref="BarkChanceMultSpudSteelBonus"/> on top (scaled by the spud's metal). Clamped to [0, 1]; the steel
    /// bonus below is the only way past 1.0, and even then the roll's own cumulative top-clamp keeps a certain species
    /// (oak <c>tan 1.0</c>) at 100%.</summary>
    public float BarkChanceMultSpud = 1.0f;

    /// <summary>EXTRA special-bark chance a STEEL bark spud adds ON TOP of <see cref="BarkChanceMultSpud"/> (T02.05),
    /// scaled by the spud's metal tier (0 at a copper spud, this full amount at steel). Default 0.25 = a steel spud
    /// rolls the special kinds at up to 1.25× the table. Clamped to [0, 1]. Only the bark spud earns it; the knife and
    /// axe+hammer tiers are flat. The roll's cumulative top-clamp still keeps oak's certain tan at 100%.</summary>
    public float BarkChanceMultSpudSteelBonus = 0.25f;

    /// <summary>How many "points" of packed birch bark fill a tar pot to the brim (BarkPlan/E04, T04.09). Default 64:
    /// with DRY birch worth <see cref="TarBirchPointsDry"/> (2) and WET worth <see cref="TarBirchPointsWet"/> (1), a
    /// full pot is 32 dry OR 64 wet birch. The four cumulative fill-stage models render EVENLY across 0..this at any
    /// value (the stage COUNT, 4, is tied to the authored shape and is NOT configurable). A full pot opens into
    /// <see cref="TarYield"/> tar. Clamped to [1, 10000]. Read live through <c>TarRig</c> (the pot blocks resolve it
    /// from the synced <see cref="ImmersiveWoodworkingConfig"/>).</summary>
    public int TarPotCapacityPoints = 64;

    /// <summary>Fill points ONE piece of WET (still-drying) birch bark adds when packed into a tar pot (T04.09). Default
    /// 1 = wet birch is the wasteful path (needs <see cref="TarPotCapacityPoints"/> of it for a full pot). A foreign
    /// mod's non-drying birch bark counts as DRY. Clamped to [1, <see cref="TarPotCapacityPoints"/>]; wet is never
    /// better than dry (see <see cref="TarBirchPointsDry"/>).</summary>
    public int TarBirchPointsWet = 1;

    /// <summary>Fill points ONE piece of DRY birch bark adds when packed (T04.09). Default 2 = dry birch is twice as
    /// thrifty as wet, so 32 dry fill a 64-point pot. Clamped to [<see cref="TarBirchPointsWet"/>,
    /// <see cref="TarPotCapacityPoints"/>] (dry is never worse than wet, nor more than a whole pot in one piece).</summary>
    public int TarBirchPointsDry = 2;

    /// <summary>How much tar (<c>barktar</c>) a full tar pot yields when opened (BarkPlan/E04, T04.05). Default 8 (the
    /// wet-birch penalty is paid at PACKING, not here, so a full pot always gives this). A value over <c>barktar</c>'s
    /// max stack size is split across several stacks, never truncated (BehaviorTarPotOpen.GiveOrDrop). Clamped to
    /// [1, 1000]. Read server-side in <see cref="ImmersiveWoodworking.BehaviorTarPotOpen"/>.</summary>
    public int TarYield = 8;

    /// <summary>In-game HOURS for green tanning / birch bark to dry into its dry state (BarkPlan/E01, T01.04). Default
    /// 96 (4 days). The value lives in the item asset's <c>transitionableProps</c>, so the mod REWRITES it from this
    /// field at load — on BOTH sides (the server resolves the transition, the client shows the tooltip + progress bar),
    /// see <c>ApplyBarkDryHours</c> (T08.02). Drying is never a hard gate (green bark works everywhere, just wastefully),
    /// so a long value is safe. Clamped to [0.1, 100000]. It applies to bark that STARTS drying after the change: VS samples a stack's transition
    /// time ONCE (when its transitionstate is first created) and stores it per-stack, so bark already part-dried on an
    /// existing save keeps its original time (not rescaled, not broken). Only the two green bark kinds carry a Dry transition; the other five are untouched.</summary>
    public float BarkDryHours = 96f;

    /// <summary>
    /// Clamps every field into a safe range so a hand-edited config file can never crash the mod or make
    /// it behave nonsensically. The critical one is <see cref="ChopSeconds"/>: a value of 0 would
    /// divide-by-zero when computing the chop animation speed (BlockChoppingBlock.StartChopAnimation).
    /// Called once right after the config is loaded (see ImmersiveWoodworkingModSystem.StartPre), before
    /// the file is written back, so any corrected values are persisted and visible to the user.
    /// <paramref name="logger"/> receives the T01.03 band-clamp corrections (old/new value + geometric reason); it is
    /// the server's logger on load and null on the client's post-sync re-sanitize, whose received config is already
    /// clamped (invariant I9), so a correction logs exactly once, server-side.
    /// </summary>
    public void Sanitize(ILogger? logger = null)
    {
        // It's the divisor for the animation speed AND the chop timer, so it must stay positive and sane:
        // too small = an absurdly fast/instant swing, too large = a multi-second hold. Clamp to [0.1, 2]s.
        if (ChopSeconds < 0.1f) ChopSeconds = 0.1f;
        if (ChopSeconds > 2f) ChopSeconds = 2f;

        // A chop must yield at least one item (also guards against negative ItemStack sizes). The UPPER
        // bound — the produced item's max stack size — can't be applied here because the firewood/stick
        // items aren't loaded yet at config time (StartPre); it's enforced at chop time in
        // BlockEntityChoppingBlock.GetChopResult (GameMath.Clamp(amount, 1, item.MaxStackSize)).
        if (FirewoodPerLog < 1) FirewoodPerLog = 1;
        if (SticksPerFirewood < 1) SticksPerFirewood = 1;

        // Drop object counts: at least one entity, and never more piles than there are items to fill them
        // (SpawnDistributed re-caps to the actual amount too, but keep the persisted value coherent).
        if (FirewoodDropCount < 1) FirewoodDropCount = 1;
        if (FirewoodDropCount > FirewoodPerLog) FirewoodDropCount = FirewoodPerLog;
        if (SticksDropCount < 1) SticksDropCount = 1;
        if (SticksDropCount > SticksPerFirewood) SticksDropCount = SticksPerFirewood;

        // 0 is allowed and means "no durability cost"; only negatives are nonsensical.
        // MIGRATION, not a guard rail (AuditFixPlan T03.01): the target is either exactly 0 ("the axe takes no wear")
        // or at least ChopChainSteps (3), because 1 and 2 hand the split back its free ride. Sanitize runs BEFORE the
        // config folder is written, so an already-playing world whose chopping.json still holds the old shipped 2 is
        // migrated on its next launch rather than keeping the hole.
        if (AxeDurabilityPerLog < 0) AxeDurabilityPerLog = 0;
        else if (AxeDurabilityPerLog > 0 && AxeDurabilityPerLog < 3) AxeDurabilityPerLog = 3;

        // The chop-speed multiplier scales the per-strike progress notch, so it must stay POSITIVE (0 or negative would
        // never fill the bar — the chop would freeze) and sane on the high end (an enormous value just means "always one
        // strike"). The floor mirrors ToolTierSpeedFactor's TierSpeedFloor so the slowest allowed setting is a long
        // grind, not a freeze; the metal-tier factor multiplies on top and stays capped at steel separately. Clamp to
        // [0.05, 10].
        if (ChopSpeedMultiplier < 0.05f) ChopSpeedMultiplier = 0.05f;
        if (ChopSpeedMultiplier > 10f) ChopSpeedMultiplier = 10f;

        // Billet wood (firewood AND half-logs, each its own multiplier now) must chop at least as fast as a log (never
        // slower — it's already-split wood), and an absurdly high multiplier just means "always one strike", so a generous
        // cap keeps the values sane. Clamp both to [1, 10].
        if (FirewoodChopSpeedMultiplier < 1f) FirewoodChopSpeedMultiplier = 1f;
        if (FirewoodChopSpeedMultiplier > 10f) FirewoodChopSpeedMultiplier = 10f;
        if (HalfLogChopSpeedMultiplier < 1f) HalfLogChopSpeedMultiplier = 1f;
        if (HalfLogChopSpeedMultiplier > 10f) HalfLogChopSpeedMultiplier = 10f;

        // ChopProgressJitter is a symmetric per-strike SPREAD (the notch is multiplied by a value in [1-J, 1+J]). 0 is
        // the OFF switch — the chop code then skips the hash entirely and reproduces the old increment bit-for-bit — so
        // the floor is 0, not a positive minimum. Above 0.5 the notch would swing by more than half its nominal, so the
        // strike count (and the moment durability is charged) lurches; a negative is meaningless (it flips the interval
        // to [1+J, 1-J]). Clamp to [0, 0.5].
        if (ChopProgressJitter < 0f) ChopProgressJitter = 0f;
        if (ChopProgressJitter > 0.5f) ChopProgressJitter = 0.5f;

        // MaulChopSpeedMultiplier scales the maul's per-strike notch on a whole log, so like ChopSpeedMultiplier it must
        // stay POSITIVE (0 or negative would never fill the bar — the maul would freeze on a log) and sane on the high end
        // (a huge value just means "one-shot the log", erasing the maul's balance penalty). Same [0.05, 10] window as the
        // master chop-speed knob.
        if (MaulChopSpeedMultiplier < 0.05f) MaulChopSpeedMultiplier = 0.05f;
        if (MaulChopSpeedMultiplier > 10f) MaulChopSpeedMultiplier = 10f;

        // FlotsamBreakSeconds is the hold-to-break duration (progress = the engine's per-interaction timer), so it must
        // stay positive and sane like the other hold timers: too small = an instant break, too large = a multi-minute
        // hold. Clamp to [0.1, 30]s.
        if (FlotsamBreakSeconds < 0.1f) FlotsamBreakSeconds = 0.1f;
        if (FlotsamBreakSeconds > 30f) FlotsamBreakSeconds = 30f;

        // Driftwood-break yields: a size may legitimately drop 0 of one output (a small chunk gives no firewood), so
        // clamp only out negatives — do NOT floor to 1. The spawn side no-ops a 0 count.
        if (FlotsamFirewoodLarge < 0) FlotsamFirewoodLarge = 0;
        if (FlotsamSticksLarge < 0) FlotsamSticksLarge = 0;
        if (FlotsamFirewoodNormal < 0) FlotsamFirewoodNormal = 0;
        if (FlotsamSticksNormal < 0) FlotsamSticksNormal = 0;
        if (FlotsamFirewoodSmall < 0) FlotsamFirewoodSmall = 0;
        if (FlotsamSticksSmall < 0) FlotsamSticksSmall = 0;

        // SawSeconds is the divisor for the per-stage sawing timer and animation, so it must stay positive
        // and sane: too small = an instant rip, too large = a multi-minute hold. Clamp to [0.1, 30]s.
        if (SawSeconds < 0.1f) SawSeconds = 0.1f;
        if (SawSeconds > 30f) SawSeconds = 30f;

        // A rip must yield at least one plank/beam (also guards against negative ItemStack sizes). The upper
        // bound is the output item's max stack size, which can't be applied here because plank/supportbeam
        // aren't loaded yet at config time (StartPre); it's enforced at saw time.
        if (PlanksPerLog < 1) PlanksPerLog = 1;
        if (BeamsPerDebarkedLog < 1) BeamsPerDebarkedLog = 1;

        // At least one drop entity each (planks and beams). The over-cap (never more piles than items) is NOT
        // applied here: the per-stage batch is total/4, capped to the actual batch amount at saw time.
        if (PlankDropCount < 1) PlankDropCount = 1;
        if (BeamDropCount < 1) BeamDropCount = 1;

        // Automated-station drop overrides (ImprovementsPlan E04/T04.06): each is either the -1 inherit sentinel or a
        // real value held to its MANUAL counterpart's rule. A stray negative (a typo like -5) coerces back to -1 =
        // inherit — NOT to 0 (a 0 amount/pile is almost never intended); a real 0 floors to 1. The FOUR amount
        // overrides + the two SAWMILL pile counts only need a min of 1 (the sawmill pile counts mirror the manual
        // Plank/BeamDropCount, which carry no Sanitize upper cap — the per-pass batch caps piles at saw time). The two
        // CHOPPING pile counts also cap to the EFFECTIVE amount (mirroring FirewoodDropCount <= FirewoodPerLog), so the
        // amount overrides are sanitized FIRST and the caps read the *Effective accessor (a -1 amount falls back to the
        // already-sanitized manual field). See SanitizeOverrideMin1 / SanitizeOverrideCapped below.
        ChopperFirewoodPerLog = SanitizeOverrideMin1(ChopperFirewoodPerLog);
        ChopperSticksPerFirewood = SanitizeOverrideMin1(ChopperSticksPerFirewood);
        SawmillPlanksPerLog = SanitizeOverrideMin1(SawmillPlanksPerLog);
        SawmillBeamsPerDebarkedLog = SanitizeOverrideMin1(SawmillBeamsPerDebarkedLog);
        SawmillPlankDropCount = SanitizeOverrideMin1(SawmillPlankDropCount);
        SawmillBeamDropCount = SanitizeOverrideMin1(SawmillBeamDropCount);
        ChopperFirewoodDropCount = SanitizeOverrideCapped(ChopperFirewoodDropCount, ChopperFirewoodPerLogEffective);
        ChopperSticksDropCount = SanitizeOverrideCapped(ChopperSticksDropCount, ChopperSticksPerFirewoodEffective);

        // 0 is allowed and means "no durability cost"; only negatives are nonsensical.
        if (SawDurabilityPerStage < 0) SawDurabilityPerStage = 0;

        // An ordinary saw must be no faster than the pit saw (≤ 1) and not so slow it effectively never finishes
        // (a 0/negative would freeze an allowed saw at 0 progress). Clamp to [0.05, 1].
        if (VanillaSawSpeedMultiplier < 0.05f) VanillaSawSpeedMultiplier = 0.05f;
        if (VanillaSawSpeedMultiplier > 1f) VanillaSawSpeedMultiplier = 1f;

        // AutoRestock is a plain bool — both values (on/off) are valid, so there is nothing to clamp. Noted here
        // explicitly so its absence below doesn't read as an oversight. CraftableSawhorse is likewise a plain
        // bool (craft+place vs in-world stick build), nothing to clamp either. RemovePitSaw (remove the pit saw →
        // ordinary saws only) is a plain bool as well — nothing to clamp. ScaleWorkSpeedByToolTier (metal-tier speed
        // scaling on/off) is a plain bool too — nothing to clamp (its derived factor is clamped in code, not here).
        // DropsAtPlayer (manual-station drops at the worker's feet vs the station's own physics) is a plain bool too.

        // The partial-work recovery grace is a fraction in [0, 1]: 0 = only untouched logs come back (strictest),
        // 1 = the whole log always comes back (never forfeit). Out-of-range values would invert / trivialise the
        // "return vs forfeit" split, so clamp. (An untouched log is returned whole regardless — see LogRecoverable.)
        if (LogRecoverThreshold < 0f) LogRecoverThreshold = 0f;
        if (LogRecoverThreshold > 1f) LogRecoverThreshold = 1f;

        // The auto-restock chop pause: 0 (no pause = back-to-back swings) is valid; only clamp out negatives and
        // absurdly long pauses. It's a breather between strikes, not a divisor, so the upper bound is generous.
        if (ChopRestockDelaySeconds < 0f) ChopRestockDelaySeconds = 0f;
        if (ChopRestockDelaySeconds > 10f) ChopRestockDelaySeconds = 10f;

        // The auto-restock saw pause (the saw's counterpart to ChopRestockDelaySeconds): 0 (no pause = the next log
        // is laid instantly, the seamless feel) is valid; only clamp out negatives and absurdly long pauses. It's a
        // breather between logs, not a divisor, so the upper bound is generous.
        if (SawRestockDelaySeconds < 0f) SawRestockDelaySeconds = 0f;
        if (SawRestockDelaySeconds > 10f) SawRestockDelaySeconds = 10f;

        // DebarkSeconds is the strip DURATION (the spud reference) and the divisor the per-tick debark progress
        // scales against, so it must stay positive and sane exactly like SawSeconds: too small = an instant strip,
        // too large = a multi-minute hold. Clamp to [0.1, 30]s.
        if (DebarkSeconds < 0.1f) DebarkSeconds = 0.1f;
        if (DebarkSeconds > 30f) DebarkSeconds = 30f;

        // The per-tier debark speed multipliers are fractions of the spud (the 1.0 reference tool), so they mirror
        // VanillaSawSpeedMultiplier's clamp [0.05, 1]: a 0/negative would freeze that tier at 0 progress (the 0.05
        // floor stops an admitted tool from silently never finishing), and > 1 would make a WORSE tier faster than
        // the spud reference — nonsensical for the knife → axe+hammer → spud ladder, so cap at 1. AllowKnifeDebark
        // is a plain bool (knife tier on/off) — nothing to clamp.
        if (KnifeDebarkSpeedMultiplier < 0.05f) KnifeDebarkSpeedMultiplier = 0.05f;
        if (KnifeDebarkSpeedMultiplier > 1f) KnifeDebarkSpeedMultiplier = 1f;
        if (AxeHammerDebarkSpeedMultiplier < 0.05f) AxeHammerDebarkSpeedMultiplier = 0.05f;
        if (AxeHammerDebarkSpeedMultiplier > 1f) AxeHammerDebarkSpeedMultiplier = 1f;

        // 0 is allowed and means "no durability cost" (the wear loop already treats any value ≤ 0 as no wear); only
        // negatives are nonsensical.
        if (DebarkDurabilityPerLog < 0) DebarkDurabilityPerLog = 0;

        // The auto-restock debark pause (the debark counterpart to SawRestockDelaySeconds): 0 (no pause = the next log
        // is laid instantly, the seamless feel) is valid; only clamp out negatives and absurdly long pauses. It's a
        // breather between logs, not a divisor, so the upper bound is generous.
        if (DebarkRestockDelaySeconds < 0f) DebarkRestockDelaySeconds = 0f;
        if (DebarkRestockDelaySeconds > 10f) DebarkRestockDelaySeconds = 10f;

        // (The former SawmillStepsPerLog integer clamp is gone: it is now the float SawmillSpeed, floored
        // positive and — in T01.03 — band-clamped next to the derived-constants fill at the end of Sanitize.)

        // Network thresholds/loads: 0 is allowed (no minimum / no added resistance); only negatives are
        // nonsensical and would invert the gates.
        if (SawmillMinTorque < 0f) SawmillMinTorque = 0f;
        if (SawmillResistance < 0f) SawmillResistance = 0f;

        // Wear rate: 0 means "blades never wear"; the DEFAULT is 1 (one durability charge per completed log). Only
        // negatives are nonsensical (they'd make wear negative). Any value ≤ 0 is treated as "no wear", so flooring
        // at 0 is enough.
        if (SawmillBladeWearMultiplier < 0f) SawmillBladeWearMultiplier = 0f;

        // (The former ChopperStepsPerLog integer clamp is gone: it is now the float ChopperSpeed, handled with
        // the derived-constants fill at the end of Sanitize — SpeedDecouplingPlan.)

        // Network thresholds/loads mirror the sawmill's (SawmillMinTorque/SawmillResistance): 0 is allowed (no minimum
        // / no added resistance); only negatives are nonsensical and would invert the gates. Floor at 0, NO upper cap.
        if (ChopperMinTorque < 0f) ChopperMinTorque = 0f;
        if (ChopperResistance < 0f) ChopperResistance = 0f;

        // Head wear rate: 0 means "the head never wears"; the default is 1 (one durability charge per completed log).
        // Negatives would make wear negative (nonsensical); a generous upper cap of 100 keeps a hand-edit sane while
        // still allowing very fast wear. Mirrors SawmillBladeWearMultiplier's intent, with an added ceiling.
        if (ChopperHeadWearMultiplier < 0f) ChopperHeadWearMultiplier = 0f;
        if (ChopperHeadWearMultiplier > 100f) ChopperHeadWearMultiplier = 100f;

        // The blunt-tool sharpness floor mirrors the speed multipliers' clamp [0.05, 1]: a 0/negative would freeze a
        // fully blunt pit saw / bark spud at ~0 progress (the 0.05 floor stops it silently never finishing), and > 1 —
        // a blunt tool FASTER than a sharp one — is nonsensical for the linear floor…1 sharpness model (1.0 already
        // means "sharpness doesn't affect speed"). Only meaningful with Toolsmith installed.
        if (SharpnessSpeedFloor < 0.05f) SharpnessSpeedFloor = 0.05f;
        if (SharpnessSpeedFloor > 1f) SharpnessSpeedFloor = 1f;

        // The chop sharpness ceiling is the sharp-side (>65%) bonus; below 1 would make a sharp axe slower than a
        // mid-sharpness one (nonsensical). 1.0 disables the bonus (plateau then caps at vanilla); 5 is a generous ceiling.
        if (ChopSharpnessCeiling < 1f) ChopSharpnessCeiling = 1f;
        if (ChopSharpnessCeiling > 5f) ChopSharpnessCeiling = 5f;

        // xSkills XP grants are per-operation amounts; a negative would DRAIN forestry XP, so floor at 0 (0 = that
        // operation grants no XP). The master switch, co-op flag and per-ability apply toggles are plain bools —
        // nothing to clamp.
        if (XSkillsSawXpPerSection < 0f) XSkillsSawXpPerSection = 0f;
        if (XSkillsDebarkXpPerStrip < 0f) XSkillsDebarkXpPerStrip = 0f;
        if (XSkillsChopXpPerWorkpiece < 0f) XSkillsChopXpPerWorkpiece = 0f;
        if (XSkillsFirewoodChopXp < 0f) XSkillsFirewoodChopXp = 0f;

        // The swiftsaw chop speed-up is a FRACTION of the swing time (EffectiveChopSeconds = ChopSeconds × (1 − bonus)),
        // symmetric with the rip cap. Clamp to [0, 0.5]: 0 = no chop speed-up; 0.5 is the ceiling the EffectiveChopSeconds
        // half-swing floor enforces anyway (a higher value is just clipped by that floor), which also guarantees the swing
        // can never approach 0s and divide-by-zero the chop animation. Floor at 0 first (a negative would ADD swing time).
        if (XSkillsSwiftsawMaxChopSpeedBonus < 0f) XSkillsSwiftsawMaxChopSpeedBonus = 0f;
        if (XSkillsSwiftsawMaxChopSpeedBonus > 0.5f) XSkillsSwiftsawMaxChopSpeedBonus = 0.5f;

        // The swiftsaw saw-speed bonus is a fractional multiplier on the rip tempo (×(1 + bonus)); 0 = swiftsaw never
        // speeds ripping. A large value is harmless (just faster, no divisor) and the ability's own tier value caps the
        // real bonus, so only floor out negatives (which would SLOW the saw).
        if (XSkillsSwiftsawMaxSawSpeedBonus < 0f) XSkillsSwiftsawMaxSawSpeedBonus = 0f;

        // (The flywheel multipliers and the sawdust-wave knobs are no longer config — baked into constants in
        // Power.cs / BEBehaviorSawmillMP.cs / Effects.cs — so there is nothing to clamp for them here anymore.)

        // (The completed-pass product is ejected forward, never buffered, so there is no output-full / ground-drop
        // config left to sanitize. The saw-sound knobs are no longer config either — baked into Effects.cs constants.)

        // ── Bark chain (BarkPlan/E08, T08.03) ──
        // The tool-tier special-bark multipliers each SCALE the barkdrops.json chances, so each input stays in [0, 1]:
        // 0 = that tier never rolls special bark, 1 = the spud reference. The steel bonus is ADDED to BarkChanceMultSpud
        // inside BarkChanceMultFor (so a steel spud can reach 1.25), but the ROLL itself top-clamps the cumulative at 1.0
        // (oak's certain tan stays 100%) — so clamping each input to [0, 1] here is sufficient; the product can never
        // push a real chance past 100%. A negative would collapse the roll to generic (guarded anyway), so floor at 0.
        if (BarkChanceMultKnife < 0f) BarkChanceMultKnife = 0f;
        if (BarkChanceMultKnife > 1f) BarkChanceMultKnife = 1f;
        if (BarkChanceMultAxeHammer < 0f) BarkChanceMultAxeHammer = 0f;
        if (BarkChanceMultAxeHammer > 1f) BarkChanceMultAxeHammer = 1f;
        if (BarkChanceMultSpud < 0f) BarkChanceMultSpud = 0f;
        if (BarkChanceMultSpud > 1f) BarkChanceMultSpud = 1f;
        if (BarkChanceMultSpudSteelBonus < 0f) BarkChanceMultSpudSteelBonus = 0f;
        if (BarkChanceMultSpudSteelBonus > 1f) BarkChanceMultSpudSteelBonus = 1f;

        // Bark per stripped log is the drop's stack size. Floor at 1 like the other per-log yields (no upper cap — a
        // value above the bark item's max stack just splits on pickup); 0/negative would drop no bark and silently
        // starve the whole bark chain.
        if (BarkPerLog < 1) BarkPerLog = 1;

        // Per-species bark counts: a null map (the file said "BarkPerLogBySpecies": null, which PopulateObject applies as
        // written) goes back to the shipped template; otherwise keys are trimmed and lowercased and each value is mapped
        // like an automated override, so BarkPerLogFor only ever sees -1 (inherit) or a count ≥ 1.
        BarkPerLogBySpecies = SanitizeBarkPerLogBySpecies(BarkPerLogBySpecies);

        // Tar pot capacity is the packing gate AND the divisor for the fill-percent / render-stage math (TarRig), so it
        // must be a sane positive integer: 0/negative would divide-by-zero the readout. Clamped FIRST because the
        // per-birch point clamps below hold to it. Clamp to [1, 10000].
        if (TarPotCapacityPoints < 1) TarPotCapacityPoints = 1;
        if (TarPotCapacityPoints > 10000) TarPotCapacityPoints = 10000;

        // Per-birch fill points: each piece must add at least 1 (a 0 would let a pot never fill), DRY is never worse than
        // wet (dry is the thrifty path), and neither exceeds a whole pot in one piece. Floor wet at 1 and cap it at the
        // capacity first, then hold dry to [wet, capacity].
        if (TarBirchPointsWet < 1) TarBirchPointsWet = 1;
        if (TarBirchPointsWet > TarPotCapacityPoints) TarBirchPointsWet = TarPotCapacityPoints;
        if (TarBirchPointsDry < TarBirchPointsWet) TarBirchPointsDry = TarBirchPointsWet;
        if (TarBirchPointsDry > TarPotCapacityPoints) TarBirchPointsDry = TarPotCapacityPoints;

        // Tar yield: a full pot must give at least 1 tar; a huge value is split across stacks at open time (never
        // truncated), so a generous cap only guards a hand-edit typo. Clamp to [1, 1000].
        if (TarYield < 1) TarYield = 1;
        if (TarYield > 1000) TarYield = 1000;

        // Bark drying hours is the transition divisor, so it must stay positive and sane (0/negative would divide-by-zero
        // the drying progress). A long dry time is fine — drying is optional (green bark works, just wastefully). Clamp
        // to [0.1, 100000].
        if (BarkDryHours < 0.1f) BarkDryHours = 0.1f;
        if (BarkDryHours > 100000f) BarkDryHours = 100000f;

        // ── Speed-multiplier plausibility band-clamp (SpeedDecouplingPlan T01.03) ──
        // Pin the two SPEED multipliers into the safe window BEFORE the derived constants below consume them, and log any
        // correction (the config folder is rewritten from these values, so a silent shift is not allowed). The band is
        // now wide [0.01, 100] (author's call — freedom over animation smoothness); the ceiling keeps the derived wrap /
        // FeedAdvance divisor away from zero and the floor keeps a log from a pointless crawl. A non-positive/NaN value
        // is garbage and resets to the speed=1 default. The default 1.0 is in band for both and never logs. Client-side
        // logger is null (invariant I9), so this logs once.
        SawmillSpeed = SanitizeSpeed(
            SawmillSpeed, SawmillSpeedMin, SawmillSpeedMax, nameof(SawmillSpeed), logger);
        ChopperSpeed = SanitizeSpeed(
            ChopperSpeed, ChopperSpeedMin, ChopperSpeedMax, nameof(ChopperSpeed), logger);

        // ── Derived speed constants (SpeedDecouplingPlan T01.02) ──
        // Filled LAST from the now-clamped SPEED multipliers, so the tick/render read these straight (invariant I5). The
        // public knob is speed (higher = faster); the derived math is all in the WORK factor M = 1/speed, so every formula
        // below keeps its original work-factor form (and the render/anim comments' "M" stays valid). At speed=1 → M=1 and
        // every value equals the pre-feature constant (invariant I8): 1f/1f = 1f exact, so no precision is lost at default.

        // Pilorama: M = 1/speed is the work factor. workScale = 1/M = speed gears the rectified mechanism (faster speed =
        // faster mechanism); the wrap-bound 2π·SawmillDefaultWork·M = 2π·50/speed is both the fold period of that clock and
        // the FeedAdvance divisor (one pass = R = 50·M = 50/speed revolutions). speed=1 → M=1 → 1.0 and 2π·50.
        float sawWork = 1f / SawmillSpeed;
        SawmillWorkScale = 1f / sawWork;
        SawmillFwdWrapBound = GameMath.TWOPI * SawmillDefaultWork * sawWork;

        // Drovokol: M = 1/speed work factor. R = 48·M = 48/speed revolutions of work, N = ⌈M⌉ strikes (speed=1 → 1; slower
        // speed → more strikes), each a cycle of R/N ≤ 48 revolutions and a 1/N split. workScale = cap/cycleRevs = N/M
        // gears the rectified clock so the geared read animAngle·workScale folds on exactly one whole arm cycle (speed=1 →
        // 48/48 = 1.0 exact, bit-identical). The "без ремодела" revision (2026-09-15) restored the cap to 48 = the modeled
        // reducer nominal ChopperAnim.ReducerRevs, so the fold divides every mover period (1/4/16/48) with no snap.
        float chopWork = 1f / ChopperSpeed;
        int strikes = (int)System.Math.Ceiling((double)chopWork);
        if (strikes < 1) strikes = 1;
        ChopperStrikes = strikes;
        ChopperCycleRevs = ChopperCycleCap * chopWork / strikes;
        ChopperSplitPerStrike = 1f / strikes;
        ChopperWorkScale = ChopperCycleCap / ChopperCycleRevs;
    }

    /// <summary>Band-clamp a SPEED multiplier into its safe window, logging any correction with the old/new value and the
    /// reason (SpeedDecouplingPlan T01.03). <see cref="Sanitize"/> rewrites the config folder from the result, so a
    /// correction must never be silent. A non-positive or NaN value is garbage — a hand-edit typo that would also
    /// divide-by-zero the derived work factor <c>1/speed</c> — and resets to the speed=1 default rather than pinning to an
    /// edge, since a negative "speed multiplier" has no reading; an in-band positive value (the 1.0 default included)
    /// passes through untouched and logs nothing. <paramref name="logger"/> is null on the client (invariant I9), so the
    /// correction logs once, server-side. Warning level so it lands in the main log the admin actually reads.</summary>
    static float SanitizeSpeed(float value, float min, float max, string name, ILogger? logger)
    {
        if (!(value > 0f))   // catches NaN (all comparisons false), 0 and negatives in one guard
        {
            logger?.Warning("[immersivewoodworking] {0}={1} is not a positive speed multiplier - reset to the default 1.0.", name, value);
            return 1f;
        }
        if (value < min)
        {
            logger?.Warning("[immersivewoodworking] {0}={1} is below the plausibility band [{2}, {3}] - pinned to {2} (below it the machine is a near-frozen crawl). The config file is rewritten with the pinned value.", name, value, min, max);
            return min;
        }
        if (value > max)
        {
            logger?.Warning("[immersivewoodworking] {0}={1} is above the plausibility band [{2}, {3}] - pinned to {3} (above it a log costs under half a revolution and the derived wrap/divisor lose safety). The config file is rewritten with the pinned value.", name, value, min, max);
            return max;
        }
        return value;
    }

    /// <summary>Sentinel-aware clamp for an automated-station override that only needs a MIN of 1 (the four amount
    /// overrides + the two SAWMILL pile counts, which mirror the manual Plank/BeamDropCount that carry no Sanitize
    /// upper cap). -1 = inherit, passes through untouched; any OTHER negative is a typo coerced back to -1 (inherit),
    /// NOT to 0 (a 0 amount/pile is almost never intended); a real 0 floors to 1 like the manual field.
    /// (ImprovementsPlan E04/T04.06.)</summary>
    static int SanitizeOverrideMin1(int v)
    {
        if (v < -1) return -1;   // stray negative -> inherit, not 0
        if (v == 0) return 1;    // a real override must be >= 1, like its manual counterpart
        return v;                // -1 (inherit) or >= 1 pass through
    }

    /// <summary>Sentinel-aware clamp for an automated-station pile-count override that also caps to "no more piles than
    /// items" (the two CHOPPING pile counts, mirroring the manual FirewoodDropCount &lt;= FirewoodPerLog clamp). Same -1 /
    /// stray-negative / 0 handling as <see cref="SanitizeOverrideMin1"/>, plus the upper cap applied to the EFFECTIVE
    /// amount (<paramref name="effectiveAmount"/>, e.g. ChopperFirewoodPerLogEffective) rather than the raw field, so a
    /// -1-inherit amount still caps against the value actually used. (E04/T04.06.)</summary>
    static int SanitizeOverrideCapped(int v, int effectiveAmount)
    {
        if (v < -1) return -1;                                       // stray negative -> inherit
        if (v == -1) return -1;                                      // inherit sentinel untouched
        if (v < 1) v = 1;                                            // a real override must be >= 1
        if (effectiveAmount >= 1 && v > effectiveAmount) v = effectiveAmount; // no more piles than items (effective)
        return v;
    }

    /// <summary>The shipped <see cref="BarkPerLogBySpecies"/> template: the species the retired <c>countBySpecies</c>
    /// map listed (the 13 vanilla woods plus the Wildcraft Trees woods that earn a special bark in
    /// <c>barkdrops.json</c>), each at -1 = inherit. A template to edit, not a whitelist: any species code works as a
    /// key. A fresh dictionary per call, so no two config objects share one.</summary>
    static Dictionary<string, int> DefaultBarkPerLogBySpecies()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        foreach (string wood in new[]
        {
            "oak", "acacia", "larch", "pine", "birch", "baldcypress", "redwood", "maple", "kapok", "ebony",
            "walnut", "purpleheart", "aged",
            "willow", "linden", "elm", "poplar", "spruce", "fir",
        })
            map[wood] = -1;
        return map;
    }

    /// <summary>Rebuild <see cref="BarkPerLogBySpecies"/> clean: null → the template; a blank key is dropped; keys are
    /// trimmed and lowercased (two spellings of one species collapse, the later one read wins, as PopulateObject would
    /// have it); each value goes through <see cref="SanitizeOverrideMin1"/>. Insertion order is kept, so the server's
    /// map and the one a client rebuilds from the sync packet serialize alike for the round-trip self-check.</summary>
    static Dictionary<string, int> SanitizeBarkPerLogBySpecies(Dictionary<string, int>? raw)
    {
        if (raw == null) return DefaultBarkPerLogBySpecies();
        var clean = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, int> kv in raw)
        {
            string species = kv.Key?.Trim().ToLowerInvariant() ?? "";
            if (species.Length == 0) continue;
            clean[species] = SanitizeOverrideMin1(kv.Value);
        }
        return clean;
    }
}

/// <summary>
/// The network form of <see cref="ImmersiveWoodworkingConfig"/> — a ProtoBuf envelope carrying the whole config as one
/// JSON string (AuditFixPlan T02.05, F-L4-02). The server sends it once per player join; the client rebuilds its
/// <c>Config</c> from <see cref="Json"/>.
///
/// WHY A STRING and not the config object itself: ProtoBuf omits any member equal to the CLR zero of its type, and the
/// receiver's field initializers then re-supply a non-zero default — so every `false` and every 0 the server had set
/// silently became the client's default instead (see the header comment on the config class). JSON has no such
/// asymmetry: <c>SerializeObject</c> writes all 84 members, zeros included, and a member added to the config later is
/// carried automatically — no attribute to add, no field number to allocate, nothing to keep in sync. The one member
/// here is a non-empty string, so the zero-suppression rule cannot bite the envelope itself.
///
/// Cost is one ~4 KB packet per join instead of a few hundred bytes — irrelevant at that frequency, and the client
/// reuses the received text verbatim for its join-time digest instead of re-serializing.
/// </summary>
[ProtoContract]
public class ConfigSyncPacket
{
    /// <summary>The server's config as compact JSON (<c>JsonConvert.SerializeObject</c> over the live object). Explicit
    /// <see cref="ProtoMemberAttribute"/> rather than an implicit contract, so the envelope never inherits the
    /// zero-suppression behaviour this class exists to avoid.</summary>
    [ProtoMember(1)]
    public string Json = "";

    /// <summary>The wood states that must NOT become support beams, read off the vanilla (or a wood mod's own)
    /// <c>debarkedlog → supportbeam</c> grid recipe's <c>skipVariants</c> before that recipe is stripped
    /// (AuditFixPlan T03.03, F-L9-10). It rides along on this packet because the rule is DATA only the SERVER can see:
    /// recipe assets are a server-side asset category, so a client cannot derive the list itself — yet its copy of the
    /// rip gate has to answer the same, or it animates a rip the server refuses. Null on the wire when the list is
    /// empty (ProtoBuf omits a zero-length array), which the receiver reads as "nothing skipped" — the same meaning,
    /// so the omission is harmless here, unlike the bool/int fields that forced <see cref="Json"/> to be a string.</summary>
    [ProtoMember(2)]
    public string[]? BeamSkipWoods;

    /// <summary>The recipe-declared product REMAP table, one entry per <c>&lt;log domain&gt;:plank-&lt;species&gt;</c> (or
    /// <c>supportbeam-</c>) key whose sawn product the wood mod's own grid recipe sends to a DIFFERENTLY-named plank/beam
    /// (e.g. FloralZones' <c>log-placed-pinussibirica-ud → game:plank-pine</c>). Each entry is <c>keyvalue</c>
    /// (the U+0001 delimiter cannot occur in an asset code); the sawhorse resolver consults it only after its own
    /// domain→game shape lookup comes up empty, so a well-named mod is unaffected. Rides this packet for the same reason
    /// as <see cref="BeamSkipWoods"/>: it is read off recipe assets, a server-only category, yet the client's put/rip
    /// gate has to answer the same or it refuses a log the server accepts. Null on the wire when nothing is remapped
    /// (ProtoBuf omits a zero-length array), read as "no remaps".</summary>
    [ProtoMember(3)]
    public string[]? ProductRemap;
}
