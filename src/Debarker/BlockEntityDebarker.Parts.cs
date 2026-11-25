using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ImmersiveWoodworking;

/// <summary>
/// The part-install machine (AutoDebarkerPlan T03.06), a port of <see cref="BlockEntityChopper"/>'s Parts partial. The
/// player installs the drive, the disc and the feed by RMB in any order, and the knife set only once the disc is in,
/// because the knives bolt onto it. A part or a set already in is refused with an ingame error and nothing leaves the
/// hand. Ctrl+RMB takes back only the knife set; the three mechanism parts come back when the frame is broken (T03.04).
/// The flags, the knife stack, <see cref="BlockEntityDebarker.IsComplete"/> and their serialization live in the main
/// partial; this file changes them, keeps the collectible mappings of both stored stacks and lists the parts that drop
/// on teardown (T03.04, <see cref="AppendPartDrops"/>).
/// </summary>
public partial class BlockEntityDebarker
{
    // The three mechanism parts. Membership only: install order among them is free. Matched against the held item's
    // code path; TryAddPart checks the domain first. BlockDebarker.OnLoaded resolves their hint icons from this list.
    internal static readonly string[] MandatoryParts = { "debarkerdrive", "debarkerdisc", "debarkerfeed" };

    // Reads for the interaction help (T08.01) and the block info (T08.03).

    /// <summary>The mechanism part codes not installed yet, in <see cref="MandatoryParts"/> order; empty once all three
    /// are in. The knife set is not listed: <see cref="HasKnives"/> says whether it is in, <see cref="HasDisc"/> whether
    /// it can go in now.</summary>
    internal string[] MissingMandatoryParts()
    {
        var list = new List<string>();
        foreach (string code in MandatoryParts) if (!IsInstalled(code)) list.Add(code);
        return list.ToArray();
    }

    /// <summary>True once the disc is in: the knife set installs only after it.</summary>
    internal bool HasDisc => hasDisc;

    internal bool HasKnives => knifeStack != null;

    bool IsInstalled(string code) => code switch
    {
        "debarkerdrive" => hasDrive,
        "debarkerdisc" => hasDisc,
        "debarkerfeed" => hasFeed,
        _ => false
    };

    void SetInstalled(string code)
    {
        switch (code)
        {
            case "debarkerdrive": hasDrive = true; break;
            case "debarkerdisc": hasDisc = true; break;
            case "debarkerfeed": hasFeed = true; break;
        }
    }

    /// <summary>
    /// Install the held item as a debarker part. True when the item is one of the parts or a knife set, installed or
    /// refused with an ingame error; false for anything else, which T05.02 then tries as a log. Outside creative the
    /// installed item leaves the hand.
    /// </summary>
    bool TryAddPart(ItemSlot slot, IPlayer byPlayer)
    {
        CollectibleObject? coll = slot.Itemstack?.Collectible;
        // Only this mod's items: the teardown (T03.04) gives back our own item for each flag, so another mod's item
        // with the same code path must not set one.
        if (coll?.Code == null || coll.Code.Domain != "immersivewoodworking") return false;

        // The first code part keeps the knife blank (debarkerknifeblank-*) out: that is a crafting ingredient.
        if (coll.FirstCodePart() == "debarkerknife") return TryAddKnives(slot, byPlayer);

        string path = coll.Code.Path;
        if (Array.IndexOf(MandatoryParts, path) < 0) return false;
        if (IsInstalled(path)) { Error("immersivewoodworking:debarker-part-installed"); return true; }

        Consume(slot, byPlayer);
        SetInstalled(path);
        OnPartInstalled(byPlayer);
        return true;
    }

    /// <summary>Install the knife set: only onto an installed disc and only when no set is in. The stored stack is a
    /// clone of the held one, so a set that has already worn keeps its durability.</summary>
    bool TryAddKnives(ItemSlot slot, IPlayer byPlayer)
    {
        if (!hasDisc) { Error("immersivewoodworking:debarker-need-disc"); return true; }
        if (knifeStack != null) { Error("immersivewoodworking:debarker-knives-installed"); return true; }

        // Non-null: TryAddPart only gets here after reading the stack's collectible.
        ItemStack knives = slot.Itemstack!.Clone();
        knives.StackSize = 1;
        Consume(slot, byPlayer);
        knifeStack = knives;
        OnPartInstalled(byPlayer);
        return true;
    }

    /// <summary>Ctrl+RMB with no log in: the knife set goes back to the player's inventory, with the log's fallback drop at
    /// the player's feet (<see cref="TryTakeLog"/>).</summary>
    void TryTakeKnives(IPlayer byPlayer)
    {
        if (knifeStack == null) return;

        ItemStack give = knifeStack;
        knifeStack = null;
        MarkDirty(true);
        UpdateRenderer(); // the clicker's prediction, as the chopper's TryTake; a no-op on the server

        // A set is one item, so true means it went in whole; the drop is the log's fallback (TryTakeLog).
        if (!byPlayer.InventoryManager.TryGiveItemstack(give))
        {
            Api.World.SpawnItemEntity(give, byPlayer.Entity.Pos.XYZ.Add(0, 0.5, 0));
        }
    }

    /// <summary>
    /// The drop half of the teardown (T03.04): the frame's own drop, then each installed mechanism part as a fresh item
    /// and the knife set as a copy of its stored stack, so a worn set comes back with the durability it had. The input
    /// log is not listed; T05.02 releases it from the BE's own OnBlockBroken.
    /// </summary>
    public ItemStack[] AppendPartDrops(ItemStack[]? frameDrops)
    {
        var drops = new List<ItemStack>();
        if (frameDrops != null) drops.AddRange(frameDrops);
        foreach (string code in MandatoryParts)
        {
            if (IsInstalled(code)) drops.Add(new ItemStack(Api.World.GetItem(new AssetLocation("immersivewoodworking", code))));
        }
        if (knifeStack != null) drops.Add(knifeStack.Clone());
        return drops.ToArray();
    }

    /// <summary>Take one of the held item and sync the hotbar slot. Creative installs are free.</summary>
    static void Consume(ItemSlot slot, IPlayer byPlayer)
    {
        if (byPlayer.WorldData.CurrentGameMode == EnumGameMode.Creative) return;
        slot.TakeOut(1);
        slot.MarkDirty();
    }

    /// <summary>After a real install only, so a refused click stays silent but for its error: sync to clients, the
    /// clicker's renderer (T07.02; it shows the part before the server's sync, as the chopper's does, and is a no-op on
    /// the server), the frame's place sound and the first-person use bob.</summary>
    void OnPartInstalled(IPlayer byPlayer)
    {
        MarkDirty(true);
        UpdateRenderer();
        Api.World.PlaySoundAt(Block.Sounds.Place, Pos, -0.25, byPlayer);
        (Api as ICoreClientAPI)?.World.Player.TriggerFpAnimation(EnumHandInteract.HeldItemInteract);
    }

    /// <summary>
    /// Show the ingame error for a lang key; client only, a no-op on the server. Unlike the chopper's
    /// <c>Error("chopper-need-arm")</c>, each call passes the whole domain-prefixed key as a literal, so
    /// <c>tools/check-lang.py</c> sees it and a misspelt key fails the check instead of showing up raw in game. The
    /// error HUD never reads the error code (memory vs-ingameerror-is-vtml-channel), so the key serves as the code too.
    /// </summary>
    void Error(string langKey)
        => (Api as ICoreClientAPI)?.TriggerIngameError(this, langKey, Lang.Get(langKey));

    // The input log and the knife set carry a world-specific numeric id. A schematic export stores their codes, and an
    // import into a world with another id table remaps them; without this either would come back as whatever holds the
    // old id. The load side follows BEQuern rather than the chopper: a stack whose code no longer exists is discarded
    // instead of staying there unresolved.

    public override void OnStoreCollectibleMappings(Dictionary<int, AssetLocation> blockIdMapping, Dictionary<int, AssetLocation> itemIdMapping)
    {
        base.OnStoreCollectibleMappings(blockIdMapping, itemIdMapping);
        foreach (ItemSlot slot in inventory)
            slot.Itemstack?.Collectible.OnStoreCollectibleMappings(Api.World, slot, blockIdMapping, itemIdMapping);
        knifeStack?.Collectible.OnStoreCollectibleMappings(Api.World, new DummySlot(knifeStack), blockIdMapping, itemIdMapping);
    }

    public override void OnLoadCollectibleMappings(IWorldAccessor worldForResolve, Dictionary<int, AssetLocation> oldBlockIdMapping, Dictionary<int, AssetLocation> oldItemIdMapping, int schematicSeed, bool resolveImports)
    {
        base.OnLoadCollectibleMappings(worldForResolve, oldBlockIdMapping, oldItemIdMapping, schematicSeed, resolveImports);
        foreach (ItemSlot slot in inventory)
        {
            if (slot.Itemstack == null) continue;
            if (!slot.Itemstack.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve)) slot.Itemstack = null;
            slot.Itemstack?.Collectible.OnLoadCollectibleMappings(worldForResolve, slot, oldBlockIdMapping, oldItemIdMapping, resolveImports);
        }

        if (knifeStack != null && !knifeStack.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve)) knifeStack = null;
        knifeStack?.Collectible.OnLoadCollectibleMappings(worldForResolve, new DummySlot(knifeStack), oldBlockIdMapping, oldItemIdMapping, resolveImports);
    }
}
