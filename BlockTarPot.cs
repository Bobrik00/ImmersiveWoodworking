using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// The assembled two-vessel tar rig (BarkPlan/E04, T04.03, decision D14; re-architected 2026-07-28). A clay-pot
/// receiver with a birch-packable perforated pot on top, UNSEALED.
///
/// It is NOT a standalone block any more. It lives as a stack INSIDE vanilla ground storage — one rig per quadrant —
/// so up to four rigs (and/or bare pots) share a cell exactly like the pots they are built on, and each sits on its own
/// receiver rather than centring the whole cell. All rig state rides the stack's attributes (no BlockEntity):
///   • <c>receiver</c>   — the exact clay/dirty pot consumed into the rig (returned verbatim on disassembly)
///   • <c>recvColor</c>  — the receiver's colour (bottom body render + the sealed pot's code)
///   • <c>recvDirty</c>  — set when the receiver was a dirty pot, so the bottom body renders soot-stained (dirty texture)
///   • <c>perfColor</c>  — the perforated pot's colour (top body render + the pot handed back)
///   • <c>fillPoints</c> — the bark pack level 0..<see cref="ImmersiveWoodworkingConfig.TarPotCapacityPoints"/>
///
/// The block implements <see cref="IContainedMeshSource"/> (renders the coloured pot-on-pot + birch fill overlay in the
/// quadrant, keyed per appearance) and <see cref="IContainedInteractable"/> (Shift+RMB birch packs, Shift+RMB clay seals
/// a full rig IN PLACE into a <c>tarpot-{colour}-{perfcolour}-sealed</c> item that STAYS in the quadrant — taken by RMB or
/// fired in a pit kiln, not handed to the player, per the 2026-07-28 seal-in-place decision — plain empty-hand RMB takes
/// it apart back into the same quadrant). ANY other click on the rig's own quadrant is claimed as a no-op so vanilla
/// ground storage can never scoop the rig out (the rig leaves the cell only by disassembly or by breaking the cell). It is
/// <c>GroundStorable</c>+<c>Unplaceable</c>, so breaking the cell drops rig stacks the player can re-place into another
/// cell — a first-class ground-storage citizen. Assembly (perforated pot → rig) lives on <see cref="BlockPerforatedPot"/>;
/// the standalone perforated station is unchanged.
/// </summary>
public class BlockTarPot : Block, IContainedInteractable, IContainedMeshSource, IContainedCustomName
{
    // The merged rig shape: pot-combined (top=perforated body, bot=receiver body, lid/daub) PLUS the four cumulative
    // birch groups (receiver-raised, D20). Since the 2026-07-28 model merge one shape carries shell + birch, so PotMesh
    // renders the whole rig (shell coloured, lid hidden while unsealed, birch up to the fill stage) per appearance.
    static readonly AssetLocation PotShape = new AssetLocation("immersivewoodworking", "shapes/block/pot-combined.json");
    static readonly AssetLocation WorkSound = new AssetLocation("game:sounds/player/build");

    ICoreClientAPI? capi;
    // The mod system, cached in OnLoaded, for the live (server-synced) tar-pot capacity (config, T08.01).
    ImmersiveWoodworkingModSystem? modSystem;
    int Capacity => modSystem?.Config.TarPotCapacityPoints ?? TarRig.DefaultCapacity;
    // Per-appearance held/gui/dropped meshes for a broken-off rig stack, uploaded once and disposed in OnUnloaded.
    readonly Dictionary<string, MultiTextureMeshRef?> heldMeshes = new();

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        capi = api as ICoreClientAPI;
        modSystem = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
    }

    // ─────────────────────────── Rig stack helpers ───────────────────────────

    /// <summary>Build the ground-storable rig stack that carries the whole rig state in its attributes. Called by the
    /// assembly on the server (<see cref="BlockPerforatedPot"/>). <paramref name="receiver"/> is stored verbatim (its
    /// colour + clean/dirty identity come back on disassembly); its colour is also cached as a plain string so the render
    /// path never has to resolve the receiver stack.</summary>
    public static ItemStack? CreateRigStack(IWorldAccessor world, ItemStack receiver, string perfColor, int fillPoints)
    {
        Block? rigBlock = world.GetBlock(new AssetLocation("immersivewoodworking", "tarpot"));
        if (rigBlock == null)
        {
            TarDbg.Log(world, "ASSEMBLE FAILED: block immersivewoodworking:tarpot did not resolve");
            return null;
        }

        ItemStack rig = new ItemStack(rigBlock);
        ItemStack r = receiver.Clone();
        r.StackSize = 1;
        rig.Attributes.SetItemstack("receiver", r);
        rig.Attributes.SetString("recvColor", ReceiverColor(receiver));
        // Cache whether the receiver was a DIRTY pot so the render path draws the bottom body soot-stained WITHOUT having to
        // resolve the stored receiver stack (same reason recvColor is cached). The clean/dirty identity itself still travels
        // in the verbatim `receiver` stack and comes back unchanged on disassembly — this flag only drives the shell texture.
        if (IsDirtyReceiver(receiver)) rig.Attributes.SetBool("recvDirty", true);
        rig.Attributes.SetString("perfColor", perfColor);
        if (fillPoints > 0) rig.Attributes.SetInt("fillPoints", fillPoints);
        // The authoritative record of what the rig WAS given. Compare against the client-side "RIG GenMesh stack=" line
        // for the same cell to see whether the colours survived the trip.
        TarDbg.Log(world, $"ASSEMBLE receiver={TarDbg.Describe(receiver)} perfColor={perfColor} -> rig={TarDbg.Describe(rig)}");
        return rig;
    }

    static string PerfColor(ItemStack? rig) => rig?.Attributes.GetString("perfColor", "blue") ?? "blue";
    static string RecvColor(ItemStack? rig) => rig?.Attributes.GetString("recvColor", "blue") ?? "blue";
    static bool RecvDirty(ItemStack? rig) => rig?.Attributes.GetBool("recvDirty", false) ?? false;
    static int FillPoints(ItemStack? rig) => rig?.Attributes.GetInt("fillPoints", 0) ?? 0;

    /// <summary>Whether the receiver pot consumed into a rig was a DIRTY pot (dirtyclaypot-*) rather than a clean fired
    /// claypot — the two accepted receiver forms (see BlockPerforatedPot.IsEmptyFiredClayPot).</summary>
    static bool IsDirtyReceiver(ItemStack? receiver) => receiver?.Collectible?.FirstCodePart() == "dirtyclaypot";

    /// <summary>The receiver clay/dirty pot stored in a rig stack, RESOLVED against the world (server-only paths — the
    /// stack read from attributes loads unresolved, GetItemstack docs). Returns null for a degenerate rig.</summary>
    static ItemStack? ResolveReceiver(IWorldAccessor world, ItemStack? rig)
    {
        ItemStack? r = rig?.Attributes.GetItemstack("receiver");
        r?.ResolveBlockOrItem(world);
        return r;
    }

    /// <summary>Colour part of a receiver pot code (claypot-{color}-fired OR dirtyclaypot-{color}-empty — {color} is part
    /// 1 in both). Blue fallback for an unexpected code.</summary>
    static string ReceiverColor(ItemStack? receiver)
    {
        string[]? parts = receiver?.Collectible?.Code?.Path.Split('-');
        if (parts != null && parts.Length >= 3 && (parts[0] == "claypot" || parts[0] == "dirtyclaypot")) return parts[1];
        return "blue";
    }

    // ─────────────────────────── IContainedMeshSource (render in quadrant) ───────────────────────────

    public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
    {
        // Dump the stack EXACTLY as the client sees it: if perfColor/recvColor are missing here but present in the
        // server-side ASSEMBLE line, the colours are being lost in transit rather than mis-rendered.
        TarDbg.Log(capi, $"RIG GenMesh @{atBlockPos} stack={TarDbg.Describe(slot.Itemstack)}");
        // The whole rig (coloured pot-on-pot + birch to stage, lid hidden) is one shared cached mesh — CLONE it, so the
        // display base's applyDefaultTransforms (run next on the quadrant mesh) never mutates the shared cache.
        MeshData? m = capi != null ? RigMesh(capi, slot.Itemstack) : null;
        if (m == null) TarDbg.Log(capi, $"RIG GenMesh -> NULL mesh (capi={(capi != null)}) - returning EMPTY 4/3 mesh");
        return m != null ? m.Clone() : new MeshData(4, 3);
    }

    /// <summary>Unique per visible appearance (top colour, bottom colour+dirty, fill stage), so packing a rig — which
    /// bumps the stage — re-meshes it, and two identically-packed rigs of the same colours share one cached mesh.</summary>
    public string GetMeshCacheKey(ItemSlot slot)
    {
        string key = "iw-tarrig|" + AppearanceKey(slot.Itemstack);
        // If two rigs of DIFFERENT colours ever print the same key here, the engine will serve one the other's mesh —
        // which is exactly what "everything repaints to red+red" would look like.
        TarDbg.Log(capi, $"RIG CacheKey={key} for {TarDbg.Describe(slot.Itemstack)}");
        return key;
    }

    // The rig's whole coloured mesh for the current stage: top body = perforated-pot colour, bottom body = receiver
    // colour (its DIRTY texture when the receiver was a dirty pot), lid+daub hidden (the assembled rig is unsealed).
    MeshData? RigMesh(ICoreClientAPI capi, ItemStack? rig)
    {
        AssetLocation topTex = new AssetLocation("game", "block/clay/pot/" + PerfColor(rig));
        AssetLocation botTex = new AssetLocation("game", "block/clay/pot/" + (RecvDirty(rig) ? "dirty" : "") + RecvColor(rig));
        return TarRig.PotMesh(capi, PotShape, topTex, botTex, TarRig.FillStage(FillPoints(rig), Capacity), false);
    }

    // Appearance identity shared by the cache key and OnBeforeRender: top colour, bottom colour (+dirty), fill stage.
    string AppearanceKey(ItemStack? rig)
        => PerfColor(rig) + "|" + RecvColor(rig) + (RecvDirty(rig) ? "|d" : "") + "|s" + TarRig.FillStage(FillPoints(rig), Capacity);

    // Held/first-person/GUI/dropped rig stack (broken off a cell): show the correct fill stage, like the standalone pot.
    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
        string key = AppearanceKey(itemstack);
        if (!heldMeshes.TryGetValue(key, out MultiTextureMeshRef? mref))
        {
            MeshData? md = RigMesh(capi, itemstack);
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

    // ─────────────────────────── IContainedInteractable (interact in quadrant) ───────────────────────────

    public bool OnContainedInteractStart(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        EntityControls? controls = byPlayer?.Entity?.Controls;
        ItemSlot? active = byPlayer?.InventoryManager?.ActiveHotbarSlot;
        // Proves whether a click on a broken ("?") cell reaches US at all, or is swallowed upstream by the engine.
        TarDbg.Log(be.Api, $"RIG Interact shift={controls?.ShiftKey} ctrl={controls?.CtrlKey} hand={TarDbg.Describe(active?.Itemstack)} slot={TarDbg.Describe(slot.Itemstack)}");
        if (controls == null || active == null || byPlayer == null) return false;

        // Shift+RMB with birch/clay: pack, or seal a full rig. Ctrl+Shift+RMB with birch packs a whole batch at once (like
        // bulk-placing bark on the ground). Any OTHER item + Shift is CLAIMED as a no-op (return true), never handed to
        // vanilla — see the tail comment for why.
        if (controls.ShiftKey && !active.Empty)
        {
            if (TarRig.IsBirchBark(be.Api, active.Itemstack)) return PackBirch(be, slot, byPlayer, active, blockSel, controls.CtrlKey);
            if (TarRig.IsSealingClay(active.Itemstack)) return SealRig(be, slot, byPlayer, active, blockSel);
            return ClaimAsNoOp(be);
        }

        // Plain empty-hand RMB: take the rig apart (perforated pot back to hand, receiver restored in this quadrant).
        if (!controls.ShiftKey && !controls.CtrlKey && active.Empty) return DisassembleRig(be, slot, byPlayer, blockSel);

        // Every OTHER click on the rig's own occupied quadrant (item-in-hand plain RMB, Ctrl-clicks, Shift+empty-hand) is
        // CLAIMED as a no-op. Returning false here would fall through to vanilla ground storage, whose take branch
        // (BEGroundStorage.putOrGetItemSingle) lifts the WHOLE rig stack — packed birch and all — into the player's
        // inventory on the "wrong" click (e.g. Shift+RMB with seeds in hand while following the generic add hint). The rig
        // must only leave the cell via disassembly (above) or by breaking the cell. OnContainedInteractStart fires ONLY for
        // a NON-empty clicked slot (BEGroundStorage.cs: `!ourSlot.Empty`), i.e. the rig's own quadrant, so claiming here
        // never blocks adding items to the cell's OTHER empty quadrants — those never reach this handler.
        return ClaimAsNoOp(be);
    }

    /// <summary>
    /// Swallow a click on the rig's own quadrant without doing anything, but SAY SO. Claiming is what keeps vanilla ground
    /// storage from scooping the packed rig out of the cell (see the tail of <see cref="OnContainedInteractStart"/>), but
    /// it also swallows the place/take sound the engine would otherwise have played at BEGroundStorage.cs:1041/1071 — so
    /// before this hint the gesture was a completely dead click: no effect, no sound, no message. That is one of the
    /// "sometimes there is no sound when I set a pot down" cases, because aiming at a quadrant that already holds a rig
    /// silently refuses to place the pot in your hand.
    ///
    /// <para>THROTTLED, because a held Shift+RMB re-enters this repeatedly. Vanilla's own re-entry guard is what causes it:
    /// the first press runs through <c>BlockGroundStorage.OnBlockInteractStart</c> and sets <c>IsUsingContainedBlock</c>
    /// (BEGroundStorage.cs:466), which makes every later re-entry short-circuit out of the block path
    /// (BlockGroundStorage.cs:141, cleared only on MouseUp at :92-95) and fall through to the HELD item instead — and
    /// <c>CollectibleBehaviorGroundStorable.Interact</c> then calls <c>OnPlayerInteractStart</c> directly
    /// (BehaviorGroundStorable.cs:175-181), never reading the <c>firstEvent</c> flag it was handed. So with any
    /// ground-storable item in hand the hint would re-fire several times a second until the button is released. Only the
    /// MESSAGE is gated; the claim itself stays unconditional, since that is the part protecting the rig. Client-only
    /// state (<see cref="TriggerIngameError"/> is a client API), so the field is never touched server-side.</para>
    /// </summary>
    const long HintIntervalMs = 1000;
    long lastHintMs = -HintIntervalMs;

    bool ClaimAsNoOp(BlockEntityContainer be)
    {
        if (be.Api is ICoreClientAPI capi2)
        {
            long now = be.Api.World.ElapsedMilliseconds;
            if (now - lastHintMs >= HintIntervalMs)
            {
                lastHintMs = now;
                capi2.TriggerIngameError(be, "tarrigoccupied", Lang.Get("immersivewoodworking:ingameerror-tarrig-occupied"));
            }
        }
        return true;
    }

    // Every rig action is instant (done in Start). Step ends the interaction immediately so no hold hangs; Stop is a no-op.
    public bool OnContainedInteractStep(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel) => false;
    public void OnContainedInteractStop(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel) { }
    public bool OnContainedInteractCancel(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel, EnumItemUseCancelReason cancelReason) => true;

    /// <summary>Shift+RMB birch: add birch points to this rig stack (server-authoritative via
    /// <see cref="TarRig.TryPackBirch"/>: full-hint + consume + sound), then re-mesh. <paramref name="bulk"/> (Ctrl held)
    /// packs a whole batch in one click instead of a single chunk. Always claims the click.</summary>
    bool PackBirch(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, ItemSlot birchSlot, BlockSelection blockSel, bool bulk)
    {
        IWorldAccessor world = be.Api.World;
        if (world.Side == EnumAppSide.Server && !world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
            return true;

        int fill = FillPoints(slot.Itemstack);
        if (TarRig.TryPackBirch(be.Api, blockSel.Position, birchSlot, ref fill, assembled: true, bulk: bulk))
        {
            slot.Itemstack?.Attributes.SetInt("fillPoints", fill);
            slot.MarkDirty();
            be.MarkDirty(true);
        }
        return true;
    }

    /// <summary>Shift+RMB clay on a FULL rig: consume one clay and turn the rig IN PLACE into a
    /// <c>tarpot-{colour}-{perfcolour}-sealed</c> (receiver + packed perforated pot + lid/daub fold into it, BOTH colours
    /// carried on in the code). The sealed pot STAYS in this quadrant on the ground and is
    /// picked up later by plain RMB (vanilla ground-storage take) or laid into a pit kiln — it is NOT handed to the player
    /// (user decision 2026-07-28, deviation from D14 "sealed pot to hand"; logged in PLAN.md/09-tar-fill-be.md). Colour rides
    /// the CODE so it survives the pit kiln (D11). Refuses (hint) unless full. Server-authoritative; client just claims. The
    /// slot swap mirrors <see cref="DisassembleRig"/>: a direct <c>slot.Itemstack = …</c> in the same quadrant, so no engine
    /// layout gate is touched (rig block and sealed item share the Quadrants GroundStorable layout).</summary>
    bool SealRig(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, ItemSlot claySlot, BlockSelection blockSel)
    {
        IWorldAccessor world = be.Api.World;
        if (FillPoints(slot.Itemstack) < Capacity)
        {
            (world.Api as ICoreClientAPI)?.TriggerIngameError(this, "tarpotnotfull", Lang.Get("immersivewoodworking:ingameerror-tarpot-notfull"));
            return true;
        }
        if (world.Side != EnumAppSide.Server) return true;
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return true;

        string color = RecvColor(slot.Itemstack);
        string perfColor = PerfColor(slot.Itemstack);
        // BOTH colours ride the sealed pot's CODE (tarpot-{color}-{perfcolor}-sealed), because the pit kiln clones the
        // JSON SmeltedStack and drops item attributes (D11/D23) — a code is the only channel that survives firing, and
        // opening has to hand back BOTH vessels in their own colour. The item axes cover every real pairing (10 receiver
        // colours x 3 perforated-pot colours), so the exact code should always resolve; the ladder below only guards a
        // desynced/edited axis and never leaves the player without a pot.
        var wantedLoc = new AssetLocation("immersivewoodworking", "tarpot-" + color + "-" + perfColor + "-sealed");
        Item? exact = world.GetItem(wantedLoc);
        Item? sealedPot = exact
                          ?? world.GetItem(new AssetLocation("immersivewoodworking", "tarpot-" + color + "-blue-sealed"))
                          ?? world.GetItem(new AssetLocation("immersivewoodworking", "tarpot-blue-" + perfColor + "-sealed"))
                          ?? world.GetItem(new AssetLocation("immersivewoodworking", "tarpot-blue-blue-sealed"));
        TarDbg.Log(world, $"SEAL recvColor={color} perfColor={perfColor} wanted={wantedLoc} exactResolved={exact != null} "
                        + $"using={sealedPot?.Code?.ToString() ?? "<NULL - seal aborted>"}");
        if (sealedPot == null) return true;

        claySlot.TakeOut(1);
        claySlot.MarkDirty();
        // Seal IN PLACE: the rig (stackSize 1) becomes the sealed pot in the SAME quadrant. It stays on the ground, picked up
        // later by plain RMB or fired in a pit kiln — no drop, no hand-off. The cell can't empty here (the slot is refilled),
        // so there is no SetBlock(0). Same in-quadrant swap idiom as DisassembleRig.
        //
        // The swap replaces the WHOLE attribute tree, which is where the receiver's clean/dirty identity used to die: an
        // unsealed rig built on a sooty pot rendered sooty and then repainted itself pristine the instant you capped it
        // («запайка визуально отмывает грязный приёмник», PLAN.md D45). Colour rides the code because it must cross the
        // kiln; soot does NOT need to, because a FIRED pot is dirty by definition (opening always yields dirtyclaypot,
        // D8 — the itemtype declares that state's soot outright). Only the SEALED-but-unfired window carries real
        // information, and that window never touches the kiln, so a plain stack attribute is the right channel — no
        // fourth code axis, no doubling of the 60 codes, no save break. ItemTarPot.ReceiverTex reads it back.
        ItemStack sealedStack = new ItemStack(sealedPot);
        if (RecvDirty(slot.Itemstack)) sealedStack.Attributes.SetBool("recvDirty", true);
        slot.Itemstack = sealedStack;
        TarDbg.Log(world, $"SEAL DONE quadrant now holds {TarDbg.Describe(slot.Itemstack)}");
        slot.MarkDirty();
        // Third height in the same cell: the sealed pot declares y2 0.35 where the rig it replaced declared 0.72
        // (TarRig.RefreshCellStorageProps — the cell's one box is latched, not re-resolved).
        TarRig.RefreshCellStorageProps(be);
        be.MarkDirty(true);

        world.PlaySoundAt(WorkSound, be.Pos.X + 0.5, be.Pos.Y + 0.5, be.Pos.Z + 0.5, null, true, 12f);
        return true;
    }

    /// <summary>Plain empty-hand RMB: hand the perforated pot back (with its pack level, in its colour) and restore the
    /// receiver clay/dirty pot into THIS quadrant. Server-authoritative; client just claims. Other quadrants untouched.</summary>
    bool DisassembleRig(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        IWorldAccessor world = be.Api.World;
        if (world.Side != EnumAppSide.Server) return true;
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return true;

        ItemStack? rig = slot.Itemstack;
        int fill = FillPoints(rig);
        string perfColor = PerfColor(rig);
        ItemStack? receiver = ResolveReceiver(world, rig);

        Block? perfBlock = world.GetBlock(new AssetLocation("immersivewoodworking", "perforatedpot-" + perfColor + "-fired"))
                           ?? world.GetBlock(new AssetLocation("immersivewoodworking", "perforatedpot-blue-fired"));
        // The receiver is restored VERBATIM from the stored stack — if it comes back unresolved here, the quadrant ends
        // up holding a stack with no collectible, which is exactly what renders as the vanilla "?" quad.
        TarDbg.Log(world, $"DISASSEMBLE perfColor={perfColor} perfBlock={perfBlock?.Code?.ToString() ?? "<NULL>"} "
                        + $"restoredReceiver={TarDbg.Describe(receiver)}");
        if (perfBlock != null)
        {
            ItemStack perfStack = new ItemStack(perfBlock);
            if (fill > 0) perfStack.Attributes.SetInt("fillPoints", fill);
            GiveOrDrop(world, byPlayer, be.Pos, perfStack);
        }

        // Restore the receiver back into the SAME slot (quadrant preserved). A degenerate rig with no receiver just clears.
        slot.Itemstack = receiver;
        slot.MarkDirty();
        // The mirrored case of the assembly swap: a cell that latched the rig's 0.72 box (on the chunk load that found
        // a rig leading it) would otherwise keep it as an invisible wall over the flat pot now standing there.
        TarRig.RefreshCellStorageProps(be);
        be.MarkDirty(true);
        if (be.Inventory.Empty) world.BlockAccessor.SetBlock(0, be.Pos);

        world.PlaySoundAt(WorkSound, be.Pos.X + 0.5, be.Pos.Y + 0.5, be.Pos.Z + 0.5, null, true, 12f);
        return true;
    }

    static void GiveOrDrop(IWorldAccessor world, IPlayer byPlayer, BlockPos pos, ItemStack stack)
    {
        if (!byPlayer.InventoryManager.TryGiveItemstack(stack, true))
            world.SpawnItemEntity(stack, pos.ToVec3d().Add(0.5, 0.5, 0.5));
    }

    // ─────────────────────────── Fill readout (IContainedCustomName + tooltip) ───────────────────────────

    /// <summary>The rig's line in a ground pile's / shelf's contents list: its name plus how full of bark it is. Per
    /// SLOT, so four rigs in one cell each read their own level — see
    /// <see cref="BlockPerforatedPot.GetContainedInfo"/> for why this hangs off <see cref="IContainedCustomName"/> and
    /// what it changes in <c>BEGroundStorage.GetBlockInfo</c>. Always shown: unlike the perforated pot the rig has no
    /// non-packable variant — every rig is a working vessel, and 100% is its gate for sealing.</summary>
    public string? GetContainedInfo(ItemSlot inSlot) => TarRig.ContainedFillInfo(inSlot, FillPoints(inSlot.Itemstack), Capacity);

    /// <summary>The pile TITLE ("Tar Pot Assembly" / "2x Tar Pot Assembly"); the fill goes in the description lines
    /// below it. Null does NOT get that — see <see cref="TarRig.ContainedPileTitle"/>. The rig needs this more
    /// than the perforated pot does: it has no non-packing variant, so before the fix EVERY rig cell was titled by its
    /// fill line.</summary>
    public string? GetContainedName(ItemSlot inSlot, int quantity) => TarRig.ContainedPileTitle(inSlot, quantity);

    /// <summary>The same pack level on the held/inventory tooltip of a rig stack broken out of a cell.</summary>
    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        dsc.AppendLine(TarRig.HeldFillInfo(FillPoints(inSlot.Itemstack), Capacity));
    }

    public WorldInteraction[] GetContainedInteractionHelp(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        bool full = FillPoints(slot.Itemstack) >= Capacity;
        if (full)
        {
            return new[]
            {
                new WorldInteraction { ActionLangCode = "immersivewoodworking:blockhelp-tarrig-seal",         MouseButton = EnumMouseButton.Right, HotKeyCode = "shift" },
                new WorldInteraction { ActionLangCode = "immersivewoodworking:blockhelp-tarrig-disassemble",  MouseButton = EnumMouseButton.Right }
            };
        }
        // Same two pack lines as the standalone perforated pot (the vessels pack identically, D14), so they carry the
        // same bark icons from the same builder — see TarRig.BirchBarkStacks.
        return new[]
        {
            new WorldInteraction { ActionLangCode = "immersivewoodworking:blockhelp-tarrig-pack",         MouseButton = EnumMouseButton.Right, HotKeyCode = "shift",                    Itemstacks = TarRig.BirchBarkStacks(be.Api, false) },
            new WorldInteraction { ActionLangCode = "immersivewoodworking:blockhelp-tarrig-packbulk",     MouseButton = EnumMouseButton.Right, HotKeyCodes = new[] { "ctrl", "shift" }, Itemstacks = TarRig.BirchBarkStacks(be.Api, true) },
            new WorldInteraction { ActionLangCode = "immersivewoodworking:blockhelp-tarrig-disassemble",  MouseButton = EnumMouseButton.Right }
        };
    }
}
