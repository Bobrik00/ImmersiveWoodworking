# Immersive Woodworking: New Config Settings

Settings added in recent updates, grouped by the version that introduced them (newest first). For the full list of every option see `config-reference.md`.

As of v1.2.1 the config lives in a FOLDER of thematic files, `ModConfig/ImmersiveWoodworking/`; an older single `immersivewoodworking.json` is migrated into it automatically on first launch (the old file is renamed to `immersivewoodworking.json.bak`). Existing values are preserved; new settings take their defaults until you edit them.

## Unreleased — Bark per wood moved into the config (in `bark.json`)

`BarkPerLog` now applies to every wood. Before, the `barkdrops.json` asset listed each vanilla wood at 3 bark, and that line beat the config value, so raising `BarkPerLog` changed nothing for oak or any other listed tree. The per-wood counts are now a config setting, and the asset no longer carries them.

| Setting | Default | What it does |
|---|---|---|
| `BarkPerLogBySpecies` | each listed wood at `-1` | Bark per stripped log for one wood, keyed by species code (`"oak": 5`). `-1` = use `BarkPerLog`. A wood with no line uses `BarkPerLog` too, so a tree from any mod works without one; add a line for any species code you want to tune. `0` becomes `1`, any other negative becomes `-1`. A shipped line you delete comes back at `-1` on the next launch. |

If you kept your own copy of `barkdrops.json` with a `countBySpecies` map, move those numbers here: the mod no longer reads that map and says so in the server log.

## Unreleased — Forestry XP doubled (in `integration.json`)

The four xSkills XP rates have twice their old defaults; the ratios between them are unchanged.

| Setting | Old default | New default |
|---|---|---|
| `XSkillsSawXpPerSection` | `0.075` | `0.15` (4 sections per log ≈ 0.6 XP per log ripped) |
| `XSkillsDebarkXpPerStrip` | `0.15` | `0.3` |
| `XSkillsChopXpPerWorkpiece` | `0.1` | `0.2` (≈ 0.4 XP per log chopped down to firewood) |
| `XSkillsFirewoodChopXp` | `0.025` | `0.05` |

A config folder created from now on gets the new values. **An existing `integration.json` keeps the numbers it already holds.** To take the new defaults, delete those four lines from it: the next launch writes them back at the new values.

## v1.3.12 — Switching off the sawmill, the chopper or the bark chain (in `general.json`)

Each switch takes one part of the mod out of a world. All three are on by default, so nothing changes until you turn one off. A switched-off part can no longer be made and is gone from the creative inventory and the handbook; what the world already holds keeps working.

| Setting | Default | What it does |
|---|---|---|
| `EnableSawmill` | `true` | `false`: the automated sawmill's frame, parts and blade kits can no longer be crafted and are hidden. A sawmill already built keeps sawing on the blade kits that already exist. |
| `EnableChopper` | `true` | `false`: the automated chopper's frame, drive, arm, head and head half can no longer be made and are hidden. A chopper already built keeps chopping on the heads that already exist. The chopping block is not affected. |
| `EnableBarkChain` | `true` | `false`: a log stripped on the sawhorses drops no bark (the debarked log still drops, so beams are unaffected). Bark, birch tar, the bark decoction and the tar-making pots are hidden, and the recipes that make or use them are gone. The vanilla way to make tannin from logs comes back on its own, whatever `DisableVanillaLogTannin` says. |

## v1.3.8 — Class mods: Seraph Leveling and trait-locked recipes

| Setting | File | Default | What it does |
|---|---|---|---|
| `KeepTraitRecipes` | `general.json` | `true` | The vanilla-recipe strips (`DisableVanillaFirewoodCrafting`, `DisableVanillaPlankCrafting`, `DisableVanillaDebarkCrafting`) leave alone any grid recipe locked behind a character trait. Such a recipe is a class mod's reward: Sacred Classes' Carpenter gets its planks, beams and firewood recipes back. Vanilla has no trait-locked recipe in these families, so without a class mod nothing changes. On a world with class-exclusive recipes switched off (the Exploration preset) the game unlocks such recipes for everyone, so they are stripped as before. `false` strips them too. |
| `SeraphLevelingIntegration` | `integration.json` | `true` | Requires Seraph Leveling ReClassed. Planks and beams ripped on the sawhorses count as crafted, the way Seraph counts the grid recipe the rip replaces, so the Carpenter trait (120 boards) can progress with the plank strip on. Each sawyer on the log gets the full batch. The automated sawmill counts for nobody. Turn it off if a future Seraph version counts the sawhorses itself. |

## v1.3.5 — Drops at the player's feet (in `general.json`)

The hand stations can now drop their output on the player instead of around themselves. Off by default, so nothing changes until you turn it on.

| Setting | Default | What it does |
|---|---|---|
| `DropsAtPlayer` | `false` | When `true`, everything the chopping block and the sawhorses produce drops at the feet of the player who did the work: firewood, half-logs and sticks from chopping; planks, beams and Wear & Tear sawdust from sawing; bark and the debarked log from stripping. The game's normal item pickup collects it, so the "collect only while sneaking" setting still applies and whatever doesn't fit in the inventory stays on the ground at your feet. In a two-person rip or strip each section goes to whoever finished it. The automated sawmill and chopper keep ejecting from the machine. |

## v1.3.2 — Bark yield per log (in `bark.json`)

Stripping a log on the sawhorse now drops a configurable number of bark. The **kind** is still rolled per species from `barkdrops.json` (unchanged); this only sets how many.

| Setting | Default | What it does |
|---|---|---|
| `BarkPerLog` | `3` | How many bark a fully-stripped log drops. Global fallback: a wood without its own count uses this. Floored at 1; a value above the bark's max stack size splits across stacks on pickup. |

Per-species overrides lived in the `barkdrops.json` **asset**, under a `countBySpecies` map. They have since moved to the config as `BarkPerLogBySpecies` (see the Unreleased section above), because the asset's lines overrode `BarkPerLog`.

## v1.3.1 — Automated machine speed (in `automation.json`)

The automated sawmill and chopper now have a speed setting each. A machine still turns only as fast as its mechanical network drives it; the setting scales how much work a log costs, so a higher value gets through a log faster on the same drive. At the default `1.0` each machine behaves exactly as it did before. These replace the old `SawmillStepsPerLog` / `ChopperStepsPerLog`, which counted revolutions and strikes directly; a value saved under the old name no longer applies and resets to `1.0`.

| Setting | Default | What it does |
|---|---|---|
| `SawmillSpeed` | `1.0` | Sawmill throughput multiplier. Higher is faster, lower is slower; `1.0` is the original pace. The whole visible mechanism (saw, feed ratchet, carriage) keeps in step at any value. Clamped to the range `0.01` to `100`. |
| `ChopperSpeed` | `1.0` | Chopper throughput multiplier. Higher is faster, lower is slower; `1.0` is the original pace. Below `1.0` a log is split over more than one strike (about `1 / ChopperSpeed`, rounded up); at `1.0` and above it stays one strike per log. Clamped to the range `0.01` to `100`. |

## v1.2.2 — Forestry XP for firewood → sticks (in `integration.json`)

Chopping firewood into sticks granted no forestry XP at all before this version — a deliberate anti-farm rule, since firewood is a stockpileable byproduct whose input can be acquired in bulk. It now pays a small rate of its own.

| Setting | Default | What it does |
|---|---|---|
| `XSkillsFirewoodChopXp` | `0.05` | Forestry XP for chopping firewood into sticks — a quarter of the log rate. Deliberately its OWN absolute value rather than a share of `XSkillsChopXpPerWorkpiece`: set it to `0` to restore the previous no-XP rule without flattening the log and half-log grants, and raising the log rate never drags this one up with it. Inert with `AllowFirewoodToSticks` off (that chop is refused outright). |

## Bark system (in `bark.json`)

The bark chain (strip a log's bark → tanning without burning the log, birch tar, forest rope, forest medicine, plus sinks for ordinary bark) adds a `bark.json` file. Only the numbers that were code constants live here; the tannin barrel and poultice/decoction recipe amounts stay in their own editable JSON assets, and the quern grind ratio is fixed 1:1 by the game.

| Setting | Default | What it does |
|---|---|---|
| `BarkChanceMultKnife` | `0.5` | Multiplier on the special-bark chance for a knife strip (tier 1). `1.0` = the bark-spud reference. Scales the `barkdrops.json` odds; a species with no special bark is unaffected. |
| `BarkChanceMultAxeHammer` | `0.75` | Special-bark chance multiplier for an axe+hammer strip (tier 2). |
| `BarkChanceMultSpud` | `1.0` | Special-bark chance multiplier for the bark spud (tier 3, the reference). |
| `BarkChanceMultSpudSteelBonus` | `0.25` | Extra special-bark chance a steel spud adds on top, scaled by the spud's metal (0 at copper). |
| `TarPotCapacityPoints` | `64` | Birch "points" that fill a tar pot (32 dry or 64 wet birch). |
| `TarBirchPointsWet` | `1` | Fill points one wet birch bark adds. |
| `TarBirchPointsDry` | `2` | Fill points one dry birch bark adds. |
| `TarYield` | `8` | Tar from opening a full tar pot. |
| `BarkDryHours` | `96` | In-game hours for green tanning / birch bark to dry. |

## v1.2.1 — Chopping (splitting maul and strike jitter, in `chopping.json`)

| Setting | Default | What it does |
|---|---|---|
| `MaulChopSpeedMultiplier` | `1.5` | Per-strike speed for the new splitting maul, which chops a whole log straight into firewood in one pass (skipping the half-log stage). At `1.5` a steel maul reliably splits a whole log in a single strike — the margin above `1.0` lets it clear the finish line even at the low end of the strike-jitter spread (`ChopProgressJitter`), so it never leaves a leftover second strike — strictly better than an axe (which needs the half-log chain). Lower it below `1` to slow the maul down (`0.5` ≈ twice the strikes an axe spends just splitting). Weaker metals scale by their tier, so a copper maul takes about two strikes at `1.5`; raise the knob to speed those up too. Applies to a maul (with `HalfLogChopStage` on) on a whole log, a log section, or a laid chopping block; a plain axe, billet wood (half-log or firewood) and the `HalfLogChopStage`-off case are unaffected. |
| `ChopProgressJitter` | `0.2` | Random spread on each axe strike's chop progress, so a log takes a slightly different number of strikes each time. `0.2` = each strike counts for 80% to 120% of normal. `0` turns it off and restores the old strictly predictable chopping. The spread is symmetric and stays perfectly in sync between server and client. A steel axe that one-shots a log will need a second strike about half the time (a steel maul is unaffected: at `1.5` it stays a one-shot). Hand chopping only, never the automated chopper. |
| `AxeDurabilityPerLog` | `2` | Renamed from the old inverse `AxeItemsPerDurability` and flipped to read directly: the axe durability spent to fully process one whole log into firewood (spread across the chop, so a maul in one pass and an axe via the split chain both total this). Higher = the axe wears faster, `0` = no wear. If you had customized the old `AxeItemsPerDurability`, set this one after updating (the old value does not carry across the rename). **The default is `2` only up to 1.3.0** — 1.3.1 raised it to `3` so that splitting a log costs something; see `config-reference.md`. |

## v1.2.1 — Automated-station drop overrides (in `drops.json`)

The automated chopper and sawmill can now be tuned to yield different amounts than the hand stations they mirror. Every drop amount (manual and automated) now lives together in `drops.json`. Each automated field defaults to `-1`, meaning "inherit the matching manual value," so out of the box the powered stations drop exactly what the hand stations do and the two tiers stay in lockstep. Set a field to `0` or more to override just that machine; a stray negative other than `-1` is read as `-1` (inherit). Pile-count fields only change how many separate stacks the drop scatters into, never the total.

| Setting | Default | What it does |
|---|---|---|
| `ChopperFirewoodPerLog` | `-1` | Automated chopper's firewood per log. `-1` = inherit `FirewoodPerLog`. |
| `ChopperFirewoodDropCount` | `-1` | Chopper's firewood pile count. `-1` = inherit `FirewoodDropCount`. |
| `ChopperSticksPerFirewood` | `-1` | Chopper's sticks per firewood. `-1` = inherit `SticksPerFirewood`. |
| `ChopperSticksDropCount` | `-1` | Chopper's stick pile count. `-1` = inherit `SticksDropCount`. |
| `SawmillPlanksPerLog` | `-1` | Automated sawmill's planks per log. `-1` = inherit `PlanksPerLog`. |
| `SawmillPlankDropCount` | `-1` | Sawmill's plank pile count. `-1` = inherit `PlankDropCount`. |
| `SawmillBeamsPerDebarkedLog` | `-1` | Sawmill's beams per debarked log. `-1` = inherit `BeamsPerDebarkedLog`. |
| `SawmillBeamDropCount` | `-1` | Sawmill's beam pile count. `-1` = inherit `BeamDropCount`. |

## v1.2.0 — xSkills integration (in `integration.json`)

Requires the xSkills mod (forestry skill). Without it every setting here is inert. Per-tier ability values live in xSkills' own `ModConfig/XLeveling/forestry.json`, not here. Automated sawmill/chopper are not covered.

| Setting | Default | What it does |
|---|---|---|
| `XSkillsIntegration` | `true` | Master switch for the whole integration (forestry XP + the 4 abilities). `false` = behaves as if xSkills weren't installed. |
| `XSkillsSawXpPerSection` | `0.15` | Forestry XP per rip section on the sawhorses (4 sections per log ≈ 0.6 XP/log). |
| `XSkillsDebarkXpPerStrip` | `0.3` | Forestry XP per fully-stripped log (debarking). |
| `XSkillsChopXpPerWorkpiece` | `0.2` | Forestry XP for splitting a log into half-logs; each half-log chopped to firewood gives half of it, so with `HalfLogChopStage` on a whole log ≈ 2× this. |
| `XSkillsCoopXpFullToBoth` | `true` | In co-op (two players on a paired sawhorse) both get full XP. `false` = only the master side. |
| `XSkillsSawyerYield` | `true` | Enables the `sawyer` ability's bonus (+plank/beam yield when ripping). |
| `XSkillsSplitterYield` | `true` | Enables the `splitter` ability's bonus (+firewood yield when chopping; sticks never boosted). |
| `XSkillsCarefulSawyer` | `true` | Enables the `carefulsawyer` ability (chance to spend no saw/spud durability). |
| `XSkillsSwiftsawSpeed` | `true` | Enables the `swiftsaw` ability (faster hand chopping and ripping). |
| `XSkillsSwiftsawMaxChopSpeedBonus` | `0.35` | Cap on swiftsaw's chopping speed-up (`0.35` = at most 35% faster swing). |
| `XSkillsSwiftsawMaxSawSpeedBonus` | `0.35` | Cap on swiftsaw's ripping speed-up (`0.35` = at most ×1.35). |

## v1.1.1 — Chopping toggles (in `chopping.json`)

| Setting | Default | What it does |
|---|---|---|
| `HalfLogChopStage` | `true` | Hand-chopping a whole log first splits it into two half-logs (log → 2 half-logs → firewood). `false` = a log chops straight to firewood in one pass, like the automated chopper. At default settings the firewood total and axe wear per log are identical either way. |
| `AllowFirewoodToSticks` | `true` | Firewood can be chopped into sticks (on the block and the automated chopper). `false` = firewood is refused and yields no drop; logs still chop into firewood as normal. |
