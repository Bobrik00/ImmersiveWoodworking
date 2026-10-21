using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

public partial class BlockEntitySawhorse : IMultiblockOffset
{
    // ───────────────────────────────── E04: multiblock pairing ─────────────────────────────────

    /// <summary>
    /// T04.01/T04.04 — pair this freshly-Complete, not-yet-paired trestle with an orthogonal neighbour (N/E/S/W,
    /// same Y) that is itself Complete and free. Diagonals are never scanned, so they can't pair. A trestle
    /// already in a pair is excluded as a candidate (and, via the early return, as an initiator), which keeps a
    /// formed pair locked and lets a row of four resolve into two independent pairs. When two free neighbours
    /// qualify, the lexicographically smaller position wins the tie — this makes a single initiator's CHOICE
    /// deterministic. (For an ODD run of 3+ Complete trestles, WHICH two pair still depends on the order the cells
    /// initiate on load; both resulting pairings are valid — a leftover single just stays unpaired.) Server-side.
    /// </summary>
    public void TryFormPair()
    {
        if (Api.Side != EnumAppSide.Server) return;
        if (stage != SawhorseStage.Complete || inPair) return;

        BlockEntitySawhorse? best = null;
        foreach (BlockFacing face in BlockFacing.HORIZONTALS) // N/E/S/W only — orthogonal, never diagonal
        {
            BlockPos npos = Pos.AddCopy(face);
            if (Api.World.BlockAccessor.GetBlockEntity(npos) is BlockEntitySawhorse nb
                && nb.stage == SawhorseStage.Complete && !nb.inPair)
            {
                if (best == null || IsLexLess(nb.Pos, best.Pos)) best = nb;
            }
        }

        if (best != null) FormPairWith(best);
    }

    /// <summary>Bind this trestle and <paramref name="partner"/> into a pair: derive the shared axis from their
    /// relative position (differing X → east-west, else north-south) and the deterministic master (the
    /// lexicographically smaller position), then write the pair state into both BEs. Server-side.</summary>
    void FormPairWith(BlockEntitySawhorse partner)
    {
        EnumAxis axis = partner.Pos.X != Pos.X ? EnumAxis.X : EnumAxis.Z;
        bool iAmMaster = IsLexLess(Pos, partner.Pos);

        SetPair(partner.Pos, iAmMaster, axis);
        partner.SetPair(Pos, !iAmMaster, axis);
    }

    /// <summary>Write this BE's pair state and persist+sync it (the client re-meshes onto the shared axis from
    /// the synced pairAxis). Called on both members of a pair by <see cref="FormPairWith"/>.</summary>
    void SetPair(BlockPos partner, bool master, EnumAxis axis)
    {
        inPair = true;
        partnerPos = partner;
        isMaster = master;
        pairAxis = axis;
        MarkDirty(true);
    }

    /// <summary>
    /// The pair member that owns the log/progress (T04.03): the master returns itself; a slave delegates to the
    /// resolved master, falling back to itself if the partner can't currently be resolved (e.g. its chunk isn't
    /// loaded). E05/E06 route every log placement / sawing / take / discard through this so interacting with
    /// EITHER cell hits the same master — "взаимодействие с любой из двух клеток даёт одинаковый эффект".
    /// <para>It NEVER jumps to a partner that is not a master itself (AuditFixPlan T07.01, F-L1-03). The pairing code
    /// cannot produce two mutual slaves — <see cref="FormPairWith"/> always hands out complementary roles,
    /// <see cref="DissolveToSingle"/> drops <see cref="isMaster"/> together with <see cref="inPair"/>, and both fields
    /// are written unconditionally by <c>ToTreeAttributes</c>, so neither the save nor the network sync can lose the
    /// master — but hand-edited BE data can. Jumping would then be fatal rather than merely wrong: every routed
    /// method re-asks GetMaster on arrival, so <c>master != this → master.X()</c> ping-pongs A→B→A until the stack
    /// ends, and that StackOverflow is neither catchable nor logged, sits in the world and so repeats on every load of
    /// the chunk. Instead the server dissolves BOTH halves (each re-pairs a tick later, this time with a master) and
    /// the client keeps itself and waits for that state to sync — the same split <see cref="GetPartner"/> uses.</para>
    /// </summary>
    public BlockEntitySawhorse GetMaster()
    {
        if (!inPair || isMaster) return this;

        BlockEntitySawhorse? partner = GetPartner();
        if (partner == null) return this;        // partner chunk unloaded, or the half-pair just self-healed to single
        if (partner.isMaster) return partner;    // the ordinary slave → master hop

        if (Api.Side == EnumAppSide.Server)
        {
            DissolveToSingle();          // ours first: that clears inPair, so the call below cannot route back into us
            partner.DissolveToSingle();
        }
        return this;
    }

    /// <summary>Resolve the partner BE, validating the back-reference, and SELF-HEAL a broken half-pair. Returns
    /// null (without healing) when the partner's chunk isn't loaded — we can't tell "gone" from "not yet loaded"
    /// there, so we keep the pair and re-check on the next access. When the chunk IS loaded but the partner is
    /// missing or no longer points back, the pair is genuinely broken: the server dissolves it to single (the
    /// client only reports "no partner" and waits for the authoritative state to sync).</summary>
    BlockEntitySawhorse? GetPartner()
    {
        if (!inPair || partnerPos == null) return null;
        if (Api.World.BlockAccessor.GetChunkAtBlockPos(partnerPos) == null) return null; // can't validate yet

        if (Api.World.BlockAccessor.GetBlockEntity(partnerPos) is BlockEntitySawhorse p
            && p.inPair && p.partnerPos != null && p.partnerPos.Equals(Pos))
        {
            return p;
        }

        if (Api.Side == EnumAppSide.Server) DissolveToSingle();
        return null;
    }

    /// <summary>
    /// Land-claim redirect for the pair (AuditFixPlan T07.02, F-L3-02): a USE test aimed at the SLAVE trestle belongs
    /// to the master cell, because that is where the click actually lands — every log gesture on either cell is
    /// routed through <see cref="GetMaster"/>, so a claim tested on the clicked cell guards the wrong block.
    ///
    /// <para>The engine reads this in exactly ONE place, <c>WorldMap.GetBlockingLandClaimant</c>, and only in its
    /// <c>EnumBlockAccessFlags.Use</c> branch: it asks the clicked block for <see cref="IMultiblockOffset"/> via
    /// <c>Block.GetInterface</c>, which also queries the block entity. Without it a claim boundary running between the
    /// two trestles — the pair is two cells wide, so any border through the machine does it — lets a player standing
    /// at the unclaimed half take the log out of the claimed one, and blocks the owner from their own pair when the
    /// halves fall the other way round.</para>
    ///
    /// <para>Deliberately NOT built on <see cref="GetMaster"/>: that one has side effects (it dissolves a pair whose
    /// partner is not a master, T07.01) and this runs inside a permission check on both sides, where a write is out of
    /// place. So the hop is re-derived here as a pure read, holding to the same three rules — a single or master
    /// trestle answers with itself, the partner must point back at us (<see cref="GetPartner"/>'s validation, minus
    /// its self-heal), and it must be a master itself.</para>
    ///
    /// <para>Returns a COPY and never touches the argument. The interface's own doc comment claims the passed-in
    /// position is modified, but the caller hands over the live <c>BlockSelection.Position</c> it goes on to interact
    /// with, and vanilla's <c>BlockMultiblock.GetControlBlockPos</c> leaves it alone too — mutating it would move the
    /// player's aim. Every fallback answers with the clicked cell, which is exactly where <see cref="GetMaster"/>
    /// falls back to acting, so the cell tested and the cell acted on stay the same one.</para>
    /// </summary>
    public BlockPos GetControlBlockPos(BlockPos pos)
    {
        if (!inPair || isMaster || partnerPos == null || Api == null) return pos;

        if (Api.World.BlockAccessor.GetBlockEntity(partnerPos) is not BlockEntitySawhorse partner
            || !partner.isMaster || !partner.inPair || partner.partnerPos == null || !partner.partnerPos.Equals(Pos))
        {
            return pos;
        }

        return partnerPos.Copy();
    }

    // The last successfully resolved partner BE, and this BE's own "I am torn down" flag (AuditFixPlan T05.05).
    //
    // The reference alone would be unsafe to trust: a chunk unload/reload replaces the partner's BE object, and the
    // dead one keeps answering field reads forever. `gone` closes that — a BE marks ITSELF on the two seams that end
    // its life, so a holder of a stale reference can tell a live partner from a corpse with one field read and go back
    // to the world lookup when it sees one. Written on the main thread, read off it; a bool read is atomic, so the
    // worst case is one extra lookup, never a wrong answer.
    //
    // This exists for the box cache's state signature (BlockEntitySawhorse.Collision.cs): the "does the pair hold a
    // log" read runs on every collision / selection / particle query, and on a SLAVE cell it can never short-circuit
    // (a slave's own heldLog is null by construction), so it went to two chunk-map lookups every time — thousands per
    // second while the mod's own sawdust falls on the pair.
    BlockEntitySawhorse? partnerCache;
    bool gone;

    /// <summary>Mark this BE dead, so a partner holding a cached reference to it stops trusting it. Called from the
    /// two seams that retire a BE — <c>OnBlockRemoved</c> and <c>OnBlockUnloaded</c>.</summary>
    void MarkGone() => gone = true;

    /// <summary>Raw live partner lookup with no validation or healing — for teardown, where we only need the
    /// other BE to notify it. Null if the partner's chunk is unloaded or no sawhorse BE is there.
    /// <para>Memoized (T05.05): a cached reference is handed straight back while it is still alive and still sits at
    /// <see cref="partnerPos"/>; anything else re-resolves through the world. Semantics are unchanged — the cache can
    /// only ever return the same BE the lookup would have.</para></summary>
    BlockEntitySawhorse? PartnerBE()
    {
        BlockPos? ppos = partnerPos;
        if (ppos == null) { partnerCache = null; return null; }

        BlockEntitySawhorse? cached = partnerCache;
        if (cached != null && !cached.gone && ppos.Equals(cached.Pos)) return cached;

        if (Api.World.BlockAccessor.GetChunkAtBlockPos(ppos) == null) { partnerCache = null; return null; }
        BlockEntitySawhorse? p = Api.World.BlockAccessor.GetBlockEntity(ppos) as BlockEntitySawhorse;
        partnerCache = p;
        return p;
    }

    // How many times the on-load pair check re-arms itself while the partner's chunk is still unloaded, and the
    // gap between those retries. Bounded so a survivor sitting at the edge of the loaded area (partner intact but
    // permanently out of range) can't spin forever — after this we stop and rely on the next interaction to heal.
    const int LoadResolveRetries = 4;
    const int LoadResolveRetryMs = 1000;

    /// <summary>Server, on load (see <see cref="Initialize"/>): validate an existing pair or try to form one.
    /// Guards against having been removed meanwhile. While in a pair whose partner chunk isn't loaded yet we can't
    /// tell "intact but out of range" from "silently broken", so we DON'T heal — but re-check a bounded number of
    /// times in case the partner chunk simply loads a beat later (a border-straddling pair on a dedicated server,
    /// where nothing else would call <see cref="GetPartner"/> until an interaction). A genuinely broken half-pair
    /// still heals here once the partner chunk is loaded, or on the first E05/E06 interaction otherwise.</summary>
    void ResolvePairingOnLoad(int attempt = 0)
    {
        if (Api?.World.BlockAccessor.GetBlockEntity(Pos) != this) return; // removed before the callback fired
        if (stage != SawhorseStage.Complete) return;

        if (!inPair) { TryFormPair(); return; }      // single → pick up a neighbour that loaded alongside us
        if (partnerPos == null) { DissolveToSingle(); return; } // corrupt half-pair (no link) → heal to single

        if (Api.World.BlockAccessor.GetChunkAtBlockPos(partnerPos) == null)
        {
            if (attempt < LoadResolveRetries) RegisterDelayedCallback(_ => ResolvePairingOnLoad(attempt + 1), LoadResolveRetryMs);
            return; // partner chunk not loaded — leave the pair intact and re-check (or give up and wait for use)
        }

        GetPartner(); // partner chunk loaded → validate the back-reference, healing a genuinely broken half-pair
    }

    /// <summary>
    /// T04.05 — turn this paired trestle back into a single Complete one: clear the pair link, release any held log
    /// (a lone trestle can't hold one), re-mesh to the un-rotated single orientation, and (deferred a tick)
    /// try to re-pair with another eligible neighbour. The re-pair is deferred so that when this runs from a
    /// partner's break, the dying trestle is fully gone before we scan — otherwise the tie-break could re-grab it
    /// by its smaller coordinate. Releasing the log here (not only from the breaking trestle's OnBlockBroken) is
    /// what covers the "silent loss" case: a surviving MASTER reached through <see cref="GetPartner"/>'s self-heal
    /// must still drop its log. <see cref="ReleaseHeldLog"/> is idempotent, so a slave (no log) is a no-op and the
    /// break path calling both can't drop twice. Server-side.
    /// </summary>
    void DissolveToSingle()
    {
        if (!inPair) return;

        // The pair state goes FIRST, before the log is released (AuditFixPlan T07.01): ReleaseHeldLog asks
        // LogRecoverable, which reads the MASTER's progress through GetMaster — i.e. it re-enters the pair resolve
        // while we are half torn down. Cleared up front, that resolve answers `this`, and the `!inPair` early return
        // above turns any second entry into a no-op, so teardown cannot feed itself. Nothing moves on the normal
        // path: a master's GetMaster returned itself either way, and a slave has no log to release.
        inPair = false;
        partnerPos = null;
        isMaster = false;
        // pairAxis is left as-is; it's ignored while single (the mesh only rotates when inPair).

        ReleaseHeldLog(); // no-op unless this was a master still holding a log (heals the silent-loss master case)
        MarkDirty(true);  // re-mesh back to the single (un-rotated) orientation

        if (Api.Side == EnumAppSide.Server && stage == SawhorseStage.Complete)
        {
            RegisterDelayedCallback(_ =>
            {
                if (Api?.World.BlockAccessor.GetBlockEntity(Pos) == this
                    && stage == SawhorseStage.Complete && !inPair)
                {
                    TryFormPair();
                }
            }, 50);
        }
    }

    /// <summary>Release the master's held log when the pair dissolves (T04.05): a log still WITHIN the recovery grace
    /// (<see cref="LogRecoverable"/> — under the grace on both the rip and the strip) falls out whole; a log past the grace on either
    /// process is consumed/lost — the same "past the grace → no free hand-back" rule the Ctrl+RMB remove uses, so
    /// breaking a trestle mid-strip can't recover a well-begun log for free (while a barely-touched one, or a misclick,
    /// is forgiven). Idempotent — clears the slot so a double-call can't drop twice. Server-side.
    /// <para>With no log held it is NOT quite a no-op any more: a pair dissolving DURING an auto-restock breather has
    /// no log by construction — the pause IS the empty window between two of them — so the early return used to carry
    /// <c>restockPending</c> / <c>barkRestockPending</c> (and the co-op hold maps behind them) onto the now-single
    /// trestle, where nothing can ever finish or clear them: a single trestle holds no log, so no rip or strip tick
    /// ever runs on it again. Every dissolve path passes through here, so this is where that state dies with the pair
    /// — cleared by the same <see cref="ResetSawState"/> the log-holding branch below uses, rather than a second,
    /// partial teardown (AuditFixPlan T03.04).</para></summary>
    void ReleaseHeldLog()
    {
        if (heldLog == null)
        {
            // Guarded so an ordinary dissolve (an idle pair) still costs nothing — MarkDirty only where there was
            // something to clear.
            if (restockPending || barkRestockPending || sawEnds.Count > 0 || barkSides.Count > 0)
            {
                ResetSawState();
                MarkDirty(true); // the pending flags are synced — clear the client's copy with them
            }
            return;
        }

        if (LogRecoverable) // under the recovery grace on BOTH the rip and the strip → falls out whole
        {
            Api.World.SpawnItemEntity(heldLog, Pos.ToVec3d().Add(0.5, 0.5, 0.5));
        }
        heldLog = null;
        ResetSawState();
        MarkDirty(true);
    }

    /// <summary>Lexicographic ordering on (X, Y, Z) — the single deterministic rule behind both the two-free-
    /// neighbours tie-break and the master choice, so "smaller coordinate" means the same thing everywhere.</summary>
    static bool IsLexLess(BlockPos a, BlockPos b)
    {
        if (a.X != b.X) return a.X < b.X;
        if (a.Y != b.Y) return a.Y < b.Y;
        return a.Z < b.Z;
    }
}
