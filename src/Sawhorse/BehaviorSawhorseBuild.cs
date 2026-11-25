using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

/// <summary>
/// Collectible behavior patched onto <c>game:stick</c> (patches/sawhorse_build_behavior.json) so a stick can
/// lay down the FIRST sawhorse trestle (build stage 1). Advancing an already-placed trestle (stick → stage 2,
/// rope → complete) is NOT here — that is owned by <see cref="BlockSawhorse.OnBlockInteractStart"/>, because on
/// a plain right-click the block is consulted before the held item (HeldPriorityInteract defaults to false), so
/// the existing block grabs the interaction first. Rope therefore needs no behavior at all — it never PLACES a
/// trestle, it only finishes one that the block already owns.
///
/// Gesture = PLAIN right-click (no Shift, no Ctrl). Shift+RMB is deliberately left to the stick's vanilla
/// <c>GroundStorable</c> behavior (the "stick pile"), which only fires while sneaking — so the two never
/// collide. This mirrors <see cref="BlockChoppingBlock"/>, where plain RMB is the interaction and Shift falls
/// through to vanilla. Because our behavior is appended AFTER GroundStorable, and GroundStorable passes through
/// untouched when not sneaking, our handler still runs on the plain right-click.
///
/// Placement is SERVER-authoritative (the client just reports the interaction so the server runs it, then
/// reflects the synced block) — the same split <see cref="BlockEntityChoppingBlock.TryPut"/> uses.
/// </summary>
public class BehaviorSawhorseBuild : CollectibleBehavior
{
    static readonly AssetLocation SawhorseCode = new AssetLocation("immersivewoodworking", "sawhorse");

    // VSRoofing compat (ModDbFeedbackPlan T02.01): the off-hand materials that make a stick's plain right-click YIELD
    // to VSRoofing's PlaceFrame come from VSRoofing's own frame table (ResolveRoofingOffhand), so materials patched into
    // it yield too (VSRoofing's own Primitive Survival compat adds cordage). These four codes, from VSRoofing 1.6.3, are
    // only the fallback for when that table is missing or names nothing for the stick. Note the vanilla item code is
    // cattailtops (two t's) even though its asset file is catttailtops.json, and wildvine is a variant block so it needs
    // the -* glob.
    static readonly AssetLocation[] RoofingOffhandFallback =
    {
        new AssetLocation("game", "wildvine-*"),
        new AssetLocation("game", "cattailtops"),
        new AssetLocation("game", "papyrustops"),
        new AssetLocation("game", "flaxfibers"),
    };

    // VS Siding compat (ModDbFeedbackPlan T02.02): its build signal, a saw or any stone in the off-hand, copied from VS
    // Siding 1.5.0 SidingWallBlock.BuildSignalCodes (SidingWallBlock.cs:34); 1.4.x has the same two.
    static readonly AssetLocation[] SidingBuildSignalCodes =
    {
        new AssetLocation("game", "saw-*"),
        new AssetLocation("game", "stone-*"),
    };

    public BehaviorSawhorseBuild(CollectibleObject collObj) : base(collObj) { }

    // Cached at load: is the VSRoofing mod present? Its PlaceFrame behavior shares the stick's plain right-click, so we
    // only consult the off-hand (and possibly yield the click to it) when it's actually installed. When false the yield
    // in OnHeldInteractStart is skipped wholesale and the stick behaves exactly as it did before this compat hook — so a
    // player NOT running VSRoofing can always build a trestle whatever is in the off-hand. A code mod's presence is
    // identical on client and server, so this one-shot cache stays consistent on both sides of the interaction.
    bool roofingCompat;

    // Cached at load: does this collectible carry VS Siding's PlaceWallFrame (the class name its patch uses)? Only then can
    // VS Siding take the click. Gating on the mod alone would be wrong for 1.0.0-1.3.0: a saw already fits the off-hand
    // there, but the stick framing only came in 1.4.0, so the yield would leave the stick doing nothing. The behavior
    // list reaches the client with the item type packet, so both sides get the same answer.
    bool sidingCompat;

    // Off-hand codes and tools VSRoofing accepts beside this collectible in a frame; null until the first stick click
    // with something in the off-hand. Per instance, not static: a static would outlive the world and carry one
    // server's VSRoofing version and patches into the next.
    AssetLocation[]? roofingOffhandCodes;
    EnumTool[] roofingOffhandTools = Array.Empty<EnumTool>();

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        roofingCompat = api.ModLoader.IsModEnabled("vsroofing");
        sidingCompat = Array.Exists(collObj.CollectibleBehaviors, b => api.ClassRegistry.GetCollectibleBehaviorClassName(b.GetType()) == "vssiding.PlaceWallFrame");
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        // Our gesture is a plain right-click on the top of a surface. Bail (pass through to vanilla) for
        // anything else: a non-first event (held button repeat), an entity/empty-air click, or a sneak click —
        // Shift is the stick's vanilla GroundStorable "stick pile", which we must not steal.
        if (!firstEvent || blockSel == null || entitySel != null || byEntity?.World == null) return;
        if (byEntity.Controls.ShiftKey) return;

        // Ctrl+RMB is not our gesture either (ModDbFeedbackPlan T06.02): it stays with vanilla and other mods, and a
        // player reported one that shares it with us. A build below sets PreventSubsequent, which ends the behavior loop
        // before a later stick behavior sees the click. Ctrl is also the default sprint key, so a sprinting player lets
        // go of it to lay a trestle.
        if (byEntity.Controls.CtrlKey) return;

        IWorldAccessor world = byEntity.World;

        // Config "CraftableSawhorse" ON → the trestle is hand-crafted + placed as a finished block instead, and
        // this in-world stick build is fully OFF: pass the plain right-click straight through to vanilla / any other
        // mod. This is the whole point of the flag — it's what frees the stick's plain RMB from this mod, resolving a
        // conflict with another mod that also claims it. (Reads the live, server-synced config, like the rest of the mod.)
        ImmersiveWoodworkingModSystem? modSys = world.Api?.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        if (modSys != null && modSys.Config.CraftableSawhorse) return;

        // VSRoofing compat: it drives the SAME plain-RMB-with-a-stick gesture to raise roof frames, but only while a
        // roofing material is held in the OFF-HAND (read from its frame table, see ResolveRoofingOffhand).
        // When one is present we step aside — this early return leaves handling at PassThrough — so
        // VSRoofing's PlaceFrame gets the click whichever stick behavior the engine runs first. If PlaceFrame runs first
        // and raises a frame, its PreventSubsequent ends the loop before us; if it passes the click on (too few sticks),
        // this return keeps us from building in its place. If we run first, this return is what lets the loop reach it,
        // because a build below sets PreventSubsequent (ModDbFeedbackPlan T02.03). Gated on VSRoofing actually being
        // installed (roofingCompat) so this never blocks a build.
        if (roofingCompat && OffhandYieldsToRoofing(byEntity)) return;

        // VS Siding compat: with its build signal in the off-hand the click builds its frame. In 1.4.x PlaceWallFrame
        // claimed the click only once a frame stood, so a failed frame (too few sticks) fell through to us, and it ran
        // after us whenever our patch applied first (VS Siding issue #143): the server built the trestle, while the
        // client, where we place nothing, predicted the frame. The server refused it in the taken cell and the client
        // kept an unbreakable phantom frame inside the trestle. 1.5.0 moves PlaceWallFrame ahead of us in AssetsFinalize,
        // clients get that order with the item type packet, and it ends the loop whenever the stick has a framing, so
        // with 1.5.0 and its stock framings this return is never reached. It stays for 1.4.x and, like the VSRoofing
        // yield, works in either behavior order.
        if (sidingCompat && OffhandYieldsToSiding(byEntity)) return;

        IPlayer? byPlayer = (byEntity as EntityPlayer) != null ? world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID) : null;
        if (byPlayer == null) return;

        // An already-placed trestle is driven by BlockSawhorse (block-first on plain RMB), so we'd never be
        // reached for it — but never start a SECOND trestle on top of an existing one regardless.
        Block targetBlock = world.BlockAccessor.GetBlock(blockSel.Position);
        if (targetBlock is BlockSawhorse) return;

        // Place on the TOP face of a solid block into the free space above — the same placement test the
        // vanilla stick pile uses, so the trestle lands where a player expects and never overwrites anything.
        // (Full support/stability — breaking when the block below goes away — is T03.04.)
        if (blockSel.Face != BlockFacing.UP) return;
        Block? sawhorse = world.GetBlock(SawhorseCode);
        if (sawhorse == null) return;
        BlockPos pos = blockSel.Position.UpCopy();
        if (world.BlockAccessor.GetBlock(pos).Replaceable < 6000) return;                              // free space above
        if (!targetBlock.CanAttachBlockAt(world.BlockAccessor, sawhorse, blockSel.Position, BlockFacing.UP)) return; // solid support below

        // We own this interaction now: stop the default action (no "eating" the stick) on both sides and tell
        // the engine to run it on the server too. PreventSubsequent also ends the behavior loop, so no later stick
        // behavior's OnHeldInteractStart runs for this click (ModDbFeedbackPlan T02.03). PreventDefault let the loop run
        // on into VS Siding 1.4.x's PlaceWallFrame (its issue #143, see the yield above). The yields return before this
        // point, so VSRoofing and VS Siding still get the click with their item in the off-hand.
        handHandling = EnumHandHandling.PreventDefaultAction;
        handling = EnumHandling.PreventSubsequent;

        // Server is authoritative: it places the block + spends the stick; the client reflects the synced block.
        if (world.Side != EnumAppSide.Server) return;
        if (!world.Claims.TryAccess(byPlayer, pos, EnumBlockAccessFlags.BuildOrBreak)) return;

        world.BlockAccessor.SetBlock(sawhorse.BlockId, pos);
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntitySawhorse be)
        {
            be.OnBuilt(byPlayer, slot); // spend 1 stick (survival) + build sound; stage starts at Stage1
        }
        else
        {
            // Should be unreachable (sawhorse.json declares entityClass "iw-sawhorse", registered at startup), but
            // if the BE ever fails to attach the trestle would sit there without consuming the stick — log it so
            // the silent anomaly is diagnosable rather than looking like a free block.
            world.Logger.Warning("[immersivewoodworking] Placed sawhorse at {0} but no BlockEntitySawhorse attached; stick not consumed.", pos);
        }
    }

    /// <summary>Whether the player's OFF-HAND holds something VSRoofing accepts for a frame of this collectible (see
    /// <see cref="ResolveRoofingOffhand"/>) — in which case the plain right-click is theirs to raise a roof frame
    /// and this stick behavior yields it. Codes are matched with <see cref="WildcardUtil"/> like VSRoofing does, so
    /// they may glob (e.g. <c>game:wildvine-*-north</c>). An empty off-hand → false (build the trestle as usual).
    /// Caller gates this on <see cref="roofingCompat"/>, so it never runs when VSRoofing isn't installed.</summary>
    bool OffhandYieldsToRoofing(EntityAgent byEntity)
    {
        CollectibleObject? offhand = byEntity.LeftHandItemSlot?.Itemstack?.Collectible;
        if (offhand?.Code == null) return false;

        if (roofingOffhandCodes == null) ResolveRoofingOffhand(byEntity.World);

        if (offhand.Tool is EnumTool tool && Array.IndexOf(roofingOffhandTools, tool) >= 0) return true;
        foreach (AssetLocation pattern in roofingOffhandCodes!)
            if (WildcardUtil.Match(pattern, offhand.Code)) return true;
        return false;
    }

    /// <summary>Reads VSRoofing's frame table (attribute <c>Frames</c> of block <c>vsroofing:roof-*</c>, the same
    /// attributes its RoofBlock loads them from) and keeps the off-hand codes and tools of every frame whose
    /// <c>Material.Code</c> matches this collectible. Both schemas: 2.x keeps the codes in <c>Offhand.Material.Code</c>,
    /// 1.6.x in <c>Offhand.Code</c>; a frame may instead want <c>Offhand.Tool</c>. Falls back to
    /// <see cref="RoofingOffhandFallback"/> with one warning when nothing is found.</summary>
    void ResolveRoofingOffhand(IWorldAccessor world)
    {
        var codes = new List<AssetLocation>();
        var tools = new List<EnumTool>();

        // Every roof-* variant comes from one blocktype with shared attributes, so the first one carries the whole table.
        Block[] roofs = world.SearchBlocks(new AssetLocation("vsroofing", "roof-*"));
        if (roofs.Length > 0 && roofs[0].Attributes?["Frames"].Token is JObject frames)
        {
            foreach (JProperty frameProp in frames.Properties())
            {
                JsonObject frame = new JsonObject(frameProp.Value);
                if (!ParseCodes(frame["Material"]["Code"]).Exists(p => WildcardUtil.Match(p, collObj.Code))) continue;

                JsonObject offhandInfo = frame["Offhand"];
                if (Enum.TryParse(offhandInfo["Tool"].AsString(), true, out EnumTool tool)) tools.Add(tool);
                codes.AddRange(ParseCodes(offhandInfo["Material"]["Code"]));
                codes.AddRange(ParseCodes(offhandInfo["Code"]));
            }
        }

        if (codes.Count == 0 && tools.Count == 0)
        {
            world.Logger.Warning("[immersivewoodworking] No VSRoofing frame table entry for {0} found; the stick yields to VSRoofing only for its 1.6.3 off-hand materials.", collObj.Code);
            codes.AddRange(RoofingOffhandFallback);
        }

        roofingOffhandCodes = codes.ToArray();
        roofingOffhandTools = tools.ToArray();
    }

    static List<AssetLocation> ParseCodes(JsonObject codeArray)
    {
        var codes = new List<AssetLocation>();
        foreach (string? code in codeArray.AsArray<string>() ?? Array.Empty<string>())
            if (!string.IsNullOrEmpty(code)) codes.Add(new AssetLocation(code));
        return codes;
    }

    /// <summary>Whether the player's OFF-HAND holds VS Siding's build signal (<see cref="SidingBuildSignalCodes"/>),
    /// matched with <see cref="WildcardUtil"/> like its <c>SidingWallBlock.HasBuildSignal</c> (SidingWallBlock.cs:497),
    /// which reads the same hotbar slot. Caller gates this on <see cref="sidingCompat"/>.</summary>
    static bool OffhandYieldsToSiding(EntityAgent byEntity)
    {
        AssetLocation? offhandCode = byEntity.LeftHandItemSlot?.Itemstack?.Collectible?.Code;
        if (offhandCode == null) return false;

        foreach (AssetLocation pattern in SidingBuildSignalCodes)
            if (WildcardUtil.Match(pattern, offhandCode)) return true;
        return false;
    }
}
