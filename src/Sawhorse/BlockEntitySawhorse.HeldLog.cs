using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

public partial class BlockEntitySawhorse
{
    // ──────────────────── E05: held log — place / take / discard (T05.02) ────────────────────
    //
    // The log lives ONLY on a completed pair, stored as the master's nested stack (E04 set up heldLog +
    // sawProgress + the progress-aware ReleaseHeldLog). All three gestures route through GetMaster() so clicking
    // EITHER cell hits the same state (T04.03). Server-authoritative, mirroring BlockEntityChoppingBlock's
    // TryPut/TryTake: the client returns true to swallow the click and reflects the heldLog the server syncs
    // back via FromTreeAttributes. Progress drives the Ctrl+RMB split here through the recovery grace (LogRecoverable): a log
    // still under the grace on the rip AND the strip is taken back WHOLE, one past the grace on either is scrapped with no drop.

    /// <summary>Whether the pair this cell belongs to currently holds a log — read from the master, so either
    /// cell answers the same (T04.03). Always false for a single/half-built trestle, which can't hold one.</summary>
    public bool HasLog => GetMaster().heldLog != null;

    /// <summary>Whether the held log has already been partially SAWN (<see cref="sawProgress"/> &gt; 0) — read from the
    /// master, so either cell answers the same. False with no log and on a single/half-built trestle. This is the rip
    /// half of the per-log MODE LOCK (the strip half is <see cref="BarkStarted"/>): once either is true the other
    /// process is refused on that log. The Ctrl+RMB take/discard split and its hint instead key on
    /// <see cref="LogRecoverable"/> (the recovery grace), so a barely-begun log can still be handed back whole.</summary>
    public bool SawStarted => GetMaster().sawProgress > 0f;

    /// <summary>Whether the held log can be handed back WHOLE on a Ctrl+RMB remove or a break/teardown: an UNTOUCHED log
    /// always can, and a begun one can while NEITHER the rip (<see cref="sawProgress"/>) nor the strip
    /// (<see cref="barkProgress"/>) has passed the configurable grace
    /// (<see cref="ImmersiveWoodworkingConfig.LogRecoverThreshold"/>, default 25%) of its full duration
    /// (<see cref="SawSeconds"/> / <see cref="DebarkSeconds"/>). Below that grace the intact log is returned; at/above it a
    /// begun log is scrapped with no drop — so a genuine mid-cut abandon still can't recover the log for free, while a
    /// barely-touched one (or a misclick) is forgiven. The untouched fast-path keeps a freshly-loaded log recoverable
    /// even if the grace is configured to 0. Read from the master, so either cell answers the same. True with no log /
    /// on a single trestle (nothing begun).</summary>
    internal bool LogRecoverable
    {
        get
        {
            BlockEntitySawhorse m = GetMaster();
            if (m.sawProgress <= 0f && m.barkProgress <= 0f) return true; // untouched → always returned whole (even at grace 0)

            float grace = Config.LogRecoverThreshold;             // live, synced; Sanitize keeps it in [0, 1]
            float sawSeconds = SawSeconds, barkSeconds = DebarkSeconds;    // live, synced config; Sanitize keeps both ≥ 0.1
            float sawFrac  = sawSeconds  > 0f ? m.sawProgress  / sawSeconds  : 1f;
            float barkFrac = barkSeconds > 0f ? m.barkProgress / barkSeconds : 1f;
            return sawFrac < grace && barkFrac < grace;
        }
    }

    /// <summary>The pit saw tool (<c>pitsaw-{metal}</c>) — the mod's purpose-built rip saw, effective by default
    /// (<see cref="SawIsEffective"/>) regardless of <see cref="ImmersiveWoodworkingConfig.AllowVanillaSaw"/> — unless
    /// <see cref="ImmersiveWoodworkingConfig.RemovePitSaw"/> removes it entirely, after which only ordinary saws rip.
    /// Identified by code part rather than tool type so the privileged-rip-saw status is OPT-IN: any mod can mark its
    /// own tool as the pit saw by naming it <c>pitsaw-*</c> (domain-agnostic). Held, it turns a plain right-click into
    /// sawing (E06) rather than a take.</summary>
    /// <remarks>Read through <see cref="CodeParts"/>, not <c>FirstCodePart</c> (AuditFixPlan T05.03): the rip gate
    /// folds this in and is re-asked on every tick of a hold.</remarks>
    internal static bool IsPitSaw(ItemStack? stack) => CodeParts.Is(stack?.Item?.Code, 0, "pitsaw");

    /// <summary>Whether <paramref name="stack"/> is a saw at all. MOD-INDEPENDENT: it keys on the tool TYPE
    /// (<see cref="EnumTool.Saw"/> — i.e. the item's <c>tool: "saw"</c>), so the vanilla saw, the pit saw, AND any
    /// saw added by another mod all qualify, no matter how they're coded. (A <c>pitsaw-*</c> that somehow omits the
    /// tool type is folded in defensively so an effective saw is always a saw.) This is NOT the rip gate on its own —
    /// it's the "saw-ness" building block <see cref="SawIsEffective"/> folds together with the
    /// <see cref="ImmersiveWoodworkingConfig.AllowVanillaSaw"/> flag. Static: no config needed.</summary>
    internal static bool IsSawTool(ItemStack? stack) => stack?.Item?.Tool == EnumTool.Saw || IsPitSaw(stack);

    /// <summary>Whether holding <paramref name="stack"/> rips a log here — the single gate that drives EVERYTHING
    /// (start, animation, particles, sound, progress; see <see cref="CanSawHere"/>). In the normal tier: true for the
    /// pit saw ALWAYS (<see cref="IsPitSaw"/>), and for any OTHER saw (<see cref="IsSawTool"/>) only when
    /// <see cref="ImmersiveWoodworkingConfig.AllowVanillaSaw"/> is enabled — so an ordinary saw while that flag is off
    /// is rejected outright (no motion, just a "need a pit saw" nudge), exactly like a non-saw. When
    /// <see cref="ImmersiveWoodworkingConfig.RemovePitSaw"/> is on the gate INVERTS: the pit saw is removed and no
    /// longer rips, while an ordinary saw becomes the only rip tool and works REGARDLESS of
    /// <see cref="ImmersiveWoodworkingConfig.AllowVanillaSaw"/> (otherwise the station would have no valid tool at
    /// all). Because <see cref="IsSawTool"/> is type-based, the flags govern EVERY non-pit saw (vanilla or modded),
    /// not just the literal vanilla one. (How FAST an allowed saw rips is the separate <see cref="SawSpeedFor"/>.)
    /// Instance method (not static like <see cref="IsSawTool"/>) because it reads the live, server-synced
    /// <see cref="Config"/>; call on the master, where every saw gesture already routes.</summary>
    internal bool SawIsEffective(ItemStack? stack)
    {
        // RemovePitSaw removes the pit-saw tier: the pit saw no longer rips, and an ORDINARY saw becomes the only
        // rip tool — so it works regardless of AllowVanillaSaw (else the station would accept no tool at all).
        if (Config.RemovePitSaw) return !IsPitSaw(stack) && IsSawTool(stack);
        return IsPitSaw(stack) || (Config.AllowVanillaSaw && IsSawTool(stack));
    }

    /// <summary>The sawing-speed factor for <paramref name="stack"/>, scaling the progress it accrues per tick: the
    /// pit saw rips at its SHARPNESS-scaled speed (<see cref="SharpnessSpeedFactor"/> — full speed 1.0 when sharp,
    /// down to <see cref="ImmersiveWoodworkingConfig.SharpnessSpeedFloor"/> when blunt; always 1.0 WITHOUT Toolsmith,
    /// so the base behaviour is unchanged), an ordinary saw at <see cref="ImmersiveWoodworkingConfig.VanillaSawSpeedMultiplier"/>
    /// (default 0.5 — slower, the worse tool, and NOT affected by sharpness). Only meaningful for an EFFECTIVE saw
    /// (<see cref="SawIsEffective"/>); callers gate on that first, so a non-pit saw here is always an allowed ordinary
    /// saw. When <see cref="ImmersiveWoodworkingConfig.RemovePitSaw"/> is on the pit saw never passes that gate, so the
    /// only stacks that reach here are ordinary saws — which keep the <see cref="ImmersiveWoodworkingConfig.VanillaSawSpeedMultiplier"/>
    /// penalty (the rip stays slower even though the pit saw is gone). On TOP of either branch the metal-tier factor
    /// (<see cref="ToolTierSpeedFactor"/>, E02) multiplies BOTH uniformly, so a copper saw/pit saw rips slower than a
    /// steel one (1.0 when the feature is off or the saw has no wood mining speed). Instance method: reads the live,
    /// server-synced <see cref="Config"/>.</summary>
    float SawSpeedFor(ItemStack? stack) => (IsPitSaw(stack) ? SharpnessSpeedFactor(stack) : Config.VanillaSawSpeedMultiplier) * ToolTierSpeedFactor(stack);

    /// <summary>Linear sharpness→speed factor for a Toolsmith-tinkered tool (pit saw / bark spud), delegating to the
    /// shared <see cref="ImmersiveWoodworkingModSystem.SharpnessSpeedFactor"/> — same lazy <see cref="modSystem"/> resolve
    /// as <see cref="Config"/> / <see cref="ToolTierSpeedFactor"/> (the attribute keys + linear floor…1.0 model now live
    /// once on the mod system, shared with the chopping-block strike). Full speed 1.0 when sharp (or WITHOUT Toolsmith),
    /// down to <see cref="ImmersiveWoodworkingConfig.SharpnessSpeedFloor"/> when blunt; the ordinary saw, the axe+hammer
    /// debark tier and the knife never route through here (user decision). Co-op is unaffected: each side passes its OWN
    /// tool, so each end's progress scales by that end's tool's sharpness.</summary>
    float SharpnessSpeedFactor(ItemStack? stack) => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).SharpnessSpeedFactor(stack);

    /// <summary>Metal-tier speed factor for <paramref name="stack"/> (E02) — a stone/copper tool works proportionally
    /// slower than a steel one, from the tool's OWN wood mining speed relative to a steel-grade anchor. A flat 1.0 when
    /// the feature (<see cref="ImmersiveWoodworkingConfig.ScaleWorkSpeedByToolTier"/>) is off, and 1.0 for a tool that is
    /// not a woodworking tool at all — no wood mining speed AND a family the index never saw, i.e. the knife and the
    /// plain hammer, which is what keeps those debark tiers FLAT. The other two "we can't tell" shapes no longer answer
    /// 1.0: a third-party family with an honest wood speed is measured against the matching vanilla anchor, and a KNOWN
    /// family's speed-less variant drops to that family's weakest tier (AuditFixPlan T03.02). Thin instance wrapper over
    /// the mod system's shared family index (built once on load, read on both sides) so the rip
    /// (<see cref="SawSpeedFor"/>) and the strip (<see cref="BarkSpeedFor"/>) route through ONE definition; the master
    /// resolves the mod system the same lazy way as <see cref="Config"/>. See
    /// <see cref="ImmersiveWoodworkingModSystem.ToolTierSpeedFactor"/>.</summary>
    float ToolTierSpeedFactor(ItemStack? stack) => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).ToolTierSpeedFactor(stack);

    /// <summary>Metal-tier CHANCE bonus fraction [0, 1] for <paramref name="stack"/> — 0 at its family's weakest metal, 1 at
    /// steel (BarkPlan T02.05: the bark spud's per-metal drop bonus). Thin instance wrapper over the mod system's shared
    /// family index (the CHANCE counterpart to <see cref="ToolTierSpeedFactor"/>), resolved the same lazy way as
    /// <see cref="Config"/>. Used by <see cref="BarkChanceMultFor"/>. See
    /// <see cref="ImmersiveWoodworkingModSystem.MetalTierBonusFraction"/>.</summary>
    float MetalTierBonusFraction(ItemStack? stack) => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).MetalTierBonusFraction(stack);

    /// <summary>
    /// Plain RMB with a log / debarked log in hand: lay it on a completed, EMPTY pair (one stack). Rejects a
    /// single or half-built trestle (only a pair holds a log), an already-occupied pair, any non-sawable held item,
    /// and — since T03.03 — a sawable one this pair could do NOTHING with (<see cref="CanProcessInput"/>: neither its
    /// plank/beam nor its debarked form exists), returning false so the caller falls through to the build step. Routes to the master, so either cell
    /// works. Server-authoritative: the client returns true to swallow the click and waits for the synced state;
    /// the server moves one log out of the held slot into <see cref="heldLog"/>. (Plain RMB, not Shift+RMB —
    /// Shift+RMB is left to vanilla so a held log places adjacent as usual.)
    /// </summary>
    public bool TryPutLog(ItemSlot fromSlot, IPlayer byPlayer)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) return master.TryPutLog(fromSlot, byPlayer);

        if (!inPair || !isMaster || heldLog != null) return false; // only an empty pair's master holds a log
        if (fromSlot?.Itemstack == null || !IsSawableInput(fromSlot.Itemstack)) return false; // not a sawable input
        // The FOURTH door of the "the product must resolve" rule, which the three others already had (AuditFixPlan
        // T03.03 / F-L1-01): a log this pair could neither rip nor strip is turned away HERE rather than laid, worked
        // and silently eaten. Deliberately the LOOSE form (CanProcessInput = rip OR strip), so a wood with a
        // debarkedlog but no plank still lays down for the strip; the per-action gates refuse the rip on their own.
        if (!CanProcessInput(Api.World, fromSlot.Itemstack)) return false; // nothing this pair could ever make from it

        if (Api.Side != EnumAppSide.Server) return true; // client swallows; the server performs the move

        heldLog = fromSlot.TakeOut(1);
        fromSlot.MarkDirty();
        ResetSawState();

        Api.World.PlaySoundAt(BuildSound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
        MarkDirty(true); // persist + sync → the client (re)builds the log mesh in T05.03
        return true;
    }

    /// <summary>
    /// Ctrl+RMB on a log still WITHIN the recovery grace (<see cref="LogRecoverable"/> — under the grace on the rip AND the
    /// strip): pop it back to the player's inventory, or the world if that's full. Returns false once the log is past the grace
    /// into either process so the caller scraps it instead (<see cref="DiscardLog"/>) — Ctrl+RMB is one "remove the
    /// log" gesture whose outcome depends on how far work has gone. Routes to the master. Server-authoritative; returns
    /// true (swallow) whenever a log is present and still recoverable, on both sides.
    /// </summary>
    public bool TryTakeLog(IPlayer byPlayer)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) return master.TryTakeLog(byPlayer);

        if (heldLog == null || !LogRecoverable) return false; // empty, or past the recovery grace (rip OR strip) → scrapped instead, not taken

        if (Api.Side == EnumAppSide.Server)
        {
            ItemStack stack = heldLog;
            heldLog = null;
            ResetSawState();

            if (!byPlayer.InventoryManager.TryGiveItemstack(stack))
            {
                Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 1.0, 0.5));
            }

            Api.World.PlaySoundAt(BuildSound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
            MarkDirty(true);
        }
        return true;
    }

    /// <summary>
    /// Ctrl+RMB on a log past the grace (NOT <see cref="LogRecoverable"/> — sawn OR debarked past the grace):
    /// scrap it with NO drop — the player abandons the partial work. The caller reaches this only after
    /// <see cref="TryTakeLog"/> declined (i.e. the log is past the grace, not recoverable); on a still-recoverable or
    /// absent log it returns false and does nothing. Routes to the master. Server-authoritative; returns true to
    /// swallow whenever there IS a non-recoverable log; a still-recoverable one is taken back instead (TryTakeLog).
    /// </summary>
    public bool DiscardLog(IPlayer byPlayer)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) return master.DiscardLog(byPlayer);

        if (heldLog == null || LogRecoverable) return false; // nothing to discard: empty, or still within the recovery grace → taken back whole instead

        if (Api.Side == EnumAppSide.Server)
        {
            heldLog = null;
            ResetSawState();
            MarkDirty(true);
        }
        return true;
    }
}
