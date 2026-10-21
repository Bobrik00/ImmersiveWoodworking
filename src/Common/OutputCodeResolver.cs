using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Util; // ObjectCacheUtil

namespace ImmersiveWoodworking;

/// <summary>
/// Turns a server-configured OUTPUT-item CODE — the string a station drops, e.g. <c>game:firewood</c> or the
/// species template <c>plank-{wood}</c> — into the live <see cref="CollectibleObject"/> to drop, substituting the
/// wood species and resolving the domain the same way the stations hardcode it today. The sibling of
/// <see cref="DropAmounts"/> / <see cref="DropDistribution"/> (which already make the DROP quantity configurable);
/// this makes the drop's KIND configurable (ConfigurabilityPlan E01/T01.01). One resolver, so every station's
/// output resolves by the SAME rule and the four stations can never drift — the same "thread the resolved value,
/// don't copy the resolver" discipline as <see cref="DropAmounts"/>.
///
/// <para>Placeholders in the template, substituted before lookup:</para>
/// <list type="bullet">
/// <item><c>{wood}</c> → the log's species (<c>plank-{wood}</c> → <c>plank-oak</c>; the dash-less
/// <c>{wood}firewood</c> → <c>agedfirewood</c> works too — it is a plain string replace, so the template owns the
/// separator).</item>
/// <item><c>{domain}</c> → the log's domain. Rarely needed, because a BARE path already resolves in the log's
/// domain (below); its one distinct use is forcing the log's domain with NO game fallback (see the domain rule).
/// Whether the config docs actually expose <c>{domain}</c> is ConfigurabilityPlan T01.04's open question — the
/// resolver supports it either way so that decision needs no code change here.</item>
/// </list>
///
/// <para>Domain rule, matching the stations' current behaviour (memory <c>vs-wood-mod-compat</c>: a station's drop
/// resolves in the LOG's domain so a wood mod's own <c>wildcrafttree:plank-azobe</c> works, not only <c>game:</c>):</para>
/// <list type="bullet">
/// <item>A BARE code (no <c>domain:</c> prefix) is looked up in <paramref name="logDomain"/> FIRST, then <c>game</c> —
/// the exact fallback <see cref="BlockEntitySawhorse.ResolveProductItem"/> /
/// <see cref="BlockEntityChoppingBlock"/>'s <c>ResolveFirewood</c> use.</item>
/// <item>An EXPLICIT domain (<c>game:plank-{wood}</c>, <c>wildcrafttree:plank-{wood}</c>) is looked up in THAT domain
/// only, with no game fallback — "as written" forces the domain regardless of the log's.</item>
/// </list>
///
/// <para>At each candidate location the code is tried as an ITEM first, then a BLOCK, so one method resolves every
/// station's output whatever its class — the firewood/sticks/planks the stations drop are items, a support beam is a
/// block, and a server may configure either; no current output code exists as both, so the item-first order is
/// unambiguous. Returns <c>null</c> when nothing resolves — the caller (T01.02) is what turns that into a fallback to
/// the hardcoded default plus a load warning; this stays a pure lookup and never logs. No IsMissing guard, matching
/// <see cref="BlockEntitySawhorse.ResolveProductItem"/>: a config-typo'd code was never registered, so
/// <see cref="IWorldAccessor.GetItem"/>/<see cref="IWorldAccessor.GetBlock"/> return null, not a removed-mod stub.</para>
///
/// <para>Server-side: outputs are resolved when a station drops, on the server thread. The per-world cache is a plain
/// <see cref="Dictionary{TKey,TValue}"/> in <c>api.ObjectCache</c> (memory
/// <c>vs-objectcache-not-threadsafe-dies-with-clientmain</c> — not thread-safe, fine here since resolution never runs
/// off the server thread) keyed by (template + species + domain); a config re-sync that edits a template simply keys
/// fresh, leaving the stale entry unread. A null resolution is cached too — within one world a code that does not
/// resolve now never will.</para>
/// </summary>
internal static class OutputCodeResolver
{
    // One per-world dictionary under this key, holding every (template|wood|domain) → resolved collectible this world
    // has looked up. iw* prefix like the mod's other ObjectCache entries (BlockChoppingBlock, BlockSawhorse, …).
    const string CacheKey = "iwOutputCodeResolveCache";

    /// <summary>
    /// The collectible a station should drop for output <paramref name="template"/> on a log of species
    /// <paramref name="wood"/> from domain <paramref name="logDomain"/>, or <c>null</c> if the code resolves to
    /// nothing (a fixed code that does not exist, or a species/domain the template's item was never registered for —
    /// the caller decides the fallback). Cached per world by (template, wood, domain).
    /// </summary>
    internal static CollectibleObject? Resolve(ICoreAPI api, string template, string? wood, string logDomain)
    {
        if (string.IsNullOrEmpty(template)) return null;
        if (string.IsNullOrEmpty(logDomain)) logDomain = "game";

        Dictionary<string, CollectibleObject?> cache = ObjectCacheUtil.GetOrCreate(
            api, CacheKey, () => new Dictionary<string, CollectibleObject?>());

        // '\n' can't occur inside a template, species or domain, so it's a collision-free key separator.
        string key = template + "\n" + (wood ?? "") + "\n" + logDomain;
        if (cache.TryGetValue(key, out CollectibleObject? cached)) return cached;

        CollectibleObject? resolved = ResolveUncached(api.World, template, wood, logDomain);
        cache[key] = resolved;
        return resolved;
    }

    /// <summary>An <see cref="ItemStack"/> of <paramref name="stackSize"/> for a resolved
    /// <paramref name="collectible"/>, picking the block or item stack constructor by the collectible's own class —
    /// the one place the Block-vs-Item split for a config-driven output is handled, so the stations that consume
    /// <see cref="Resolve"/> (T01.03–T01.05) don't each re-derive it. A support beam resolves to a Block, everything
    /// else to an Item; both are valid stacks.</summary>
    internal static ItemStack MakeStack(CollectibleObject collectible, int stackSize)
        => collectible is Block block
            ? new ItemStack(block, stackSize)
            : new ItemStack((Item)collectible, stackSize);

    // The uncached lookup: substitute the placeholders, then resolve the resulting code by the domain rule above.
    static CollectibleObject? ResolveUncached(IWorldAccessor world, string template, string? wood, string logDomain)
    {
        string code = template
            .Replace("{wood}", wood ?? "")
            .Replace("{domain}", logDomain);

        // Explicit "domain:path" → that domain only, no game fallback ("as written" forces it).
        if (code.IndexOf(':') >= 0)
            return At(world, new AssetLocation(code));

        // Bare path → the log's domain first, then game — the wood-mod-compat fallback the stations already use.
        CollectibleObject? found = At(world, new AssetLocation(logDomain, code));
        if (found != null || logDomain == "game") return found;
        return At(world, new AssetLocation("game", code));
    }

    // One candidate location: an item if the code names one, else a block, else null (both GetItem and GetBlock
    // return null for an unregistered code — memory vs-getblock-silent-on-missing-code).
    static CollectibleObject? At(IWorldAccessor world, AssetLocation loc)
    {
        Item? item = world.GetItem(loc);
        if (item != null) return item;
        return world.GetBlock(loc);
    }
}
