using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// Block entity for the automated chopper's master cell (the placed <c>chopper-frame</c>) — the mech-powered
/// upgrade tier over the hand chopping block. It is the single owner of all machine state; the ghost-filler cells
/// (<see cref="BlockChopperGhost"/> + <see cref="BEMultiblockChopper"/>) route every interaction here, so the
/// multiblock behaves as one object — exactly like the neighbouring sawmill (<see cref="BlockEntitySawmill"/>) and
/// vanilla's pulverizer.
///
/// The state container + serialization + the interaction entry point (<see cref="OnInteract"/>), the T03.01
/// skeleton that later tasks bolted onto without reshaping it. Declared <c>partial</c> because the rest of
/// E03+ grows it across separate files (mirroring <see cref="BlockEntitySawmill"/>): the footprint table
/// (T03.02, <c>.Footprint</c>), the ghost placement + teardown (T03.03/T03.05, <c>.Multiblock</c>), the part-install
/// machine (T03.07, <c>.Parts</c>), mechanical power (E04, <c>.Power</c>), I/O inventory (E05, <c>.Inventory</c> /
/// <c>.IO</c>), the chop cycle (E06, <c>.Chopping</c>) and render/effects (E07, <c>.Render</c> / <c>.Effects</c>).
///
/// Part state mirrors <c>BEPulverizer</c> / the sawmill: the three mandatory parts are bool flags (set by RMB install
/// in T03.07), the head is a single held <see cref="headStack"/> (the sawmill blade-kit analogue — its metal is the
/// WEAR tier, not speed — held HammerStack-style, not an inventory slot), and <see cref="IsComplete"/> is the boolean
/// aggregate the whole machine gates on (the optional flywheel is NOT part of it). All of it is serialized so an
/// assembled (or half-assembled) chopper survives a reload.
/// </summary>
public partial class BlockEntityChopper : BlockEntity
{
    // ───────────────────────── Orientation (T03.04) ─────────────────────────
    //
    // The strike/feed axis the multiblock is laid along, read from the block's "side" variant in Initialize. The
    // variantgroup + the HorizontalOrientable behavior live in frame.json, so a placed frame reports its real facing
    // here; the NORTH fallback only covers a missing/odd variant. The footprint, per-cell collision and ghost
    // placement (E03) all rotate off this single value. Re-derived from block state on every load (NOT serialized),
    // like BEPulverizer.Facing — so it can never drift from the block's own variant.
    public BlockFacing Facing { get; protected set; } = BlockFacing.NORTH;

    // Raised once Initialize has read Facing off the block variant. The ghost cells check it before they hold this
    // master's per-cell boxes for good (T05.05, BEMultiblockChopper.CellBoxes): asked earlier, the master would answer
    // from the NORTH default above and a ghost would freeze the wrong frame onto itself for the machine's whole life.
    // Volatile so the flag can never be observed ahead of the Facing write it follows.
    internal volatile bool FacingResolved;

    // ───────────────────────── Installed parts (set by RMB install in T03.07) ─────────────────────────
    //
    // The 3 MANDATORY mechanism parts as bool flags: the drive-cam (chopperdrive — the crankshaft analogue, through
    // which the frame is logically in the mech network), the axe-arm (chopperarm — the pendulum lever holding the
    // head), and the clamp-bed — the existing hand `choppingblock` (colода) RMB-installed as the bed under the head
    // (user decision 2026-07-04: reuse the block, not a bespoke `chopperbed` part; its surface re-textures to the fed
    // log's species in E07). Install order is FREE among them (the head requires the arm). Plus the head (headStack),
    // installed into the arm. (The optional flywheel was cut 2026-07-04, so there is no flywheel flag any more.)
    bool hasDrive;
    bool hasArm;
    bool hasBed;

    // The wood species (+ its texture domain) of the choppingblock installed as the clamp-bed, captured from that
    // block's OWN stack attributes at install (BlockChoppingBlock stamps `wood`/`woodDomain` on pick/craft). The bed
    // renders in THIS species — its own wood, NOT the fed log's species and NOT a hardcoded oak (user-reported
    // 2026-07-05; this reverses the T07.03 "bed follows the input log" choice) — and teardown returns a same-species
    // choppingblock (see BedStack / UpdateLogMesh). null = a bare/legacy bed with no recorded species → default oak
    // look. Persisted alongside hasBed. woodDomain defaults to "game" so a modded species finds its own textures.
    string? bedWood;
    string bedWoodDomain = "game";

    // The installed axe/maul HEAD — ONE item that IS the cutting part AND the wear tier (its metal). Stored as a
    // single held stack (HammerStack-style, like the sawmill blade kit), NOT an inventory slot, so a state swap /
    // render is a cheap re-point. It installs/drops as the single item it is. The metal -> WEAR-tier read happens in
    // T01.02/E04 (native durabilitybytype), and the per-log wear decrement in E06 — the metal never sets the chop
    // PACE (that is the drive RPM), only the head's lifespan.
    ItemStack? headStack;

    // ───────────────────────── Chop cycle (E06/T06.01) ─────────────────────────
    //
    // The strike progress of the current pass, 0..1 (a fraction of one full log — ChopperStrikes strikes), NOT a
    // wall-clock timer: it accrues in OnChopperTick (the .Chopping partial) by how far the powered cam turned this
    // tick, FREEZES when the drive stalls / the log is gone, and is consumed+reset by the pass completion (T06.02).
    // Serialized below, so a half-chopped log resumes from the same fraction after a power loss or reload — the
    // authoritative accumulator, never re-derived from the live speed/config (memory vs-saw-hold-progress).
    float chopProgress;

    // Monotonic count of COMPLETED chops (server-authoritative, serialized + synced). The client keys the deferred
    // "log vanishes at the visible strike" on this incrementing TOGETHER with the input slot going empty (see
    // DetectChopVanish / ResolveChopVanish in the Render/Effects partials): a bump means the server consumed the log via
    // a CHOP, so the client HOLDS the log mesh until its OWN visible axe-landing (the server's strike phase leads the
    // client render — an un-deferred removal drops the log while the visible axe is still up). A slot that empties
    // WITHOUT a bump (player Ctrl+RMB take/discard, a break) removes the mesh immediately. Server increments it in
    // CompletePass; the client only ever reads the synced value.
    int chopsCompleted;

    /// <summary>True once all 3 mandatory parts (drive + arm + bed-choppingblock) AND the head are installed — the
    /// single gate the mechanical power (E04), I/O (E05) and chop cycle (E06) all check first (mirrors
    /// <c>BEPulverizer.IsComplete</c> / <see cref="BlockEntitySawmill.IsComplete"/>).</summary>
    public bool IsComplete => hasDrive && hasArm && hasBed && headStack != null;

    // Client handle, kept for the render path (E07). Set in Initialize; null on the dedicated server. (Declared here in
    // the main partial, like the sawmill; the rest of the client render state lives in the .Render partial.)
    ICoreClientAPI? capi;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        capi = api as ICoreClientAPI;

        // Orientation along the strike axis, from the "side" variant the HorizontalOrientable behavior set at
        // placement. Block.Variant is a RelaxedReadOnlyDictionary (returns null for an absent key), so a frame somehow
        // lacking the variant just falls back to NORTH rather than throwing. Mirrors BEPulverizer/BlockEntitySawmill.
        Facing = BlockFacing.FromCode(Block.Variant["side"]) ?? BlockFacing.NORTH;
        FacingResolved = true; // ghosts may now hold our per-cell boxes (T05.05)

        // I/O inventory (E05/T05.01): give the input slot its world-scoped id + Api on BOTH sides, like
        // BEPulverizer/BEQuern's inv.LateInitialize. The slot exists from the field initializer, so a FromTreeAttributes
        // that already ran on load just populated it; this only stamps the id/api. A single dash-free InventoryClassName
        // stem kept separate from the position id (memory vs-inventorygeneric-invid-dash).
        inventory.LateInitialize(InventoryClassName + "-" + Pos, api);

        // Hopper/chute auto-input (E05/T05.04) moves a log into the input slot through the perimeter ghost proxy
        // (BEMultiblockChopper), which writes the slot DIRECTLY — bypassing the manual TryPutLog MarkDirty. Persist +
        // sync on any slot change so an auto-fed log saves and reaches the client (server-authoritative; the client
        // populates its slot from the synced tree, not via this event). MarkDirty coalesces, so the manual path also
        // calling it is harmless. (There is no auto-OUTPUT: the product is ejected forward, never stored — EjectBatch.)
        if (api.Side == EnumAppSide.Server) inventory.SlotModified += OnInventorySlotModified;

        // Self-heal the ghost footprint on load (T03.03). Saved ghost-fillers normally restore themselves with their
        // serialized Principal, so this only re-asserts ones lost to corruption / external world edits. Deferred a
        // beat so neighbour chunks holding the footprint cells get a chance to load first (EnsureFootprintGhosts skips
        // any cell whose chunk isn't loaded yet, and re-runs on the next load). Server-only; guarded against the BE
        // having been removed before the callback fires. Mirrors BlockEntitySawmill.Initialize.
        if (api.Side == EnumAppSide.Server)
        {
            RegisterDelayedCallback(_ => { if (Api?.World.BlockAccessor.GetBlockEntity(Pos) == this) EnsureFootprintGhosts(); }, 100);

            // The chop-cycle heartbeat (E06/T06.01): a 25 ms server tick accrues the strike progress while the machine
            // is powered, fed and assembled (BlockEntityChopper.Chopping.OnChopperTick). Server-only, like
            // BEHelveHammer.onEvery25ms — the client only renders the synced progress (E07), it never integrates it.
            RegisterGameTickListener(OnChopperTick, 25);
        }

        // E07/T07.01 — client render: this BE is its own ITexPositionSource (the head metal), the static frame bakes
        // into the chunk mesh (OnTesselation) and the moving parts get a custom ChopperRenderer. Built here and
        // refreshed on every tree sync (FromTreeAttributes) / part (un)install (OnPartInstalled/TryTake/DamageHead).
        // No-op on the dedicated server (capi == null). T07.03 adds the workpiece-log mesh + the species-painted bed
        // (UpdateLogMesh, refreshed on the same events); the client cut effects (T07.04) still attach in their own task.
        if (capi != null)
        {
            blockTexSource = capi.Tesselator.GetTextureSource(Block);
            AnchorAnimToServer(); // T12.03b — re-base the render's estimate of the server strike phase on load (FromTreeAttributes already read lastSyncedServerAnim), so the visible axe-fall converges onto the firewood eject
            UpdateRenderer();
            UpdateLogMesh(); // T07.03 — build the workpiece billet + species-painted bed for the loaded input (if any)
            InitEffects();   // AutoChopperPlan T07.05 — register the client ambient-loop tick (the strike one-shots/chips ride the render frame)
            lastHelpSig = HelpSignature(); // baseline for the live interaction-help refresh (F-S05-03, see RefreshInteractionHelpOnChange)
            // Baseline for the deferred chop-vanish's bump detector (DetectChopVanish), the SAME seed-on-load shape as
            // lastHelpSig above. lastSeenChops starts at -1 = "not seeded", and its only other assignment lives inside
            // DetectChopVanish, which FromTreeAttributes skips while capi is still null — and on a client BE built from
            // a chunk packet FromTreeAttributes always runs BEFORE Initialize (ClientChunk builds the BE, feeds it the
            // tree, and initialises it afterwards). So without this line the first tree sync after load is swallowed as
            // the seed instead of read as a bump, and if that sync happens to be the pass-complete one, that log's
            // vanish is not deferred: the billet disappears at the server's leading phase and its final chips + wood-
            // crack never play (the "firewood before the axe" artifact T12.03b exists to kill). chopsCompleted was
            // already restored from the tree by then, so it is the correct baseline; a freshly placed machine seeds 0.
            //
            // This was previously masked by the anchor's 1 Hz MarkDirty, which handed every turning machine a tree sync
            // within a second of Initialize and burned the seed harmlessly. Moving the anchor onto its own packet
            // (AuditFixPlan T07.05, BlockEntityChopper.Chopping.SendAnimAnchor) took that accident away, so the seed is
            // stated here instead of depending on a cadence.
            lastSeenChops = chopsCompleted;

            // SpeedDecouplingPlan T01.04 — the server's M-derived strike constants (ChopperStrikes / ChopperCycleRevs /
            // ChopperSplitPerStrike) reach the client inside the ConfigSyncPacket, which is NOT ordered against the chunk
            // holding this block: on a busy join the machine can tesselate and start animating on the compiled M=1
            // defaults before the packet lands. Subscribe to the "server config adopted" seam so a late arrival rebuilds
            // the pose from the authoritative constants (OnClientConfigApplied). If the packet instead arrived BEFORE
            // this Initialize, Config already held the server values above and the handler simply never fires for us.
            modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
            modSystem.ClientConfigApplied += OnClientConfigApplied;
        }

#if IW_DEBUG
        api.Logger.Notification("[immersivewoodworking] Chopper BE initialized at {0}, facing {1}, complete={2}.", Pos, Facing, IsComplete);
#endif
    }

    /// <summary>Server-side: persist + sync any inventory slot change — notably the hopper auto-input (T05.04), which
    /// moves a log into the input slot through the perimeter proxy and so bypasses the manual <see cref="TryPutLog"/>
    /// MarkDirty. Subscribed to <c>inventory.SlotModified</c> in <see cref="Initialize"/>.</summary>
    void OnInventorySlotModified(int slotId) => MarkDirty(true);

    /// <summary>
    /// Master-side interaction entry. Both the master block (<see cref="BlockChopper.OnBlockInteractStart"/>) and
    /// every ghost-filler (<see cref="BlockChopperGhost.OnBlockInteractStart"/>, via
    /// <see cref="BEMultiblockChopper.Principal"/>) route here, so a right-click on ANY cell of the multiblock
    /// reaches this one method.
    ///
    /// T03.07 — the part-install machine (pulverizer-style, mirroring <see cref="BlockEntitySawmill.OnInteract"/>);
    /// T05.02 — the manual log I/O layered on top. The click is dispatched by the Ctrl modifier first, then hand state:
    /// <list type="bullet">
    ///   <item>Ctrl+RMB WITHOUT Shift (any hand — hand-independent, like the sawhorse/sawmill) → remove the LOADED LOG
    ///   first (<see cref="TryTakeLog"/> recoverable within the grace → back to inventory whole / <see cref="DiscardLog"/>
    ///   past the grace → scrapped, no drop), and only with no log the HEAD (<see cref="TryTake"/>) — the mechanism parts
    ///   stay committed (break the frame to recover them).</item>
    ///   <item>plain RMB, item in hand → install the next part (<see cref="TryAddPart"/>); if it isn't a part, try to
    ///   load it as a log into the input (<see cref="TryPutLog"/>, accepted only once <see cref="IsComplete"/>).</item>
    ///   <item>plain RMB, empty hand → nothing (the product is ejected forward on completion, not taken from a slot).</item>
    /// </list>
    /// Always swallows the click (returns true) so the player can't place blocks into the machine's footprint. The
    /// install + part state live in the <c>.Parts</c> partial, the manual I/O in <c>.IO</c>; the chop cycle (E06) extends
    /// this once <see cref="IsComplete"/> is up.
    /// </summary>
    public bool OnInteract(IPlayer byPlayer, BlockSelection blockSel)
    {
        ItemSlot hand = byPlayer.InventoryManager.ActiveHotbarSlot;

        // Ctrl+RMB is the "remove" gesture REGARDLESS of what's in hand — checked before the install/load path so
        // holding a part or a log can't block removal. State-dependent outcome, mirroring the sawmill: a loaded log
        // comes off FIRST — recoverable (within the grace) → back to the inventory whole (TryTakeLog), past the grace →
        // scrapped with no drop (DiscardLog) — and only when there's no log does the HEAD come off (TryTake). The
        // mechanism parts stay committed (break the frame to recover them).
        //
        // !ShiftKey (AuditFixPlan T03.05, F-L8-21): the remove gesture is Ctrl WITHOUT Shift on ALL FOUR stations. A bare
        // `CtrlKey` here also answered Ctrl+SHIFT+RMB, which the hand stations can't (their Shift branch runs first and
        // hands the click to vanilla), so the machines carried a second, undocumented removal chord that worked on two
        // stations out of four and on neither hint. Requiring Shift to be up makes the ONE advertised chord
        // (HotKeyCode "ctrl" on takelog / discardlog / removehead) the only one that removes anything, everywhere.
        // Shift-held clicks keep falling through to the install/load path below and stay swallowed, so nothing can be
        // placed into the machine's footprint either way.
        if (byPlayer.Entity.Controls.CtrlKey && !byPlayer.Entity.Controls.ShiftKey)
        {
            if (!TryTakeLog(byPlayer) && !DiscardLog(byPlayer)) TryTake(byPlayer);
            return true;
        }

        if (hand.Empty)
        {
            // Plain empty RMB does nothing: the finished product is ejected forward out of the machine on completion
            // (pulverizer-style), never stored in a slot to take. Still swallow the click (return true) so the player
            // can't place a block into the machine's footprint.
            return true;
        }

        // A recognised build part installs (swallowed whether it took or only hinted); anything else falls through to
        // the log-input load, which itself rejects a non-log / unassembled machine / occupied input.
        if (TryAddPart(hand, byPlayer)) return true;
        TryPutLog(hand, byPlayer);
        return true;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);

        hasDrive = tree.GetBool("hasDrive");
        hasArm = tree.GetBool("hasArm");
        hasBed = tree.GetBool("hasBed");
        bedWood = tree.GetString("bedWood", null);        // the installed bed choppingblock's OWN species (drives its render + drop)
        bedWoodDomain = tree.GetString("bedWoodDomain", "game");

        headStack = tree.GetItemstack("headStack");
        headStack?.ResolveBlockOrItem(worldForResolving); // tree itemstacks load unresolved (see GetItemstack docs)

        chopProgress = tree.GetFloat("chopProgress"); // E06/T06.01 — accrued strike fraction; a half-chopped log resumes from here after reload
        int syncedChops = tree.GetInt("chopsCompleted"); // for the client's deferred chop-vanish; assigned to the field after DetectChopVanish reads the delta

        // T12.03b — the server twin of the render's animAngle (the ÷48 reduction phase, .Chopping partial). Restore it on
        // the SERVER so the strike phase resumes across a reload (FromTreeAttributes runs load-only server-side, before the
        // first tick, so this never clobbers the live accumulator), and remember it on the CLIENT for the one-time render
        // phase-align (AnchorAnimToServer) — so the visible axe-fall coincides with the server's split/eject instead of
        // drifting up to a full cycle apart (the two accumulators are otherwise independent). serverAnimAngle is dead on
        // the client (HeadClearOfBed reads animAngle there), so assigning it there is harmless.
        float syncedServerAnim = tree.GetFloat("serverAnimAngle");
        serverAnimAngle = syncedServerAnim;
        lastSyncedServerAnim = syncedServerAnim;
#if IW_DEBUG
        // [chopper-sync] per-sync offset: how far the client's live animAngle sits from the freshly synced serverAnimAngle.
        // A large, roughly-constant offset ⇒ the accumulators diverged at startup / a reload (a one-time seed can't hold);
        // a growing offset ⇒ ongoing drift. Client-only.
        if (capi != null)
            capi.Logger.Notification("[immersivewoodworking] [chopper-sync] CLIENT sync @{0}ms serverAnimAngle={1:0.000} animAngle={2:0.000} offset={3:0.000} seeded={4}",
                capi.World.ElapsedMilliseconds, syncedServerAnim, animAngle, syncedServerAnim - animAngle, animAngleSeeded);
#endif

        // Capture the pre-sync input BEFORE the inventory read below overwrites the slot, so the client can DEFER a
        // completed chop's log-mesh removal to its own visible axe-landing with the right log for the final chips/crack
        // (DetectChopVanish). Harmless on the server (it skips DetectChopVanish).
        ItemStack? prevLog = InputSlot.Itemstack;

        // Input inventory (E05/T05.01) — read from its own subtree (its numeric slot key would otherwise collide with
        // the part keys above) and resolve the restored stack against worldForResolving (Api can still be null this
        // early on load), so the input log comes back with a live Block/Item, not a raw stack — the acceptance filter
        // (T05.03) and species render (E07) need block.Code. Mirrors the headStack resolve above.
        ITreeAttribute? invTree = tree.GetTreeAttribute("inventory");
        if (invTree != null)
        {
            inventory.FromTreeAttributes(invTree);
            foreach (ItemSlot slot in inventory) slot.Itemstack?.ResolveBlockOrItem(worldForResolving);
        }

        // Client-only: if this sync emptied the slot via a CHOP (chopsCompleted bumped), HOLD the log mesh until the
        // client's own visible strike drops it (so the log vanishes when the axe you SEE lands, not at the server's
        // leading phase). MUST run before UpdateLogMesh, which it gates via pendingChopVanish. The server BE (capi ==
        // null) skips it; the field is assigned the synced value afterward so the next sync's delta is correct.
        if (capi != null) DetectChopVanish(syncedChops, prevLog);
        chopsCompleted = syncedChops;

        // T12.03b — re-base the render's estimate of the server strike phase to this sync's value so the displayed axe
        // converges onto the firewood eject. Runs EVERY client sync (not once): the ~25.9 rad offset between the two
        // independent |Δ| re-integrations develops after placement, so only a repeated re-base — fed by the server's
        // periodic sync — catches and closes it. On a disk/chunk load this FromTreeAttributes runs before Initialize (capi
        // null) and Initialize does the first anchor with the value just read; here on every later sync.
        if (capi != null) AnchorAnimToServer();

        // E07/T07.01 — a server tree sync may have changed the installed parts (which movers are drawn) or the head
        // metal (the head texture); refresh the client render. This is the load-bearing refresh on the CLIENT (install
        // state arrives via the synced tree, not via the server-side OnPartInstalled). No-op before Initialize (capi
        // still null) — Initialize builds it then — and a cheap dedup'd no-op when nothing render-relevant changed.
        UpdateRenderer();
        // E07/T07.03 — likewise the loaded input log (its species / kind) and the bed-part install arrive via this sync;
        // rebuild the workpiece billet + species-painted bed. Its own workpieceKey dedups when nothing visible changed.
        UpdateLogMesh();

        // F-S05-03 — force the bottom-screen interaction hints to refresh if this sync changed what they show (part
        // installed, log loaded, log crossed the recover→discard grace) — the help HUD doesn't poll, so otherwise they'd
        // stay stale until re-hover. This is also what lets the chop-progress sync go data-only (F-S12-03): the Ctrl+RMB
        // hint no longer needs the retesselation to refresh. Mirrors BlockEntitySawmill.FromTreeAttributes.
        RefreshInteractionHelpOnChange();
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);

        tree.SetBool("hasDrive", hasDrive);
        tree.SetBool("hasArm", hasArm);
        tree.SetBool("hasBed", hasBed);
        if (bedWood != null) // the bed's own species rides along only when a species was recorded (domain is meaningless without one)
        {
            tree.SetString("bedWood", bedWood);
            tree.SetString("bedWoodDomain", bedWoodDomain);
        }

        if (headStack != null) tree.SetItemstack("headStack", headStack);

        tree.SetFloat("chopProgress", chopProgress); // E06/T06.01 — strike fraction, frozen across power loss / reload
        tree.SetInt("chopsCompleted", chopsCompleted); // drives the client's deferred chop-vanish (see the chopsCompleted field)
        tree.SetFloat("serverAnimAngle", serverAnimAngle); // T12.03b — the ÷48 reduction phase; synced so the client phase-aligns its render (AnchorAnimToServer) and the server resumes phase across a reload

        // I/O inventory (E05/T05.01) under its own subtree so its numeric slot keys can't collide with the part keys
        // above (mirrors how BlockEntityContainer namespaces its inventory).
        ITreeAttribute invTree = new TreeAttribute();
        inventory.ToTreeAttributes(invTree);
        tree["inventory"] = invTree;
    }

    /// <summary>
    /// E08/T08.03 — the hover tooltip: what the machine is doing right now. Shown on the master cell directly and on
    /// every ghost cell via <see cref="BEMultiblockChopper.GetBlockInfo"/> (which routes here), so any cell reads the
    /// same. Like the sawmill / the sawhorse it shows only RELEVANT state — an unassembled frame says just that; an
    /// assembled one shows the head tier, the loaded log, the chop progress (only once a pass has begun) and a single
    /// WHY-line when it isn't chopping. Runs client-side on hover; every field is server-synced (parts, inventory,
    /// progress) or client-interpolated (the network speed via <see cref="NetworkSpeed"/>/<see cref="EffectiveChopSpeed"/>),
    /// so it reads correctly client-side. Direct mirror of <see cref="BlockEntitySawmill.GetBlockInfo"/>, with the head
    /// in place of the blade kit and NO flywheel line (the flywheel was cut 2026-07-04).
    /// </summary>
    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder sb)
    {
        base.GetBlockInfo(forPlayer, sb);

        // Inert until fully assembled: say so and list the mandatory parts still missing (the head too, once the drive,
        // arm and bed are in) — the player sees exactly what's left for IsComplete. Then stop; progress/speed are
        // meaningless on a half-built frame. (The interaction-help shows the same parts as installable icons.)
        if (!IsComplete)
        {
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-chopper-incomplete"));
            foreach (string code in MissingMandatoryParts())
                sb.AppendLine("• " + Lang.Get("immersivewoodworking:" + PartLangKey(code)));
            if (HasArm && !HasHead)
                sb.AppendLine("• " + Lang.Get("immersivewoodworking:blockinfo-chopper-needhead"));
            return;
        }

        // Head tier — the installed head's metal (its durability/wear tier and the install requirement; it does NOT
        // pace the chop — the drive RPM does, see below); show its already-localized item name.
        if (headStack != null)
        {
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-chopper-head", headStack.GetName()));
            // Remaining head durability, like a tool's "X / Y" — only when the item declares durability (T06.05).
            int headMax = headStack.Collectible.GetMaxDurability(headStack);
            if (headMax > 1)
                sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-chopper-head-durability",
                    headStack.Collectible.GetRemainingDurability(headStack), headMax));
        }

        // The loaded workpiece log (its already-localized name carries the species, e.g. "Birch log"). There is no
        // output line: the product is ejected forward on completion, never accumulated in the machine.
        ItemStack? log = InputSlot.Itemstack;
        if (log != null) sb.AppendLine(log.GetName());

        // Chop progress as a percent of one pass (chopProgress is already a 0..1 fraction). Only once a pass has begun
        // so an untouched log reads as just its name (no "0%" noise). On the shipped default a pass is never observably
        // begun — chopProgress goes 0 → 1 → 0 within one tick call — so this line only ever appears at
        // ChopperStrikes > 1, unlike the sawmill's, whose progress creeps (see BlockEntityChopper.LogRecoverable).
        if (chopProgress > 0f)
        {
            int percent = GameMath.Clamp((int)(chopProgress * 100f), 0, 100);
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-chopper-progress", percent));
        }

        // Drive + work state, diagnosed in the same gate order EffectiveChopSpeed uses so the player sees WHY it isn't
        // chopping: no power / below torque / no head → "no power"; otherwise the DRIVE speed (the chop is paced by
        // shaft rotation — a log costs ChopperCycleCap/ChopperSpeed revolutions per log — so the network RPM is what matters; EffectiveChopSpeed
        // only gates here) plus why it's idle (no log) / running. The head metal is the WEAR tier, never the pace.
        float spd = EffectiveChopSpeed(); // gate only: 0 = no head / below torque
        if (spd <= 0f)
        {
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-chopper-nopower"));
        }
        else
        {
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-chopper-speed", NetworkSpeed.ToString("0.00")));
            // Empty bed: distinguish "you can load now" (head lifted clear) from "wait — the axe is down" (the
            // animation-tied input gate, HeadClearOfBed): a running machine only accepts the next log once the head has
            // risen off the bed, so the hint tells the player (and explains why a hopper isn't feeding yet) instead of
            // just reading "idle". Loaded → it's chopping.
            if (!InputEmpty) sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-chopper-running"));
            else if (!HeadClearOfBed()) sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-chopper-headdown"));
            else sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-chopper-idle"));
        }
#if IW_DEBUG
        // T04.07: the SHARED workpiece render identity — the exact string this machine's mesh cache is keyed on, and the
        // twin of the hand stand's own line (BlockEntityChoppingBlock.GetBlockInfo). "One item, one look" is checkable by
        // hovering both with the same workpiece: every term but the trailing yaw must read identical (this machine passes
        // 0 and bakes its facing into the mesh afterwards; the stand passes the yaw that turns content to the player).
        // Stripped from Release, like every redesign diagnostic.
        sb.AppendLine($"[IW-DBG] chop={chopProgress:0.00} workpiece={(InputSlot.Itemstack is { } dbgLog ? BlockEntityChoppingBlock.LaidWorkpieceIdentity(dbgLog, chopProgress, 0f) : "none")}");
#endif
    }

    /// <summary>The lang key naming a mandatory-part code in the "still missing" list. The drive/arm are mod ITEMS
    /// (<c>item-…</c>), but the clamp-bed is the hand <c>choppingblock</c> BLOCK (2026-07-04 — reuse the block, not a
    /// bespoke part), so it resolves through <c>block-choppingblock</c> — an <c>item-choppingblock</c> key doesn't
    /// exist. Keeps the incomplete list free of a raw key on the one non-item mandatory part. (The sawmill needs no such
    /// helper — all its mandatory parts are items.)</summary>
    static string PartLangKey(string code) => code == "choppingblock" ? "block-choppingblock" : "item-" + code;
}
