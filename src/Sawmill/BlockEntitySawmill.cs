using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ImmersiveWoodworking;

/// <summary>
/// Block entity for the automated sawmill's master cell (the placed <c>sawmill-frame</c>) — the mech-powered
/// upgrade tier over the hand sawhorses. It is the single owner of all machine state; the ghost-filler cells
/// (<see cref="BlockSawmillGhost"/> + <see cref="BEMultiblockSawmill"/>) route every interaction here, so the
/// multiblock behaves as one object — exactly like vanilla's pulverizer.
///
/// The master state: the state container + serialization + the interaction entry point (<see cref="OnInteract"/>),
/// built so the later tasks bolted onto it without reshaping it. Declared <c>partial</c> because the rest of
/// E03+ lives across separate files (mirroring <c>BlockEntitySawhorse</c>): the footprint table (single source
/// of truth in <c>BlockEntitySawmill.Footprint</c>, currently 19 cells fitted to the authored model) +
/// per-cell collision (T03.02/T03.03), orientation (T03.04), teardown drop (T03.05), the part-install machine
/// (T03.07), mechanical power (E04), I/O inventory (E05), the saw cycle (E06) and render (E07).
///
/// Part state mirrors <c>BEPulverizer</c>: the mandatory parts are bool flags (set by RMB install in T03.07), the
/// blade kit is a single held <see cref="bladeStack"/> (the pulverizer's pounder-caps analogue
/// — its metal is the speed tier in E04), and <see cref="IsComplete"/> is the boolean aggregate the whole machine
/// gates on (the optional flywheel is NOT part of it). All of it is serialized so an assembled (or half-assembled)
/// sawmill survives a reload.
/// </summary>
public partial class BlockEntitySawmill : BlockEntity
{
    // ───────────────────────── Orientation (T03.04) ─────────────────────────
    //
    // The feed axis the multiblock is laid along, read from the block's "side" variant in Initialize. T03.04 added
    // that variantgroup + the HorizontalOrientable behavior to frame.json, so a placed frame reports its real facing
    // here (north/east/south/west); the NORTH fallback now only covers a missing/odd variant. GetCells, the per-cell
    // collision and the ghost placement all rotate off this single value. Re-derived from block state on every load
    // (NOT serialized), like BEPulverizer.Facing — so it can never drift from the block's own variant.
    public BlockFacing Facing { get; protected set; } = BlockFacing.NORTH;

    // Raised once Initialize has read Facing off the block variant. The ghost cells check it before they hold this
    // master's per-cell boxes for good (T05.05, BEMultiblockSawmill.CellBoxes): asked earlier, the master would answer
    // from the NORTH default above and a ghost would freeze the wrong frame onto itself for the machine's whole life.
    // Volatile so the flag can never be observed ahead of the Facing write it follows.
    internal volatile bool FacingResolved;

    // ───────────────────────── Installed parts (set by RMB install in T03.07) ─────────────────────────
    //
    // The 4 MANDATORY mechanism parts as bool flags (T02.05 redesign 2026-06-19): the saw sash, the crankshaft
    // assembly (crank + rod), the feed levers and the carriage (cart + spring). Install order is FREE among them.
    // Plus the blade kit (bladeStack), installed into the sash (so it requires hasSash first). The OPTIONAL flywheel
    // is a separate flag that does NOT count toward IsComplete (it only eases torque/resistance in E04) and installs
    // only after the crankshaft. (The retired reducer/feed parts were folded into the crankshaft + levers here.)
    bool hasSash;
    bool hasCrankshaft;
    bool hasLevers;
    bool hasCarriage;
    bool hasFlywheel;

    // The installed blade kit — ONE item that IS the assembled kit (the cutting part AND the speed tier, its metal).
    // Stored as a single held stack (HammerStack-style), NOT an inventory slot, so a state swap / render is a cheap
    // re-point. It installs/drops as the single item it is (no separate count — the "stack N blades" model was
    // dropped 2026-06-19). The metal -> speed multiplier read happens in E04; the durability/wear (config-gated,
    // default OFF) in E06.
    ItemStack? bladeStack;

    // ───────────────────────── Saw cycle (E06/T06.01) ─────────────────────────
    //
    // The feed-stroke progress of the current pass, 0..1 (a fraction of one full feed pass), NOT a
    // wall-clock timer: it accrues in OnSawmillTick (the BlockEntitySawmill.Sawing partial) by how far the powered
    // frame fed the log this tick, FREEZES when the drive stalls / the log is gone, and is
    // consumed+reset by the pass completion (T06.02). Serialized below, so a half-sawn log resumes from the same
    // fraction after a power loss or reload — the authoritative accumulator, never re-derived from the live
    // speed/config (memory vs-saw-hold-progress).
    float sawmillProgress;

    // Which PRODUCT the machine rips into (SawModePlan) — the mechanical counterpart of the pit saw's tool mode. The
    // sawmill has no held tool, so it lives on the machine and is cycled with Shift+RMB (empty hand) on the frame
    // (see OnInteract). Auto = the input decides (log/section → planks, debarked → beams), so a machine nobody
    // reconfigured keeps the pre-plan behavior. Serialized + synced (the client reads it for the hover readout).
    SawMode sawMode = SawMode.Auto;

    /// <summary>True once all 4 mandatory parts AND the blade kit are installed — the single gate the mechanical
    /// power (E04), I/O (E05) and saw cycle (E06) all check first (mirrors <c>BEPulverizer.IsComplete</c> /
    /// its <c>OnServerTick: if (!IsComplete) return;</c>). The optional flywheel is deliberately NOT included.</summary>
    public bool IsComplete => hasSash && hasCrankshaft && hasLevers && hasCarriage && bladeStack != null;

    /// <summary>Whether the OPTIONAL flywheel (<c>sawmillflywheel</c>, iron) is installed (T03.07). Deliberately NOT
    /// part of <see cref="IsComplete"/> — it only EASES the drive, never raises the speed ceiling. The mechanical
    /// power node reads it cross-cell (it lives on the interior-unreachable input ghost, not here) to lower the
    /// declared network load (<see cref="BEBehaviorSawmillMP.GetResistance"/>, T04.02) and, later, the minimum-torque
    /// gate (T04.04).</summary>
    public bool HasFlywheel => hasFlywheel;

    // Client handle, kept for the render path (E07). Null on the server.
    ICoreClientAPI? capi;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        capi = api as ICoreClientAPI;

        // I/O inventory (E05/T05.01): give the input/output slots their world-scoped id + Api on BOTH sides, like
        // BEPulverizer/BEQuern's inv.LateInitialize. The slots exist from the field initializer, so a FromTreeAttributes
        // that ran before this on load has already populated (and resolved) them.
        inventory.LateInitialize(InventoryClassName + "-" + Pos, api);

        // Hopper/chute auto-input (E05/T05.04) moves a log into the input slot through the perimeter ghost proxy
        // (BEMultiblockSawmill), which writes the slot DIRECTLY — bypassing the manual TryPutLog MarkDirty. Persist +
        // sync on any slot change so an auto-fed log saves and reaches the client (server-authoritative; the client
        // populates its slot from the synced tree, not via this event). MarkDirty coalesces, so the manual path also
        // calling it is harmless. (There is no auto-OUTPUT: the product is ejected forward, never stored — EjectBatch.)
        if (api.Side == EnumAppSide.Server) inventory.SlotModified += OnInventorySlotModified;

        // Orientation along the feed axis, from the "side" variant the HorizontalOrientable behavior set at placement
        // (T03.04). Block.Variant is a RelaxedReadOnlyDictionary (returns null for an absent key), so a frame somehow
        // lacking the variant just falls back to NORTH rather than throwing. Mirrors BEPulverizer.Initialize.
        Facing = BlockFacing.FromCode(Block.Variant["side"]) ?? BlockFacing.NORTH;
        FacingResolved = true; // ghosts may now hold our per-cell boxes (T05.05)

        // E07/T07.01 — client render: this BE is its own ITexPositionSource (the blade kit's metal), the static frame
        // bakes into the chunk mesh (OnTesselation) and the moving parts get a custom SawmillRenderer. Built here and
        // refreshed on every tree sync (FromTreeAttributes) / part (un)install (OnPartInstalled/TryTake). No-op on the
        // dedicated server (capi == null).
        if (capi != null)
        {
            blockTexSource = capi.Tesselator.GetTextureSource(Block);
            UpdateRenderer();
            UpdateLogMesh();         // E07/T07.03 — build the workpiece-log mesh if a log was loaded before this load
            clientProgress = sawmillProgress; // seed the smooth feed estimate from the loaded fraction
            InitEffects();           // E07/T07.04+T07.05 — client cut-effects tick (sawdust + loop sound); after UpdateRenderer so partPivots exist
            lastHelpSig = HelpSignature(); // baseline for the live interaction-help refresh (see RefreshInteractionHelpOnChange)

            // SpeedDecouplingPlan T01.04 — the server's M-derived animation constants (SawmillWorkScale /
            // SawmillFwdWrapBound) reach the client inside the ConfigSyncPacket, which is NOT ordered against the chunk
            // holding this block: on a busy join the machine can tesselate and start animating on the compiled M=1
            // defaults before the packet lands. Subscribe to the "server config adopted" seam so a late arrival rebuilds
            // the pose from the authoritative constants (OnClientConfigApplied). If the packet instead arrived BEFORE
            // this Initialize, Config already held the server values above and the handler simply never fires for us.
            modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
            modSystem.ClientConfigApplied += OnClientConfigApplied;
        }

        // Self-heal the ghost footprint on load (T03.03). Saved ghost-fillers normally restore themselves with their
        // serialized Principal, so this only re-asserts ones lost to corruption / external world edits. Deferred a
        // beat so neighbour chunks holding the ±1 cells get a chance to load first (EnsureFootprintGhosts skips any
        // cell whose chunk isn't loaded yet, and re-runs on the next load). Server-only; guarded against the BE
        // having been removed before the callback fires.
        if (api.Side == EnumAppSide.Server)
        {
            RegisterDelayedCallback(_ => { if (Api?.World.BlockAccessor.GetBlockEntity(Pos) == this) EnsureFootprintGhosts(); }, 100);

            // The saw-cycle heartbeat (E06/T06.01): a 25 ms server tick accrues the feed-stroke progress while the
            // machine is powered, fed and has output room (BlockEntitySawmill.Sawing.OnSawmillTick). Server-only, like
            // BEHelveHammer.onEvery25ms — the client only renders the synced progress (E07), it never integrates it.
            RegisterGameTickListener(OnSawmillTick, 25);
        }

#if IW_DEBUG
        api.Logger.Notification("[immersivewoodworking] Sawmill BE initialized at {0}, facing {1}, complete={2}.", Pos, Facing, IsComplete);
#endif
    }

    /// <summary>Server-side: persist + sync any inventory slot change — notably the hopper auto-input (T05.04), which
    /// moves a log into the input slot through the perimeter proxy and so bypasses the manual <see cref="TryPutLog"/>
    /// MarkDirty. Subscribed to <c>inventory.SlotModified</c> in <see cref="Initialize"/>.</summary>
    void OnInventorySlotModified(int slotId) => MarkDirty(true);

    /// <summary>
    /// Master-side interaction entry. Both the master block (<see cref="BlockSawmill.OnBlockInteractStart"/>) and
    /// every ghost-filler (<see cref="BlockSawmillGhost.OnBlockInteractStart"/>, via
    /// <see cref="BEMultiblockSawmill.Principal"/>) route here, so a right-click on ANY cell of the multiblock
    /// reaches this one method.
    ///
    /// T03.07 — the part-install machine (pulverizer-style); T05.02 — the manual log I/O layered on top. The click is
    /// dispatched by the Ctrl modifier first, then hand state:
    /// <list type="bullet">
    ///   <item>Ctrl+RMB WITHOUT Shift (any hand — hand-independent, like the sawhorse) → remove the LOADED LOG first
    ///   (<see cref="TryTakeLog"/> recoverable within the grace → back to inventory whole / <see cref="DiscardLog"/> past the grace → scrapped, no drop),
    ///   and only with no log the BLADE KIT (<see cref="TryTake"/>) — the mechanism parts stay committed (break the
    ///   frame to recover them), mirroring the sawhorse's progress-dependent Ctrl+RMB log removal
    ///   (memory vs-sawhorse-build-gesture);</item>
    ///   <item>plain RMB, item in hand → install the next part (<see cref="TryAddPart"/>); if it isn't a part, try to
    ///   load it as a log into the input (<see cref="TryPutLog"/>, accepted only once <see cref="IsComplete"/>);</item>
    ///   <item>plain RMB, empty hand → nothing (the product is ejected forward on completion, not taken from a slot).</item>
    /// </list>
    /// Always swallows the click (returns true) so the player can't place blocks into the machine's footprint. The
    /// install + part state live in the <c>BlockEntitySawmill.Parts</c> partial, the manual I/O in
    /// <c>BlockEntitySawmill.IO</c>; the saw cycle (E06) extends this once <see cref="IsComplete"/> is up.
    /// </summary>
    public bool OnInteract(IPlayer byPlayer, BlockSelection blockSel)
    {
        ItemSlot hand = byPlayer.InventoryManager.ActiveHotbarSlot;

        // Ctrl+RMB is the "remove" gesture REGARDLESS of what's in hand — checked before the install/load path so
        // holding a part or a log can't block removal. Mirrors the sawhorse, whose Ctrl+RMB take/discard never depends
        // on the held item (BlockSawhorse.OnBlockInteractStart). State-dependent outcome: a loaded log comes off
        // FIRST — recoverable (within the grace) → back to the inventory whole (TryTakeLog), past the grace → scrapped with no drop
        // (DiscardLog) — and only when there's no log does the blade kit come off (TryTake). The mechanism parts stay committed (break the
        // frame to recover them).
        //
        // !ShiftKey (AuditFixPlan T03.05, F-L8-21): the remove gesture is Ctrl WITHOUT Shift on ALL FOUR stations. A bare
        // `CtrlKey` here also answered Ctrl+SHIFT+RMB, which the hand stations can't (their Shift branch runs first and
        // hands the click to vanilla), so the machines carried a second, undocumented removal chord that worked on two
        // stations out of four and on neither hint. Requiring Shift to be up makes the ONE advertised chord
        // (HotKeyCode "ctrl" on takelog / discardlog / removeblades) the only one that removes anything, everywhere.
        // Shift-held clicks keep falling through to the install/load path below and stay swallowed, so nothing can be
        // placed into the machine's footprint either way.
        if (byPlayer.Entity.Controls.CtrlKey && !byPlayer.Entity.Controls.ShiftKey)
        {
            if (!TryTakeLog(byPlayer) && !DiscardLog(byPlayer)) TryTake(byPlayer);
            return true;
        }

        if (hand.Empty)
        {
            // Shift+RMB (empty hand) on an assembled frame cycles the cut mode (SawModePlan): the machine carries no
            // held tool to hold an F tool mode, so the product choice lives here. The gesture is shared with the block's
            // OnBlockInteractStep (which keeps the hold alive so one press = one cycle); a PLAIN empty RMB still does nothing.
            if (IsCutModeGesture(byPlayer)) return CycleSawMode(byPlayer);
            // Plain empty RMB does nothing: the finished product is ejected forward out of the machine on completion
            // (pulverizer-style, EjectBatch), never stored in a slot to take. Still swallow the click (return true) so
            // the player can't place a block into the machine's footprint.
            return true;
        }

        // A recognised build part installs (swallowed whether it took or only hinted); anything else falls through to
        // the log-input load, which itself rejects a non-log / unassembled machine / occupied input.
        if (TryAddPart(hand, byPlayer)) return true;
        TryPutLog(hand, byPlayer);
        return true;
    }

    /// <summary>Whether this interaction is the Shift+RMB (empty hand) cut-mode cycle on an assembled frame
    /// (SawModePlan). Shared by <see cref="OnInteract"/> (which cycles once on the START) and the block's
    /// <c>OnBlockInteractStep</c> (which returns true for it, keeping the hold ALIVE so the engine doesn't re-fire START
    /// ~4×/s and spin the mode — memory vs-block-interact-repeats-while-held). Requires <c>!CtrlKey</c> so it never
    /// overlaps the Ctrl+RMB remove gesture (which requires <c>!ShiftKey</c>): the two chords stay mutually exclusive.</summary>
    internal bool IsCutModeGesture(IPlayer byPlayer)
    {
        var c = byPlayer?.Entity?.Controls;
        return IsComplete && c != null && c.ShiftKey && !c.CtrlKey
               && byPlayer!.InventoryManager.ActiveHotbarSlot?.Itemstack == null;
    }

    /// <summary>Advance the machine's cut mode Auto → Planks → Beams → Auto (SawModePlan), from a Shift+RMB (empty hand)
    /// on the frame. SERVER-authoritative: the server flips <see cref="sawMode"/>, syncs it (<see cref="MarkDirty"/> so
    /// the hover readout and <c>GetPassBatch</c> follow) and sends the resulting mode name to the player IN THEIR OWN
    /// language (<see cref="Lang.GetL"/> — the recipient's, not the server's, the mod's SendIngameError idiom). One press
    /// = one cycle is enforced by the block keeping the hold alive (see <see cref="IsCutModeGesture"/>), not by a
    /// throttle. Always swallows the click; the client side is a pure no-op (it only had to swallow the click).</summary>
    bool CycleSawMode(IPlayer byPlayer)
    {
        if (Api.Side != EnumAppSide.Server) return true; // client just swallows; the server owns the mode + the confirmation line

        sawMode = (SawMode)(((int)sawMode + 1) % 3);
        MarkDirty(true);
        if (byPlayer is IServerPlayer sp)
            sp.SendIngameError("iwsawmode", Lang.GetL(sp.LanguageCode, "immersivewoodworking:ingameerror-sawmill-cutmode", sawMode.LangName(sp.LanguageCode)));
        return true;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);

        hasSash = tree.GetBool("hasSash");
        hasCrankshaft = tree.GetBool("hasCrankshaft");
        hasLevers = tree.GetBool("hasLevers");
        hasCarriage = tree.GetBool("hasCarriage");
        hasFlywheel = tree.GetBool("hasFlywheel");

        bladeStack = tree.GetItemstack("bladeStack");
        bladeStack?.ResolveBlockOrItem(worldForResolving); // tree itemstacks load unresolved (see GetItemstack docs)

        sawmillProgress = tree.GetFloat("sawmillProgress"); // E06/T06.01 — accrued feed-stroke fraction; a half-sawn log resumes from here after reload
        sawMode = (SawMode)tree.GetInt("sawMode", (int)SawMode.Auto); // SawModePlan — cut product cycled with Shift+RMB; synced for the hover readout

        // Input inventory (E05/T05.01) — read from its own subtree (its numeric slot key would otherwise collide with
        // the part keys above) and resolve the restored stack against worldForResolving (Api can still be null this
        // early on load), so the input log comes back with a live Block/Item, not a raw stack — the acceptance filter
        // (T05.03) and species render (E06) need block.Code. Mirrors the bladeStack resolve above.
        ITreeAttribute? invTree = tree.GetTreeAttribute("inventory");
        if (invTree != null)
        {
            inventory.FromTreeAttributes(invTree);
            foreach (ItemSlot slot in inventory) slot.Itemstack?.ResolveBlockOrItem(worldForResolving);
        }

        // E07/T07.01 — a server tree sync may have changed the installed blade kit (its metal drives the saw texture);
        // refresh the client render. No-op before Initialize (capi still null) — Initialize builds it then — and a
        // cheap dedup'd no-op when the metal is unchanged.
        UpdateRenderer();

        // E07/T07.03 — the input log (workpiece mesh) and the feed progress may also have changed in this sync. Rebuild
        // the log mesh on an input change (dedup'd) and snap the smooth client feed to the authoritative fraction — this
        // is what resets the carriage/log to the start when the server completes a pass (sawmillProgress → 0).
        UpdateLogMesh();
        clientProgress = sawmillProgress;

        // Force the bottom-screen interaction hints to refresh if this sync changed what they show (part installed, log
        // loaded, product appeared/cleared) — the help HUD doesn't poll, so otherwise they'd stay stale until re-hover.
        RefreshInteractionHelpOnChange();
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);

        tree.SetBool("hasSash", hasSash);
        tree.SetBool("hasCrankshaft", hasCrankshaft);
        tree.SetBool("hasLevers", hasLevers);
        tree.SetBool("hasCarriage", hasCarriage);
        tree.SetBool("hasFlywheel", hasFlywheel);

        if (bladeStack != null) tree.SetItemstack("bladeStack", bladeStack);

        tree.SetFloat("sawmillProgress", sawmillProgress); // E06/T06.01 — feed-stroke fraction, frozen across power loss / reload
        tree.SetInt("sawMode", (int)sawMode); // SawModePlan — cut product, synced so the client's hover readout matches

        // I/O inventory (E05/T05.01) under its own subtree so its numeric slot keys can't collide with the part keys
        // above (mirrors how BlockEntityContainer namespaces its inventory).
        ITreeAttribute invTree = new TreeAttribute();
        inventory.ToTreeAttributes(invTree);
        tree["inventory"] = invTree;
    }

    /// <summary>
    /// E08/T08.03 — the hover tooltip: what the machine is doing right now. Shown on the master cell directly and on
    /// every ghost cell via <see cref="BEMultiblockSawmill.GetBlockInfo"/> (which routes here), so any cell reads the
    /// same. Like the sawhorse's info it shows only RELEVANT state — an unassembled frame says just that; an assembled
    /// one shows the blade tier, the loaded log, the cut progress (only once a cut has begun) and a single WHY-line
    /// when it isn't cutting. Runs client-side on hover; every field is server-synced (parts, inventory, progress) or
    /// client-interpolated (the network speed via <see cref="EffectiveSawSpeed"/>), so it reads correctly client-side.
    /// </summary>
    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder sb)
    {
        base.GetBlockInfo(forPlayer, sb);

        // Inert until fully assembled: say so and list the mandatory parts still missing (the blade kit too, once the
        // four mechanism parts are in) — the player sees exactly what's left for IsComplete. Then stop; progress/speed
        // are meaningless on a half-built frame. (The interaction-help shows the same parts as installable icons.)
        if (!IsComplete)
        {
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawmill-incomplete"));
            foreach (string code in MissingMandatoryParts())
                sb.AppendLine("• " + Lang.Get("immersivewoodworking:item-" + code));
            if (HasSash && !HasBladeKit)
                sb.AppendLine("• " + Lang.Get("immersivewoodworking:blockinfo-sawmill-needblades"));
            return;
        }

        // Blade tier — the installed kit's metal (its durability/wear tier and the install requirement; it no longer
        // paces the cut — the drive RPM does, see below); show its already-localized item name. The optional flywheel
        // shows as a separate upgrade line (it isn't part of IsComplete).
        if (bladeStack != null)
        {
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawmill-blades", bladeStack.GetName()));
            // Remaining blade-kit durability, like a tool's "X / Y" — only when the item declares durability (T06.06).
            int bladeMax = bladeStack.Collectible.GetMaxDurability(bladeStack);
            if (bladeMax > 1)
                sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawmill-blade-durability",
                    bladeStack.Collectible.GetRemainingDurability(bladeStack), bladeMax));
        }
        if (hasFlywheel) sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawmill-flywheel"));

        // The standing cut product (SawModePlan) — planks / beams / auto, cycled with Shift+RMB. A persistent machine
        // setting (unlike the sawhorse's per-log lock, which shows only mid-rip), so it reads whenever assembled.
        sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawmill-cutmode", sawMode.LangName()));

        // The loaded workpiece log (its already-localized name carries the species, e.g. "Birch log"). There is no
        // output line: the product is ejected forward on completion, never accumulated in the machine.
        ItemStack? log = InputSlot.Itemstack;
        if (log != null) sb.AppendLine(log.GetName());

        // Cut progress as a percent of one pass (sawmillProgress is already a 0..1 fraction). Only once a cut has begun
        // so an untouched log reads as just its name (no "0%" noise).
        if (sawmillProgress > 0f)
        {
            int percent = GameMath.Clamp((int)(sawmillProgress * 100f), 0, 100);
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawmill-progress", percent));
        }

        // Drive + work state, diagnosed in CanSaw's gate order so the player sees WHY it isn't cutting: no power /
        // below torque → "no power"; otherwise the DRIVE speed (the cut is paced by shaft rotation —
        // R = SawmillDefaultWork/SawmillSpeed revolutions per log — so the network RPM is what matters; EffectiveSawSpeed
        // only gates here) plus why it's idle (no log) / running.
        float spd = EffectiveSawSpeed(); // gate only: 0 = no blade kit / below torque
        if (spd <= 0f)
        {
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawmill-nopower"));
        }
        else
        {
            sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawmill-speed", NetworkSpeed.ToString("0.00")));
            if (InputEmpty) sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawmill-idle"));
            else sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawmill-running"));
        }
    }
}
