using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// Hand-break for beach driftwood (E07) — the mod's first <see cref="BlockBehavior"/>, bolted onto the vanilla
/// <c>game:flotsam-driftwood-*</c> blocks via <c>patches/flotsam_break_behavior.json</c>. Driftwood is no longer
/// chopped on the chopping block (T07.01 dropped it from the accept-list); instead you snap it apart into firewood +
/// sticks, BY SIZE (the vanilla model variant): a large piece (<c>-3</c>) yields the most, a small one (<c>-4</c>)
/// only sticks. The bare-handed gesture fits brittle beach wood — the <c>driftwood-break</c> animation carries no tool.
///
/// Two entry points, same size-table + progress + drops:
///   • <b>In hand (primary)</b> — hold plain right-click with a flotsam ITEM in the active hand to break it where you
///     stand, no need to place it first. Shift(+Ctrl)-right instead falls through to vanilla, which is how you PLACE
///     the driftwood as a block. Rides <see cref="OnHeldInteractStart"/> etc. — a block's <c>behaviors</c> are
///     consulted for held interactions too (same path the vanilla bucket's MilkingContainer uses), so no separate
///     collectible behavior is needed. Claiming the click with <c>EnumHandHandling.PreventDefault</c> suppresses the
///     vanilla placement (mirrors GroundStorable's pile) and plays no default anim — we drive <c>driftwood-break</c>.
///   • <b>Placed block</b> — hold <b>Shift + right-click</b> on a flotsam block in the world with an EMPTY hand
///     (rides <see cref="OnBlockInteractStart"/>). A PLAIN right-click on the placed block is left for vanilla (does
///     nothing) or a wood mod's <c>game:RightClickPickup</c> (lifts the driftwood off the ground) — that pickup only
///     fires with Shift UP, so the Shift gate keeps the break from ever stealing the pickup. Empty-hand-only so it
///     never collides with a held item. Being a gesture that DELETES a world block, it carries the same authorization
///     the vanilla pickup does — land claim at <c>BuildOrBreak</c> plus reinforcement, see <see cref="MayBreakAt"/>.
///
/// A single continuous hold, like mining a block: progress IS the engine's per-interaction <c>secondsUsed</c> timer
/// (no persisted state — releasing early just resets), and the block breaks once it reaches
/// <see cref="ImmersiveWoodworkingConfig.FlotsamBreakSeconds"/>. Server-authoritative: only the server removes the
/// block and spawns the drops; the client drives the gesture + the fading work sound locally. Composes with the
/// block's existing <c>Unstable</c> behavior — this one claims ONLY a Shift, empty-handed right-click on the
/// placed block (a plain click or a held item fall through to pickup / vanilla placement). Attaching an <c>iw-</c> behavior to a foreign
/// vanilla collectible via an <c>op:add /behaviors/-</c> patch mirrors how the mod already bolts
/// <c>iw-sawhorsebuild</c> onto <c>game:stick</c> — no Harmony.
/// </summary>
public class BlockBehaviorFlotsamBreak : BlockBehavior
{
    public BlockBehaviorFlotsamBreak(Block block) : base(block) { }

    // FirstCodePart shared by all four flotsam variants (flotsam-driftwood-1..4) — the "still our block?" re-check.
    // "flotsam" is the only code-base in the game with this first part (see BlockEntityChoppingBlock's LogCodeParts
    // note), so it never matches unrelated blocks.
    const string FlotsamPart = "flotsam";

    // The break gesture — a one-shot arm "snap" (art .../driftwood-break; registered by
    // patches/driftwood-break_animations.json, backfilled onto reskin shapes by EntityBehaviorChopAnim's
    // CustomAnimCodes). Self-completes (shape onActivityStopped=PlayTillEnd / onAnimationEnd=EaseOut); the engine
    // cascades the "-fp" sibling for the holder's first-person view.
    public const string BreakAnim = "driftwood-break";

    ICoreAPI? api;
    ICoreClientAPI? capi;
    ImmersiveWoodworkingModSystem? modSys;

    // Read the live, server-synced config each access — the ModSystem swaps the whole Config object in on the sync
    // packet, so cache the stable ModSystem ref, NEVER the Config object. Mirrors the block-entity accessors.
    ImmersiveWoodworkingConfig Config =>
        (modSys ??= api?.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>())?.Config ?? new ImmersiveWoodworkingConfig();

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        this.api = api;
        capi = api as ICoreClientAPI;
        Dbg(api.Logger, "[IW-FLOTSAM-DBG] behavior OnLoaded on block '{0}' (side {1})", block?.Code, api.Side);
    }

    // ── Client work sound: rotating debarking1/2 with a small fade-out on release (the "bark coming off" clips the
    // user reserved for this trigger). A code-driven ILoadedSound handle — not a fire-and-forget PlaySoundAt — so the
    // release can FADE it. Clones the sawhorse debark loop (BlockEntitySawhorse.Debarking.cs), narrowed to clips 1&2.
    // One handle per client (the local player breaks at most one flotsam at a time); the server instance never touches
    // it. Explicit clips (NOT a debarking* wildcard, which would also catch the per-tier hit clips). All MONO so
    // OpenAL spatializes them (vs-positional-audio-mono). ──
    static readonly AssetLocation[] BreakLoopClips =
    {
        new AssetLocation("immersivewoodworking", "sounds/debark/debarking1"),
        new AssetLocation("immersivewoodworking", "sounds/debark/debarking2"),
    };
    const float BreakSoundVolume  = 1f;
    const float BreakSoundRange   = 16f;
    const float BreakSoundFadeOut = 0.3f; // seconds — the "small fade-out"
    ILoadedSound? breakSound;

    WorldInteraction[]? breakInteraction;

    /// <summary>Only a SHIFT + empty-handed right-click breaks a PLACED driftwood block. A plain right-click is
    /// deliberately left alone: in vanilla it does nothing, and with a wood mod (Wildcraft: Trees patches
    /// game:RightClickPickup onto driftwood) it LIFTS the piece — and that pickup itself only fires when Shift is UP
    /// (BehaviorRightClickPickup line 79). So the modifier split (plain = pick up / nothing, Shift = break) keeps the
    /// two gestures from ever colliding, with no need to sniff for a pickup behavior. Ctrl still falls through; the
    /// break is bare-handed (the animation carries no tool), so an empty active hand is the gate.</summary>
    static bool CanBreak(IPlayer? byPlayer)
    {
        var c = byPlayer?.Entity?.Controls;
        if (c == null || !c.ShiftKey || c.CtrlKey) return false;
        ItemSlot? active = byPlayer!.InventoryManager?.ActiveHotbarSlot;
        return active != null && active.Empty;
    }

    // Read through CodeParts, not Block.FirstCodePart (AuditFixPlan T05.03): this one runs on every Step of the break
    // hold, on both sides, where the engine accessor would split the whole flotsam code per call. The one-shot
    // re-checks in BreakInHand / BreakDriftwood keep the engine idiom — they fire once, at the end of the hold.
    static bool IsFlotsamAt(IWorldAccessor world, BlockPos pos)
        => CodeParts.Is(world.BlockAccessor.GetBlock(pos)?.Code, 0, FlotsamPart);

    /// <summary>May <paramref name="byPlayer"/> REMOVE the driftwood at <paramref name="pos"/>? The placed-block gesture
    /// deletes a world block and hands over its drops, so it is a <c>BuildOrBreak</c> action — but the engine only ever
    /// authorized it as <c>Use</c> (AuditFixPlan T07.03, F-L3-01). Both entry points into the interaction dispatch gate
    /// the whole Start/Step/Stop chain on <c>EnumBlockAccessFlags.Use</c>: <c>ServerSystemBlockSimulation.HandleBlockInteract</c>
    /// and the base <c>Block.OnBlockInteractStart</c>. And <c>WorldMap.testBlockAccess</c> rejects <c>EnumGameMode.Guest</c>
    /// and a missing <c>buildblocks</c> privilege ONLY in its BuildOrBreak branch, while its Use branch additionally
    /// lets a claim through when the owner set <c>AllowUseEveryone</c>. So a visitor in <c>/gamemode guest</c>, and
    /// anyone holding only <c>use</c> on someone else's beach, snapped the driftwood apart and pocketed the firewood.
    /// The vanilla gesture this one is modelled on tests the flag explicitly (<c>BehaviorRightClickPickup</c>).
    ///
    /// <para><c>TryAccess</c>, not <c>TestAccess</c>: it MESSAGES the player itself (<c>noprivilege-buildbreak-*</c>),
    /// re-syncs the active slot and the block, and raises the server's <c>CanPlaceOrBreakBlock</c> event — the only seam
    /// through which a foreign anti-grief mod can veto us at all. Never write a refusal of our own on top; that doubles
    /// the message. Calling it on the CLIENT too is deliberate and safe: the server ships the full claim list on join
    /// and <c>WorldMap.TryAccess</c> has no client short-circuit (the "always true on the client" XML doc is stale), so
    /// the client judges for real and a refused click never reaches the wire.</para>
    ///
    /// <para>Reinforcement has to be asked for by hand, with the same static vanilla helper the right-click pickup uses:
    /// <c>BlockBehaviorReinforcable</c> only overrides <c>OnBlockBroken</c>/<c>OnBlockExploded</c>, and
    /// <see cref="BreakDriftwood"/> removes the block through a bare <c>SetBlock(0)</c> that enters neither. Driftwood IS
    /// reinforcable (<c>blockmaterial: "Wood"</c>, no <c>reinforcable: false</c>, so <c>BlockReinforcement</c>'s
    /// <c>IsReinforcable</c> sweep attaches the behavior), which without this made a plumb-and-square on a piece
    /// decorative. The refusal is silent, exactly as vanilla's pickup is. Both sides again, which is strictly better
    /// than vanilla's server-only test: reinforcements ride chunk moddata under the key <c>reinforcements</c>, and that
    /// reaches the client twice over — with the chunk itself (<c>IWorldChunk.SetModdata</c>: "When set on the server
    /// before the chunk is sent to the client, the data will also be sent to the client") and again on every change,
    /// through the <c>blockreinforcement</c> channel's <c>ChunkReinforcementData</c>. So the click normally dies before
    /// the hold even starts, and a client somehow missing the data just falls through to the server's verdict.</para>
    ///
    /// <para><paramref name="byPlayer"/> must be non-null: <c>testBlockAccess</c> dereferences it (<c>WorldData.CurrentGameMode</c>,
    /// <c>Entity.Alive</c>) BEFORE the null-guard that only covers the message branch. Both call sites clear
    /// <see cref="CanBreak"/> first, which already dereferences the player, so the site is safe — do not lift this
    /// helper onto a path where the player can be null.</para></summary>
    static bool MayBreakAt(IWorldAccessor world, IPlayer byPlayer, BlockPos pos)
        => world.Claims.TryAccess(byPlayer, pos, EnumBlockAccessFlags.BuildOrBreak)
        && BlockBehaviorReinforcable.AllowRightClickPickup(world, pos, byPlayer);

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
    {
        Dbg(world.Logger, "[IW-FLOTSAM-DBG] OnBlockInteractStart side={0} canBreak={1} activeEmpty={2}",
            world.Side, CanBreak(byPlayer), byPlayer?.InventoryManager?.ActiveHotbarSlot?.Empty);

        if (blockSel == null || !CanBreak(byPlayer)) return false; // handling stays PassThrough → vanilla / other behaviors

        // Authorize the REMOVAL rather than the use (AuditFixPlan T07.03, F-L3-01 — see MayBreakAt for what the engine
        // gives us instead). Deliberately AFTER the gesture gate, though the finding phrased it as "the first line":
        // TryAccess speaks to the player, so testing it before we know the click is even ours would fire our refusal at
        // every PLAIN right-click on driftwood inside a claim — a gesture that belongs to a wood mod's pickup, which
        // then messages a second time out of its own BuildOrBreak test. Leaving handling at PassThrough on refusal
        // mirrors that same vanilla pickup, which returns false without claiming.
        // byPlayer! : CanBreak returned true, which it can only do after dereferencing byPlayer.Entity.Controls — the
        // same proof the line below this block relies on. The bang is needed only because the Dbg call above reads the
        // player through `?.` and so leaves the compiler's flow state at maybe-null.
        if (!MayBreakAt(world, byPlayer!, blockSel.Position)) return false;

        // No pickup-coexistence check needed: the break now demands Shift, while a wood mod's pickup
        // (game:RightClickPickup) fires ONLY on a plain — Shift-UP — right-click (BehaviorRightClickPickup line 79). The
        // two gestures are disjoint, so lifting driftwood off the ground (plain RMB) never reaches us, and our
        // Shift-break never collides with the pickup — regardless of behavior order OR how the wood mod implements its
        // pickup. The IN-HAND break (plain RMB on a held flotsam ITEM) is untouched.
        handling = EnumHandling.PreventSubsequent; // claim it fully — we drive the hold
        byPlayer?.Entity?.StartAnimation(BreakAnim); // both sides; the engine cascades "-fp" for the holder's 1st person
        return true;                                // swallow the click + begin the timed hold (Step/Stop follow)
    }

    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
    {
        // Bail unless THIS is our Shift-break. The engine calls Step on EVERY behavior of a block whose interaction
        // ANY behavior claimed — so on a plain right-click that a wood mod's RightClickPickup claimed to LIFT the
        // driftwood, our Step still fires. Without re-checking CanBreak (Shift), the block is briefly still flotsam on
        // the client (the pickup's removal hasn't synced back) and we'd start the break work sound OVER the pickup.
        // The Shift gate is disjoint from pickup (which needs Shift UP), so this cleanly stays out. Also ends the hold
        // the instant the block is no longer our driftwood (broke this tick, or a sync removed it). StopBreakSound is
        // idempotent — a no-op when we never started one (e.g. this very pickup case).
        if (blockSel == null || !CanBreak(byPlayer) || !IsFlotsamAt(world, blockSel.Position))
        {
            if (world.Side == EnumAppSide.Client) StopBreakSound(); // fade any in-flight clip rather than a hard cut
            return false;
        }
        handling = EnumHandling.PreventSubsequent;

        // Break is server-authoritative. The block-gone check above then ends the hold on the next tick (both sides).
        // The threshold is tested against the HONEST hold clock (AuditFixPlan T02.03): the engine's `secondsUsed`
        // arrives on the server already inflated by the frames it replayed for the last 0.15 s packet (+0.02 s each,
        // ~0.14 s in all — ImmersiveWoodworkingModSystem.HonestHoldSeconds), so an unclamped test snapped the
        // driftwood measurably before the configured time (F-L3-04).
        if (world.Side == EnumAppSide.Server
            && ImmersiveWoodworkingModSystem.HonestHoldSeconds(world, byPlayer, secondsUsed) >= Config.FlotsamBreakSeconds)
        {
            // Re-authorize at the instant of removal (AuditFixPlan T07.03): a land claim or a plumb-and-square can land
            // in the middle of a hold that began legitimately, and Start's verdict is stale by then. It sits INSIDE the
            // threshold branch rather than at the top of Step on purpose — that way it costs one claim lookup per
            // completed break instead of one per 20 ms tick, and the message TryAccess emits on refusal is a single
            // line rather than a stream. Returning false ends the server's hold (Stop still runs).
            if (!MayBreakAt(world, byPlayer, blockSel.Position)) return false;
            BreakDriftwood(world, blockSel.Position);
        }

        if (world.Side == EnumAppSide.Client) UpdateBreakSound(blockSel.Position);
        return true;
    }

    public override void OnBlockInteractStop(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
    {
        byPlayer.Entity.StopAnimation(BreakAnim); // idempotent; PlayTillEnd lets the snap finish, EaseOut fades the pose
        if (world.Side == EnumAppSide.Client) StopBreakSound();
    }

    public override bool OnBlockInteractCancel(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
    {
        byPlayer.Entity.StopAnimation(BreakAnim);
        if (world.Side == EnumAppSide.Client) StopBreakSound();
        return true; // allow the cancel (stop the hold) — the safe default the sawhorse's base cancel returns
    }

    // ──────────────────── In-hand break (held flotsam item) ────────────────────
    // Symmetric to the world-block gesture above but for flotsam held in the ACTIVE hand — the primary way to break
    // driftwood. Because BlockBehavior : CollectibleBehavior and a block's `behaviors` are also consulted for held
    // interactions (Collectible.OnHeldInteractStart iterates them — the same path the vanilla bucket's MilkingContainer
    // rides), these overrides fire while flotsam is in hand. Same size-table (DropsFor) and threshold
    // (FlotsamBreakSeconds) as the world break; progress IS the engine's `secondsUsed` (no persist).
    //
    // Lifecycle mirrors vanilla MilkingContainer / tryEat — the two subtleties that pattern exists to solve both bite
    // here (a flotsam item is a placeable Block, and the client's use-clock leads the server's):
    //   • CLAIM UNCONDITIONALLY. Once a break cycle ends (HandUse→None) while right-click is still held, the engine
    //     re-calls OnHeldInteractStart every frame with firstEvent=false. Relinquishing the claim there would let the
    //     vanilla block-place path (sysmouse OnBlockBuild) PLACE the rest of the stack. So we set handHandling on EVERY
    //     qualifying call, not just firstEvent → holding right-click CHAIN-breaks the stack (one piece per
    //     FlotsamBreakSeconds); only the one-shot snap animation is gated on the initial press.
    //   • FINALIZE IN Stop, NOT Step. The client owns the timing and ends the hold at the threshold on its own clock,
    //     so breaking inside the server's Step would race (and usually lose to) the client-driven stop, silently
    //     dropping the break. Instead the server keeps the hold alive in Step and spends the flotsam in
    //     OnHeldInteractStop, gated near the threshold (a small tolerance absorbs the residual skew).
    //     The old note here claimed "the server's secondsUsed lags" — the skew runs the OTHER way (AuditFixPlan
    //     T02.03 / F-L3-04). ServerSystemInventory computes the hold time once per packet and then REPLAYS the
    //     frames that packet covered, adding 0.02 s per replayed step, so the value reaching the server's Stop is up
    //     to ~0.16 s AHEAD of the time actually held — which is why an unclamped gate here completed the break for a
    //     player who let go early. Both gates below therefore read the honest clock
    //     (ImmersiveWoodworkingModSystem.HonestHoldSeconds), leaving the tolerance to do only the job it was named
    //     for: absorb network jitter.

    // Tolerance (s) absorbing the residual client→server jitter at the hold's end — mirrors MilkingContainer's
    // 2.95/3.0. It is NOT what covers the engine's replay inflation; HonestHoldSeconds removes that first (T02.03).
    const float BreakCompleteTolerance = 0.05f;

    /// <summary>Only a PLAIN right-click breaks a held flotsam in hand. Shift (the vanilla PLACE-as-block gesture) and
    /// Ctrl fall through to vanilla. The active slot holds flotsam by construction — this behavior only rides flotsam.
    ///
    /// <para>An <paramref name="entitySel"/> disqualifies the click too (AuditFixPlan T07.03, F-L8-03). When the ray
    /// hits an entity nearer than any block, <c>GameMain.RayTraceForSelection</c> nulls the BLOCK selection and keeps
    /// only the entity one, and the dispatcher then asks the held item FIRST:
    /// <c>SystemMouseInWorldInteractions.HandleMouseInteractionsNoBlockSelected</c> →
    /// <c>TryBeginUseActiveSlotItem(null, EntitySelection)</c>, whose <c>true</c> short-circuits BOTH the local
    /// <c>Entity.OnInteract</c> and the <c>EntityInteraction</c> packet. Claiming unconditionally therefore meant a
    /// player carrying driftwood could not right-click any entity that does something with a full hand: the click was
    /// eaten, and held long enough it spent a piece of driftwood instead. Only the ENTITY term is added — a null
    /// <c>blockSel</c> with no entity is the documented "snap it apart where you stand" case and stays ours. The same
    /// test already guards <c>ItemHalfLog</c> and <c>BehaviorSawhorseBuild</c>.</para></summary>
    static bool CanBreakInHand(EntityAgent? byEntity, EntitySelection? entitySel)
    {
        if (entitySel != null) return false;
        var c = byEntity?.Controls;
        return c != null && !c.ShiftKey && !c.CtrlKey;
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        Dbg(byEntity.World.Logger, "[IW-FLOTSAM-DBG] OnHeldInteractStart side={0} first={1} shift={2} ctrl={3} entity={4} canBreak={5} item={6}",
            byEntity.World.Side, firstEvent, byEntity.Controls?.ShiftKey, byEntity.Controls?.CtrlKey, entitySel?.Entity?.Code,
            CanBreakInHand(byEntity, entitySel), slot?.Itemstack?.Collectible?.Code);

        // Shift/Ctrl are left untouched (handling stays PassThrough) so the engine runs vanilla placement — that is how
        // the player PLACES the driftwood. An entity under the crosshair is left alone for the same reason: the click
        // is the entity's, not ours (see CanBreakInHand).
        if (!CanBreakInHand(byEntity, entitySel)) return;

        // Claim UNCONDITIONALLY (see the note above) so a firstEvent=false re-entry mid-hold can never fall through to
        // vanilla placement; only the snap animation is a one-shot on the initial press.
        if (firstEvent) byEntity.StartAnimation(BreakAnim); // engine cascades "-fp" for the holder's 1st person
        handHandling = EnumHandHandling.PreventDefault;      // no vanilla placement, no default anim; still calls Step/Stop + notifies server
        handling = EnumHandling.PreventSubsequent;           // we fully own this held interaction
    }

    public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        // The entity gate of CanBreakInHand is deliberately NOT repeated here (AuditFixPlan T07.03). It guards the
        // DISPATCH decision, which the engine only makes on a press: while a held interaction is live the dispatcher
        // drives Step alone and starts no entity interaction, so there is nothing left to steal. Re-testing it would
        // instead throw away a nearly finished break every time a chicken wanders into the crosshair. (The tar pot's
        // twin re-gate in its own Step is a different case: its work is already done on the press, so releasing costs
        // the player nothing.)
        //
        // End the hold the instant the active hand is no longer flotsam (player switched/emptied the slot mid-hold).
        if (slot?.Itemstack?.Collectible?.FirstCodePart() != FlotsamPart)
        {
            if (byEntity.World.Side == EnumAppSide.Client) StopBreakSound();
            return false;
        }
        handling = EnumHandling.PreventSubsequent;

        // Server keeps the hold alive (never self-completes); the break is finalized in OnHeldInteractStop. The client
        // owns the timing: drive the work loop, then end the hold at the threshold, which triggers Stop on both sides.
        // (The client's own secondsUsed below is a genuine per-frame clock — it is only the SERVER's copy that the
        // engine replays and inflates, T02.03 — so the threshold here needs no clamp.)
        if (byEntity.World.Side == EnumAppSide.Server) return true;

        if (secondsUsed >= Config.FlotsamBreakSeconds) { StopBreakSound(); return false; }
        UpdateBreakSoundInHand();
        return true;
    }

    public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        // Server-authoritative completion: Stop is the point guaranteed to run when the client ends the hold, and the
        // honest hold clock lands ~at the threshold there. Gated near the threshold so an early release does NOT
        // break; the tolerance absorbs the residual jitter. BreakInHand re-checks the slot, so a switched/emptied slot
        // is safe. The raw argument is the engine's replay-inflated one (T02.03) — measure with the honest clock, or a
        // release up to ~0.16 s short of the threshold still snaps the driftwood (F-L3-04). Logged side by side so the
        // IwDebug trace shows how far apart the two run.
        float held = ImmersiveWoodworkingModSystem.HonestHoldSeconds(byEntity.World, byEntity, secondsUsed);
        Dbg(byEntity.World.Logger, "[IW-FLOTSAM-DBG] OnHeldInteractStop side={0} secondsUsed={1:0.###} held={2:0.###} threshold={3:0.###} willBreak={4}",
            byEntity.World.Side, secondsUsed, held, Config.FlotsamBreakSeconds, byEntity.World.Side == EnumAppSide.Server && held >= Config.FlotsamBreakSeconds - BreakCompleteTolerance);

        if (byEntity.World.Side == EnumAppSide.Server && held >= Config.FlotsamBreakSeconds - BreakCompleteTolerance)
        {
            BreakInHand(byEntity, slot);
            handling = EnumHandling.PreventSubsequent;
        }

        byEntity.StopAnimation(BreakAnim); // idempotent; PlayTillEnd lets the snap finish, EaseOut fades the pose
        if (byEntity.World.Side == EnumAppSide.Client) StopBreakSound();
    }

    public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason, ref EnumHandling handling)
    {
        byEntity.StopAnimation(BreakAnim);
        if (byEntity.World.Side == EnumAppSide.Client) StopBreakSound();
        return true; // allow the cancel (releasing right-click ends the hold)
    }

    /// <summary>Snap ONE held flotsam apart into firewood + sticks by SIZE (same <see cref="DropsFor"/> table as the
    /// world break), dropping them at the breaker's feet (<c>SpawnItemEntity</c> → auto-pickup; never lost, unlike a
    /// full-inventory give), and spend the piece. Server-only; re-checks the slot so it can't spend on a stale slot.</summary>
    void BreakInHand(EntityAgent byEntity, ItemSlot slot)
    {
        Block? block = slot?.Itemstack?.Block;
        if (slot == null || block?.FirstCodePart() != FlotsamPart) return; // slot changed out from under us

        (int firewood, int sticks) = DropsFor(block);
        slot.TakeOut(1);
        slot.MarkDirty();

        Vec3d dropPos = byEntity.Pos.XYZ.Add(0, 0.5, 0);
        SpawnStack(byEntity.World, "firewood", firewood, dropPos);
        SpawnStack(byEntity.World, "stick", sticks, dropPos);
    }

    WorldInteraction[]? heldBreakInteraction;

    /// <summary>The in-hand "hold-right to break" hint, shown while a flotsam item is selected. Added to (not
    /// replacing) any vanilla held hints — handling left PassThrough.</summary>
    public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot, ref EnumHandling handling)
    {
        return heldBreakInteraction ??= new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-flotsam-break",
                MouseButton = EnumMouseButton.Right
                // Itemstacks left null — a bare-hand gesture, no required stack.
            }
        };
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer, ref EnumHandling handling)
    {
        // Shift + right-click breaks the placed piece. Shown alongside a wood mod's own "pick up" hint (plain RMB, free
        // hand) — the modifier split means both are valid and unambiguous, so this is no longer suppressed when a
        // RightClickPickup behavior is present. Added to (not replacing) the block's own help — handling left
        // PassThrough so other behaviors' hints still show.
        return breakInteraction ??= new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-flotsam-break",
                // "shift", NOT "sneak". They are two separately rebindable hotkeys, and CanBreak gates on
                // Controls.ShiftKey, which SystemPlayerControl feeds from `shift` (`sneak` feeds Controls.Sneak, a
                // different flag) — so the old "sneak" here did NOT match the gate, whatever its comment claimed. The
                // HUD resolves this code to the player's CURRENT mapping, so naming the wrong one advertises the wrong
                // key the moment a player separates the two in the controls settings. vsapi says which to use in as
                // many words (EntityControls.cs:239-240: "Use Controls.ShiftKey instead for mouse interaction
                // modifiers, as it is a separable control"), and vanilla agrees 66 to 1 across vssurvivalmod.
                HotKeyCode = "shift",
                MouseButton = EnumMouseButton.Right
                // Itemstacks left null — a bare-hand gesture (like the sawhorse take/discard hints).
            }
        };
    }

    // ──────────────────── Break + drops (server) ────────────────────

    /// <summary>Snap the driftwood at <paramref name="pos"/> into firewood + sticks by SIZE, then remove it through a
    /// bare <c>SetBlock(0)</c> — NOT <c>BreakBlock</c>, so the vanilla flotsam ITEM doesn't drop on top of what we spawn.
    /// Server-only; re-checks the block so a double-fire (two ticks past the threshold before the removal syncs back)
    /// can't drop twice.
    ///
    /// <para>Bypassing <c>BreakBlock</c> also drops the block's break SOUND, and that half has to be paid back by hand
    /// (AuditFixPlan T06.01, F-L3-17). The note that used to stand here — "the debarking1/2 work loop already carried the
    /// audio" — was false for everyone but the breaker: that loop is a <c>capi.World.LoadSound</c> handle living in the
    /// breaking client's own process (<see cref="UpdateBreakSound(float, float, float, bool)"/>), and the
    /// driftwood-break animation patch declares no <c>animationSounds</c>, so a bystander watched a whole block dissolve
    /// in silence. Vanilla pairs the two explicitly — <c>Block.OnBlockBroken</c> plays <c>Sounds.GetBreakSound</c>
    /// immediately before its own <c>SetBlock(0)</c> — and we do the same below. <c>dualCallByPlayer</c> is null because
    /// this is server-only: the breaker's client never runs this method, so it must stay IN the broadcast.</para></summary>
    void BreakDriftwood(IWorldAccessor world, BlockPos pos)
    {
        Block block = world.BlockAccessor.GetBlock(pos);
        if (block?.FirstCodePart() != FlotsamPart) return; // already gone

        (int firewood, int sticks) = DropsFor(block);
        Vec3d dropPos = pos.ToVec3d().Add(0.5, 0.25, 0.5);
        SpawnStack(world, "firewood", firewood, dropPos);
        SpawnStack(world, "stick", sticks, dropPos);

        // The snap everyone nearby hears (the work loop above is local to the breaker). Bare-hand gesture by
        // construction (CanBreak demands an empty active slot), so there is no tool override to resolve and the
        // block's plain Break attributes are exact; Sounds is null-guarded like vanilla's own call.
        if (block.Sounds != null) world.PlaySoundAt(block.Sounds.Break, pos, 0, null);

        world.BlockAccessor.SetBlock(0, pos);
        world.BlockAccessor.TriggerNeighbourBlockUpdate(pos);
    }

    /// <summary>Firewood + stick yield by driftwood SIZE — the vanilla model variant: <c>-3</c> large, <c>-1</c>/<c>-2</c>
    /// normal, <c>-4</c> small (confirmed by burnDuration in flotsam.json: 72 / 24 / 10 s). All counts live in
    /// <see cref="ImmersiveWoodworkingConfig"/>. LastCodePart() is the model number.</summary>
    (int firewood, int sticks) DropsFor(Block block)
    {
        ImmersiveWoodworkingConfig cfg = Config;
        return block.LastCodePart() switch
        {
            "3" => (cfg.FlotsamFirewoodLarge,  cfg.FlotsamSticksLarge),
            "4" => (cfg.FlotsamFirewoodSmall,  cfg.FlotsamSticksSmall),
            _   => (cfg.FlotsamFirewoodNormal, cfg.FlotsamSticksNormal), // "1" / "2" (and any future variant)
        };
    }

    /// <summary>Drop <paramref name="count"/> of <c>game:<paramref name="code"/></c> at <paramref name="pos"/> with a
    /// small upward pop (blocks/tick, like the chopping block's SpawnDistributed). Clamped to the item's max stack.
    /// No-op on a zero/absent yield (a small chunk drops no firewood).</summary>
    static void SpawnStack(IWorldAccessor world, string code, int count, Vec3d pos)
    {
        if (count <= 0) return;
        Item? item = world.GetItem(new AssetLocation("game", code));
        if (item == null) return;
        int n = GameMath.Clamp(count, 1, item.MaxStackSize);
        Vec3d vel = new Vec3d((world.Rand.NextDouble() * 2 - 1) * 0.05, 0.1, (world.Rand.NextDouble() * 2 - 1) * 0.05);
        world.SpawnItemEntity(new ItemStack(item, n), pos, vel);
    }

    // ──────────────────── Client work-sound loop ────────────────────

    /// <summary>Drive the rotating debarking1/2 work loop one Step (client). Each clip is a one-shot; once the current
    /// has ended, start another random one — so the two variants cycle for as long as the hold lasts. A no-op while a
    /// clip is still loading / playing.</summary>
    void UpdateBreakSound(BlockPos pos) // world break: spatialized at the block
        => UpdateBreakSound(pos.X + 0.5f, pos.Y + 0.25f, pos.Z + 0.5f, relative: false);

    /// <summary>In-hand break: the source is the breaker themselves, so play RELATIVE to the listener (centered on the
    /// head, <see cref="Vec3f.Zero"/>) rather than at a world block — the same rotating clips + owned handle.</summary>
    void UpdateBreakSoundInHand()
        => UpdateBreakSound(0f, 0f, 0f, relative: true);

    // Position taken as floats, not a Vec3f, so the ~30/sec Step calls that early-return below (a clip is still
    // playing) allocate nothing — the Vec3f is built only when a fresh clip actually starts (F-S07-04).
    void UpdateBreakSound(float x, float y, float z, bool relative)
    {
        if (capi == null) return;
        if (breakSound != null && !breakSound.IsDisposed && !breakSound.HasStopped) return; // still playing → wait
        if (breakSound != null && !breakSound.IsDisposed) breakSound.Dispose();             // free the finished handle
        AssetLocation loc = BreakLoopClips[capi.World.Rand.Next(BreakLoopClips.Length)];
        breakSound = capi.World.LoadSound(new SoundParams
        {
            Location         = loc,
            ShouldLoop       = false,    // ROTATION, not one repeated buffer
            DisposeOnFinish  = false,    // we own the handle so the release can fade it
            RelativePosition = relative, // false = WORLD (block); true = at the listener (in-hand)
            Position         = new Vec3f(x, y, z),
            Volume           = BreakSoundVolume,
            Range            = BreakSoundRange,
        });
        breakSound?.Start();
    }

    // [IW-FLOTSAM-DBG] dev trace for the driftwood-break gesture (both sides). [Conditional("IW_DEBUG")] strips every
    // call — arguments included — unless IW_DEBUG is defined, so Release pays nothing; the iwdebug build configs
    // (-p:IwDebug=true) turn it on. Mirrors BlockChoppingBlock.Dbg.
    [Conditional("IW_DEBUG")]
    static void Dbg(ILogger logger, string format, params object?[] args) => logger.Notification(format, args);

    /// <summary>Fade the work loop out over <see cref="BreakSoundFadeOut"/> seconds and drop our handle — on release /
    /// look-away / the break completing. Idempotent (null handle = no-op).</summary>
    void StopBreakSound()
    {
        ILoadedSound? snd = breakSound;
        breakSound = null; // detach first → an immediate re-press starts a fresh clip, not this fading one
        if (snd == null || snd.IsDisposed) return;
        if (snd.IsPlaying) snd.FadeOut(BreakSoundFadeOut, s => s.Dispose());
        else snd.Dispose();
    }
}
