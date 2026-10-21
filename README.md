# Immersive Woodworking

*Stop crafting your firewood in a magic 2×2 grid. Chop it. Saw it. Earn it.*

**Immersive Woodworking** replaces Vintage Story's instant, hand-crafted woodworking with hands-on, in-world stations. Logs no longer turn into firewood or planks by dropping them next to a tool in the crafting menu — instead you set the wood on a real workstation and put the tool to it, axe-swing by axe-swing and stroke by stroke. It's a small, focused immersion mod that makes processing timber feel like work, not paperwork.

Requires Vintage Story **1.22.2+**.

---

## What it adds

### 🪓 The Chopping Block
A sturdy stump for splitting wood with an axe, available in every wood type. Craft one by combining an **axe + a placed log** in the grid; like the log it came from, it can also be **burned as fuel** in a firepit.

- Place a **log, firewood, or driftwood** on the block with right-click.
- Hold **right-click with an axe** to split it.
- A **log** yields **firewood**; **firewood** and **driftwood** yield **sticks**.
- Each chop costs axe durability scaled to the amount produced — roughly on par with vanilla hand-crafting.

### 🔨 The Splitting Maul
A heavy maul for splitting logs on the chopping block. Where an axe first halves a whole log and then splits each half into firewood, the maul drives a whole log straight into firewood in a single pass.

- Forge the **maul head** on an anvil from **2 ingots**, then assemble it with a **stick** like an axe. If you run the **Toolsmith** mod, you can build and sharpen it on the workbench instead.
- Available in **every metal tier**, and it inherits the quench-buff behaviour of iron and steel heads.
- It is built for splitting, so it makes a poor felling axe.

### 🪚 The Sawhorses (pit-saw trestle pair)
A two-piece rig for ripping logs lengthwise — the immersive replacement for sawing planks in the crafting grid.

- **Build a trestle in-world**: place a stick, then a second stick, then a length of **rope** (right-click) to finish one trestle. Set a **second trestle right beside it at a right angle** and the two lock together into a working pair.
- Lay a **log** or **debarked log** across the finished pair with right-click.
- Stand at **either end**, hold **right-click with a pit saw**, and rip it lengthwise. Sawdust flies as you work, and the cut pieces drop off in piles.
- Pick the cut with the saw's **tool mode** (press **F** at the pair): any log, barked or debarked, rips into **planks** or **support beams** — always in the same wood species as the tree it came from.
- **Co-op sawing:** two players can rip the same log together, one at each end — both saws share the work and both wear down.
- The **pit saw** is the purpose-built tool and the fast one. By default an ordinary saw works here too, at a fraction of that speed (`VanillaSawSpeedMultiplier`). Set `AllowVanillaSaw` to `false` to block ordinary saws entirely — they then just show a *“need a pit saw”* prompt (no animation, particles or sound). Pull an untouched log back off with **Ctrl + right-click**; once a cut has begun the wood is committed.

### ⚒️ The Pit Saw
A two-handed rip saw, the dedicated tool for the sawhorses. Crafted from **1 pit saw blade + 1 stick**, and available in **every metal tier** — copper, tin/bismuth/black bronze, silver, gold, iron, meteoric iron, and steel. It inherits the saw's quench-buff behaviour, so quench bonuses carry over to a freshly forged one.

### ⚙️ The Automated Sawmill
A mechanically-powered mill that rips timber hands-off, the powered counterpart to the sawhorses. Assemble it from a frame plus its parts, then drive it with an axle.

- Place the **sawmill frame**, then install its parts with right-click (a carriage, saw sash, crankshaft and feed levers), then slot a **blade kit** into the sash.
- Drive it with an **axle from a windmill or water wheel**, and it runs on its own.
- Feed a **log** in by hand or through a chute; it rips into **planks** or **support beams** in a single pass. Cycle which with **Shift + right-click** on the frame.
- The **metal of the blades** sets their durability, and blade kits come in every metal tier.

### ⚙️ The Automated Chopper
A mechanically-powered chopper that splits firewood hands-off, the powered counterpart to the chopping block. Assemble it from a frame plus its parts, then drive it with an axle.

- Place the **chopper frame**, then install its parts with right-click (a drive, an arm and a chopping block as the bed), then slot a **chopper head** into the arm.
- Drive it with an **axle from a windmill or water wheel**: the reduction gearing trades many turns of the shaft for one slow, heavy fall of the axe.
- Feed a **log or firewood** in by hand or through a chute: a log goes straight to **firewood** and firewood to **sticks**, ejected in front.
- The **metal of the head** sets its durability, and heads come in every metal tier.

---

## Why use it

- **Real, animated work.** Stations use full first/third-person tool animations — axe swings, two-man sawing strokes, flying wood chips and sawdust — instead of a silent grid craft.
- **Drop-in survival fit.** Yields, durability costs and timings are tuned to stay close to vanilla balance, so it slots into a normal playthrough without breaking the early game.
- **Multiplayer-ready.** Server-authoritative timing and progress, plus genuine two-player co-op sawing.
- **In-game documentation.** Every station and the pit saw ship with handbook pages and on-screen interaction hints — no wiki needed.
- **Localized.** English, Russian, German, Brazilian Portuguese, and Simplified Chinese included.

---

## Configuration

The config lives in a folder of thematic files (`ModConfig/ImmersiveWoodworking/`), generated on first run and synced from the server to all clients. If you are updating from an older version that used a single `ModConfig/immersivewoodworking.json`, your values are migrated into the folder automatically on first launch and the old file is kept as a `.bak` backup, so there is nothing to do by hand. Every value is range-checked on load, so a bad hand-edit can't crash the mod. The table below highlights the common settings; see `docs/config-reference.md` for the full list.

| Setting | Default | What it does |
| --- | --- | --- |
| `FirewoodPerLog` | 8 | Firewood produced from one log |
| `SticksPerFirewood` | 2 | Sticks produced from one firewood |
| `ChopSeconds` | 0.75 | Hold time per chop (also drives swing speed) |
| `AxeDurabilityPerLog` | 3 | Axe durability spent per whole log (spread across the chop; higher = wears faster, 0 = no wear) |
| `PlanksPerLog` | 24 | Planks ripped from one log |
| `BeamsPerDebarkedLog` | 6 | Support beams ripped from one debarked log |
| `SawSeconds` | 6 | Total hold time to fully rip a log |
| `SawDurabilityPerStage` | 1 | Saw durability spent per sawing stage (0 = no wear) |
| `AllowVanillaSaw` | true | Let any ordinary saw (vanilla **or** from another mod — anything with `tool: "saw"` that isn't the pit saw) rip on the sawhorses, at `VanillaSawSpeedMultiplier` speed. Set **false** to block ordinary saws entirely — the attempt is then refused with a "need a pit saw" prompt (no animation/particles/sound) and only the pit saw rips |
| `VanillaSawSpeedMultiplier` | 0.5 | When `AllowVanillaSaw` is on, how fast an ordinary saw rips vs the pit saw (1.0 = equal, 0.5 = half speed / twice as slow, so the pit saw cuts twice as fast). No effect when `AllowVanillaSaw` is off |
| `FirewoodDropCount` / `SticksDropCount` / `PlankDropCount` / `BeamDropCount` | 4 / 2 / 4 / 2 | How many item piles each drop is split into |
| `ChopProgressJitter` | 0.2 | Random spread on each axe strike's progress, so a log takes a slightly different number of strikes each time. Symmetric, stays in sync in multiplayer. `0` turns it off |
| `MaulChopSpeedMultiplier` | 1.5 | Per-strike speed of the splitting maul on a whole log. `1.5` = a steel maul reliably one-shots a log (margin over `1.0` so the strike jitter never leaves a second strike). Lower it to slow the maul down |
| `DropsAtPlayer` | false | Drop everything the chopping block and sawhorses produce at the working player's feet instead of around the station, where it is picked up like any dropped item. The automated machines are unaffected |
| `DisableVanillaFirewoodCrafting` | true | Removes the vanilla axe-in-grid → firewood recipe |
| `DisableVanillaPlankCrafting` | true | Removes the vanilla saw-in-grid → planks/beams recipe |

By default the mod **disables the vanilla grid recipes** for firewood, planks and beams, so these can only be made at the new stations. Set either flag to `false` to keep hand-crafting alongside them. Recycling recipes (plank block → planks) and other saw recipes (cabinet, crate, table…) are never touched. Recipes a class mod locks behind a trait (Sacred Classes' Carpenter, for example) are kept by default, because they are that class's reward; `KeepTraitRecipes` set to `false` strips them too. With **Seraph Leveling ReClassed** installed, boards ripped on the sawhorses count toward its Carpenter trait just like grid-crafted ones (`SeraphLevelingIntegration`, on by default).

The **automated sawmill and chopper** can be tuned to yield different amounts than the hand stations they mirror (the `Chopper*` and `Sawmill*` overrides in `drops.json`). Out of the box each inherits the matching hand-station value, so the powered stations drop exactly what the hand stations do until you set an override.

A server can switch off the automated sawmill, the automated chopper or the whole bark chain (`EnableSawmill`, `EnableChopper`, `EnableBarkChain` in `general.json`). A switched-off part can no longer be made and is hidden from the creative inventory and the handbook, while machines already built keep working. With the bark chain off a stripped log gives no bark, and the vanilla log-to-tannin barrel recipes come back.

---

*Author: Bobrik00 · mod id: `immersivewoodworking`*
