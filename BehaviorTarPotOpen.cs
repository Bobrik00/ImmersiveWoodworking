using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// Held-item "open the fired tar pot" gesture (BarkPlan E04, T04.05). A PLAIN right-click on a
/// <c>tarpot-{color}-{perfcolor}-fired</c> in the active hand INSTANTLY pops it open (no hold, no work timer) and you
/// receive the batch: <c>barktar</c> ×<see cref="ImmersiveWoodworkingConfig.TarYield"/> (the hard tar) + a DIRTY receiver
/// <c>game:dirtyclaypot-{color}-empty</c> + the reusable <c>perforatedpot-{perfcolor}-fired</c> back. The ceramic is not
/// consumed — only the birch (now tar) and the clay daub are — so the perforated pot returns clean and the receiver
/// returns dirty (clean it in water like any pot after glue-boiling, decision D8). BOTH colours ride the CODE, not
/// attributes, because the pit kiln stripped the item's attributes when it fired it (D11/D23) — so we read them back off
/// <c>tarpot-{color}-{perfcolor}-fired</c> and hand each vessel back in its own colour (receiver mirroring vanilla
/// claypot/dirtyclaypot's ten, perforated pot its clay-formed blue/fire/red).
///
/// INSTANT, not a hold (2026-07-28): prying the sealed pot open is a single discrete pop, so it is finalized on the
/// initial press in <see cref="OnHeldInteractStart"/> — exactly like vanilla one-shot right-clicks that do their
/// authoritative work in Start (<c>ItemJournalEntry</c> spends the stack + plays a sound in Start; <c>ItemFlute</c>
/// dual-calls its sound there). This replaces the earlier 2.5s work-timer draft (finalize-in-Stop, T04.05) — the hold
/// and its generic <c>game:sounds/player/build</c> place/thunk are both gone (user decision 2026-07-28). One click opens
/// exactly ONE pot: the open is gated on <c>firstEvent</c>, so holding right-click does NOT chain-open the stack.
///
/// WHY a coded gesture and not a grid recipe: a grid recipe yields one <c>Output</c> + one <c>ReturnedStack</c> per
/// ingredient = at most two result stacks from a single input, but opening must return THREE (tar + two vessels). The
/// user chose the tactile gesture (it also fits the mod's whole hands-on idiom). Registered <c>iw-tarpotopen</c> and
/// attached via <c>behaviorsByType "tarpot-*-fired"</c> in <c>itemtypes/tarpot.json</c> — the SEALED variant never
/// carries it, so only a FIRED pot opens.
///
/// The two subtleties the mod's held-lifecycle pattern (cloned from <see cref="BlockBehaviorFlotsamBreak"/>) exists to
/// solve still bite even though the action is now instant:
///   • CLAIM every QUALIFYING call (Start AND Step) so a <c>firstEvent=false</c> re-entry while right-click is still
///     held can never leak into the vanilla place-as-ground-storage path. Only the OPEN is gated on <c>firstEvent</c>;
///     the CLAIM is not. "Qualifying" is the whole of <see cref="CanOpenInHand"/>, and that is narrower than it once
///     read: an entity under the crosshair is NOT ours (AuditFixPlan T07.03, F-L8-03). The old wording here — "the
///     click is fully ours from press to release" — was reasoning about the ground-storage path only, but the claim as
///     written also covered the dispatcher's no-block-selected branch, where it swallowed the player's click on an
///     entity along with the <c>EntityInteraction</c> packet.
///   • RE-GATE Step/Stop, not just Start. A Shift+RMB ground-store PLACE also returns <c>handHandling=PreventDefault</c>
///     (BehaviorGroundStorable), and the engine drives Step/Stop on EVERY behavior of the item with no owner-scoping
///     (Collectible.cs) — so without the same <see cref="CanOpenInHand"/> re-check in Step the Shift path would be held
///     open here. Step keeps the interaction claimed while a fired pot is in hand and releases the instant the slot is
///     no longer one (opened/switched/emptied); there is no timer left to run.
///
/// Server-authoritative: only the server spends the pot and gives the outputs. The lid pop is a dual-call — the holder's
/// client predicts it locally on the press (snappy) and the server broadcasts it to everyone else — using the canonical
/// vanilla ceramic-vessel open sound <c>game:sounds/block/vesselopen</c> (the same clip <c>storagevessel</c> plays when
/// its lid comes off), not the old generic build thunk.
/// </summary>
public class BehaviorTarPotOpen : CollectibleBehavior
{
    public BehaviorTarPotOpen(CollectibleObject collObj) : base(collObj) { }

    // FirstCodePart shared by every tar-pot item variant — the "still our fired pot?" re-check. Guards against the
    // active slot changing out from under a held click.
    const string TarPotPart = "tarpot";
    const string FiredState = "fired";

    // Fixed batch: a full pot always yields this many tar (D11 — the pit kiln clones the JSON SmeltedStack and drops
    // attributes, so a fixed opening yield is the clean path; the wet-birch penalty is paid at PACKING, T04.09, not here).
    // Raised 4 -> 8 at the tar-pricing pass (playtest 2026-07-28, Set B): a full pot (32 dry birch) now yields 8 tar =
    // 4 dry birch/tar (was 8). In LOGS that is ~4 birch logs/tar only at the TOP strip tier: barkdrops.json gives birch a
    // table chance of 1.0, but the tool tier MULTIPLIES it before the roll (spud 100%, axe+hammer 75%, knife 50% —
    // Config.BarkChanceMult*), so a knife-only player pays ~8 logs/tar, and either figure doubles on wet bark. (This
    // comment quoted the spud's number as everyone's until AuditFixPlan T08.07.)
    // barktar max-stacks at 32 and GiveOrDrop splits full stacks, so 8 is safe. A BarkPlan scalar => CONFIG since T08.01
    // (Config.TarYield), read live off the server-synced config in OpenPot.

    // The lid pop: the canonical vanilla ceramic-vessel open clip (storagevessel's openSound). Replaces the old generic
    // game:sounds/player/build place/thunk — which was literally the crock's GroundStorable placeRemoveSound, i.e. a
    // "set it down" noise, not an "open it" one. Dual-called (client predicts, server broadcasts to others).
    static readonly AssetLocation LidSound = new AssetLocation("game:sounds/block/vesselopen");

    // Sound attenuation range (blocks) — the pot open is a small, local pop, not a broadcast event. Matches the old thunk.
    const float SoundRange = 12f;

    /// <summary>Only a PLAIN right-click opens a held fired pot. Shift is the pot's vanilla <c>GroundStorable</c> gesture
    /// (lay a pile down to build a kiln over, or just store it), Ctrl falls through — both left untouched. The active
    /// slot holds a tar pot by construction (this behavior only rides <c>tarpot-*-fired</c>).
    ///
    /// <para>An <paramref name="entitySel"/> disqualifies the click as well (AuditFixPlan T07.03, F-L8-03). A ray that
    /// hits an entity nearer than any block leaves <c>GameMain.RayTraceForSelection</c> with a null BLOCK selection and
    /// only the entity one, and <c>SystemMouseInWorldInteractions.HandleMouseInteractionsNoBlockSelected</c> then asks
    /// the held item FIRST — <c>TryBeginUseActiveSlotItem(null, EntitySelection)</c>, whose <c>true</c> short-circuits
    /// both the local <c>Entity.OnInteract</c> and the <c>EntityInteraction</c> packet. Claiming unconditionally
    /// therefore ate every right-click on an entity made while carrying a fired pot, and worse: the open is INSTANT, so
    /// the same press also popped the pot. Letting the click go costs nothing on the ground-storage side the claim
    /// exists to protect — with an entity selected <c>blockSel</c> is null, and vanilla placement has nowhere to put
    /// the pot anyway. The same test already guards <c>ItemHalfLog</c> and <c>BehaviorSawhorseBuild</c>.</para></summary>
    static bool CanOpenInHand(EntityAgent? byEntity, EntitySelection? entitySel)
    {
        if (entitySel != null) return false;
        var c = byEntity?.Controls;
        return c != null && !c.ShiftKey && !c.CtrlKey;
    }

    // Read through CodeParts, not the collectible's accessors (AuditFixPlan T05.03): OnHeldInteractStep re-asks this
    // on every frame the pot is held, and each engine call would split the whole code to compare one part.
    static bool IsFiredTarPot(ItemSlot? slot)
    {
        AssetLocation? code = slot?.Itemstack?.Collectible?.Code;
        return CodeParts.Is(code, 0, TarPotPart) && CodeParts.IsLast(code, FiredState);
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        // Shift/Ctrl are left at PassThrough so the engine runs vanilla GroundStorable placement — that is how the player
        // lays the fired pot down. An entity under the crosshair passes through for a different reason: that click
        // belongs to the entity (see CanOpenInHand). A safety re-check on the slot in case another behavior mutated it.
        if (!CanOpenInHand(byEntity, entitySel) || !IsFiredTarPot(slot)) return;

        // INSTANT open on the initial press. Gated on firstEvent so ONE click opens exactly one pot — a firstEvent=false
        // re-entry mid-hold (below) only re-claims, it does NOT chain-open the stack. Vanilla one-shot right-clicks do
        // their authoritative work in Start the same way (ItemJournalEntry consumes + sounds here; ItemFlute dual-calls).
        if (firstEvent)
        {
            if (byEntity.World.Side == EnumAppSide.Server)
                OpenPot(byEntity, slot);   // server-authoritative: spend the pot, give the batch, broadcast the lid pop to others
            else
                byEntity.World.PlaySoundAt(LidSound, byEntity, (byEntity as EntityPlayer)?.Player, true, SoundRange);   // client-local predicted pop
        }

        // Claim UNCONDITIONALLY (not just firstEvent) so a firstEvent=false re-entry mid-hold can never fall through to
        // vanilla ground-storage placement.
        handHandling = EnumHandHandling.PreventDefault;   // no vanilla placement, no default anim; still calls Step/Stop + notifies server
        handling = EnumHandling.PreventSubsequent;         // we fully own this held interaction
    }

    public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        // The open is INSTANT (done in Start). Step has no timer left to run — it only keeps the interaction CLAIMED
        // while a fired pot is still in hand, so a firstEvent=false re-entry can never fall through to vanilla
        // GroundStorable placement, and RELEASES it the moment the slot is no longer our fired pot (just opened,
        // switched, or emptied). The CanOpenInHand re-check keeps a Shift+RMB ground-store HOLD (which also returns
        // handHandling=PreventDefault and so drives Step on EVERY behavior of the item, Collectible.cs) from being held
        // open here — mirrors the same gate at Start, entity term included: both sites read the SAME predicate, so a
        // hold Start refuses can never be kept alive here (and vice versa).
        if (!CanOpenInHand(byEntity, entitySel) || !IsFiredTarPot(slot)) return false;
        handling = EnumHandling.PreventSubsequent;
        return true;   // stay claimed until release / slot change — no hold-to-progress, the pot already opened on the press
    }

    public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason, ref EnumHandling handling)
    {
        return true; // allow the cancel (releasing right-click ends the hold) — nothing in flight to undo, the open was instant
    }

    public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot, ref EnumHandling handling)
    {
        // The in-hand "right-click to open" hint, shown while a fired pot is selected. Added to (not replacing) vanilla
        // held hints — handling left PassThrough. Only the FIRED variant carries this behavior, so the hint never shows
        // on a sealed pot.
        return heldOpenInteraction ??= new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "immersivewoodworking:blockhelp-tarpot-open",
                MouseButton = EnumMouseButton.Right
                // Itemstacks left null — a click gesture on the held pot, no required second stack.
            }
        };
    }

    WorldInteraction[]? heldOpenInteraction;

    /// <summary>Open ONE held fired pot: read BOTH vessels' colours off the CODE, spend the pot, and give the batch
    /// (<see cref="ImmersiveWoodworkingConfig.TarYield"/> barktar + the dirty receiver in its colour + the perforated pot in ITS colour) via
    /// TryGiveItemstack, dropping whatever does NOT fit as an entity at the player's feet (never lost — see GiveOrDrop).
    /// Server-only; re-checks the slot so it can't spend on a stale one.</summary>
    void OpenPot(EntityAgent byEntity, ItemSlot slot)
    {
        IWorldAccessor world = byEntity.World;
        int tarYield = world.Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().Config.TarYield; // live, server-synced (T08.01)
        CollectibleObject? coll = slot.Itemstack?.Collectible;
        if (coll == null || coll.FirstCodePart() != TarPotPart || coll.LastCodePart() != FiredState) return; // slot changed out from under us

        // BOTH colours ride the code: tarpot-{color}-{perfcolor}-fired -> parts [tarpot, color, perfcolor, fired].
        // FirstCodePart(1) is the RECEIVER's colour, FirstCodePart(2) the PERFORATED pot's. Fall back to blue for an
        // unexpected code so a foreign/edited variant still returns SOME pot rather than null.
        string color = coll.FirstCodePart(1) ?? "blue";
        string perfColor = coll.FirstCodePart(2) ?? "blue";

        slot.TakeOut(1);
        slot.MarkDirty();

        GiveOrDrop(world, byEntity, new AssetLocation("immersivewoodworking", "barktar"), isBlock: false, tarYield);
        GiveOrDrop(world, byEntity, new AssetLocation("game", "dirtyclaypot-" + color + "-empty"), isBlock: true, 1,
                   fallback: new AssetLocation("game", "dirtyclaypot-blue-empty"));
        // Both vessels come back in their OWN colour. The receiver: game:dirtyclaypot covers all ten vanilla colours, so
        // an exotic receiver round-trips exactly (the point of widening the `color` axis back to ten on 2026-07-29). The
        // perforated pot: its OWN colour now rides the code too (the `perfcolor` axis, added 2026-07-29), so the pot you
        // packed is the pot you get back — you no longer feed a red pot into a brown receiver and get a brown one out.
        // This retires the D39 sacrifice ("sealing forgets the perforated pot's colour"): sealing records both colours.
        // The fallback stays only for a foreign/edited code with no `perfcolor` part.
        GiveOrDrop(world, byEntity, new AssetLocation("immersivewoodworking", "perforatedpot-" + perfColor + "-fired"), isBlock: true, 1,
                   fallback: new AssetLocation("immersivewoodworking", "perforatedpot-blue-fired"));

        // Lid pop broadcast to everyone EXCEPT the holder — their client already predicted it on the press (dualCall,
        // see OnHeldInteractStart). Canonical vanilla ceramic-vessel open clip.
        world.PlaySoundAt(LidSound, byEntity, (byEntity as EntityPlayer)?.Player, true, SoundRange);
    }

    /// <summary>Resolve <paramref name="code"/> (item or block, with an optional <paramref name="fallback"/> code) into a
    /// stack of <paramref name="count"/> and put it in the player's inventory, dropping at their feet if it won't fit.
    /// A no-op if the code (and fallback) resolve to nothing, logged so a missing asset is diagnosable.</summary>
    void GiveOrDrop(IWorldAccessor world, EntityAgent byEntity, AssetLocation code, bool isBlock, int count, AssetLocation? fallback = null)
    {
        if (count <= 0) return;

        CollectibleObject? coll = isBlock
            ? (CollectibleObject?)world.GetBlock(code) ?? (fallback != null ? world.GetBlock(fallback) : null)
            : (CollectibleObject?)world.GetItem(code) ?? (fallback != null ? world.GetItem(fallback) : null);

        if (coll == null)
        {
            world.Logger.Warning("[immersivewoodworking] Tar pot open: could not resolve {0} - output skipped.", code);
            return;
        }

        // Emit in FULL stacks: a count over the item's max stack size (E08/T08.01 made TarYield configurable, so a
        // server can set it above barktar's max) is split across several stacks rather than silently truncated into one.
        IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;
        int max = coll.MaxStackSize < 1 ? 1 : coll.MaxStackSize;
        while (count > 0)
        {
            int n = count < max ? count : max;
            count -= n;
            var stack = new ItemStack(coll, n);

            // Test the REMAINDER, never the return value. TryGiveItemstack wraps the caller's stack in a DummySlot and
            // returns "at least ONE item moved" (TryTransferAway returns null only when nothing moved at all) — it does
            // NOT mean the whole stack fit. A pack with room for 2 of the 8 tar returns true, and the old `!= true`
            // check then skipped the drop and let the other 6 cease to exist. TryPutInto decrements the caller's stack
            // in place, so after the call StackSize IS the remainder: 0 when it all went in, >0 when it did not. A null
            // byPlayer (non-player opener) leaves the stack untouched and falls through to the drop, as before.
            // Verified against Vintagestory.Common.PlayerInventoryManager, VintagestoryLib 1.22.3.
            byPlayer?.InventoryManager.TryGiveItemstack(stack, true);
            if (stack.StackSize > 0) world.SpawnItemEntity(stack, byEntity.Pos.XYZ.Add(0, 0.5, 0));
        }
    }
}
