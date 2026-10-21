# Recipe conventions (for maintainers)

Developer note, not shipped in the mod zip (like `config-reference.md`). Read this before adding a crafting recipe.

## The named-wildcard rule

> If an ingredient's `code` contains `*` **and** it has a `name`, **and** that `{name}` is substituted into the recipe's **output** `code`, then the ingredient MUST carry an `allowedVariants` array that closes the wildcard to exactly the variants the output itemtype has.

**Why.** The engine (`RecipeBase.GetNameToCodeMappingForBasicWildcard`) expands a named wildcard over *every* collectible matching the mask, including variants added by **other mods** (metal mods add `ingot-uraniumsteel`, `sawblade-meteoricsteel`, ...). If `{name}` feeds the output and the output only exists for our own fixed variant set, the mod-added variants resolve to a **non-existent output** and the game logs a `[Warning]` + `[Error]` at load for each. `allowedVariants` bounds the expansion to variants that actually exist, so mod metals are simply skipped.

**Reference examples (already correct):**
- `recipes/smithing/pitsawblade.json`, `recipes/smithing/chopperheadhalf.json`, `recipes/smithing/barkspudhead.json` — `game:ingot-*` name `metal` + `allowedVariants` (9 metals).
- `recipes/grid/sawmill_blade.json` — `game:sawblade-*` name `metal` + `allowedVariants`; the list MUST mirror the `metal` variantgroup in `itemtypes/sawmill/blade.json` (single source of truth).

## Exceptions — when `allowedVariants` is NOT needed

1. **Mod-own input.** If the named wildcard's `code` points at *this mod's own* item (no `game:` prefix, resolves to `immersivewoodworking:`), other mods cannot add variants to it, so the expansion is already bounded. Examples: `pitsaw.json` (`pitsawblade-*`), `barkspud.json` (`barkspudhead-*`), `chopper_head.json` (`chopperheadhalf-*`).
2. **`{name}` does not feed the output.** A named wildcard whose `{name}` is *not* in the output `code` cannot produce a non-existent output, so it never warns. It still causes registry **bloat** (a combinatorial cross-product of sub-recipes) — use a `name` here *only* when you deliberately need several cells to share one variant (e.g. "all N cells the same wood"); otherwise drop the `name` so the ingredient is a plain wildcard. (See `sawmill_blade.json` ingredient `B`: `name` was dropped in T02.03 because the bit metal is irrelevant to the output.)
3. **`enabled:false` code-stamped templates.** Recipes registered from C# (`RegisterChoppingBlockRecipes`, `RegisterWoodDomainRecipes` → `RegisterSawmillRecipes`/`RegisterChopperRecipes`, `RegisterSawhorseCraftingRecipe`) ship with `enabled:false` so the `RecipeLoader` skips them; they are resolved manually via `GridRecipe.Resolve`, which **bypasses** `GenerateRecipesForAllIngredientCombinations`. They do not undergo the combinatorial wildcard expansion at all, so the rule above does not apply to them. The 8 sawmill/chopper structural templates plus `choppingblock.json` and `sawhorse.json` are in this group.

   **On this path `name` binds nothing** — and that is the intended behaviour, not an oversight (author decision 2026-08-29, `AuditFixPlan/PLAN.md` deviation for `T10.02`, audit finding `F-L7-20`). "All N cells the same variant" is not a match-time rule anywhere in the engine: `CraftingRecipeIngredient.SatisfiesAsIngredient` never reads `Name`, and homogeneity is a *side effect* of the loader having replaced the wildcard with a concrete variant in each generated sub-recipe. Skip the expansion and every named cell matches independently, so a player may assemble one part from several woods and several metals at once. The 8 structural templates keep their `name` keys purely as documentation of which cells hold the same material; their outputs bake neither species nor metal, so nothing downstream can tell a mixed craft from a clean one.

   Consequences for anyone touching these files:
   - Do not add a `name` to a template in this group expecting it to restrict anything, and do not remove one thinking it changes behaviour. Both are no-ops here.
   - Flipping one of these templates to `enabled: true` (or otherwise routing it through the `RecipeLoader`) silently *turns binding on* and multiplies the registry: with the current `name` keys the 8 templates expand to ~5700 sub-recipes on vanilla alone and several times that with a wood mod installed, because a `name` on a **single-cell** ingredient (levers' plate, arm's and drive's nails, chopper frame's plank) binds nothing yet still multiplies. Strip those first if you ever go that route.
   - The handbook is a separate expander and unaffected by any of this: `SlideshowGridRecipeTextComponent.ResolveIngredients` clones a recipe per variant for a NAMED wildcard and per-cell shuffles an UNNAMED one, for display only.
