using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ImmersiveWoodworking;

/// <summary>
/// T03.07 — the pulverizer-style part-install machine for the automated sawmill (composition redesigned in T02.05,
/// 2026-06-19). After the frame captures its footprint (T03.03), the player RMB-installs the crafted item-parts into
/// it: the 4 mandatory mechanism parts — saw sash, crankshaft assembly (crank + rod), feed levers and carriage
/// (cart + spring) — in ANY order, then the blade kit (a SINGLE <c>sawmillblade-{metal}</c> item — itself a gang of
/// blades in a mount), plus the optional iron flywheel. Only two ordering rules exist (per the user): the blade kit
/// needs the sash first (it sits in it), and the flywheel needs the crankshaft first. An empty hand removes the last
/// installed part (fixed reverse
/// order that respects those two deps). <see cref="IsComplete"/> (in the main partial) flips once all 4 mandatory
/// parts AND a blade kit are in — the optional flywheel is NOT part of it — and gates the whole machine (power E04,
/// I/O E05, cut E06).
///
/// There is NO bronze cap: mechanical power (a windmill behind silk, a water wheel behind metal) is itself the tech
/// gate over the hand sawhorses, so the parts are plain wood + common metal (T02.05 recipes). The retired reducer/feed
/// parts were folded into the crankshaft + levers.
///
/// Mirrors <c>BEPulverizer.OnInteract</c> → <c>TryAddPart</c>/<c>TryTake</c> (bool flags + a tier stack; a part that
/// can't go in yet shows an ingame hint and is not consumed) and <c>BEHelveHammer.HammerStack</c> (the held tier
/// stack + its collectible mappings). The part fields, <see cref="IsComplete"/> and the tree (de)serialization live
/// in the main partial; this file only mutates that state and adds the blade-stack id remapping.
/// </summary>
public partial class BlockEntitySawmill
{
    // The 4 mandatory mechanism parts (T02.05). Membership only — install order is FREE (the blade kit's sash
    // dependency and the flywheel's crankshaft dependency are handled explicitly below, not by array position).
    // Matched against the held item's domain-stripped Code.Path. Their codes/assets are the item-parts in T02.02/T02.05.
    static readonly string[] MandatoryParts =
        { "sawmillsash", "sawmillcrankshaft", "sawmilllevers", "sawmillcarriage" };

    // Lazily-resolved mod system → the live, server-synced config (mirrors BlockEntitySawhorse.Config). The shared
    // accessor for the BE partials (Power/Sawing read the sawmill timings & thresholds from it); read live so a
    // config change applies immediately.
    ImmersiveWoodworkingModSystem? modSystem;
    ImmersiveWoodworkingConfig Config => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).Config;

    /// <summary>The installed blade kit (one item — the cutting part AND the speed tier, its metal), or null when none
    /// is in. Read by the teardown drop (T03.05), the speed-tier resolve (E04/T04.04) and the render (E07/T07.01).</summary>
    public ItemStack? BladeStack => bladeStack;

    // ───────────────────────── Interaction-help reads (T08.01) ─────────────────────────
    //
    // Pure reads the master block's GetPlacedBlockInteractionHelp consults to pick the hint set per assembly state.

    /// <summary>The mandatory part codes NOT yet installed — the icons for the "install part" hint (T08.01). Empty
    /// once all four mechanism parts are in; order follows <see cref="MandatoryParts"/>.</summary>
    internal string[] MissingMandatoryParts()
    {
        var list = new List<string>();
        foreach (string code in MandatoryParts) if (!IsInstalled(code)) list.Add(code);
        return list.ToArray();
    }

    /// <summary>True once the blade kit is installed (the cutting part + speed tier).</summary>
    internal bool HasBladeKit => bladeStack != null;

    /// <summary>True once the saw sash is installed — the blade kit installs only after it.</summary>
    internal bool HasSash => hasSash;

    /// <summary>True when the OPTIONAL flywheel can still be installed: the crankshaft (its prerequisite) is in and no
    /// flywheel is yet — so the "install flywheel" hint shows independently of <see cref="IsComplete"/>.</summary>
    internal bool CanAddFlywheel => hasCrankshaft && !hasFlywheel;

    /// <summary>Whether a given mandatory part code is already installed.</summary>
    bool IsInstalled(string code) => code switch
    {
        "sawmillsash" => hasSash,
        "sawmillcrankshaft" => hasCrankshaft,
        "sawmilllevers" => hasLevers,
        "sawmillcarriage" => hasCarriage,
        _ => false
    };

    /// <summary>Flag a mandatory part as installed by its code.</summary>
    void SetInstalled(string code)
    {
        switch (code)
        {
            case "sawmillsash": hasSash = true; break;
            case "sawmillcrankshaft": hasCrankshaft = true; break;
            case "sawmilllevers": hasLevers = true; break;
            case "sawmillcarriage": hasCarriage = true; break;
        }
    }

    /// <summary>
    /// Install the held item as a sawmill part (mirrors <c>BEPulverizer.TryAddPart</c>). Returns true when the held
    /// item is a recognised sawmill part — whether it installed OR was rejected with an ingame hint, so the caller
    /// swallows the click either way — and false when it isn't a sawmill part at all (so E05 can later route a log to
    /// the input). The 4 mandatory parts install in ANY order; the blade kit only after the sash (it sits in it); the
    /// flywheel only after the crankshaft. In non-creative the part is consumed; a part that can't go in yet (wrong
    /// order, or already present) shows a localized <see cref="Error"/> and consumes nothing.
    /// </summary>
    bool TryAddPart(ItemSlot slot, IPlayer byPlayer)
    {
        CollectibleObject? coll = slot.Itemstack?.Collectible;
        if (coll?.Code == null) return false;
        string path = coll.Code.Path;

        // Optional flywheel — independent of the mandatory set, but only AFTER the crankshaft (user rule 2026-06-19).
        if (path == "sawmillflywheel")
        {
            if (hasFlywheel) { Error("sawmill-flywheel-installed"); return true; }
            if (!hasCrankshaft) { Error("sawmill-need-crankshaft"); return true; }
            Consume(slot, byPlayer, 1);
            hasFlywheel = true;
            OnPartInstalled(byPlayer);
            return true;
        }

        // Blade kit — the mod's sawmillblade-{metal} item; installs into the sash, so it needs the sash first.
        if (coll.FirstCodePart() == "sawmillblade") return TryAddBladeKit(slot, byPlayer);

        // Mandatory mechanism parts — installable in ANY order (T02.05).
        if (Array.IndexOf(MandatoryParts, path) < 0) return false; // not a sawmill part → caller handles it (E05 log input)
        if (IsInstalled(path)) { Error("sawmill-part-installed"); return true; }

        Consume(slot, byPlayer, 1);
        SetInstalled(path);
        OnPartInstalled(byPlayer);
        return true;
    }

    /// <summary>Install the blade kit: only after the sash (the frame it sits in must be present) and only when no kit
    /// is in yet. The kit is a SINGLE item (<c>sawmillblade-{metal}</c> — itself a gang of blades in a mount, crafted
    /// from vanilla saw blades), so exactly ONE is consumed; its metal is the speed tier (E04/T04.04). One item, not a
    /// stack-of-N, keeps the tier unambiguous and serialization trivial (the old "stack N blades" model was dropped).</summary>
    bool TryAddBladeKit(ItemSlot slot, IPlayer byPlayer)
    {
        // Non-null by construction: only reached from TryAddPart after it dereferenced slot.Itemstack.Collectible.
        ItemStack held = slot.Itemstack!;

        if (!hasSash) { Error("sawmill-need-sash"); return true; }
        if (bladeStack != null) { Error("sawmill-blades-installed"); return true; }

        ItemStack kit = held.Clone();
        kit.StackSize = 1;
        if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            slot.TakeOut(1);
            slot.MarkDirty();
        }
        bladeStack = kit;
        OnPartInstalled(byPlayer);
        return true;
    }

    /// <summary>Empty-hand (Ctrl+RMB) removal — ONLY the blade kit can be pulled back out. The 4 mechanism parts are
    /// committed once installed; to recover them the player breaks the frame, which drops everything
    /// (<see cref="AppendPartDrops"/>). Per the design change the old pulverizer-style reverse-order take (blade →
    /// flywheel → carriage → levers → crankshaft → sash) was narrowed to just the kit — the sash/crankshaft/levers/
    /// carriage no longer come off individually.</summary>
    void TryTake(IPlayer byPlayer)
    {
        if (bladeStack == null) return; // only the blade kit is removable; the mechanism parts are committed

        ItemStack give = bladeStack;    // the single kit item
        bladeStack = null;

        MarkDirty(true);
        UpdateRenderer(); // E07/T07.01 — removing the blade kit changes the saw texture; refresh the client render
        if (!byPlayer.InventoryManager.TryGiveItemstack(give))
        {
            Api.World.SpawnItemEntity(give, Pos.ToVec3d().Add(0.5, 0.6, 0.5));
        }
    }

    /// <summary>An ItemStack of one of this mod's sawmill item-parts, by code.</summary>
    ItemStack PartStack(string code) => new ItemStack(Api.World.GetItem(new AssetLocation("immersivewoodworking", code)));

    /// <summary>
    /// Teardown drop (T03.05): the installed parts appended to the frame's own drop(s), mirroring
    /// <c>BEPulverizer.getDrops</c> (frame + parts by flag). Each mandatory part that's installed, the optional
    /// flywheel if present, and the blade kit (the single installed item) come back as WHOLE
    /// items. A PURE read (the kit is cloned, no field is mutated), so the master's <see cref="BlockSawmill.GetDrops"/>
    /// — called once per teardown — can't double the drop. The held input log (E05) is released separately by the BE's
    /// own <see cref="OnBlockBroken"/> (whole while under the recovery grace, else forfeit), never here.
    /// <paramref name="frameDrops"/> may be null (no frame drop).
    /// </summary>
    public ItemStack[] AppendPartDrops(ItemStack[]? frameDrops)
    {
        var drops = new List<ItemStack>();
        if (frameDrops != null) drops.AddRange(frameDrops);

        if (hasSash) drops.Add(PartStack("sawmillsash"));
        if (hasCrankshaft) drops.Add(PartStack("sawmillcrankshaft"));
        if (hasLevers) drops.Add(PartStack("sawmilllevers"));
        if (hasCarriage) drops.Add(PartStack("sawmillcarriage"));
        if (hasFlywheel) drops.Add(PartStack("sawmillflywheel"));
        if (bladeStack != null) drops.Add(bladeStack.Clone()); // the single installed kit item

        return drops.ToArray();
    }

    /// <summary>Take <paramref name="n"/> of the held item in non-creative (creative installs are free), keeping the
    /// hotbar slot in sync. Mirrors the per-part consume in <c>BEPulverizer.TryAddPart</c>.</summary>
    static void Consume(ItemSlot slot, IPlayer byPlayer, int n)
    {
        if (byPlayer.WorldData.CurrentGameMode == EnumGameMode.Creative) return;
        slot.TakeOut(n);
        slot.MarkDirty();
    }

    /// <summary>Common post-install: sync to clients, play the build sound and the first-person use bob. Called only
    /// on a real install (never on a rejected one), so an out-of-order click stays silent except for its hint.</summary>
    void OnPartInstalled(IPlayer byPlayer)
    {
        MarkDirty(true);
        Api.World.PlaySoundAt(Block.Sounds.Place, Pos, -0.25, byPlayer);
        (Api as ICoreClientAPI)?.World.Player.TriggerFpAnimation(EnumHandInteract.HeldItemInteract);
        UpdateRenderer(); // E07/T07.01 — the blade kit's metal may have changed; refresh the client render (dedup'd, server no-op)
    }

    /// <summary>Show a localized ingame error (client-only UI), e.g. a part installed out of order. No-op on the
    /// server, like <c>BEPulverizer</c>'s <c>(Api as ICoreClientAPI)?.TriggerIngameError</c>.</summary>
    void Error(string code, params object[] args)
        => (Api as ICoreClientAPI)?.TriggerIngameError(this, code, Lang.Get("immersivewoodworking:" + code, args));

    // ───────────────────────── Blade-kit collectible mappings (T03.07 DoD: survive an id remap) ─────────────────
    //
    // Two persistent stacks carry a world-specific numeric block/item id: the blade kit (bladeStack, (de)serialized by
    // code in the main partial's To/FromTreeAttributes) and the input log in the Inventory. Without remapping them, an
    // imported schematic or a world whose id table differs would resolve either to the wrong collectible. The blade
    // mirrors BEHelveHammer.HammerStack; the input slot is iterated like BEQuern (its own log has no nested content).

    public override void OnStoreCollectibleMappings(Dictionary<int, AssetLocation> blockIdMapping, Dictionary<int, AssetLocation> itemIdMapping)
    {
        base.OnStoreCollectibleMappings(blockIdMapping, itemIdMapping);
        bladeStack?.Collectible.OnStoreCollectibleMappings(Api.World, new DummySlot(bladeStack), blockIdMapping, itemIdMapping);
        foreach (ItemSlot slot in Inventory)
            slot.Itemstack?.Collectible.OnStoreCollectibleMappings(Api.World, slot, blockIdMapping, itemIdMapping);
    }

    public override void OnLoadCollectibleMappings(IWorldAccessor worldForResolve, Dictionary<int, AssetLocation> oldBlockIdMapping, Dictionary<int, AssetLocation> oldItemIdMapping, int schematicSeed, bool resolveImports)
    {
        base.OnLoadCollectibleMappings(worldForResolve, oldBlockIdMapping, oldItemIdMapping, schematicSeed, resolveImports);
        bladeStack?.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve);
        foreach (ItemSlot slot in Inventory)
            slot.Itemstack?.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve);
    }
}
