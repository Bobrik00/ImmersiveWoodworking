# Source annotations: plan IDs and named invariants (for maintainers)

Developer note, not shipped in the mod zip (like `config-reference.md` and `recipe-conventions.md`).
Read this when a comment in the source says `E03`, `T06.02`, or refers to a *mod-independence rule*.

---

## 1. Plan IDs (`E0x` / `T0x.yy`) in comments

The mod is built plan-first: every feature gets a task-planner tree (`<Name>Plan/`) of **epics** (`E01`, `E02`, ...)
and **tasks** (`T03.07` = task 07 of epic 03). Source comments cite those IDs so a line of code can be traced back
to the decision that produced it. Finished trees are archived to `Plans_archive/*.rar` (see the README there for how
to read one without unpacking).

### ⚠️ The numbers are per-plan, not global

`E03` alone is **ambiguous** — every plan has one, and they mean twelve different things:

| Plan | `E03` is |
|---|---|
| `AuditFixPlan` | wear, tiers & acceptance gates |
| `AutoChopperPlan` | multiblock assembly |
| `AutomatedSawmillPlan` | multiblock assembly |
| `AutoRestockPlan` | polish |
| `BarkPlan` | tannin: grinding & the barrel |
| `ChoppingBlockRedesignPlan` | inventory surfaces |
| `DebarkingPlan` | animation / fx |
| `HalfLogBlockPlan` | the slab block: JSON + class |
| `ImprovementsPlan` | pit-saw compat with saw-requiring recipes |
| `LogSawingPlan` | sawhorse construction |
| `ManualOpsBalancePlan` | chop progress bar |
| `ToolsmithIntegrationPlan` | sharpness → speed |
| `XSkillsIntegrationPlan` | apply yield / durability |

**Rule when writing new comments:** inside a file that clearly belongs to one subsystem, a bare ID is fine — the
owning plan is obvious from the table below. In **shared or infrastructure** code that several plans touch
(`ImmersiveWoodworkingModSystem*.cs`, `ImmersiveWoodworkingConfig.cs`, shared helpers), always **qualify** it:
`AutoChopperPlan E03/T03.07`, never a bare `E03`.

### Which plan owns which code

| Plan | Owns (subsystem / files) | Epics |
|---|---|---|
| `LogSawingPlan` | Sawhorse + pit saw, manual ripping (`BlockSawhorse`, `BlockEntitySawhorse.*`) | 01 config & recipe removal · 02 pit-saw item · 03 sawhorse construction · 04 multiblock pairing · 05 log placement & render · 06 sawing mechanic · 07 integration polish |
| `DebarkingPlan` | Bark stripping on the sawhorse (`BlockEntitySawhorse.Debarking.cs`, bark spud) | 01 thin slice · 02 bark spud · 03 animation & fx · 04 progressive visual · 05 integration polish · 06 deferred bark drop |
| `ChoppingBlockRedesignPlan` | Chopping block as a data block (`BlockChoppingBlock`, `BlockEntityChoppingBlock`) | 01 data-block core · 02 runtime wood render · 03 inventory surfaces · 04 generic recipe · 05 migration · 06 integration |
| `ManualOpsBalancePlan` | Balance of the hand operations: yields, tool tiers, chop progress, half-logs | 01 drops · 02 tier speed · 03 chop progress bar · 04 half-log item · 05 chop visual stages · 06 spill on remove · 07 flotsam hand-break · 08 log sections · 09 pit-saw blade strip · 10 README · 11 floating chop speed multiplier · 12 chopper assembly & balance |
| `AutomatedSawmillPlan` | Powered sawmill (`BlockSawmill*`, `BlockEntitySawmill.*`, `Sawmill{Anim,Parts,Renderer}`) | 01 config · 02 blades & recipes · 03 multiblock assembly · 04 mechanical power · 05 inventory io · 06 sawing cycle · 07 visual & sound · 08 integration polish · 09 part materials |
| `AutoChopperPlan` | Powered chopper (`BlockChopper*`, `BlockEntityChopper.*`, `Chopper{Anim,Parts,Renderer}`) | 01 config · 02 head & recipes · 03 multiblock assembly · 04 mechanical power · 05 inventory io · 06 chopping cycle · 07 visual & sound · 08 integration polish |
| `AutoRestockPlan` | Auto-feeding logs from the player inventory (`AutoRestockUtil.cs`) | 01 foundation · 02 station feed · 03 polish |
| `ImprovementsPlan` | v1.2.1 polish pack: the maul, the config FOLDER, chop jitter, recipe compat (`DropAmounts.cs`, the config I/O in `ImmersiveWoodworkingModSystem.cs`, `ImmersiveWoodworkingConfig.cs`) | 01 half-log visual-stage threshold · 02 per-metal recipe expansion · 03 pit-saw compat with saw-requiring recipes · 04 config reorg: folder, files, drops · 05 chop-progress jitter · 06 the maul: log straight to firewood · 07 verification & release |
| `BarkPlan` | The bark chain: tanning, birch tar, bast, medicinal bark (`BarkDropTable.cs`, `TarRig.cs`, `TarDbg.cs`, `BlockTarPot`, `BlockPerforatedPot`, `ItemTarPot`, `BehaviorTarPotOpen`, `BehaviorSneakToPlace`) | 01 bark items: kinds, tags, drying · 02 debark drop table · 03 tannin: grind & barrel · 04 birch tar · 05 bast & medicinal bark · 06 ordinary-bark sinks & storage · 07 compat: bark mods, species, roofing · 08 config, verification, release |
| `HalfLogBlockPlan` | The half-log as a building block (`BlockHalfLogSlab.cs`, `BlockHalfLogSlab.Wrench.cs`, `HalfLogPlacement.cs`, `WoodName.cs`) | 01 spike: per-species variant generation · 02 model & profile · 03 block: JSON + class · 04 placement from the hand & drop · 05 rotations: the wrench · 06 lang & handbook · 07 verification & release |
| `ToolsmithIntegrationPlan` | `pitsaw` / `barkspud` ↔ Toolsmith heads and sharpness | 01 pit-saw blade head · 02 crafting recipes & tinkerable · 03 sharpness → speed · 04 visual parity · 05 docs & test |
| `XSkillsIntegrationPlan` | Own forestry abilities + XP (`ImmersiveWoodworkingModSystem.XSkills.cs`, `XSkills*.cs`) | 01 soft-dependency infra · 02 tier-1 xp grants · 03 apply yield & durability · 04 swift-saw speed · 05 test & verification |
| `AuditFixPlan` (ACTIVE) | Fixing the second audit's findings (`AuditPlan2/FINDINGS.md`), release 1.3.1. No subsystem of its own: its IDs sit in ~50 files across every station | 01 eyes-on baseline · 02 client/server seams · 03 wear, tiers & acceptance gates · 04 half-log, species & textures · 05 hot render paths · 06 sound & particles · 07 stations: pairing, multiblock, claims, mech power · 08 bark & tar chain · 09 player-facing text · 10 assets, build, docs, release |

Plans still in the mod root (not yet archived) are listed in `Plans_archive/README.md`.

---

## 2. The mod-independence rules

Several comments used to cite these as `invariant #N`. That numbering pointed at a list that no longer exists
anywhere in the repository — checked against the source, `INVARIANTS.md` inside `Plans_archive/AuditPlan.rar`,
and every archived plan tree (nine at the time of that check, twelve today). The **rules themselves are real and load-bearing**, so they are stated in full here and the comments now name
them instead of numbering them.

All four exist for the same reason: **a wood mod must work without this mod knowing it exists.** No species is ever
enumerated in code, and no code path may reintroduce the historical "assume `game:`" bug.

### Rule: accept by code shape, never by species

Every acceptance gate tests the *form* of the item code (is it a placed log / a debarked log / a log section /
firewood / a half-log), never a whitelist of species or domains. A modded log such as
`wildcrafttree:log-placed-azobe-ud` is therefore accepted automatically, with no compat patch.

Lives in `BlockEntityChoppingBlock.IsChoppable` and `BlockEntitySawhorse.IsSawableInput`; every other gate
(`BlockEntityChopper.IsValidInput`, `InventoryChopper.GetSuitability`, `BlockEntitySawmill.IsValidInput`,
`InventorySawmill.GetSuitability`) reuses one of those two rather than writing a second copy.

### Rule: the chop product is vanilla on purpose

Chopping always yields `game:firewood` / `game:stick`, hardcoded. That is **deliberate, not a missed generalisation**:
for chopping, the species drives the stand's paint only, never the product. Do not "fix" it into a per-species lookup.

Lives in `BlockEntityChoppingBlock.GetChopResult` / `MakeFirewood`; the powered chopper reuses it verbatim
(`BlockEntityChopper.GetChopBatch`) so the machine can never drift from the hand stand.

**One exception, and only one** (`BlockEntityChoppingBlock.ResolveFirewood`, AuditFixPlan T04.05): where a separate
firewood item for that species actually exists, it is used. The exception exists because **vanilla itself makes it** -
`recipes/grid/firewood.json` turns `log-placed-aged-ud` into `agedfirewood` and every other log into `firewood`. With
the stand replacing that recipe, chopping an aged log produced ordinary firewood while the grid still produced the
aged kind: one wood, two doors, two products.

What keeps it from becoming the per-species lookup the rule forbids:

- **No species is named in code.** The product is probed by SHAPE - `{wood}firewood`, in the log's own domain and then
  `game` - so a wood mod that ships one gets it with no patch from us, exactly as its logs are already accepted.
- **It cannot fail.** Nothing found falls back to the single hardcoded `game:firewood`, which is why this does not owe
  a "product must resolve" gate at the door (the rule below): there is no door to refuse anything at.
- **Symmetrically, the input side is a shape too.** `IsFirewoodCodePart` (ends with `firewood`) is what lets the
  species-specific firewood be laid back on the stand and chopped into sticks. Without that half, giving `agedfirewood`
  out would have been a dead end, and the same predicate is what tells the recipe strip
  (`ImmersiveWoodworkingModSystem.IsLogToFirewood`) to remove vanilla's aged recipe along with the rest.

### Rule: a per-species product must resolve, or the input is refused at the door

Sawing *does* have a per-species product (`plank-{wood}`, `supportbeam-{wood}`), so the opposite risk applies: a
modded log whose plank does not exist would be parked inside the machine forever. The acceptance gate therefore also
checks that the product resolves — in the log's **own domain first**, then `game` — and refuses the log up front if
it does not.

Lives in `BlockEntitySawhorse.SawProductResolves` (with `DebarkProductResolves` for the strip; `CanProcessInput`
is their disjunction and stands on the put-down), built on the sawhorse's single domain→game resolver
(`BlockEntitySawhorse.ResolveProductItem` / `ResolveProductBlock`). The sawmill calls it from there — it used to own
the body as `BlockEntitySawmill.CanResolveProduct`, moved by `AuditFixPlan` T03.03 because it only ever read
sawhorse classifiers. Never write a second resolver.

### Rule: textures come from the log itself

Every species texture goes through one resolver, `WoodTextures`, so a modded species paints correctly without being
listed anywhere. For one family (bark, debarked, treetrunk, treetrunk/debarked) it tries, in order:

1. `{domain}:block/wood/{family}/{wood}` in the species' own domain;
2. the same path in `game`, because Wildcraft Trees keeps bark and end grain in its domain but ships most debarked
   textures under `game`;
3. the texture the species' own log block declares (`log-placed-{wood}-ud`, `debarkedlog-{wood}-ud`, in the species'
   domain and then in `game`). This covers a log whose textures do not sit at the vanilla path of its species, for
   example a wood mod that adds its species to `game:worldproperties/block/wood` and points vanilla's generated logs
   at its own textures by patching `texturesByType`.

The log comes last on purpose: a texture steps 1 or 2 found before is still the same file (Wildcraft's
`chlorociboria` ships a debarked texture in both domains as two different files). Step 3 only fills a family that
used to miss and fall through to the in-species ladder, so such a face now shows what the log itself declares (seen
on Floral Zones Circumboreal: split faces and debarked caps of six species). Step 3 reads `Block.Textures`, which only
the client keeps, so the resolver is client-only.

The captured `woodDomain` is still the mod's riskiest domain assumption: a stack that carries no explicit one falls
back to `game`, so a missing or wrong `woodDomain` yields the unknown texture instead of a loud failure. Accepted by
design, for wood-mod compatibility.

Lives in `WoodTextures.cs`. `LogTexSource.ForLog` / `ForSpecies` bind it to a mesh for the sawhorse, the sawmill, the
chopping block, the chopper and `ItemHalfLog`; the chopping block's stand and the half-log slab call it directly.
