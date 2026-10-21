using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ImmersiveWoodworking;

/// <summary>
/// Shared inventory scan behind the <see cref="ImmersiveWoodworkingConfig.AutoRestock"/> convenience: locates the
/// next log to auto-feed onto a manual station from the WORKING player's own pack. Used by both stations —
/// <see cref="BlockEntityChoppingBlock"/> (after a chop) and <see cref="BlockEntitySawhorse"/> (after a log is
/// fully ripped) — so the "what do I pull next" rule lives in ONE place and can't drift between them.
///
/// Two design points both stations rely on:
///  • The active hotbar slot is NEVER returned — on either station the player is holding the TOOL there (axe /
///    saw), so the next log must come from any OTHER slot.
///  • The feed prefers continuity: a caller passes a <c>preferred</c> predicate (the same species / kind it just
///    processed) tried first, and a broader <c>accept</c> predicate (any acceptable input) as the fallback — so a
///    held rip or a repeated chop stays on one wood while a matching one is left, instead of randomly switching.
/// </summary>
internal static class AutoRestockUtil
{
    /// <summary>
    /// First inventory slot of <paramref name="player"/> holding a stack the station can take next, or null if
    /// none qualifies (the caller then leaves the station empty, exactly as without auto-restock). Pass 1 returns
    /// the first slot matching <paramref name="preferred"/> (e.g. the same species/kind just processed); pass 2
    /// falls back to the first matching <paramref name="accept"/>. The active hotbar slot (the held tool) is always
    /// skipped. Scans the hotbar and backpack only — not the mouse cursor / crafting grid / character slots — by
    /// index off the inventory objects directly, so it doesn't depend on which inventories are flagged "opened".
    /// PURE READ: it only LOCATES a slot; the caller removes the item, so the caller owns the count, the place
    /// sound and the sync. <paramref name="preferred"/> may be null (then only the accept pass runs).
    /// </summary>
    internal static ItemSlot? FindRestockSlot(IPlayer player, System.Func<ItemStack, bool> accept, System.Func<ItemStack, bool>? preferred)
    {
        ItemSlot? active = player.InventoryManager?.ActiveHotbarSlot;

        // Pass 1: prefer the same kind/species as just processed (checked across BOTH inventories before falling
        // back), so a continuing rip / repeated chop doesn't jump to a different wood while a matching one is left.
        if (preferred != null)
        {
            ItemSlot? hit = ScanForFirst(player, active, preferred);
            if (hit != null) return hit;
        }

        // Pass 2: any acceptable input. (preferred ⊆ accept, but pass 1 already returned any preferred match, so
        // this only ever surfaces a non-preferred-but-acceptable stack.)
        return ScanForFirst(player, active, accept);
    }

    /// <summary>First matching, non-skipped, non-empty slot across the hotbar then the backpack, or null.</summary>
    static ItemSlot? ScanForFirst(IPlayer player, ItemSlot? skip, System.Func<ItemStack, bool> match)
    {
        IPlayerInventoryManager? mgr = player.InventoryManager;
        if (mgr == null) return null;

        ItemSlot? hit = ScanInventory(mgr.GetHotbarInventory(), skip, match);
        if (hit != null) return hit;
        return ScanInventory(mgr.GetOwnInventory(GlobalConstants.backpackInvClassName), skip, match);
    }

    /// <summary>First slot of <paramref name="inv"/> whose stack matches, excluding <paramref name="skip"/> (the
    /// active tool slot) and empty slots. Indexed walk via <see cref="IInventory.Count"/> + indexer.</summary>
    static ItemSlot? ScanInventory(IInventory? inv, ItemSlot? skip, System.Func<ItemStack, bool> match)
    {
        if (inv == null) return null;

        int count = inv.Count;
        for (int i = 0; i < count; i++)
        {
            ItemSlot slot = inv[i];
            if (slot == null || slot == skip) continue;   // never the active tool slot
            ItemStack? stack = slot.Itemstack;
            if (stack == null) continue;                   // empty slot
            if (match(stack)) return slot;
        }
        return null;
    }
}
