using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ImmersiveWoodworking;

/// <summary>
/// T03.07 — the pulverizer-style part-install machine for the automated chopper. After the frame captures its
/// footprint (T03.03), the player RMB-installs the parts into it: the 3 mandatory mechanism parts — drive-cam
/// (<c>chopperdrive</c>), axe-arm (<c>chopperarm</c>) and the clamp-bed — the existing hand <c>choppingblock</c>
/// (colода) RMB-installed as the bed under the head (user decision 2026-07-04: reuse the block, not a bespoke
/// <c>chopperbed</c> part) — in ANY order, then the axe/maul HEAD (a SINGLE <c>chopperhead-{metal}</c> item — the
/// cutting part AND its WEAR tier, its metal). Only one ordering rule exists (per the user): the head needs the arm
/// first (it sits in it). An empty hand / Ctrl+RMB removes ONLY the head — the three mechanism parts are committed once
/// installed (break the frame to recover them, <see cref="AppendPartDrops"/>). <see cref="BlockEntityChopper.IsComplete"/>
/// (in the main partial) flips once all 3 mandatory parts AND a head are in — and gates the whole machine (power E04,
/// I/O E05, chop cycle E06). (The optional flywheel was cut 2026-07-04.)
///
/// There is NO bronze cap: mechanical power (the mech network) is itself the tech gate over the hand chopping block,
/// so the parts are plain wood + common metal (T02.03 recipes). The head's metal is the WEAR tier (native
/// <c>durabilitybytype</c>), never the PACE — that is the drive RPM scaled by <c>ChopperSpeed</c> (E04/E06).
///
/// Direct mirror of <see cref="BlockEntitySawmill"/>'s Parts partial (which in turn mirrors
/// <c>BEPulverizer.OnInteract</c> → <c>TryAddPart</c>/<c>TryTake</c> and <c>BEHelveHammer.HammerStack</c>). The part
/// flags, <see cref="BlockEntityChopper.IsComplete"/> and the tree (de)serialization live in the main partial; this
/// file only mutates that state and adds the head-stack id remapping.
/// </summary>
public partial class BlockEntityChopper
{
    // The 2 mandatory item mechanism parts (drive + arm). Membership only — install order is FREE among them (the
    // head's arm dependency is handled explicitly below, not by array position). Matched against the held item's
    // domain-stripped Code.Path. Their codes/assets are the item-parts in T02.02/T02.03. The THIRD mandatory part —
    // the clamp-bed — is NOT an item-part: it is the hand `choppingblock`, matched by its own branch in TryAddPart
    // (user decision 2026-07-04), so it stays out of this array (which drives the item-only IsInstalled/SetInstalled).
    static readonly string[] MandatoryParts = { "chopperdrive", "chopperarm" };

    /// <summary>The installed axe/maul head (one item — the cutting part AND the wear tier, its metal), or null when
    /// none is in. Read by the teardown drop (T03.05), the wear-tier resolve (T01.02/E06) and the render (E07).</summary>
    public ItemStack? HeadStack => headStack;

    // ───────────────────────── Interaction-help reads (T08.01) ─────────────────────────
    //
    // Pure reads the master block's GetPlacedBlockInteractionHelp consults to pick the hint set per assembly state.

    /// <summary>The mandatory part codes NOT yet installed — the icons for the "install part" hint (T08.01). Empty
    /// once all three mechanism parts are in; order follows <see cref="MandatoryParts"/>.</summary>
    internal string[] MissingMandatoryParts()
    {
        var list = new List<string>();
        foreach (string code in MandatoryParts) if (!IsInstalled(code)) list.Add(code);
        if (!hasBed) list.Add("choppingblock"); // the clamp-bed is the hand choppingblock, not an item-part (2026-07-04)
        return list.ToArray();
    }

    /// <summary>True once the head is installed (the cutting part + wear tier).</summary>
    internal bool HasHead => headStack != null;

    /// <summary>True once the axe-arm is installed — the head installs only after it.</summary>
    internal bool HasArm => hasArm;

    /// <summary>Whether a given mandatory ITEM-part code is already installed (drive/arm only — the bed is the hand
    /// choppingblock, tracked by <c>hasBed</c> via its own TryAddPart branch, not this switch).</summary>
    bool IsInstalled(string code) => code switch
    {
        "chopperdrive" => hasDrive,
        "chopperarm" => hasArm,
        _ => false
    };

    /// <summary>Flag a mandatory ITEM-part as installed by its code (drive/arm only — see <see cref="IsInstalled"/>).</summary>
    void SetInstalled(string code)
    {
        switch (code)
        {
            case "chopperdrive": hasDrive = true; break;
            case "chopperarm": hasArm = true; break;
        }
    }

    /// <summary>
    /// Install the held item as a chopper part (mirrors <c>BEPulverizer.TryAddPart</c> / the sawmill). Returns true
    /// when the held item is a recognised chopper part — whether it installed OR was rejected with an ingame hint, so
    /// the caller swallows the click either way — and false when it isn't a chopper part at all (so E05 can later route
    /// a log to the input). The 3 mandatory parts install in ANY order; the head only after the arm (it sits in it).
    /// In non-creative the part is consumed; a part that can't go in yet (wrong
    /// order, or already present) shows a localized <see cref="Error"/> and consumes nothing.
    /// </summary>
    bool TryAddPart(ItemSlot slot, IPlayer byPlayer)
    {
        CollectibleObject? coll = slot.Itemstack?.Collectible;
        if (coll?.Code == null) return false;
        string path = coll.Code.Path;

        // Clamp-bed — the existing hand `choppingblock` (colода) RMB-installed as the bed under the head (user decision
        // 2026-07-04: reuse the block, not a bespoke `chopperbed` item). Mandatory, no ordering constraint; matched by
        // OUR OWN block code so a foreign mod's like-named block can't stand in. Capture the block's OWN wood species (+
        // domain) off its stack attributes BEFORE consuming it, so the installed bed renders in — and drops back as —
        // the wood it was crafted from (user-reported 2026-07-05: the bed must keep its own species, NOT follow the fed
        // log and NOT default to oak; see UpdateLogMesh / BedStack). A bare/legacy choppingblock carries no attribute → null → oak look.
        if (coll.Code.Domain == "immersivewoodworking" && path == "choppingblock")
        {
            if (hasBed) { Error("chopper-part-installed"); return true; }
            // Through the shared reader so a BLANK species counts as none here too (AuditFixPlan T04.06 / F-L6-28) —
            // a bed captured as "" would paint and drop back a wood with no name.
            (string wood, string domain)? bedSpecies = BlockEntityChoppingBlock.StackSpecies(slot.Itemstack!);
            bedWood = bedSpecies?.wood;
            bedWoodDomain = bedSpecies?.domain ?? "game";
            Consume(slot, byPlayer, 1);
            hasBed = true;
            OnPartInstalled(byPlayer);
            return true;
        }

        // Head — the mod's chopperhead-{metal} item; installs into the arm, so it needs the arm first.
        if (coll.FirstCodePart() == "chopperhead") return TryAddHead(slot, byPlayer);

        // Mandatory mechanism parts — installable in ANY order (T02.03).
        if (Array.IndexOf(MandatoryParts, path) < 0) return false; // not a chopper part → caller handles it (E05 log input)
        if (IsInstalled(path)) { Error("chopper-part-installed"); return true; }

        Consume(slot, byPlayer, 1);
        SetInstalled(path);
        OnPartInstalled(byPlayer);
        return true;
    }

    /// <summary>Install the head: only after the arm (the lever it sits in must be present) and only when no head is in
    /// yet. The head is a SINGLE item (<c>chopperhead-{metal}</c>), so exactly ONE is consumed; its metal is the WEAR
    /// tier (native durabilitybytype, T01.02/E06), never the pace. Mirrors the sawmill's <c>TryAddBladeKit</c> —
    /// it clones the held stack WITHOUT metal validation (storage needs no metal resolve; the metal rides the
    /// item code).</summary>
    bool TryAddHead(ItemSlot slot, IPlayer byPlayer)
    {
        // Non-null by construction: only reached from TryAddPart after it dereferenced slot.Itemstack.Collectible.
        ItemStack held = slot.Itemstack!;

        if (!hasArm) { Error("chopper-need-arm"); return true; }
        if (headStack != null) { Error("chopper-head-installed"); return true; }

        ItemStack head = held.Clone();
        head.StackSize = 1;
        if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            slot.TakeOut(1);
            slot.MarkDirty();
        }
        headStack = head;
        OnPartInstalled(byPlayer);
        return true;
    }

    /// <summary>Empty-hand (Ctrl+RMB) removal — ONLY the head can be pulled back out. The 3 mechanism parts are
    /// committed once installed; to recover them the player breaks the frame, which drops everything
    /// (<see cref="AppendPartDrops"/>). Mirrors the sawmill's blade-kit-only <c>TryTake</c>.</summary>
    void TryTake(IPlayer byPlayer)
    {
        if (headStack == null) return; // only the head is removable; the mechanism parts are committed

        ItemStack give = headStack;    // the single head item
        headStack = null;

        MarkDirty(true);
        UpdateRenderer(); // E07/T07.01 — removing the head changes the rendered machine; refresh the client render (dedup'd, server no-op)
        if (!byPlayer.InventoryManager.TryGiveItemstack(give))
        {
            Api.World.SpawnItemEntity(give, Pos.ToVec3d().Add(0.5, 0.6, 0.5));
        }
    }

    /// <summary>An ItemStack of one of this mod's chopper item-parts, by code.</summary>
    ItemStack PartStack(string code) => new ItemStack(Api.World.GetItem(new AssetLocation("immersivewoodworking", code)));

    /// <summary>An ItemStack of the clamp-bed — the hand <c>choppingblock</c> BLOCK (not an item), dropped whole on
    /// teardown. Re-stamps the bed's captured species (<see cref="bedWood"/>/<see cref="bedWoodDomain"/>) onto the stack
    /// exactly as <see cref="BlockChoppingBlock.OnPickBlock"/> does, so a birch bed comes back a birch choppingblock (no
    /// silent oak downgrade — user-reported 2026-07-05). A bare/legacy bed (null species) returns a plain block. Uses
    /// GetBlock, unlike <see cref="PartStack"/>'s GetItem.</summary>
    ItemStack BedStack()
    {
        ItemStack stack = new ItemStack(Api.World.GetBlock(new AssetLocation("immersivewoodworking", "choppingblock")));
        if (bedWood != null)
        {
            stack.Attributes.SetString("wood", bedWood);
            stack.Attributes.SetString("woodDomain", bedWoodDomain);
        }
        return stack;
    }

    /// <summary>
    /// Teardown drop (T03.05): the installed parts appended to the frame's own drop(s), mirroring
    /// <c>BEPulverizer.getDrops</c> (frame + parts by flag). Each mandatory part that's installed (drive/arm as their
    /// item, the bed as a whole hand <c>choppingblock</c>) and the head (the single installed item) come back as WHOLE
    /// items. A PURE read (the head is
    /// cloned, no field is mutated), so the master's <see cref="BlockChopper.GetDrops"/> — called once per teardown —
    /// can't double the drop. The held input log (E05) is released separately by the BE's own <c>OnBlockBroken</c>,
    /// never here. <paramref name="frameDrops"/> may be null (no frame drop).
    /// </summary>
    public ItemStack[] AppendPartDrops(ItemStack[]? frameDrops)
    {
        var drops = new List<ItemStack>();
        if (frameDrops != null) drops.AddRange(frameDrops);

        if (hasDrive) drops.Add(PartStack("chopperdrive"));
        if (hasArm) drops.Add(PartStack("chopperarm"));
        if (hasBed) drops.Add(BedStack());                    // the clamp-bed is the hand choppingblock (2026-07-04)
        if (headStack != null) drops.Add(headStack.Clone()); // the single installed head item

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
        UpdateRenderer(); // E07/T07.01 — the installed part / head metal changed the rendered machine; refresh the client render (dedup'd, server no-op)
    }

    /// <summary>Show a localized ingame error (client-only UI), e.g. a part installed out of order. No-op on the
    /// server, like <c>BEPulverizer</c>'s <c>(Api as ICoreClientAPI)?.TriggerIngameError</c>.</summary>
    void Error(string code, params object[] args)
        => (Api as ICoreClientAPI)?.TriggerIngameError(this, code, Lang.Get("immersivewoodworking:" + code, args));

    // ───────────────────────── Head collectible mappings (T03.07 DoD: survive an id remap) ─────────────────────────
    //
    // Two persistent stacks carry a world-specific numeric block/item id: the head kit (headStack, (de)serialized by
    // code in the main partial's To/FromTreeAttributes) and the input log in the Inventory. Without remapping them, an
    // imported schematic or a world whose id table differs would resolve either to the wrong collectible. The head
    // mirrors BEHelveHammer.HammerStack (and the sawmill blade kit); the input slot is iterated like BEQuern. Kept
    // here with the rest of the head logic rather than in the main partial.

    public override void OnStoreCollectibleMappings(Dictionary<int, AssetLocation> blockIdMapping, Dictionary<int, AssetLocation> itemIdMapping)
    {
        base.OnStoreCollectibleMappings(blockIdMapping, itemIdMapping);
        headStack?.Collectible.OnStoreCollectibleMappings(Api.World, new DummySlot(headStack), blockIdMapping, itemIdMapping);
        foreach (ItemSlot slot in Inventory)
            slot.Itemstack?.Collectible.OnStoreCollectibleMappings(Api.World, slot, blockIdMapping, itemIdMapping);
    }

    public override void OnLoadCollectibleMappings(IWorldAccessor worldForResolve, Dictionary<int, AssetLocation> oldBlockIdMapping, Dictionary<int, AssetLocation> oldItemIdMapping, int schematicSeed, bool resolveImports)
    {
        base.OnLoadCollectibleMappings(worldForResolve, oldBlockIdMapping, oldItemIdMapping, schematicSeed, resolveImports);
        headStack?.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve);
        foreach (ItemSlot slot in Inventory)
            slot.Itemstack?.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve);
    }
}
