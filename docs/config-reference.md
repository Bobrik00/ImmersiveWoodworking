# Immersive Woodworking: Config Reference

Config lives in a FOLDER of thematic JSON files: `ModConfig/ImmersiveWoodworking/` (created on first launch). If you are updating from an older version that used the single `ModConfig/immersivewoodworking.json`, your values are migrated into the folder automatically and the old file is renamed to `immersivewoodworking.json.bak`. Only the SERVER reads and writes these files; clients receive the server's values on join. Numeric values out of range are clamped automatically on load.

## Files at a glance

| File | Holds |
|---|---|
| `general.json` | Feature toggles (the sawmill, chopper and bark-chain switches, auto-restock, the vanilla-recipe strips, tool tiers). |
| `chopping.json` | Chopping-block timing and speed. |
| `flotsam.json` | Driftwood hand-break (durations and yields). |
| `sawing.json` | Sawhorse and pit-saw timing and durability. |
| `debarking.json` | Sawhorse side action (debarking speeds and wear). |
| `automation.json` | Automated sawmill and chopper drive (processing speed, torque, wear). |
| `drops.json` | Every yield: the manual amounts and the automated-station overrides, side by side. |
| `integration.json` | Toolsmith sharpness, xSkills and Seraph Leveling. |
| `bark.json` | Bark chain: bark per log (global and per wood), debark tool-tier bonus, tar-pot capacity/yield, bark drying time. |

## general.json (feature toggles)

| Setting | Default | What it does |
|---|---|---|
| `EnableSawmill` | `true` | `false` switches the automated sawmill off for new builds: its frame, parts and blade kits can no longer be crafted, they leave the creative inventory and the handbook, and handbook notes stop naming the sawmill. A sawmill already built keeps working on the blade kits that already exist, and its `automation.json` and `drops.json` settings still apply to it. |
| `EnableChopper` | `true` | The same for the automated chopper: its frame, drive, arm, head and head half can no longer be made and leave the creative inventory and the handbook. A chopper already built keeps working on the heads that already exist. The chopping block is not affected. |
| `EnableBarkChain` | `true` | `false` switches the bark chain off. Stripping a log on the sawhorses still gives the debarked log, but no bark; every debarking tool keeps working. Bark, birch tar, the bark decoction, the perforated pot and the tar pot leave the creative inventory and the handbook, and the recipes that make or use them are gone: bark tannin and bark compost, the perforated pot, the decoction, the bark and bast poultices, bast rope and ACA's boiled bark, plus tar standing in for resin and tar glue. The vanilla log-to-tannin barrel recipes come back whatever `DisableVanillaLogTannin` says, so leather can still be tanned. Bark that already exists still works as pit-kiln kindling, on a VSRoofing roof and in Food Shelves jars, and a tar rig already placed can still be finished. |
| `AutoRestock` | `true` | Stations auto-feed the next matching log from the player's pack, so a whole stack processes hands-free. |
| `DropsAtPlayer` | `false` | Everything the chopping block and the sawhorses produce (chopping, sawing and debarking alike) drops at the feet of the player who did the work instead of around the station, where the game's normal pickup collects it. In a two-person rip or strip each section goes to whoever finished it. The automated sawmill and chopper are unaffected. |
| `ScaleWorkSpeedByToolTier` | `true` | Master switch for "the tool's metal matters". Manual chop/rip/debark speed scales with the metal tier (copper slower, steel = ceiling), and the bark spud's metal bonus to the special-bark chance (`BarkChanceMultSpudSteelBonus`) rides the same switch, so `false` flattens both. No per-metal keys. |
| `DisableVanillaFirewoodCrafting` | `true` | Removes the vanilla axe+log grid recipes, so firewood comes only from the chopping block. Covers the separate aged-log recipe too: the block now produces aged firewood itself. |
| `DisableVanillaPlankCrafting` | `true` | Removes the vanilla saw+log grid recipes, so planks/beams come only from the sawhorses. |
| `DisableVanillaDebarkCrafting` | `true` | Removes the vanilla axe+hammer+log grid recipe, so debarking happens only on the sawhorses. |
| `KeepTraitRecipes` | `true` | The three strips above skip any grid recipe locked behind a character trait (`requiresTrait`): that is a class mod's reward, such as Sacred Classes' Carpenter planks, beams and firewood. Vanilla has none in these families. Only on worlds that keep class-exclusive recipes on: where that world option is off (the Exploration preset), the game unlocks every such recipe for everyone, so they are stripped as before. `false` strips them too, so every class works logs on the stations. |
| `AllowVanillaSaw` | `true` | Lets an ordinary saw rip on the sawhorses (at `VanillaSawSpeedMultiplier`). `false` = only the pit saw rips. |
| `RemovePitSaw` | `false` | Removes the pit saw (recipe + rip function); only ordinary saws rip on the sawhorses. |
| `CraftableSawhorse` | `false` | Sawhorse becomes a crafted+placed block and disables the in-world stick build (mod-conflict escape). |
| `LogRecoverThreshold` | `0.25` | Progress fraction below which a removed/broken log is returned whole instead of lost. `0` = only untouched, `1` = always whole. |

## chopping.json (chopping block)

Yields for the chopping block live in `drops.json` (see below). This file holds only timing and speed.

| Setting | Default | What it does |
|---|---|---|
| `HalfLogChopStage` | `true` | Chopping a whole log by hand first makes two half-logs (log to 2 half-logs to firewood). `false` = a log chops straight to firewood in one pass (like the automated chopper). Axe wear per log is the same either way at any setting. The firewood total is too at the default 8; on an odd `FirewoodPerLog` the two paths differ by at most 1, always in the player's favour. |
| `AllowFirewoodToSticks` | `true` | Firewood can be chopped into sticks on the block and the automated chopper. `false` = firewood can't be placed on the block or fed into the chopper and yields no drop (only the firewood to sticks step is removed; logs still chop into firewood). |
| `ChopSeconds` | `0.75` | Duration of one axe swing on the block (also sets swing animation speed). |
| `ChopSpeedMultiplier` | `1.0` | Master speed of hand chopping (bar fill per strike). `>1` faster, `<1` slower. `1.0` = one steel-axe strike per log. |
| `FirewoodChopSpeedMultiplier` | `2.25` | How many times faster firewood (to sticks) chops than a whole log. |
| `HalfLogChopSpeedMultiplier` | `2.0` | How many times faster a half-log (to firewood) chops than a whole log. |
| `MaulChopSpeedMultiplier` | `1.5` | Per-strike speed of a splitting maul chopping a whole log straight to firewood (it skips the half-log stage). At `1.5` a steel maul reliably splits a whole log in a single strike — the margin above `1.0` lets it clear the finish line even at the low end of the strike-jitter spread (`ChopProgressJitter`), so it never leaves a leftover second strike — strictly better than an axe (which needs the half-log chain). Lower it below `1` to slow the maul down (`0.5` ≈ twice the strikes). Weaker metals scale by their tier, so a copper maul takes about two strikes at `1.5`; raise the knob to speed those up too. Applies to a maul (with `HalfLogChopStage` on) on a whole log, a log section, or a laid chopping block; a plain axe, billet wood (half-log or firewood) and the `HalfLogChopStage`-off case are unaffected. |
| `ChopProgressJitter` | `0.2` | Random spread on each axe strike's progress (`[1-J, 1+J]`, so `0.2` means each strike counts for 80% to 120% of normal). Makes the strike count per log vary; symmetric, so the average is unchanged except for a tool sitting right on the one-strike line (a steel axe splitting a log), which then averages a touch higher. `0` = off (strictly predictable, as before). Server and client stay perfectly in sync. Hand chopping only. |
| `AxeDurabilityPerLog` | `3` | Axe durability spent to fully process one whole log into firewood. The cost is spread across the chop so a maul (one pass) and an axe (split + two half-logs) both total this. The three chops of the axe path share it: a half-log costs `this / 3` (rounded down, min 1) and the split takes what is left, so splitting a log is never free. Chopping firewood into sticks costs a flat `1` at the default yields. Higher = the axe wears FASTER, lower = slower, `0` = no wear. Only `0` or `3` and up: `1` and `2` leave the split nothing to charge, so they are raised to `3`. (The default was `2` before 1.3.1, where the split was free.) |
| `ChopRestockDelaySeconds` | `0.3` | Pause between axe strikes while right-click is held. `0` = back-to-back swings. |

## flotsam.json (driftwood, broken by hand)

| Setting | Default | What it does |
|---|---|---|
| `FlotsamBreakSeconds` | `~1.11` | Hold-to-break time to snap a driftwood piece into firewood + sticks. |
| `FlotsamFirewoodLarge` | `2` | Firewood from a large driftwood piece. |
| `FlotsamSticksLarge` | `2` | Sticks from a large driftwood piece. |
| `FlotsamFirewoodNormal` | `1` | Firewood from a normal driftwood piece. |
| `FlotsamSticksNormal` | `1` | Sticks from a normal driftwood piece. |
| `FlotsamFirewoodSmall` | `0` | Firewood from a small driftwood piece. |
| `FlotsamSticksSmall` | `2` | Sticks from a small driftwood piece. |

## sawing.json (sawhorses + pit saw)

Yields for the sawhorses live in `drops.json` (see below). This file holds only timing and durability.

| Setting | Default | What it does |
|---|---|---|
| `SawSeconds` | `6` | Total hold time for a steel pit saw to fully rip a log (across 4 stages). |
| `SawDurabilityPerStage` | `1` | Pit-saw durability spent per sawing stage (4 stages per log). `0` = no wear. |
| `VanillaSawSpeedMultiplier` | `0.5` | Ordinary-saw rip speed relative to the pit saw (`0.5` = half). Only when `AllowVanillaSaw` is on. |
| `SawRestockDelaySeconds` | `0.3` | Pause before auto-feeding the next log after a rip. `0` = seamless. |

## debarking.json (sawhorse side action)

| Setting | Default | What it does |
|---|---|---|
| `DebarkSeconds` | `1.5` | Hold time for a steel bark spud (best tool) to fully strip a log. |
| `AllowKnifeDebark` | `true` | Lets a knife be the tier-1 debarking tool (slow). `false` removes the knife tier. |
| `KnifeDebarkSpeedMultiplier` | `0.15` | Knife debark speed relative to the bark spud. |
| `AxeHammerDebarkSpeedMultiplier` | `0.3` | Axe+hammer debark speed relative to the bark spud. |
| `DebarkDurabilityPerLog` | `1` | Durability spent on each debarking tool per stripped log. `0` = no wear. |
| `DebarkRestockDelaySeconds` | `0.3` | Pause before auto-feeding the next barked log after a strip. `0` = seamless. |

## automation.json (sawmill + chopper drive)

Yields for the automated stations live in `drops.json` (see below). This file holds only the drive: processing speed, torque and wear.

How fast a machine turns is set entirely by its mechanical network, not by the config. `SawmillSpeed` and `ChopperSpeed` scale only the WORK a log costs, so a higher value gets through a log faster on the same drive. At `1.0` (the default) each machine behaves exactly as it did before these settings existed.

| Setting | Default | What it does |
|---|---|---|
| `SawmillSpeed` | `1.0` | Sawmill processing speed, as a multiplier on throughput. Higher is faster (less work per log), lower is slower. The turning speed still comes from the network; this only scales the work. The whole visible mechanism speeds up or slows down together, so the saw, feed ratchet and carriage always stay in step. Clamped to the range `0.01` to `100` (a value outside it is pulled in and logged). Replaces the old `SawmillStepsPerLog`; a saved `SawmillStepsPerLog` no longer applies and resets to `1.0`. |
| `SawmillMinTorque` | `0.05` | Minimum network speed below which the sawmill won't turn. `0` = no minimum. |
| `SawmillResistance` | `0.085` | Load the running sawmill adds to its mechanical network. |
| `SawmillBladeWearMultiplier` | `1.0` | Blade-kit durability spent per log. `0` = never wears. |
| `ChopperSpeed` | `1.0` | Chopper processing speed, as a multiplier. Higher is faster, lower is slower; `1.0` is the original pace. As with the sawmill the turning speed comes from the network and this only scales the work. Below `1.0` the chopper splits a log over more than one strike (about `1 / ChopperSpeed`, rounded up); at `1.0` and above it stays one strike per log. Clamped to the range `0.01` to `100`. Replaces the old `ChopperStepsPerLog`; a saved `ChopperStepsPerLog` no longer applies and resets to `1.0`. |
| `ChopperMinTorque` | `0.05` | Minimum network speed below which the chopper won't turn. `0` = no minimum. |
| `ChopperResistance` | `0.085` | Load the running chopper adds to its mechanical network. |
| `ChopperHeadWearMultiplier` | `1.0` | Axe-head durability spent per log. `0` = never wears. |

## drops.json (yields: manual and automated overrides)

Every drop amount lives here so the manual station and its powered counterpart sit side by side. The MANUAL fields are the base. Each automated field is an OVERRIDE that defaults to the sentinel `-1`, meaning "inherit the manual value on the same row." With every override at `-1` the powered station drops exactly what the hand station does, so out of the box the two tiers are identical and can never drift apart. Set an override to `0` or more to give that one machine its own amount; a stray negative other than `-1` is treated as `-1` (inherit). "Amount" fields are the total produced; "pile count" fields are only how many separate item stacks the drop scatters into (they never change the total).

### Chopping (hand block vs automated chopper)

| Manual (block) | Default | Automated (chopper) | Default | What it controls |
|---|---|---|---|---|
| `FirewoodPerLog` | `8` | `ChopperFirewoodPerLog` | `-1` | Total firewood from one log. |
| `FirewoodDropCount` | `4` | `ChopperFirewoodDropCount` | `-1` | Piles the firewood scatters into (capped to the firewood total). |
| `SticksPerFirewood` | `2` | `ChopperSticksPerFirewood` | `-1` | Total sticks from one firewood. |
| `SticksDropCount` | `2` | `ChopperSticksDropCount` | `-1` | Piles the sticks scatter into (capped to the stick total). |

### Sawing (hand sawhorses vs automated sawmill)

| Manual (sawhorses) | Default | Automated (sawmill) | Default | What it controls |
|---|---|---|---|---|
| `PlanksPerLog` | `24` | `SawmillPlanksPerLog` | `-1` | Total planks from one log. |
| `PlankDropCount` | `4` | `SawmillPlankDropCount` | `-1` | Piles the planks scatter into. |
| `BeamsPerDebarkedLog` | `6` | `SawmillBeamsPerDebarkedLog` | `-1` | Total support beams from one debarked log. |
| `BeamDropCount` | `2` | `SawmillBeamDropCount` | `-1` | Piles the beams scatter into. |

## bark.json (bark chain)

The bark-system numbers that used to be baked into the code. The tannin **barrel** litres/seal-hours and the **poultice/decoction** recipe numbers are deliberately **not** here: they live in editable JSON assets you can patch directly (`recipes/barrel/*`, `recipes/cooking/barkbrew.json`, `recipes/grid/poultice-*.json`), and the quern grind ratio is fixed 1:1 by the game.

| Setting | Default | What it controls |
|---|---|---|
| `BarkPerLog` | `3` | How many bark a fully-stripped log drops on the sawhorses, for every wood whose `BarkPerLogBySpecies` line is `-1` or missing. The bark's **kind** is still rolled once per log from `barkdrops.json` (biased by the debark tool), then dropped as a stack of this size, so a higher value gives more bark without changing which kind a log yields — every bark off one log is the same rolled kind. Floored at 1; a value above the bark's max stack size splits across stacks on pickup. |
| `BarkPerLogBySpecies` | each listed wood at `-1` | Per-wood override of `BarkPerLog`, keyed by species code: `"oak": 5, "pine": 2`. `-1` = use `BarkPerLog`. The shipped list (the vanilla woods plus the Wildcraft Trees woods with a special bark) is a template, not a limit: add a line for any species code, from any mod; a wood with no line uses `BarkPerLog`. Keys are case-insensitive. `0` becomes `1`, any other negative becomes `-1`, no upper cap. A shipped line you delete comes back at `-1` on the next launch. |
| `BarkChanceMultKnife` | `0.5` | Multiplier on the special-bark chance when a log is stripped with a knife (tier 1). `1.0` = the bark-spud reference; `0.5` = half the odds a stripped log yields a special bark instead of ordinary bark. Scales the `barkdrops.json` chances only, so a species with no special bark is unaffected. Clamped to [0, 1]. |
| `BarkChanceMultAxeHammer` | `0.75` | Special-bark chance multiplier for an axe+hammer strip (tier 2). |
| `BarkChanceMultSpud` | `1.0` | Special-bark chance multiplier for the bark spud (tier 3, the reference). A copper spud rolls the table's odds exactly. |
| `BarkChanceMultSpudSteelBonus` | `0.25` | Extra special-bark chance a steel spud adds on top of `BarkChanceMultSpud`, scaled by the spud's metal (0 at copper, this at steel). Needs `ScaleWorkSpeedByToolTier` on — that switch governs every metal-tier effect, this one included. Oak's certain tanbark still stays 100% (the roll's own top clamp). |
| `TarPotCapacityPoints` | `64` | Birch-bark "points" that fill a tar pot to the brim. With dry birch worth `TarBirchPointsDry` and wet worth `TarBirchPointsWet`, a full pot is 32 dry or 64 wet birch. |
| `TarBirchPointsWet` | `1` | Fill points one wet (still-drying) birch bark adds. |
| `TarBirchPointsDry` | `2` | Fill points one dry birch bark adds (the thrifty path). |
| `TarYield` | `8` | Tar produced when a full tar pot is opened. A value above tar's max stack size is split across several stacks. |
| `BarkDryHours` | `96` | In-game hours for green tanning / birch bark to dry into its dry state in the player's inventory or in a pile on the ground. In a chest, crate or barrel the game slows drying to a quarter (4× this time). Near a lit heat source bark in a pile or in any of these containers dries up to twice the inventory pace (half this time). A heat source is whatever warms the player in vanilla: a firepit, forge, clay oven, bloomery, coal pile, pit kiln, burning pile, fire, embers, lava, boiling water, and mod blocks that warm the player the same way. A charcoal pit, a beehive kiln or hot ingots on the ground do not count. Applied at load to the item on both sides, so the held-item tooltip matches. It sets the time for bark that starts drying after the change; bark already part-dried keeps its original time. |

## barkdrops.json (asset, not config: which bark each species yields)

Unlike everything above, this is **not** a config file. It is a mod **asset** at `assets/immersivewoodworking/config/barkdrops.json`, read on both sides at load. It is here (an asset, not a config number) for two reasons: it stays out of the config folder rebuild, and any tree mod can add its own species with a plain JSON **patch** rather than code.

Every log stripped on the sawhorse drops bark. A single server-side roll picks the kind from `barkdrops.json` (`bySpecies`). How many bark drop is set in the config, by `BarkPerLog` and `BarkPerLogBySpecies` in `bark.json` (above); the asset used to carry a `countBySpecies` map for that, which is no longer read.

**Format.** `bySpecies` maps a wood species code (the short code the game uses: `oak`, `birch`, `willow`, ...) to an array of special-bark entries:

```
"oak": [ { "kind": "tan", "chance": 1.0 } ]
```

- `kind` is one of `tan`, `birch`, `bast`, `medicinal` — special kinds only. `generic` is never listed; it is the **remainder**.
- `chance` is a fraction `0.0`–`1.0`.
- Several entries per species are allowed (see `willow`); they are rolled as cumulative intervals, and the leftover probability (`1.0 - sum`) is ordinary `bark-generic`.
- The per-species sum must be `<= 1.0` (the loader warns and clamps otherwise).
- An empty array, or a species absent from the table, always yields `bark-generic`. So any wood from any mod works with no entry at all.

Each `kind` resolves to an item when it drops (fresh bark is the green state where one exists): `tan` → `bark-tan-green`, `birch` → `bark-birch-green`, `bast` → `bark-bast`, `medicinal` → `bark-medicinal`.

**Default table** (draft balance; the special chance, remainder is `bark-generic`):

| Species | Special bark | Chance |
|---|---|---|
| `oak` | tan | 100% |
| `acacia` | tan | 80% |
| `larch` | tan | 50% |
| `pine` | tan | 25% |
| `birch` | birch | 100% |
| `baldcypress` | bast | 40% |
| `redwood` | bast | 30% |
| `maple` | medicinal | 25% |
| `kapok` | medicinal | 60% |
| `ebony` | medicinal | 60% |
| `walnut`, `purpleheart`, `aged` | none | generic only |
| `willow` (Wildcraft) | bast + medicinal | 40% + 40% |
| `linden`, `elm`, `poplar` (Wildcraft) | bast | 70% |
| `spruce`, `fir` (Wildcraft) | tan | 25% |

The debarking tool tier and the xSkills forestry perk later multiply the special chance; those multipliers are config scalars, not part of this table.

## integration.json (Toolsmith sharpness + xSkills + Seraph Leveling)

Sharpness is only active with the Toolsmith mod installed; otherwise both values are inert (speed always 1.0). The xSkills settings in this file are only active with the xSkills mod installed; see `config-new-params.md` for the full xSkills list. `SeraphLevelingIntegration` is only active with Seraph Leveling ReClassed installed.

| Setting | Default | What it does |
|---|---|---|
| `SharpnessSpeedFloor` | `0.5` | Rip/strip speed of a fully blunt pit saw / bark spud vs a sharp one. `1.0` = sharpness ignored. |
| `ChopSharpnessCeiling` | `1.4` | Chop speed of a razor-sharp axe vs mid-sharpness. `1.0` = no sharp-side bonus. |
| `SeraphLevelingIntegration` | `true` | Planks and beams ripped on the sawhorses count as crafted for Seraph Leveling, like the grid recipe the rip replaces, so the Carpenter trait (120 boards) can progress. Every sawyer on the log gets the full batch. The automated sawmill counts for nobody. `false` if a future Seraph version counts the sawhorses itself. |
