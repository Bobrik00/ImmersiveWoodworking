using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// The perforated pot block (BarkPlan/E04, T04.03, decision D14). Two variants share this class: RAW (clayformed,
/// ground-stored, kiln-fired — plain vanilla-pottery behaviour, this class is inert for it) and FIRED (the working
/// tool). The FIRED pot is a ground-stored, birch-packable vessel that the player also assembles onto a placed clay pot
/// to build the two-vessel tar rig.
///
/// NOT A PLACED BLOCK (2026-07-29, user request "everything about the perforated pot must copy the ordinary pot"). It
/// used to be a standing block with a <c>BlockEntityPerforatedPot</c>, which made it sit in the CENTRE of a cell only,
/// break with the loot-vessel ceramic shatter and be carried with the small held pose — none of which a clay pot does.
/// It is now <c>GroundStorable</c>+<c>Unplaceable</c> exactly like <c>game:claypot</c>, so placement, pickup, stacking
/// four to a cell and every sound come from vanilla ground storage for free. The pack level therefore lives in the
/// STACK (<c>fillPoints</c> attribute) instead of a BlockEntity, which is also what makes it survive being taken,
/// dropped, stored again and folded into the rig — the same state-on-the-stack architecture <see cref="BlockTarPot"/>
/// moved to on 2026-07-28.
///
/// Two interaction surfaces:
///   • <see cref="IContainedInteractable"/> — Shift+RMB birch onto a ground-stored pot packs it (Ctrl+Shift for a
///     batch); Shift+RMB clay says "assemble first" (clay can only seal a rig, which needs a receiver underneath).
///     EVERY other click is handed back to vanilla ground storage, so a plain right click takes the pot into your hand
///     and Shift+RMB adds another one to the cell, exactly like a clay pot. That is deliberately the opposite of
///     <see cref="BlockTarPot"/>, which claims every stray click to stop the pile scooping an assembled rig out: a bare
///     pot has nothing to protect and must stay pickup-able.
///   • <see cref="OnHeldInteractStart"/> — Shift+RMB with the fired pot IN HAND onto a ground-stored clay pot assembles
///     the rig. The engine consults the target block before the held item on sneak UNLESS the held item sets
///     <see cref="CollectibleObject.HeldPriorityInteract"/> — which we do for the fired variant in
///     <see cref="OnLoaded"/> — so this runs before both the pile's own take/add AND our own GroundStorable behaviour,
///     and only falls through to them when the clicked quadrant is not a receiver. Server-authoritative: the client
///     only claims the interaction to suppress placement; the world mutation runs on the server.
/// </summary>
public class BlockPerforatedPot : Block, IContainedMeshSource, IContainedInteractable, IContainedCustomName, IHandBookPageCodeProvider
{
    // Reuse the generic build/place sound the vanilla pottery piles use.
    static readonly AssetLocation AssembleSound = new AssetLocation("game:sounds/player/build");

    // The merged pot shape (pot-perforated shell + the four cumulative birch groups). Since the 2026-07-28 model merge
    // the birch lives inside this ONE shape, so the pot's HELD/inventory/dropped appearance (OnBeforeRender) and its
    // ground-stored appearance (IContainedMeshSource) are rendered from it per fill stage — otherwise the default JSON
    // mesh would show a permanently FULL pot.
    static readonly AssetLocation PotShape = new AssetLocation("immersivewoodworking", "shapes/block/pot-perforated.json");

    ICoreClientAPI? capi;
    // The mod system, cached in OnLoaded, for the live (server-synced) tar-pot capacity (config, T08.01).
    ImmersiveWoodworkingModSystem? modSystem;
    int Capacity => modSystem?.Config.TarPotCapacityPoints ?? TarRig.DefaultCapacity;
    // Per-appearance (colour+stage) held/gui/dropped meshes, uploaded once and disposed in OnUnloaded (the vanilla
    // per-stack held-mesh idiom, e.g. BlockToolMold). A null value = shape missing → keep the default JSON mesh.
    readonly Dictionary<string, MultiTextureMeshRef?> heldMeshes = new();

    bool IsFired => LastCodePart() == "fired";

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        capi = api as ICoreClientAPI;
        modSystem = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        // Route Sneak+RMB with the fired pot in hand to OnHeldInteractStart BEFORE the target clay-pot pile's
        // OnBlockInteractStart (BlockGroundStorage sets PlacedPriorityInteract), so assembly wins over the pile's
        // take/add. Raw pots keep the default order so their vanilla ground-store gesture is untouched.
        if (IsFired) HeldPriorityInteract = true;
    }

    // ─────────────────────────── Per-stage render (held + ground-stored) ───────────────────────────

    // The pot's body texture + fill stage for a stack: FIRED pots take the fired claypot texture of their colour, RAW
    // pots the raw clay of their colour (mirroring the block JSON's texturesByType). Raw pots never carry fillPoints, so
    // they resolve to the empty shell (stage 0).
    (AssetLocation topTex, int stage) Appearance(ItemStack? stack)
    {
        // Variant is a RelaxedReadOnlyDictionary — its indexer returns null (not throw) for a missing key.
        var variant = stack?.Collectible?.Variant;
        string color = variant?["color"] ?? "blue";
        string type = variant?["type"] ?? "raw";
        AssetLocation topTex = type == "fired"
            ? new AssetLocation("game", "block/clay/pot/" + color)
            : new AssetLocation("game", "block/clay/" + color + "clay");
        int stage = TarRig.FillStage(FillPoints(stack), Capacity);
        return (topTex, stage);
    }

    /// <summary>The pack level a pot stack carries. It is the ONLY home of that state now that the fired pot is
    /// ground-stored rather than placed — read by the render paths, written by <see cref="PackBirch"/>, and read back by
    /// <see cref="TryAssembleOntoClayPot"/> when the pot folds into a rig.</summary>
    static int FillPoints(ItemStack? stack) => stack?.Attributes.GetInt("fillPoints", 0) ?? 0;

    // IContainedMeshSource: the pot's appearance inside a vanilla ground-storage pile (up to four per cell) and on a
    // shelf. The pile applies the quadrant offset/transform to the returned mesh, so it must be CLONED off the shared
    // PotMesh cache.
    public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
    {
        (AssetLocation topTex, int stage) = Appearance(slot.Itemstack);
        TarDbg.Log(capi, $"PERFPOT GenMesh @{atBlockPos} topTex={topTex} stage={stage} stack={TarDbg.Describe(slot.Itemstack)}");
        MeshData? m = capi != null ? TarRig.PotMesh(capi, PotShape, topTex, null, stage, false) : null;
        if (m == null) TarDbg.Log(capi, $"PERFPOT GenMesh -> NULL mesh - returning EMPTY 4/3 mesh");
        return m != null ? m.Clone() : new MeshData(4, 3);
    }

    public string GetMeshCacheKey(ItemSlot slot)
    {
        (AssetLocation topTex, int stage) = Appearance(slot.Itemstack);
        return "iw-perfpot|" + topTex + "|s" + stage;
    }

    // Held/first-person/GUI/dropped: swap the default JSON mesh (a full pot) for the correct fill stage — the whole point
    // of merging the birch into the pot shape (so a pot in hand shows how much bark it holds).
    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
        (AssetLocation topTex, int stage) = Appearance(itemstack);
        string key = topTex + "|s" + stage;
        if (!heldMeshes.TryGetValue(key, out MultiTextureMeshRef? mref))
        {
            MeshData? md = TarRig.PotMesh(capi, PotShape, topTex, null, stage, false);
            mref = md != null ? capi.Render.UploadMultiTextureMesh(md) : null;
            heldMeshes[key] = mref;
        }
        if (mref != null) renderinfo.ModelRef = mref;
    }

    public override void OnUnloaded(ICoreAPI api)
    {
        foreach (MultiTextureMeshRef? m in heldMeshes.Values) m?.Dispose();
        heldMeshes.Clear();
        base.OnUnloaded(api);
    }

    // ─────────────────────────── Handbook: six variants, two pages ───────────────────────────

    /// <summary>
    /// Send all three clay colours of a state to one page: raw pots to the raw page, fired pots to the fired one.
    /// Only <c>perforatedpot-blue-{type}</c> keeps a page; the other four carry <c>handbook.exclude</c>
    /// (blocktypes/perforatedpot.json), while creative keeps all six so each clay colour stays pickable. Colour is a
    /// clay-parity axis with no mechanical meaning here, and neither name mentions it, so six near-identical pages
    /// only made the pot harder to find.
    ///
    /// <para>Excluding a variant would normally break every icon pointing at it — the clayforming output, the raw
    /// pot's "Fires into" arrow — because those resolve their target through
    /// <c>CollectibleBehaviorHandbookTextAndExtraInfo.getPageCodeForStack</c>, and a dead page code opens nothing
    /// without a word. That call asks THIS method first, so the links land on the surviving page instead.</para>
    ///
    /// <para>It also covers the packed pot. The default page code appends a JSON dump of the stack's attributes
    /// (<c>GuiHandbookItemStackPage.PageCodeForStack</c>), so a pot carrying <c>fillPoints</c> asks for a code no page
    /// ever had; a fixed string cannot.</para>
    /// </summary>
    public string HandbookPageCodeForStack(IWorldAccessor world, ItemStack stack)
        => "block-" + Code.Domain + ":perforatedpot-blue-" + (IsFired ? "fired" : "raw");

    // ─────────────────────────── IContainedInteractable (packing while ground-stored) ───────────────────────────

    /// <summary>
    /// A click on the quadrant a fired pot sits in. We claim ONLY the two bark-chain gestures; everything else returns
    /// false so vanilla ground storage handles it — which is what gives the pot a clay pot's plain-right-click pickup
    /// (the playtest's «заполненный перфгоршок нельзя поднять с земли») and its Shift+RMB add-another-to-the-cell. The
    /// pack level travels on the taken stack, so picking a packed pot up never loses the birch.
    /// </summary>
    public bool OnContainedInteractStart(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (!IsFired) return false;
        EntityControls? controls = byPlayer?.Entity?.Controls;
        ItemSlot? active = byPlayer?.InventoryManager?.ActiveHotbarSlot;
        if (controls == null || active == null || byPlayer == null || active.Empty || !controls.ShiftKey) return false;

        if (TarRig.IsBirchBark(be.Api, active.Itemstack)) return PackBirch(be, slot, byPlayer, active, blockSel, controls.CtrlKey);
        if (TarRig.IsSealingClay(active.Itemstack))
        {
            // Clay cannot SEAL a lone pot (sealing needs a receiver underneath, so it lives only on the assembled rig),
            // so nudge the player to assemble first rather than silently doing nothing.
            (be.Api as ICoreClientAPI)?.TriggerIngameError(this, "tarpotassemble", Lang.Get("immersivewoodworking:ingameerror-tarpot-assemblefirst"));
            return true;
        }
        return false;
    }

    // Packing is instant (done in Start). Step ends the interaction immediately so no hold hangs; Stop is a no-op.
    public bool OnContainedInteractStep(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel) => false;
    public void OnContainedInteractStop(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel) { }
    public bool OnContainedInteractCancel(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel, EnumItemUseCancelReason cancelReason) => true;

    /// <summary>Shift+RMB birch on a ground-stored fired pot: add birch points to THIS stack (server-authoritative via
    /// <see cref="TarRig.TryPackBirch"/>: full-hint + consume + sound), then re-mesh. <paramref name="bulk"/> (Ctrl held)
    /// packs a whole batch in one click. Always claims the click.
    ///
    /// <para>Refuses a STACKED quadrant. A quadrant is one slot, so a stack of pots would share one attribute tree:
    /// packing it would spend one bark and fill all of them. Vanilla ground storage cannot actually build such a quadrant
    /// (<c>TransferQuantity</c> is 1 and an occupied quadrant is only ever taken FROM, never added to), so this guard is
    /// insurance against a foreign JSON raising <c>transferQuantity</c>, NOT a state the player meets — which is why its
    /// line only ever says what it is, and no precondition of the chain hangs off it (F-L9-19). The twin guard in
    /// <see cref="TryAssembleOntoClayPot"/> stands for the same reason and says the same thing.</para></summary>
    bool PackBirch(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, ItemSlot birchSlot, BlockSelection blockSel, bool bulk)
    {
        IWorldAccessor world = be.Api.World;
        if (slot.Itemstack == null) return false;
        if (slot.Itemstack.StackSize != 1)
        {
            (world.Api as ICoreClientAPI)?.TriggerIngameError(this, "tarpotonepot", Lang.Get("immersivewoodworking:ingameerror-tarpot-onepot"));
            return true;
        }
        if (world.Side == EnumAppSide.Server && !world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
            return true;

        int fill = FillPoints(slot.Itemstack);
        if (TarRig.TryPackBirch(be.Api, blockSel.Position, birchSlot, ref fill, assembled: false, bulk: bulk))
        {
            slot.Itemstack.Attributes.SetInt("fillPoints", fill);
            slot.MarkDirty();
            be.MarkDirty(true);
        }
        return true;
    }

    public WorldInteraction[] GetContainedInteractionHelp(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (!IsFired || FillPoints(slot.Itemstack) >= Capacity) return Array.Empty<WorldInteraction>();
        // Icons = the bark the gesture accepts (both states, plus any tag-carrying foreign bark), the bulk line sized by
        // the bark's own bulk quantity — see TarRig.BirchBarkStacks.
        return new[]
        {
            new WorldInteraction { ActionLangCode = "immersivewoodworking:blockhelp-tarrig-pack",     MouseButton = EnumMouseButton.Right, HotKeyCode = "shift",                    Itemstacks = TarRig.BirchBarkStacks(be.Api, false) },
            new WorldInteraction { ActionLangCode = "immersivewoodworking:blockhelp-tarrig-packbulk", MouseButton = EnumMouseButton.Right, HotKeyCodes = new[] { "ctrl", "shift" }, Itemstacks = TarRig.BirchBarkStacks(be.Api, true) }
        };
    }

    // ─────────────────────────── Fill readout (IContainedCustomName + tooltip) ───────────────────────────

    /// <summary>
    /// The pot's line in a ground pile's / shelf's contents list: its name plus how full of bark it is. PER SLOT, which
    /// is the whole reason this hangs off <see cref="IContainedCustomName"/> — the pack level lives on the STACK, so one
    /// cell can hold four pots packed differently and each must read its own number.
    ///
    /// <para>Implementing this interface is also what puts the number on screen at all: <c>BEGroundStorage.GetBlockInfo</c>
    /// only takes its "single content → print that block's own placed info" shortcut for a collectible that does NOT
    /// implement <see cref="IContainedCustomName"/> (BEGroundStorage.cs:1193), and that shortcut is useless here because
    /// it hands the block the PILE's position, never the slot. The visible cost is that a lone RAW pot now also prints
    /// its "1x ..." contents line, exactly as any multi-pot cell already did.</para>
    ///
    /// <para>Null for a RAW pot: it is a clayformed intermediate that never packs, so it keeps the plain stack name the
    /// caller falls back to.</para>
    /// </summary>
    public string? GetContainedInfo(ItemSlot inSlot)
        => IsFired ? TarRig.ContainedFillInfo(inSlot, FillPoints(inSlot.Itemstack), Capacity) : null;

    /// <summary>The pile TITLE: "Perforated Pot" / "4x Perforated Pot". The fill belongs in the description lines
    /// below it, not in the block's name — but null does NOT get that, it leaks the fill line into the title. See
    /// <see cref="TarRig.ContainedPileTitle"/>. Raw pots answer the same way: they never pack, so their title was
    /// never wrong, and one form for both variants keeps the two from drifting.</summary>
    public string? GetContainedName(ItemSlot inSlot, int quantity) => TarRig.ContainedPileTitle(inSlot, quantity);

    /// <summary>The same pack level on the held/inventory tooltip — the level rides the stack, so a packed pot picked up
    /// off the ground still carries its bark and has to be readable in the bag. Fired only, as above.</summary>
    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        if (IsFired) dsc.AppendLine(TarRig.HeldFillInfo(FillPoints(inSlot.Itemstack), Capacity));
    }

    // ─────────────────────────── Held gesture: assemble onto a clay pot ───────────────────────────

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (firstEvent && IsFired && blockSel != null && byEntity is EntityPlayer ep && ep.Controls.ShiftKey
            && ep.Player is IPlayer byPlayer && TryAssembleOntoClayPot(slot, byPlayer, blockSel))
        {
            // PreventDefault (not just PreventDefaultAction) mirrors the vanilla held-block-does-a-custom-action
            // precedent (BlockSmeltedContainer.OnHeldInteractStart): it suppresses BOTH the place action and the
            // place-swing animation, so a claimed assembly never leaks a block placement.
            handling = EnumHandHandling.PreventDefault;
            return;
        }

        // Not a receiver under the cursor → hand the gesture on. base runs the collectible behaviours, which is where
        // GroundStorable lays the pot into the cell — i.e. the ordinary "put my pot down" click.
        base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
    }

    /// <summary>
    /// The in-hand hint for the gesture the WHOLE tar chain starts with: Shift+RMB a ground-stored empty clay pot to
    /// assemble the rig (F-L9-15). Nothing else in the game teaches it — this pot has no <c>blockdesc</c>, its JSON
    /// declares no <c>interactions</c>, and the only line the panel shows at that crosshair is vanilla ground storage's
    /// own "add to the pile" (<c>BlockGroundStorage.GetPlacedBlockInteractionHelp</c>, Quadrants branch), which promises
    /// the opposite of what the click does: the clay pot is consumed into a rig. That vanilla line cannot be reworded —
    /// it is <c>game:blockhelp-groundstorage-add</c>, shared by every pile in the game — so ours is ADDED beside it, and
    /// the receiver icons are what tell the two apart.
    ///
    /// <para>HELD help, not contained help, and that is forced: <c>BEGroundStorage</c> reads <c>containedInteractions</c>
    /// off the collectible in the AIMED QUADRANT (the clay pot), never off the item in hand, so no
    /// <see cref="IContainedInteractable"/> on this class could surface this hint at all. Fired only — a raw pot
    /// assembles nothing. Appended to <c>base</c>, so GroundStorable's own "place" line stays.</para>
    /// </summary>
    public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
    {
        WorldInteraction[] help = base.GetHeldInteractionHelp(inSlot);
        if (!IsFired || capi == null) return help;
        return help.Append(assembleHelp ??= new WorldInteraction
        {
            ActionLangCode = "immersivewoodworking:blockhelp-tarrig-assemble",
            MouseButton = EnumMouseButton.Right,
            HotKeyCode = "shift",
            Itemstacks = ReceiverStacks(capi)
        });
    }

    // Built on the first hover, never in OnLoaded: ReceiverStacks walks every collectible, and that list is only complete
    // once all mods have loaded. Client-only field, guarded by the capi check above.
    WorldInteraction? assembleHelp;

    /// <summary>
    /// Try to assemble the fired pot in hand onto a ground-stored clay pot at the CLICKED quadrant of
    /// <paramref name="blockSel"/>. The rig replaces only that one pot's slot, so a multi-pot cell keeps its other pots
    /// and the rig sits on the pot you clicked (up to four rigs per cell, exactly like the pots — the whole point of the
    /// 2026-07-28 re-architecture). Returns true when we CLAIM the interaction: a successful assembly, or a refusal the
    /// player is told about, so default placement is suppressed. Returns false for every quadrant this gesture has no
    /// business in (empty, or holding something that was never meant as a base), so the ordinary vanilla put/take runs
    /// untouched. The world mutation is server-only.
    /// </summary>
    bool TryAssembleOntoClayPot(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        IWorldAccessor world = byPlayer.Entity.World;
        var beg = world.BlockAccessor.GetBlockEntity<BlockEntityGroundStorage>(blockSel.Position);
        if (beg == null || beg is BlockEntityPitKiln) return false; // not a ground-stored pile → let placement run

        // Target the CLICKED quadrant only. GetSlotAt maps the hit position to the quadrant/half/centre slot exactly as
        // the pile's own put/take does, so we build on the pot the player pointed at and leave the rest of the cell alone.
        ItemSlot? target = beg.GetSlotAt(blockSel);
        if (target == null || target.Empty) return false;            // empty quadrant → the ordinary "put my pot down" click, stay silent

        if (!IsEmptyFiredClayPot(target.Itemstack))
        {
            // The refusal a player actually meets (F-L9-19): there IS a vessel under the cursor, it just cannot be a base.
            // The precondition line lives HERE now, and it speaks only for the clay-pot family in a state the gate turns
            // down (a raw pot, a pot with food in it, a dirty pot still holding a meal) — the one case where the intent is
            // unmistakably "build the rig on this". Anything else (a bowl, a second perforated pot, someone's tongs) is
            // refused in silence, because Shift+RMB on an occupied quadrant is vanilla's TAKE gesture and a red line on
            // every take out of a pile would be noise.
            if (!IsClayPotFamily(target.Itemstack)) return false;    // → vanilla put/take runs, unchanged
            (world.Api as ICoreClientAPI)?.TriggerIngameError(this, "tarpotsinglepot", Lang.Get("immersivewoodworking:ingameerror-tarpot-singlepot"));
            // Claimed, so the wrong-state pot is not yanked into the bag alongside the message. Taking it stays one click
            // away: vanilla's take path never asked for Shift, so a PLAIN right click still lifts it out of the quadrant.
            return true;
        }

        // The rig takes over the whole slot stack, so the clicked quadrant must hold exactly ONE pot — you cannot build a
        // rig on one pot of a stacked quadrant and keep the rest in the same slot. Vanilla ground storage never builds
        // such a quadrant (see PackBirch), so this is insurance against a foreign JSON raising transferQuantity; it says
        // exactly that and nothing about the chain's preconditions.
        if (target.Itemstack.StackSize != 1)
        {
            (world.Api as ICoreClientAPI)?.TriggerIngameError(this, "tarpotonepot", Lang.Get("immersivewoodworking:ingameerror-tarpot-onepot"));
            return true;
        }

        if (world.Side != EnumAppSide.Server) return true;       // client claims; server is authoritative
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return true;

        int fill = FillPoints(slot.Itemstack);
        // The perforated pot's colour (perforatedpot-{color}-fired) rides into the rig so it renders its upper body in
        // that colour and hands the same pot back on disassembly (FirstCodePart(1) = colour, blue fallback).
        string perfColor = slot.Itemstack?.Collectible?.FirstCodePart(1) ?? "blue";
        // The colour read off the HELD pot's code, next to the receiver it is being built on: this is where a swapped or
        // flattened colour pair would first show up.
        TarDbg.Log(world, $"ASSEMBLE-GESTURE heldPot={TarDbg.Describe(slot.Itemstack)} parsedPerfColor={perfColor} onto={TarDbg.Describe(target.Itemstack)}");

        // Swap the receiver pot in this quadrant for the assembled rig stack (state = receiver + colours + fill in attrs).
        ItemStack? rig = BlockTarPot.CreateRigStack(world, target.Itemstack, perfColor, fill);
        if (rig == null) return true;
        target.Itemstack = rig;
        target.MarkDirty();
        // The rig is ~0.72 block tall where the receiver it replaced was a 0.125 slab, and the cell's one box was
        // latched on that slab when the cell was born — re-latch it before the sync (TarRig.RefreshCellStorageProps).
        TarRig.RefreshCellStorageProps(beg);
        beg.MarkDirty(true);

        slot.TakeOut(1);
        slot.MarkDirty();
        world.PlaySoundAt(AssembleSound, blockSel.Position.X + 0.5, blockSel.Position.Y + 0.5, blockSel.Position.Z + 0.5, null, true, 16f);
        return true;
    }

    // The receiver is a vanilla EMPTY clay pot: either a clean fired pot (claypot-{color}-fired) OR an empty DIRTY pot
    // (dirtyclaypot-{color}-empty). The dirty pot is accepted so a receiver that came back dirty from opening a tar pot
    // (D8 — opening always dirties the receiver) can be re-used for the next batch WITHOUT washing it first; the pot ends
    // dirty either way, so requiring a clean pot only forced a pointless wash. The "-cooked" pots hold food and end
    // differently, so they are excluded — only an empty pot becomes the base. Both accepted forms are GroundStorable
    // (Quadrants) and expose {color} at code part 1, so ReceiverColor / RestoreReceiverPile handle them uniformly.
    static bool IsEmptyFiredClayPot(ItemStack stack)
    {
        CollectibleObject? coll = stack?.Collectible;
        if (coll?.Code == null) return false;
        string first = coll.FirstCodePart();
        if (first == "claypot") return coll.Code.Path.EndsWith("-fired", StringComparison.Ordinal);
        if (first == "dirtyclaypot") return coll.Code.Path.EndsWith("-empty", StringComparison.Ordinal);
        return false;
    }

    // The receiver FAMILY: the two pot codes the gate above reads, in ANY state. It decides one thing only — whether a
    // refused quadrant is worth talking about. A pot the player plainly meant as a base but that cannot be one (raw,
    // cooked, dirty-with-a-meal) earns the precondition line; everything else is refused in silence so vanilla's take
    // gesture keeps working quietly.
    static bool IsClayPotFamily(ItemStack? stack)
    {
        CollectibleObject? coll = stack?.Collectible;
        if (coll?.Code == null) return false;
        string first = coll.FirstCodePart();
        return first == "claypot" || first == "dirtyclaypot";
    }

    /// <summary>The receiver ICONS for the assemble hint: every collectible the gesture's OWN gate
    /// (<see cref="IsEmptyFiredClayPot"/>) accepts as a base, so the hint can never promise a pot the click refuses. Same
    /// build-the-hint-from-the-gate rule as <see cref="TarRig.BirchBarkStacks"/>, and the same lifetime: resolved lazily
    /// on first hover (the walk needs every mod loaded) and cached for the session (ObjectCache). Null rather than an EMPTY array when
    /// nothing matches, so the line degrades to its plain text instead of being dropped for having an icon list with no
    /// stack in it.</summary>
    static ItemStack[]? ReceiverStacks(ICoreClientAPI capi)
    {
        ItemStack[] stacks = ObjectCacheUtil.GetOrCreate(capi, "iwTarReceiverStacks", () =>
        {
            List<ItemStack> list = new();
            foreach (CollectibleObject obj in capi.World.Collectibles)
            {
                if (obj?.Code == null) continue;
                // One probe stack per candidate, and the probe that passes is the very stack the hint shows — icon and
                // gate cannot disagree.
                ItemStack stack = new ItemStack(obj);
                if (IsEmptyFiredClayPot(stack)) list.Add(stack);
            }
            return list.ToArray();
        });
        return stacks.Length > 0 ? stacks : null;
    }
}
