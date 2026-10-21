using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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
/// Gesture = PLAIN right-click (no Shift). Shift+RMB is deliberately left to the stick's vanilla
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

    // VSRoofing compat: off-hand roofing materials whose presence makes a stick's plain right-click YIELD to
    // VSRoofing's PlaceFrame instead of laying a trestle. A fixed list (was the RoofingOffhandCodes config field —
    // removed, not worth a player knob) mirroring the four materials VSRoofing tags. Matched with WildcardUtil, so
    // globs (game:wildvine-*) are allowed. Note the vanilla item code is cattailtops (two t's) even though its asset
    // file is catttailtops.json, and wildvine is a variant block so it needs the -* glob. Only consulted when VSRoofing
    // is actually installed (roofingCompat).
    static readonly AssetLocation[] RoofingOffhandCodes =
    {
        new AssetLocation("game", "wildvine-*"),
        new AssetLocation("game", "cattailtops"),
        new AssetLocation("game", "papyrustops"),
        new AssetLocation("game", "flaxfibers"),
    };

    public BehaviorSawhorseBuild(CollectibleObject collObj) : base(collObj) { }

    // Cached at load: is the VSRoofing mod present? Its PlaceFrame behavior shares the stick's plain right-click, so we
    // only consult the off-hand (and possibly yield the click to it) when it's actually installed. When false the yield
    // in OnHeldInteractStart is skipped wholesale and the stick behaves exactly as it did before this compat hook — so a
    // player NOT running VSRoofing can always build a trestle whatever is in the off-hand. A code mod's presence is
    // identical on client and server, so this one-shot cache stays consistent on both sides of the interaction.
    bool roofingCompat;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        roofingCompat = api.ModLoader.IsModEnabled("vsroofing");
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        // Our gesture is a plain right-click on the top of a surface. Bail (pass through to vanilla) for
        // anything else: a non-first event (held button repeat), an entity/empty-air click, or a sneak click —
        // Shift is the stick's vanilla GroundStorable "stick pile", which we must not steal.
        if (!firstEvent || blockSel == null || entitySel != null || byEntity?.World == null) return;
        if (byEntity.Controls.ShiftKey) return;

        IWorldAccessor world = byEntity.World;

        // Config "CraftableSawhorse" ON → the trestle is hand-crafted + placed as a finished block instead, and
        // this in-world stick build is fully OFF: pass the plain right-click straight through to vanilla / any other
        // mod. This is the whole point of the flag — it's what frees the stick's plain RMB from this mod, resolving a
        // conflict with another mod that also claims it. (Reads the live, server-synced config, like the rest of the mod.)
        ImmersiveWoodworkingModSystem? modSys = world.Api?.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        if (modSys != null && modSys.Config.CraftableSawhorse) return;

        // VSRoofing compat: it drives the SAME plain-RMB-with-a-stick gesture to raise roof frames, but only while a
        // roofing material is held in the OFF-HAND (the fixed RoofingOffhandCodes list above: wildvine / cattail /
        // papyrus / flax). When one is present we step aside — this early return leaves handling at PassThrough — so
        // VSRoofing's PlaceFrame claims the click, regardless of which mod's stick behavior the engine ran first: ours
        // sets only PreventDefault (the behavior loop then continues on to it), while its PlaceFrame sets
        // PreventSubsequent. Gated on VSRoofing actually being installed (roofingCompat) so this never blocks a build.
        if (roofingCompat && OffhandYieldsToRoofing(byEntity)) return;

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
        // the engine to run it on the server too.
        handHandling = EnumHandHandling.PreventDefaultAction;
        handling = EnumHandling.PreventDefault;

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

    /// <summary>Whether the player's OFF-HAND holds a VSRoofing roofing material (the fixed
    /// <see cref="RoofingOffhandCodes"/> list) — in which case the plain right-click is theirs to raise a roof frame
    /// and this stick behavior yields it. Matched with <see cref="WildcardUtil"/>, so the entries may glob (e.g.
    /// <c>game:wildvine-*</c>). An empty off-hand → false (build the trestle as usual). Caller gates this on
    /// <see cref="roofingCompat"/>, so it never runs when VSRoofing isn't installed.</summary>
    static bool OffhandYieldsToRoofing(EntityAgent byEntity)
    {
        AssetLocation? offhandCode = byEntity.LeftHandItemSlot?.Itemstack?.Collectible?.Code;
        if (offhandCode == null) return false;

        foreach (AssetLocation pattern in RoofingOffhandCodes)
            if (WildcardUtil.Match(pattern, offhandCode)) return true;
        return false;
    }
}
