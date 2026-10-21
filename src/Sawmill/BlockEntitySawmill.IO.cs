using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// E05 (T05.02) — the sawmill's MANUAL right-click I/O, the hand path that complements the hopper auto-input (T05.04).
/// A plain RMB with a sawable log / debarked log loads the input slot; Ctrl+RMB takes a still-recoverable log (under the
/// recovery grace, <see cref="LogRecoverable"/>) back WHOLE or scraps one past the grace into its cut — and a break releases it the same
/// way (<see cref="OnBlockBroken"/>). There is NO "take product" gesture: a completed pass EJECTS the plank/support-beam batch forward out of
/// the machine (pulverizer-style, see <see cref="EjectBatch"/>), so it's picked up off the ground, never out of a slot.
/// Both put/take are server-authoritative — the client returns true to swallow the click and reflects the slot the
/// server syncs back via <c>FromTreeAttributes</c> — mirroring the hand sawhorse's <see cref="BlockEntitySawhorse.TryPutLog"/>
/// / <see cref="BlockEntitySawhorse.TryTakeLog"/> and <c>BEPulverizer.OnInteract</c>.
///
/// The gesture routing itself (which hand state hits which method, and the Ctrl+RMB part-removal split) lives in the
/// main partial's <see cref="OnInteract"/>, the same place T03.07's install routing already sits. This file only
/// holds the I/O actions plus the acceptance-filter SEAM <see cref="IsValidInput"/>; the precise filter (T05.03) and
/// the one-log gate (T05.05) tighten that seam and <see cref="InputEmpty"/>, kept as single sources so the manual path
/// here and the hopper path (T05.04) can't drift.
/// </summary>
public partial class BlockEntitySawmill
{
    /// <summary>
    /// Plain RMB with a log / debarked log in hand: load it into the input slot. Returns false (so the caller can
    /// fall through / swallow) when the machine isn't assembled yet (<see cref="IsComplete"/> — it is inert until
    /// then), the input already holds a log (the "one log, no buffer" tier rule — T05.05 builds the pause on
    /// <see cref="InputEmpty"/>), or the held item isn't an accepted input (<see cref="IsValidInput"/>, the T05.03
    /// filter seam). Server-authoritative, exactly like <see cref="BlockEntitySawhorse.TryPutLog"/>: the client
    /// swallows the click and waits for the synced slot; the server moves ONE item from the hand into the input.
    /// </summary>
    public bool TryPutLog(ItemSlot fromSlot, IPlayer byPlayer)
    {
        if (!IsComplete) return false;                                                       // inert until assembled
        if (!InputEmpty) return false;                                                       // no input buffer (T05.05)
        if (fromSlot?.Itemstack == null || !IsValidInput(fromSlot.Itemstack)) return false;  // filter (T05.03)

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
    /// Ctrl+RMB is ONE "remove the log" gesture with a progress-dependent outcome, mirroring the hand sawhorse's
    /// <see cref="BlockEntitySawhorse.TryTakeLog"/>. Server-authoritative: the client swallows the click and reflects
    /// the cleared input the server syncs back; the server performs the give. (The render/log-mesh + progress reset
    /// reach the client through <see cref="FromTreeAttributes"/> on that sync.)
    /// </summary>
    public bool TryTakeLog(IPlayer byPlayer)
    {
        if (InputEmpty || !LogRecoverable) return false; // empty, or past the recovery grace → scrapped instead (DiscardLog)

        if (Api.Side != EnumAppSide.Server) return true; // client swallows; the server performs the give

        ItemStack stack = InputSlot.TakeOutWhole(); // notifies the slot; the BE-wide MarkDirty below persists + syncs
        sawmillProgress = 0f;                        // untouched, but keep the accumulator clean for the next log
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
    /// Ctrl+RMB on a log past the grace into its pass (NOT <see cref="LogRecoverable"/>): scrap it with NO drop — the player
    /// abandons the partial cut — clearing the input and resetting the progress. The caller reaches this only after
    /// <see cref="TryTakeLog"/> declined (the log is past the grace, not recoverable); on a still-recoverable or absent
    /// log it returns false and does nothing, mirroring the hand sawhorse's <see cref="BlockEntitySawhorse.DiscardLog"/>.
    /// Server-authoritative: returns true to swallow whenever there IS a non-recoverable log; the client reflects the
    /// cleared input on the synced tree.
    /// </summary>
    public bool DiscardLog(IPlayer byPlayer)
    {
        if (InputEmpty || LogRecoverable) return false; // empty, or still within the recovery grace → taken back whole instead (TryTakeLog)

        if (Api.Side != EnumAppSide.Server) return true; // client swallows; the server performs the scrap

        InputSlot.Itemstack = null; // a past-grace-sawn log is forfeit — no drop
        InputSlot.MarkDirty();
        sawmillProgress = 0f;
        MarkDirty(true);
        return true;
    }

    /// <summary>
    /// Teardown log release. Closes the E05 item-loss gap the design comments long promised but never implemented:
    /// breaking an assembled sawmill with a log loaded used to DESTROY it (a plain <see cref="BlockEntity"/> has no
    /// container auto-drop, and the parts path — <see cref="BlockSawmill.GetDrops"/>/<see cref="AppendPartDrops"/> —
    /// deliberately omits the input log). This is the BE's own break hook that <see cref="BlockSawmill.OnBlockBroken"/>
    /// names as "the seam where E05's input log will be released"; the engine's SpawnDropsAndRemoveBlock runs it while
    /// the BE is still alive. The recovery grace applies exactly like the Ctrl+RMB remove: a barely-started pass
    /// (<see cref="LogRecoverable"/>) drops the WHOLE log, at/above the grace the partly-sawn log is forfeit — mirroring
    /// <see cref="BlockEntityChoppingBlock.OnBlockRemoved"/> / <see cref="BlockEntitySawhorse.ReleaseHeldLog"/>.
    /// Server-side; <see cref="Api"/> can be null very early on load, so it is guarded.
    /// </summary>
    public override void OnBlockBroken(IPlayer? byPlayer = null)
    {
        base.OnBlockBroken(byPlayer);
        if (Api?.Side != EnumAppSide.Server || InputEmpty) return;

        if (LogRecoverable)
            Api.World.SpawnItemEntity(InputSlot.TakeOutWhole(), Pos.ToVec3d().Add(0.5, 0.6, 0.5)); // within the grace → whole log back
        else
            InputSlot.Itemstack = null; // past the grace sawn → forfeit, no drop (matches DiscardLog)

        InputSlot.MarkDirty();
        sawmillProgress = 0f;
    }

    /// <summary>Whether the loaded input log can be handed back WHOLE on a Ctrl+RMB take or on a break/teardown: an
    /// UNTOUCHED log always can, and a begun one can while the current pass is under the configurable grace
    /// (<see cref="ImmersiveWoodworkingConfig.LogRecoverThreshold"/>, default 25%). The master block's
    /// interaction help (T08.01) branches on it to pick the Ctrl+RMB hint (recoverable → take back via
    /// <see cref="TryTakeLog"/>; past the grace → scrap via <see cref="DiscardLog"/>), and <see cref="OnBlockBroken"/>
    /// uses it to choose drop-whole vs forfeit. The untouched fast-path keeps a freshly-loaded log recoverable even if
    /// the grace is configured to 0. Vacuously true with no log — callers gate on <see cref="InputEmpty"/> first.
    /// Mirrors the hand sawhorse's <see cref="BlockEntitySawhorse.LogRecoverable"/>.</summary>
    public bool LogRecoverable => sawmillProgress <= 0f || sawmillProgress < Config.LogRecoverThreshold;

    /// <summary>
    /// Hopper/chute AUTO-PUSH target (T05.04): the input slot — but ONLY when a top-mounted chute (<paramref name="atBlockFace"/>
    /// == <see cref="BlockFacing.UP"/>, the machine's own top face the chute abuts) offers a valid log AND the machine can take it.
    /// Same gates as the manual put (<see cref="TryPutLog"/>), so the hand and hopper paths can't drift: assembled
    /// (<see cref="IsComplete"/>), input free (<see cref="InputEmpty"/> — the "one log, no buffer" rule, T05.05) and a sawable,
    /// product-resolvable log (<see cref="IsValidInput"/>, the T05.03 filter). Any other face, or a machine that can't take the
    /// log → null, so the chute holds its item rather than churning. Called by the perimeter
    /// <see cref="BEMultiblockSawmill"/>'s item-flow proxy (the master cell is interior, unreachable by a chute), which the
    /// vanilla <c>BlockEntityItemFlow</c> queries; <paramref name="atBlockFace"/> is already flipped to OUR face by the engine.
    /// </summary>
    public ItemSlot? GetAutoPushIntoSlot(BlockFacing atBlockFace, ItemSlot? fromSlot)
        => atBlockFace == BlockFacing.UP && IsComplete && InputEmpty && IsValidInput(fromSlot?.Itemstack)
            ? InputSlot
            : null;

    // (There is no hopper AUTO-PULL source: the sawmill has no output slot — a completed pass ejects the batch forward
    // out of the machine, EjectBatch — so a chute/hopper can only AUTO-PUSH a log INTO the input, never pull a product.)

    /// <summary>
    /// Acceptance gate for the input slot (T05.03), used by the manual put here and asserted again by the hopper
    /// suitability (<see cref="InventorySawmill.GetSuitability"/>). TWO layers, both reusing the hand sawhorse so the
    /// rule lives in one place, never a second copy:
    /// <list type="bullet">
    ///   <item>SHAPE — <see cref="BlockEntitySawhorse.IsSawableInput"/>: only player-placed <c>log-placed-{wood}</c> /
    ///   <c>debarkedlog-{wood}</c> / <c>logsection-placed-{wood}</c> (E08), ANY wood incl. modded / <c>aged</c>, never a
    ///   grown log or section / firewood / flotsam;</item>
    ///   <item>RESOLVABLE — <see cref="BlockEntitySawhorse.SawProductResolves"/>: the log's
    ///   <c>plank</c>/<c>supportbeam</c> must actually exist in the log's own domain or game, so a modded log the
    ///   sawmill could never turn into a product is refused at the door rather than parked inside forever (the
    ///   mod-independence rule "a per-species product must resolve, or the input is refused at the door",
    ///   <c>source-conventions.md</c>).</item>
    /// </list>
    /// The second layer used to be a sawmill method of its own (<c>CanResolveProduct</c>); AuditFixPlan T03.03 moved
    /// its body to the sawhorse beside the classifiers and the resolver it was already borrowing, because the hand
    /// pair's own doors turned out to need the very same check and a second copy is what the rule forbids.
    /// </summary>
    bool IsValidInput(ItemStack? stack)
        => BlockEntitySawhorse.IsSawableInput(stack) && BlockEntitySawhorse.CanProduceAnySaw(Api.World, stack);

    /// <summary>The accepted-input KIND currently loaded (<see cref="SawInput.Log"/> / <see cref="SawInput.DebarkedLog"/>,
    /// or <see cref="SawInput.None"/> when the input is empty / unrecognised). The seam E06 reads to pick plank vs
    /// beam — reuses the sawhorse classifier, no second mapping.</summary>
    public SawInput InputSawKind => BlockEntitySawhorse.GetSawInput(InputSlot.Itemstack);

    /// <summary>The wood species of the loaded input (oak … <c>aged</c>, modded woods included), or null when empty /
    /// unrecognised. Binds the sawn product to the parent tree in E06; reuses
    /// <see cref="BlockEntitySawhorse.GetWoodSpecies"/>, domain-agnostic, no whitelist.</summary>
    public string? InputWoodSpecies => BlockEntitySawhorse.GetWoodSpecies(InputSlot.Itemstack);

    /// <summary>
    /// The FULL product batch a completed pass would deposit for the CURRENTLY loaded input log — one whole-batch
    /// deposit (unlike the sawhorse's 4-stage scatter): the <c>plank-{wood}</c> item × <c>Config.PlanksPerLog</c> (24)
    /// for a log or log section, or the <c>supportbeam-{wood}</c> block × <c>Config.BeamsPerDebarkedLog</c> (6) for a debarked log.
    /// Null when the input is empty / unrecognised / its product can't be resolved. Reuses the hand sawhorse's
    /// classifiers (<see cref="BlockEntitySawhorse.GetSawInput"/>/<see cref="BlockEntitySawhorse.GetWoodSpecies"/>),
    /// the ONE domain→game resolver (<see cref="BlockEntitySawhorse.ResolveProductItem"/>/<see cref="BlockEntitySawhorse.ResolveProductBlock"/>)
    /// and the shared per-log counts — never a second copy, never a hardcoded <c>game:</c> (mod-independence #2). E06's
    /// <see cref="CanSaw"/> gates on it being non-null, and pass completion ejects it in one go (<see cref="EjectBatch"/>).
    /// </summary>
    internal (ItemStack product, int count)? GetPassBatch()
    {
        ItemStack? log = InputSlot.Itemstack;
        SawInput kind = BlockEntitySawhorse.GetSawInput(log);
        string? wood = BlockEntitySawhorse.GetWoodSpecies(log);
        if (kind == SawInput.None || wood == null) return null;

        string domain = log!.Block!.Code.Domain; // Block non-null once kind != None (GetSawInput requires it)
        // Product by the machine's cut mode (SawModePlan, cycled with Shift+RMB), not the input: Auto keeps the input
        // default (log/section → plank, debarked → beam), a chosen mode forces it — same rule as the hand sawhorse.
        if (BlockEntitySawhorse.ProductIsPlanks(kind, sawMode))
        {
            Item? plank = BlockEntitySawhorse.ResolveProductItem(Api.World, domain, "plank-" + wood);
            if (plank == null) return null;
            // SawmillPlanksPerLogEffective (E04/T04.05): the automated sawmill's own plank yield, -1 = inherit the
            // manual PlanksPerLog (identical out of the box). The hand sawhorses keep reading the manual field.
            return (new ItemStack(plank), Config.SawmillPlanksPerLogEffective < 1 ? 1 : Config.SawmillPlanksPerLogEffective);
        }

        Block? beam = BlockEntitySawhorse.ResolveProductBlock(Api.World, domain, "supportbeam-" + wood);
        if (beam == null) return null;
        return (new ItemStack(beam), Config.SawmillBeamsPerDebarkedLogEffective < 1 ? 1 : Config.SawmillBeamsPerDebarkedLogEffective);
    }
}
