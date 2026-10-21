using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// E05 — log input/output flow: the manual RMB put / Ctrl+RMB take-or-forfeit (T05.02), the input filter (T05.03,
/// reusing the chopping block's rules), and the vanilla chute/hopper auto-input via the perimeter proxy (T05.04,
/// <c>GetAutoPushIntoSlot</c>). Mirrors <see cref="BlockEntitySawmill"/>'s IO partial.
///
/// T05.02 — the MANUAL hand path (server-authoritative, mirroring <see cref="BlockEntitySawmill.TryPutLog"/> /
/// <see cref="BlockEntitySawmill.TryTakeLog"/> / <see cref="BlockEntitySawmill.DiscardLog"/>): a plain RMB with a
/// choppable item loads the input (<see cref="TryPutLog"/>); a Ctrl+RMB removes it — WHOLE while the current pass is
/// still within the recovery grace (<see cref="LogRecoverable"/>, <see cref="TryTakeLog"/>), scrapped once past the
/// grace into its chop (<see cref="DiscardLog"/>) — and a break releases it the same way (<see cref="OnBlockBroken"/>).
/// The grace is the mod-wide <see cref="ImmersiveWoodworkingConfig.LogRecoverThreshold"/> the sawhorse and
/// sawmill already use — but NOTE that on the SHIPPED DEFAULT the "past the grace" half of that split never happens on
/// THIS machine; <see cref="LogRecoverable"/> carries the arithmetic and the reason the contract stays anyway. (The grace
/// arrived with plan revision 2026-07-03, when the default was still 50 strikes per log and the task's original binary
/// "==0 / >0" rule left a running chopper's loaded log unrecoverable within one tick; the ÷48 reducer of 2026-07-13 then
/// took the default to one atomic strike, and the window it was guarding with it.) There is NO "take product" gesture: a
/// completed pass EJECTS the firewood/stick batch forward out of the machine (pulverizer-style, E06), never into a slot.
///
/// The OUTPUT resolve (log→firewood, firewood→sticks, quantities) is NOT here — the chopping block's
/// <c>GetChopResult</c>/<c>MakeSticks</c> were private-instance, so E06 lifted them to internal-static (T06.02) and the
/// chopper reuses them through the ONE seam <c>GetChopBatch</c> (in the <c>.Chopping</c> partial); this partial only
/// gates the input (<see cref="IsValidInput"/>) and seeds the render species (<see cref="InputWoodSpecies"/>).
/// </summary>
public partial class BlockEntityChopper
{
    /// <summary>
    /// Plain RMB with a choppable log / firewood in hand: load it into the input slot. Returns false (so the
    /// caller can fall through) when the machine isn't assembled yet (<see cref="IsComplete"/> — inert until the head is
    /// in), the input already holds a log (the "one log, no buffer" rule), the held item isn't an accepted input
    /// (<see cref="IsValidInput"/>, the T05.03 filter = the chopping block's <c>IsChoppable</c>), or the axe head is
    /// currently down/descending on the bed (<see cref="HeadClearOfBed"/> — no shoving a log under the descended maul; a
    /// running machine loads only once the head has lifted clear, tied to the animation). Server-authoritative, exactly
    /// like <see cref="BlockEntitySawmill.TryPutLog"/>: the client swallows the click and waits for the synced slot; the
    /// server moves ONE item from the hand into the input.
    /// </summary>
    public bool TryPutLog(ItemSlot fromSlot, IPlayer byPlayer)
    {
        if (!IsComplete) return false;                                                       // inert until assembled
        if (!InputEmpty) return false;                                                       // no input buffer (one log at a time)
        if (fromSlot?.Itemstack == null || !IsValidInput(fromSlot.Itemstack)) return false;  // filter (T05.03)
        if (!HeadClearOfBed())
        {
            // The axe is down/descending on the bed — refuse the load (no shoving a log under the descended maul; a
            // running machine loads only once the head has lifted clear, tied to the animation). Nudge the player WHY
            // with a transient red line above the hotbar, like the sawhorse's wrong-side hint (NotifyInteractBlocked),
            // instead of leaving the reason buried in the block-info panel (user-reported: not intuitive). Client-side
            // only (Api as ICoreClientAPI is null on the server); the client's HeadClearOfBed reads the render's
            // animAngle, so the hint fires exactly when the axe LOOKS down. We only reach here with a valid choppable
            // log in hand for an assembled, empty machine (the gates above all passed), so this is always the right
            // message. Enforcement stays server-authoritative below.
            (Api as ICoreClientAPI)?.TriggerIngameError(this, "chopperheaddown", Lang.Get("immersivewoodworking:ingameerror-chopper-headdown"));
            return false;
        }

        if (Api.Side != EnumAppSide.Server) return true; // client swallows; the server performs the move

        InputSlot.Itemstack = fromSlot.TakeOut(1);
        InputSlot.MarkDirty();
        fromSlot.MarkDirty();

        // Server plays + broadcasts so it's heard once by everyone nearby, the acting player INCLUDED
        // (AuditFixPlan T06.01, F-L3-06). dualCallByPlayer must stay null here: this line sits BELOW the
        // server gate above, so the actor's own client never reaches it — passing byPlayer would only strike
        // them off the broadcast and leave the one player who caused the sound as the only one not hearing
        // it (in singleplayer: silence). The argument is meaningful ONLY where the same call runs on both
        // sides, as in OnPartInstalled (Parts.cs), which has no side gate.
        Api.World.PlaySoundAt(Block.Sounds.Place, Pos, -0.25, null);
        MarkDirty(true); // persist + sync the loaded input to the client
        return true;
    }

    /// <summary>
    /// Ctrl+RMB on a log still WITHIN the recovery grace (<see cref="LogRecoverable"/>): pop it back to the player's
    /// inventory — or the world if that's full — and clear the input so the next log can load. Returns false when the
    /// input is empty OR the pass is already past the grace: the caller then scraps it via <see cref="DiscardLog"/>, so
    /// Ctrl+RMB is ONE "remove the log" gesture with a progress-dependent outcome, mirroring
    /// <see cref="BlockEntitySawmill.TryTakeLog"/>. Server-authoritative: the client swallows the click and reflects the
    /// cleared input the server syncs back; the server performs the give. (The render + progress reset reach the client
    /// through <see cref="FromTreeAttributes"/> on that sync.)
    /// </summary>
    public bool TryTakeLog(IPlayer byPlayer)
    {
        if (InputEmpty || !LogRecoverable) return false; // empty, or past the recovery grace → scrapped instead (DiscardLog)

        if (Api.Side != EnumAppSide.Server) return true; // client swallows; the server performs the give

        ItemStack stack = InputSlot.TakeOutWhole(); // notifies the slot; the BE-wide MarkDirty below persists + syncs
        chopProgress = 0f;                          // untouched/barely-begun, but keep the accumulator clean for the next log
        if (!byPlayer.InventoryManager.TryGiveItemstack(stack))
        {
            Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 0.6, 0.5));
        }

        // Server-side broadcast with a null dualCallByPlayer, exactly as TryPutLog above (F-L3-06).
        Api.World.PlaySoundAt(Block.Sounds.Place, Pos, -0.25, null);
        MarkDirty(true);
        return true;
    }

    /// <summary>
    /// Ctrl+RMB on a log past the grace into its chop (NOT <see cref="LogRecoverable"/>): scrap it with NO drop — the
    /// player abandons the partial chop — clearing the input and resetting the progress. The caller reaches this only
    /// after <see cref="TryTakeLog"/> declined (the log is past the grace, not recoverable); on a still-recoverable or
    /// absent log it returns false and does nothing, mirroring <see cref="BlockEntitySawmill.DiscardLog"/>.
    /// Server-authoritative: returns true to swallow whenever there IS a non-recoverable log; the client reflects the
    /// cleared input on the synced tree. Never reached on the shipped default — at <c>ChopperStrikes = 1</c> a
    /// non-recoverable log cannot exist; see <see cref="LogRecoverable"/>.
    /// </summary>
    public bool DiscardLog(IPlayer byPlayer)
    {
        if (InputEmpty || LogRecoverable) return false; // empty, or still within the recovery grace → taken back whole instead (TryTakeLog)

        if (Api.Side != EnumAppSide.Server) return true; // client swallows; the server performs the scrap

        InputSlot.Itemstack = null; // a past-grace-chopped log is forfeit — no drop
        InputSlot.MarkDirty();
        chopProgress = 0f;
        MarkDirty(true);
        return true;
    }

    /// <summary>
    /// Teardown log release (closes the E05 item-loss gap the plan deferred here from T03.05): breaking an assembled
    /// chopper with a log loaded used to DESTROY it — a plain <see cref="BlockEntity"/> has no container auto-drop, and
    /// the parts path (<see cref="BlockChopper.GetDrops"/>/<see cref="AppendPartDrops"/>) deliberately omits the input
    /// log. This is the BE's own break hook that <see cref="BlockChopper.OnBlockBroken"/> names as "the seam where E05's
    /// input log will be released"; the engine's SpawnDropsAndRemoveBlock runs it while the BE is still alive. The
    /// recovery grace applies exactly like the Ctrl+RMB remove: a still-recoverable pass (<see cref="LogRecoverable"/>)
    /// drops the WHOLE log, past the grace the partly-chopped log is forfeit — mirroring
    /// <see cref="BlockEntitySawmill.OnBlockBroken"/> / <see cref="BlockEntityChoppingBlock.OnBlockRemoved"/>.
    /// Server-side; <see cref="Api"/> can be null very early on load, so it is guarded.
    /// </summary>
    public override void OnBlockBroken(IPlayer? byPlayer = null)
    {
        base.OnBlockBroken(byPlayer);
        if (Api?.Side != EnumAppSide.Server || InputEmpty) return;

        if (LogRecoverable)
            Api.World.SpawnItemEntity(InputSlot.TakeOutWhole(), Pos.ToVec3d().Add(0.5, 0.6, 0.5)); // within the grace → whole log back
        else
            InputSlot.Itemstack = null; // past the grace → forfeit, no drop (matches DiscardLog). Unreachable at ChopperStrikes = 1 — see LogRecoverable

        InputSlot.MarkDirty();
        chopProgress = 0f;
    }

    /// <summary>Whether the loaded input log can be handed back WHOLE on a Ctrl+RMB take or on a break/teardown: an
    /// UNTOUCHED log always can, and a begun one can while the current pass is under the configurable grace
    /// (<see cref="ImmersiveWoodworkingConfig.LogRecoverThreshold"/>, default 25%). The master block's
    /// interaction help (T08.01) branches on it to pick the Ctrl+RMB hint (recoverable → take back via
    /// <see cref="TryTakeLog"/>; past the grace → scrap via <see cref="DiscardLog"/>), and <see cref="OnBlockBroken"/>
    /// uses it to choose drop-whole vs forfeit. The untouched fast-path keeps a freshly-loaded log recoverable even if
    /// the grace is configured to 0. Vacuously true with no log — callers gate on <see cref="InputEmpty"/> first.
    /// Mirrors the mod-wide <see cref="BlockEntitySawmill.LogRecoverable"/> /
    /// <see cref="BlockEntitySawhorse.LogRecoverable"/>.
    ///
    /// DEAD ON THE SHIPPED DEFAULT, KEPT DELIBERATELY (AuditFixPlan T10.04 / AuditPlan2 F-L2-08). The body is the twins'
    /// body verbatim, but their defaults differ, and at <c>ChopperStrikes = 1</c> the false branch of this one is
    /// unreachable: the step is a whole <c>1f</c>, so <see cref="OnChopperTick"/> runs <c>chopProgress</c> 0 → 1 → 0
    /// inside a SINGLE call (the <c>&gt;= 1f</c> arm hands off to <c>CompletePass</c>, which zeroes it) and every other
    /// writer assigns 0 outright. Outside that one method the field only ever holds 0, this property is therefore always
    /// true, and four consumers never fire: <see cref="DiscardLog"/>, the forfeit arm of <see cref="OnBlockBroken"/>,
    /// the Ctrl+RMB "discard" hint (<see cref="BlockChopper.SelectInteractions"/>, lang
    /// <c>blockhelp-chopper-discardlog</c>) and the pass-percent tooltip line (<see cref="GetBlockInfo"/>, lang
    /// <c>blockinfo-chopper-progress</c>). Not dead code, though: at <c>ChopperStrikes &gt; 1</c> — i.e. <c>ChopperSpeed</c>
    /// &lt; 1, the knob band-clamped to [0.01, 100] — a partly-struck log exists and all four come alive. A world that
    /// saved the retired <c>ChopperStepsPerLog</c> (or the 2026-07-12 stopgap of 50) does NOT stay in that state: the
    /// retired key does not bind, it resets to <c>ChopperSpeed</c> = 1 on load (N = 1), landing back on the dead-default
    /// path — the config has no migration on purpose (AuditPlan DECISIONS #6, archived in Plans_archive/AuditPlan.rar).
    /// The strike count is now derived from <c>ChopperSpeed</c> (<c>ChopperStrikes</c> = ⌈1/<c>ChopperSpeed</c>⌉,
    /// SpeedDecouplingPlan, shipped), not a standalone field. So the contract stays as written and keeps mirroring its
    /// twins; this note is here only so the
    /// next reader stops deriving reachability from that symmetry.</summary>
    public bool LogRecoverable => chopProgress <= 0f || chopProgress < Config.LogRecoverThreshold;

    /// <summary>
    /// Hopper/chute AUTO-PUSH target (T05.04, feed-side revised 2026-07-12; face swapped with the eject 2026-07-14): the
    /// input slot — but ONLY when a chute abuts one of the TWO accepted input cells from BEHIND (the machine's back, away
    /// from the player), offers a valid log AND the machine can take it. The accepted dock is the central column's two
    /// MIDDLE cells on the side OPPOSITE the eject (local (0,1,0) / (0,2,0), pushed on the machine's back = world
    /// <see cref="Facing"/> face, now that the eject is the player-side <see cref="Facing"/>.Opposite) — see
    /// <see cref="IsBackCentralInputDock"/>. Deliberately NOT the top face any more (user 2026-07-12: "сверху нельзя, с
    /// боков нельзя, с самой нижней клетки нельзя"): feeding is from directly behind, never from above / the sides / the
    /// bottom master cell, so the input side is unambiguous and never fights the player-side eject.
    ///
    /// Same ITEM/STATE gates as the manual put (<see cref="TryPutLog"/>), so the hand and hopper paths can't drift:
    /// assembled (<see cref="IsComplete"/>), input free (<see cref="InputEmpty"/> — the "one log, no buffer" rule), the
    /// axe head lifted clear of the bed (<see cref="HeadClearOfBed"/> — a running machine won't auto-refill under the
    /// descended maul; the chute holds its log until the head rises, tied to the animation) and a choppable item
    /// (<see cref="IsValidInput"/>, the T05.03 filter = the chopping block's <c>IsChoppable</c>). Any other face/cell, or
    /// a machine that can't take the log → null, so the chute holds its item rather than churning. Called by
    /// the perimeter <see cref="BEMultiblockChopper"/>'s item-flow proxy (the master cell is interior, unreachable by a
    /// chute), which the vanilla <c>BlockEntityItemFlow</c> queries; <paramref name="atBlockFace"/> is already flipped to
    /// OUR face by the engine, and <paramref name="dockCell"/> is the WORLD position of the ghost cell the chute docked to
    /// (the proxy passes its own <see cref="BlockEntity.Pos"/>) so the master can tell WHICH footprint cell was hit.
    /// </summary>
    public ItemSlot? GetAutoPushIntoSlot(BlockFacing atBlockFace, ItemSlot? fromSlot, BlockPos dockCell)
        => IsComplete && InputEmpty && HeadClearOfBed() && IsValidInput(fromSlot?.Itemstack) && IsBackCentralInputDock(atBlockFace, dockCell)
            ? InputSlot
            : null;

    /// <summary>
    /// The GEOMETRY gate for the hopper/chute auto-input (user 2026-07-12; face swapped with the eject 2026-07-14): a
    /// chute may push a log in ONLY through the two central-column MIDDLE cells on the side opposite the eject — the
    /// machine's BACK, away from the player. Concretely, <paramref name="atBlockFace"/> must be world <see cref="Facing"/>
    /// (the eject is now toward <see cref="Facing"/>.Opposite = the player-side front, so the input is the +Z back — the
    /// "swap перед и зад" the user asked for; before 2026-07-14 the eject was +Z and the input the −Z face), and the
    /// docked cell (mapped to the local footprint frame via <see cref="LocalOf"/>) must be local (0, 1, 0) or (0, 2, 0):
    /// the central column (X=0, Z=0), excluding the bottom master cell (Y=0) and the top crown (Y=3). Those two are the
    /// ONLY central footprint cells with an OPEN air cell behind them (on the back/+Z side) for a chute to sit in —
    /// (0,1,1)/(0,2,1) — so this is the one physically feedable spot; the master (Y=0) is interior/non-container and the
    /// crown (Y=3) is excluded so the feed is always the two clean middle cells. Rejecting the top face closes the former
    /// top-feed and the front face (world <see cref="Facing"/>.Opposite) closes a feed from the eject/player side; a side
    /// (±X) chute can't even reach these two cells (their X-neighbours are occupied frame). Pure read of
    /// <see cref="Facing"/> + the passed cell, so it is side-agnostic and off-main-thread-safe like the rest of the
    /// footprint maths.
    /// </summary>
    bool IsBackCentralInputDock(BlockFacing atBlockFace, BlockPos dockCell)
    {
        if (atBlockFace != Facing) return false;                  // ONLY the back face (Facing = away from the player, opposite the Facing.Opposite player-side eject)
        Vec3i local = LocalOf(dockCell);                          // world cell → authored footprint frame
        return local.X == 0 && local.Z == 0                       // central column
            && (local.Y == 1 || local.Y == 2);                    // the two middle cells (not the bottom master, not the top crown)
    }

    // (There is no hopper AUTO-PULL source: the chopper has no output slot — a completed pass ejects the batch forward
    // out of the machine, EjectBatch — so a chute/hopper can only AUTO-PUSH a log INTO the input, never pull a product.)

    /// <summary>
    /// Acceptance gate for the input slot (T05.03) — the SINGLE seam the manual RMB put (T05.02) consults and the hopper
    /// suitability (<see cref="InventoryChopper.GetSuitability"/>) asserts again, so the two paths can never drift. FULL
    /// parity with the hand chopping block: reuse <see cref="BlockEntityChoppingBlock.IsChoppable"/> (= <c>IsLog ||
    /// IsFirewood || IsHalfLog</c>) verbatim — a form-of-the-code check, not a domain whitelist, so a MODDED log
    /// (<c>wildcrafttree:log-placed-azobe-ud</c>) passes automatically and no species is ever enumerated (the
    /// mod-independence rule "accept by code shape, never by species", <c>source-conventions.md</c>).
    ///
    /// Deliberately SIMPLER than the sawmill's two-layer <c>IsValidInput</c> (shape + <c>SawProductResolves</c>): the
    /// chopper needs NO "product resolves" layer, because the output is always vanilla — <c>game:stick</c>, and
    /// firewood through <c>BlockEntityChoppingBlock.ResolveFirewood</c>, which prefers a species' own
    /// <c>{wood}firewood</c> (vanilla's <c>agedfirewood</c>) but FALLS BACK to the single hardcoded
    /// <c>game:firewood</c>, so it cannot fail to resolve and there is nothing to gate at the door (mod-independence
    /// rule "the chop product is vanilla on purpose", <c>source-conventions.md</c> — the <c>game:</c> hardcode is
    /// intentional, not a bug to "fix"). Kept as an
    /// instance seam mirroring the sawmill's shape (T05.02's put will call it exactly as the
    /// sawmill's <c>TryPutLog</c> does), though the underlying check is a pure static touching no world state.
    /// </summary>
    bool IsValidInput(ItemStack? stack) => BlockEntityChoppingBlock.IsChoppable(stack, Config);

    /// <summary>The saw-input KIND of the loaded input (<see cref="SawInput.Log"/> / <see cref="SawInput.DebarkedLog"/> /
    /// <see cref="SawInput.LogSection"/>, or <see cref="SawInput.None"/> when empty / not one of those). The render
    /// (T07.03) reads it to pick the workpiece billet's bark vs debarked-surface textures via
    /// <see cref="LogTexSource.ForLog"/> (a section paints as a barked log — the non-debarked branch). Reuses
    /// <see cref="BlockEntitySawhorse.GetSawInput"/>, mirroring <see cref="BlockEntitySawmill.InputSawKind"/> — no second
    /// mapping. <see cref="SawInput.None"/> for a still-choppable that isn't one of those (firewood / a grown
    /// log or section / chopping block): those seed no species and draw a default-look billet (see <see cref="InputWoodSpecies"/>).</summary>
    public SawInput InputSawKind => BlockEntitySawhorse.GetSawInput(InputSlot.Itemstack);

    /// <summary>
    /// The wood species of the loaded input (oak … <c>aged</c>, modded woods included), or null when empty. Seeds the
    /// log-mesh painting on the chopper's bed (T07.03); reuses <see cref="BlockEntitySawhorse.GetWoodSpecies"/>,
    /// domain-agnostic, no whitelist — mirroring <see cref="BlockEntitySawmill.InputWoodSpecies"/> exactly.
    ///
    /// NB: the chopper accepts a WIDER input set than that classifier resolves — <see cref="BlockEntitySawhorse.GetWoodSpecies"/>
    /// only reads <c>log-placed-{wood}</c> / <c>debarkedlog-{wood}</c> / <c>logsection-placed-{wood}</c> (E08), so a
    /// still-choppable grown log or section / chopping block / firewood seeds null here. That is by design and
    /// harmless: species only paints the log render (T07.03), which already falls back to a default look for a null
    /// species (firewood carries none anyway); a placed section now paints as a barked redwood billet rather than the
    /// default look, which is fine — it
    /// is unrelated to the filter above, which accepts the full set regardless. The output resolve is NOT here — it lives
    /// in the single seam <c>GetChopBatch</c> (T06.02).
    /// </summary>
    public string? InputWoodSpecies => BlockEntitySawhorse.GetWoodSpecies(InputSlot.Itemstack);
}
