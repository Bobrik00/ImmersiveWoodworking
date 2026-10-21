using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// Shared logic for the birch-tar rig's bark PACKING and fill-stage RENDER (BarkPlan/E04, T04.09), used by BOTH the
/// standalone perforated pot (<see cref="BlockPerforatedPot"/>) and the assembled rig
/// (<see cref="BlockTarPot"/>) — the two vessels pack identically (D14) and differ only in which single merged shape
/// they draw (<c>pot-perforated</c> vs <c>pot-combined</c>). Sealing itself lives on the rig block (it needs the
/// receiver underneath), but the item matching it uses is here too.
///
/// RENDER (2026-07-28 model merge): the four cumulative birch groups (<c>pot-birch1..4</c>) were folded INTO the pot
/// shell shapes, so each vessel is now ONE shape carrying both the clay body (+lid on pot-combined) and the birch fill.
/// <see cref="PotMesh"/> tesselates that single shape per appearance: it tints the pot body(ies) with the per-instance
/// clay textures, hides the birch groups above the current fill stage (empty = hide all four), and hides the lid+daub
/// unless the pot is sealed — so the SAME shape renders the empty pot, every fill stage, and the sealed lid. This is
/// what lets a pot show its fill stage even when HELD: the block's <c>OnBeforeRender</c> swaps the default JSON mesh for
/// the correct-stage <see cref="PotMesh"/>, and its <c>GenMesh</c>/BE tesselation use the same builder — the reason the
/// shapes were merged in the first place.
///
/// Bark is matched by TAG (<c>bark-birch</c>), the whole BarkPlan premise, so any wood/bark mod's birch bark drops into
/// the tar chain with one tag line. Wet vs dry is read off the collectible's own Dry transition, so it needs no code
/// knowledge either. The per-appearance meshes are geometry-INDEPENDENT of the individual pot (fixed transforms), so
/// they are built once per client session and shared, keyed by shape + textures + stage + lid.
/// </summary>
static class TarRig
{
    /// <summary>Fallback tar-pot capacity in fill points when the mod system somehow isn't loaded — also the shipped
    /// <see cref="ImmersiveWoodworkingConfig.TarPotCapacityPoints"/> default (D11): at the default wet=1 / dry=2 points, a
    /// full pot is 32 dry OR 64 wet birch, and a sealed full pot opens into <see cref="ImmersiveWoodworkingConfig.TarYield"/>
    /// tar (T04.05); wet is worse by needing twice the birch, not by a smaller yield. The LIVE capacity is the config field
    /// <see cref="ImmersiveWoodworkingConfig.TarPotCapacityPoints"/> (config-driven since T08.01), read per side by the pot
    /// blocks off their cached, server-synced config — NOT a static handle, which would point at the wrong side's config in
    /// single-player. This const is only the null-config fallback.</summary>
    public const int DefaultCapacity = 64;

    /// <summary>The four authored cumulative birch groups (pot-birch1..4). The 64-point range is spread across these four
    /// appearances UNEVENLY on purpose (see <see cref="FillStage"/>): stage 1 at the first point, stage 4 only when full.</summary>
    public const int FillStages = 4;

    // Design contract: birch bark is matched by TAG so a foreign mod's birch bark works with one tag line. Our own bark
    // (bark-birch-green / bark-birch-dry) carries it; the code fallback in IsBirchBark covers the shouldn't-happen case
    // of the registry not yet holding the tag.
    const string BirchTag = "bark-birch";

    // The generic build/place sound the pottery piles use — reused for packing and sealing (a nicer sound is a T04.08
    // polish item).
    static readonly AssetLocation WorkSound = new AssetLocation("game:sounds/player/build");

    // The four birch groups' element-name prefixes (trailing '_' so "pot-birch1_" can never also match a longer group
    // name). Shared by both merged shapes — pot-perforated names its birch elements pot-birchN_XXX.001, pot-combined
    // names them pot-birchN_XXX; both start with these prefixes.
    static readonly string[] GroupPrefixes = { "pot-birch1_", "pot-birch2_", "pot-birch3_", "pot-birch4_" };

    // ─────────────────────────────── Item matching ───────────────────────────────

    /// <summary>Whether <paramref name="stack"/> is birch bark accepted for packing — by the <c>bark-birch</c> TAG
    /// (cross-mod), falling back to our own bark codes if the tag registry ever lacks it.</summary>
    public static bool IsBirchBark(ICoreAPI api, ItemStack? stack)
    {
        CollectibleObject? coll = stack?.Collectible;
        if (coll == null) return false;
        if (api.CollectibleTagRegistry.TryCreateTagSet(out TagSet wanted, BirchTag) == TagRegistryError.None
            && !wanted.IsEmpty && wanted.Overlaps(coll.GetTags(stack)))
        {
            return true;
        }
        return coll.Code?.Path.StartsWith("bark-birch", StringComparison.Ordinal) == true;
    }

    /// <summary>Points one birch adds when packed: WET birch (still able to dry — carries a Dry transition) = 1, DRY = 2
    /// (D11). A foreign mod's non-drying birch bark counts as dry (2), the generous default.</summary>
    public static int BirchPoints(ItemStack? stack, int wetPoints, int dryPoints)
    {
        TransitionableProperties[]? props = stack?.Collectible?.TransitionableProps;
        if (props != null)
        {
            foreach (TransitionableProperties p in props)
            {
                if (p.Type == EnumTransitionType.Dry) return wetPoints;
            }
        }
        return dryPoints;
    }

    /// <summary>Fallback bulk batch when the birch bark isn't ground-storable (or declares a zero) — matches our own bark's
    /// <c>bulkTransferQuantity</c> (bark.json).</summary>
    const int DefaultBulkQuantity = 8;

    /// <summary>How many birch a Ctrl+Shift bulk pack swallows in one click: the bark's OWN ground-storage
    /// <c>bulkTransferQuantity</c>, so a bulk pack takes exactly as many bark as bulk-PLACING that bark on the ground would
    /// (user request 2026-07-28 — «как при размещении предметов на землю»). Read live off the stack's
    /// <see cref="CollectibleBehaviorGroundStorable"/>, so the T06.04 in-game tuning of the bark heap carries over for free
    /// and a foreign mod's birch bark uses its own number; falls back to <see cref="DefaultBulkQuantity"/> (8, our bark's
    /// value) for bark with no ground storage. A BarkPlan scalar — no separate constant to keep in sync with the heap.</summary>
    public static int BirchBulkQuantity(ItemStack? stack)
    {
        int q = stack?.Collectible?.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps?.BulkTransferQuantity ?? 0;
        return q > 0 ? q : DefaultBulkQuantity;
    }

    /// <summary>Whether <paramref name="stack"/> is the vanilla clay item (code <c>clay</c>, variants blue/red/fire) used
    /// as the daub sealant (D7).</summary>
    public static bool IsSealingClay(ItemStack? stack)
        => stack?.Collectible?.FirstCodePart() == "clay";

    // ─────────────────────────────── Interaction-help icons ───────────────────────────────

    /// <summary>
    /// The bark ICONS for the two pack hints, shared by both vessels (the standalone <see cref="BlockPerforatedPot"/>
    /// and the assembled <see cref="BlockTarPot"/>). Built from the SAME gate the gesture itself uses
    /// (<see cref="IsBirchBark"/>) rather than from our own two codes, so the hint can never drift from what a pot
    /// actually swallows: it shows BOTH our states (bark-birch-green + bark-birch-dry) and a foreign mod's bark carrying
    /// the <c>bark-birch</c> tag — the cross-mod premise the whole chain rests on.
    ///
    /// <para><paramref name="bulk"/> sizes each icon by that bark's OWN <see cref="BirchBulkQuantity"/> instead of 1,
    /// mirroring how vanilla ground storage states its own pair of hints (<c>blockhelp-groundstorage-addone</c> uses
    /// <c>new(collObj, 1)</c>, <c>-addbulk</c> uses <c>new(collObj, bulkquantity)</c>) — so the Ctrl+Shift line reads as a
    /// bundle next to the plain Shift line's single peel, and the number shown is the one the click really takes. The size
    /// is set EXPLICITLY on every stack because a stack built off a collectible's creative-inventory entry would otherwise
    /// carry that collectible's MAX stack size.</para>
    ///
    /// <para>Resolved LAZILY on first hover and cached for the session (ObjectCache), NOT baked in <c>Block.OnLoaded</c>: this walks every
    /// collectible and asks the tag registry, and neither is complete until all mods have loaded. Returns null (never an
    /// EMPTY array) when there is nothing to show — including on the server — so the hint degrades to its plain text
    /// instead of being dropped for having an icon list with no matching stack.</para>
    /// </summary>
    public static ItemStack[]? BirchBarkStacks(ICoreAPI api, bool bulk)
    {
        if (api is not ICoreClientAPI) return null; // hints are client-only; nothing on the server builds this list
        ItemStack[] stacks = ObjectCacheUtil.GetOrCreate(api, bulk ? "iwBirchBarkStacksBulk" : "iwBirchBarkStacks", () =>
        {
            List<ItemStack> list = new();
            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                if (obj?.Code == null) continue;
                // One probe stack per candidate: IsBirchBark reads tags off a STACK (GetTags may be stack-dependent),
                // and the probe that passes is the very stack we hand to the hint — so icon and gate can't disagree.
                ItemStack stack = new ItemStack(obj);
                if (!IsBirchBark(api, stack)) continue;
                stack.StackSize = bulk ? BirchBulkQuantity(stack) : 1;
                list.Add(stack);
            }
            return list.ToArray();
        });
        return stacks.Length > 0 ? stacks : null;
    }

    // ─────────────────────────────── Fill readout (block info + tooltip) ───────────────────────────────

    /// <summary>The pack level as a whole PERCENT of a full pot, for the player-facing readouts. 0 and 100 are RESERVED
    /// for the exact states — untouched and sealable — and a partly packed pot clamps into 1..99: those two numbers are
    /// the ones the player acts on (100% is the gate for sealing, <see cref="TryPackBirch"/> refuses anything more), so
    /// rounding must never claim them for a pot that is one bark short either way.</summary>
    public static int FillPercent(int fillPoints, int capacity)
    {
        if (fillPoints <= 0) return 0;
        if (fillPoints >= capacity) return 100;
        return GameMath.Clamp((int)Math.Round(100.0 * fillPoints / capacity, MidpointRounding.AwayFromZero), 1, 99);
    }

    /// <summary>The one line a packable vessel contributes to a ground pile's (or shelf's) contents list. The stack's own
    /// NAME has to be embedded because the caller wraps this in "{count}x {0}"
    /// (<c>BEGroundStorage.getContentSummary</c>) — the vanilla <c>IContainedCustomName</c> idiom, cf.
    /// <c>BlockLiquidContainerTopOpened.GetContainedInfo</c>. Two pots packed to different levels therefore produce
    /// different strings and list as their own lines instead of being summed into one.</summary>
    public static string ContainedFillInfo(ItemSlot inSlot, int fillPoints, int capacity)
        => Lang.Get("immersivewoodworking:contained-tarrig-fill", inSlot.Itemstack?.GetName(), FillPercent(fillPoints, capacity));

    /// <summary>The pack-level line for a vessel's HELD/inventory tooltip. The level rides the STACK, so a packed pot
    /// carried in a bag or dropped and picked up again keeps its bark — and must be readable without putting it down.</summary>
    public static string HeldFillInfo(int fillPoints, int capacity)
        => Lang.Get("immersivewoodworking:blockinfo-tarrig-fill", FillPercent(fillPoints, capacity));

    /// <summary>
    /// The pile TITLE for a vessel that also fills in <c>GetContainedInfo</c>: the stack's own name, carrying the
    /// vanilla "{0}x {1}" count when the cell holds more than one.
    ///
    /// <para>Returning null here does NOT keep a default title, which is what both vessels used to assume.
    /// <c>BEGroundStorage.GetBlockName</c> (:1161-1183) builds <see cref="ContainedFillInfo"/> through
    /// <c>getContentSummary()</c> FIRST and only consults <c>IContainedCustomName.GetContainedName</c> when that
    /// summary collapsed to one line; null there falls through to the summary line itself (:1179), so a cell of
    /// equally packed pots was titled "4x Perforated Pot (25% bark)". Vanilla's one other vessel that bakes a
    /// per-stack quantity into its info line answers this with a plain name for the same reason
    /// (<c>BlockLiquidContainerTopOpened</c> :226-229); the count is added back because the pile title is where the
    /// player reads how many are in the cell.</para>
    ///
    /// <para>Vessels packed to DIFFERENT levels still title as "Ground Storage": the summary then has two lines and
    /// the engine returns that constant (:1182) without asking any collectible. No collectible-side seam reaches that
    /// branch, and the only lever that would — dropping the percent out of <see cref="ContainedFillInfo"/> —
    /// costs the per-slot fill readout this whole interface is implemented for.</para>
    /// </summary>
    public static string? ContainedPileTitle(ItemSlot inSlot, int quantity)
    {
        string? name = inSlot.Itemstack?.GetName();
        if (name == null) return null; // no stack to name: let the caller fall back to its own default
        return quantity > 1 ? Lang.Get("{0}x {1}", quantity, name) : name;
    }

    // ─────────────────────────────── Packing (shared) ───────────────────────────────

    /// <summary>
    /// Server-authoritative bark packing shared by both vessels: add birch points to <paramref name="fillPoints"/>
    /// (clamped to <see cref="ImmersiveWoodworkingConfig.TarPotCapacityPoints"/>), consuming that many birch from <paramref name="birchSlot"/>. With
    /// <paramref name="bulk"/> false this is the plain Shift+RMB single chunk (one birch); with it true it is the
    /// Ctrl+Shift+RMB batch — up to <see cref="BirchBulkQuantity"/> birch in one click, stopping early when the pot fills or
    /// the hand stack runs out (so a bulk click on a near-full pot only takes what fits). Returns true when the level
    /// actually changed (server only — the caller then <c>MarkDirty(true)</c>); one work sound plays per successful click,
    /// bulk or not. A full pot shows a client hint and returns false. The client never mutates — it just shows the "full"
    /// hint and claims the click — so the world state is driven entirely by the server.
    ///
    /// <para><paramref name="assembled"/> picks the FULL hint, and it is not cosmetic: sealing exists only on the rig
    /// (<see cref="BlockTarPot.SealRig"/> is the only site that consumes clay), so telling the owner of a lone
    /// perforated pot to "seal it with clay" names an action that vessel refuses — clay on a lone pot answers
    /// <c>ingameerror-tarpot-assemblefirst</c>, i.e. the very next click contradicts the hint. A lone pot is therefore
    /// pointed at assembly instead, which IS its next step.</para>
    /// </summary>
    public static bool TryPackBirch(ICoreAPI api, BlockPos pos, ItemSlot birchSlot, ref int fillPoints, bool assembled, bool bulk = false)
    {
        // Resolve the live, server-synced config once per click (T08.01): the capacity gate + the wet/dry point values.
        ImmersiveWoodworkingConfig cfg = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().Config;
        int cap = cfg.TarPotCapacityPoints, wetPts = cfg.TarBirchPointsWet, dryPts = cfg.TarBirchPointsDry;
        if (fillPoints >= cap)
        {
            string fullKey = assembled ? "immersivewoodworking:ingameerror-tarpot-full" : "immersivewoodworking:ingameerror-tarpot-fullassemble";
            (api as ICoreClientAPI)?.TriggerIngameError(birchSlot, "tarpotfull", Lang.Get(fullKey));
            return false;
        }
        if (api.Side != EnumAppSide.Server) return false; // client claims elsewhere; the server mutates + syncs

        int maxCount = bulk ? BirchBulkQuantity(birchSlot.Itemstack) : 1;
        bool changed = false;
        // Each pass reads BirchPoints off the CURRENT slot stack — a slot holds one collectible, so all its birch share the
        // same wet/dry value; TakeOut(1) only shrinks the stack, leaving that value intact through the loop.
        for (int i = 0; i < maxCount && fillPoints < cap && !birchSlot.Empty; i++)
        {
            fillPoints = Math.Min(cap, fillPoints + BirchPoints(birchSlot.Itemstack, wetPts, dryPts));
            birchSlot.TakeOut(1);
            changed = true;
        }
        if (changed)
        {
            birchSlot.MarkDirty();
            api.World.PlaySoundAt(WorkSound, pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, null, true, 12f);
        }
        return changed;
    }

    // ─────────────────────────────── Ground-storage cell box ───────────────────────────────

    /// <summary>
    /// Re-latch the CELL's ground-storage properties onto whatever stack leads it now, and rebuild its collision and
    /// selection box from them. Call after any in-quadrant stack swap (assemble / seal / disassemble), before the
    /// <c>MarkDirty(true)</c> that syncs the cell.
    ///
    /// <para>Vanilla resolves those properties exactly ONCE. <c>BEGroundStorage.DetermineStorageProperties</c>
    /// (:654-679) reads them off a stack only while the field is still null, and it stops being null in
    /// <c>Initialize</c> (:238) — every later call is a no-op, including the one <c>FromTreeAttributes</c>
    /// (:1103-1106) makes on every sync and the one every interaction makes (:478). There is ONE box per cell and it
    /// comes from the first non-empty slot, so a cell born around a flat clay pot (y2 0.125) keeps that box after a rig
    /// (0.72) or a sealed pot (0.35) takes over its leading quadrant: the player aims at a rig two thirds of a block
    /// tall whose selection box is a slab in the bottom 2/16, and every pack, seal and disassembly click has to be
    /// aimed at the floor. Mirrored, after a chunk reload on a rig and then a disassembly, a 0.72 collision box hangs
    /// over a flat pot as an invisible wall.</para>
    ///
    /// <para><c>ForceStorageProps</c> + <c>DetermineStorageProperties</c> is the engine's own idiom for exactly this
    /// (<c>BEClayForm</c> :262-264, <c>BEPitKiln.KillFire</c> :558-560): the first assigns the properties and latches
    /// <c>forceStorageProps</c>, the second is the only public route into the private
    /// <c>regenCollisionSelectionBox</c>. The latch also serializes the properties into the cell's tree (:1134-1138),
    /// so the client rebuilds the same box from <c>FromTreeAttributes</c> — all three callers are SERVER-side and
    /// need no client mirror. It costs no re-resolve that vanilla would otherwise have done: vanilla never re-resolves
    /// either, it just sticks with whatever the cell was born with, so the latch changes WHICH properties are stuck,
    /// not that they are. A pit kiln built over the cell is unaffected — it is a fresh BE and
    /// <c>BEPitKiln.OnCreated</c> nulls the properties itself.</para>
    ///
    /// <para>No-ops when there is nothing to latch onto: a cell the swap emptied (a degenerate rig with no receiver),
    /// a leading stack with no ground storage, a host that is not a ground-storage cell at all (a shelf —
    /// <c>BEShelf</c> :178 routes <see cref="IContainedInteractable"/> too, and a shelf has no cell box), or a leading
    /// stack whose properties are already the ones in force — the swap happened in a quadrant that does not lead
    /// the cell, whose documented trade (tarpot.json) is that the leading pot keeps its flat box.</para>
    /// </summary>
    public static void RefreshCellStorageProps(BlockEntityContainer? be)
    {
        if (be is not BlockEntityGroundStorage cell) return;
        ItemStack? lead = cell.Inventory.FirstNonEmptySlot?.Itemstack;
        GroundStorageProperties? props = lead?.Collectible?.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps;
        if (props == null || ReferenceEquals(cell.StorageProps, props)) return;
        cell.ForceStorageProps(props);
        cell.DetermineStorageProperties(null);
    }

    // ─────────────────────────── Fill-stage mesh ───────────────────────────

    /// <summary>The cumulative fill stage (0..<see cref="FillStages"/>) the given point level sits at: empty at 0,
    /// stage 1 as soon as the FIRST point is packed, the top stage ONLY when the pot is completely full, and the middle
    /// stages spread EVENLY between. Stage k first appears at round(1 + (k-1)·(capacity-1)/(FillStages-1)) points —
    /// for the shipped 64 points / 4 stages that is 1 / 22 / 43 / 64 (user spec). Rounding is half-away-from-zero so a
    /// non-exact split lands the intuitive way; with 64/4 the split (63/3 = 21) is already exact.</summary>
    public static int FillStage(int fillPoints, int capacity)
    {
        if (fillPoints <= 0) return 0;
        if (fillPoints >= capacity) return FillStages; // top stage reached exactly at full

        int stage = 0;
        for (int k = 1; k <= FillStages; k++)
        {
            // Stage 1 is pinned to the first point; the k==1 branch also keeps a degenerate FillStages==1 divide-safe.
            int threshold = k == 1
                ? 1
                : 1 + (int)Math.Round((k - 1) * (capacity - 1.0) / (FillStages - 1), MidpointRounding.AwayFromZero);
            if (fillPoints >= threshold) stage = k; else break;
        }
        return stage;
    }

    // The caches live ONE CLIENT SESSION, never the process. A MeshData out of TesselateShape is bound to the block atlas
    // it was built against: its UVs are that atlas' coordinates and its TextureIds are the GL names of that atlas' pages.
    // Leaving a world deletes those pages (ClientMain.Dispose → BlockAtlasManager.Dispose) and the next world builds new
    // ones, but this assembly and its statics live until the game closes. A process-wide cache therefore handed the
    // second world meshes pointing at dead or reused GL textures: a scrambled or missing texture in hand and in the
    // clayforming recipe list. In a chunk mesh it was worse: JsonTesselator finds no atlas pool for the unknown TextureId,
    // throws RetryTesselationException, and the chunk is requeued forever while the GPU keeps its last mesh, so the
    // stored pot is invisible and anything taken from that chunk stays drawn (ModDB report, 1.3.6).
    //
    // The session key is the client API instance itself: ClientMain builds a new ClientCoreAPI on every join, and every
    // caller passes the one it was handed. ConditionalWeakTable holds it weakly, so a finished world is not kept alive by
    // this table, and a late call from the old session's tesselation thread can only ever reach the old session's cache.
    sealed class SessionCache
    {
        public readonly Dictionary<string, Shape?> Shapes = new();
        public readonly Dictionary<string, MeshData?> Meshes = new();
    }
    static readonly ConditionalWeakTable<ICoreClientAPI, SessionCache> sessionCaches = new();

    // Guarded by MeshLock because the caches are touched from MULTIPLE threads: both blocks' OnBeforeRender runs on the
    // render thread, while the ground-storage quadrant path (IContainedMeshSource.GenMesh) runs on the TESSELATION thread
    // (BEGroundStorage note: "OnTesselation runs in another thread"), and shelf/display meshes are built from the main
    // thread. Both the cache Dictionaries AND the blank-and-restore of the shared Shape.Elements would race without the
    // lock. PotMesh takes MeshLock around its whole body; the private GetShape is only ever called from inside it, so no
    // separate guard is needed. Keyed by shape + textures + stage + lid, so no two appearances share an entry.
    static readonly object MeshLock = new();

    // The merged shape's clay texture keys: the upper (perforated) + lower (receiver) pot bodies and the lid+daub. The
    // birch keys (birch-h / birch-debarked) stay bound to the shape's OWN baked textures; only these clay keys are
    // re-bound per instance. The lid + daub elements are all named "lid*" (the daub rides the "clay" texture key on them).
    const string TopKey = "pot-combined-empty-top";
    const string BotKey = "pot-combined-empty-bot";
    const string LidTexKey = "pot-combined-lid";
    const string LidElementPrefix = "lid";

    /// <summary>
    /// The pot mesh for ONE appearance of merged shape <paramref name="shapeLoc"/> (pot-perforated / pot-combined): the
    /// pot body(ies) tinted with the given clay textures, the birch fill shown up to cumulative <paramref name="stage"/>
    /// (0 = empty shell, 4 = full), and the lid+daub shown only when <paramref name="withLid"/> (a sealed pot). Built once
    /// per appearance per client session (see <see cref="SessionCache"/>). Thread-agnostic (guarded by MeshLock); the caller clones/uploads as its
    /// render path needs (the ground-storage display base mutates the mesh it is handed, so GenMesh callers CLONE first).
    ///
    /// <para><paramref name="topTex"/> tints the upper (perforated) body (key <c>pot-combined-empty-top</c>);
    /// <paramref name="botTex"/> the lower (receiver) body (<c>pot-combined-empty-bot</c>) — pass null for the standalone
    /// perforated pot, whose shape has no lower body. <paramref name="lidTex"/> tints the lid when sealed (defaults to
    /// <paramref name="topTex"/>). The birch textures ride the shape's own <c>textures</c> dict (full game: paths).</para>
    ///
    /// Implementation is the sawhorse's proven blank→tesselate→restore idiom (<c>FacesResolved = new ShapeElementFace[6]</c>):
    /// hide the birch groups above the stage (and the lid unless sealed), tesselate, then restore the shared shape exactly
    /// as it was — all synchronous under MeshLock, so a concurrent PotMesh on another thread can never observe it mid-blank.
    /// </summary>
    public static MeshData? PotMesh(ICoreClientAPI capi, AssetLocation shapeLoc, AssetLocation topTex, AssetLocation? botTex, int stage, bool withLid, AssetLocation? lidTex = null)
    {
        if (stage < 0) stage = 0;
        if (stage > FillStages) stage = FillStages;

        lock (MeshLock)
        {
            SessionCache cache = sessionCaches.GetValue(capi, _ =>
            {
                TarDbg.Log(capi, "PotMesh NEW-SESSION cache");
                return new SessionCache();
            });
            string shapeKey = shapeLoc.ToShortString();
            string key = shapeKey + "|" + topTex + "|" + (botTex?.ToString() ?? "-") + "|s" + stage + (withLid ? "|lid:" + (lidTex ?? topTex) : "");
            if (cache.Meshes.TryGetValue(key, out MeshData? cached))
            {
                TarDbg.Log(capi, $"PotMesh CACHE-HIT key={key} verts={cached?.VerticesCount.ToString() ?? "<null mesh>"}");
                return cached;
            }

            Shape? shape = GetShape(capi, cache, shapeLoc, shapeKey);
            if (shape == null)
            {
                TarDbg.Log(capi, $"PotMesh SHAPE-NULL {shapeLoc} -> returning null (caller draws default/empty)");
                cache.Meshes[key] = null; return null;
            }

            // Texture bindings: seed from the shape's OWN keys (the birch textures carry full game: paths the host block
            // does not declare), then override the clay bodies + lid with the per-instance textures. ShapeTexSource
            // returns the atlas' unknown position for any unmapped key, so every element left VISIBLE must be mapped here.
            var texMap = new Dictionary<string, AssetLocation>();
            if (shape.Textures != null)
                foreach (KeyValuePair<string, AssetLocation> kv in shape.Textures) texMap[kv.Key] = kv.Value;
            texMap[TopKey] = topTex;
            if (botTex != null) texMap[BotKey] = botTex;
            if (withLid) texMap[LidTexKey] = lidTex ?? topTex;
            var tex = new ShapeTexSource(capi, texMap);
            TarDbg.Log(capi, $"PotMesh BUILD key={key} stage={stage} withLid={withLid} texMap=[{string.Join(", ", texMap.Select(kv => kv.Key + "->" + kv.Value))}]");

            var saved = new List<(ShapeElement el, ShapeElementFace[]? faces)>();
            BlankGroupsAbove(shape.Elements, stage, saved);
            if (!withLid) BlankByPrefix(shape.Elements, LidElementPrefix, saved);
            MeshData mesh;
            try
            {
                capi.Tesselator.TesselateShape(shapeKey, shape, out mesh, tex);
            }
            finally
            {
                // Restore the shared cached Shape even if tesselation throws (e.g. RetryTesselationException when the
                // atlas has to grow off the main thread) — otherwise the blanked faces would stay blanked and EVERY later
                // build of this shape would render with birch/lid missing for the rest of the session. The exception still propagates so
                // the engine's tesselation retry keeps working (vanilla lets TesselateShape throw on the tess thread).
                foreach ((ShapeElement el, ShapeElementFace[]? faces) in saved) el.FacesResolved = faces;
            }

            // A "flat plane" is ~4 vertices; the real pot is thousands. This number alone settles whether the ?-quad the
            // player sees is OUR mesh degenerating or the engine drawing something else entirely in our place.
            TarDbg.Log(capi, $"PotMesh DONE key={key} verts={mesh.VerticesCount} blanked={saved.Count}");
            cache.Meshes[key] = mesh;
            return mesh;
        }
    }

    // Cached parsed shape (Shape.TryGet re-parses on every call — no engine cache for non-block shapes — so we keep one,
    // the sawhorse's sharedLogShape rationale). Blanked-then-restored under MeshLock, so it stays pristine between builds.
    // A Shape holds no atlas data, but it is still kept per session: the next world may run a different mod set that
    // patches or replaces this asset, and one parse per session costs nothing.
    static Shape? GetShape(ICoreClientAPI capi, SessionCache cache, AssetLocation shapeLoc, string key)
    {
        if (cache.Shapes.TryGetValue(key, out Shape? s)) return s;
        s = Shape.TryGet(capi, shapeLoc);
        cache.Shapes[key] = s;
        return s;
    }

    // Blank (no faces → no quads) every element whose name starts with one of the group prefixes ABOVE keepStages,
    // recording each element's original FacesResolved so the caller restores the shared shape after tesselation.
    // Recurses into children (mirrors the sawhorse's BlankBySequence).
    static void BlankGroupsAbove(ShapeElement[]? els, int keepStages, List<(ShapeElement, ShapeElementFace[]?)> saved)
    {
        if (els == null) return;
        foreach (ShapeElement el in els)
        {
            string name = el.Name ?? "";
            for (int g = keepStages + 1; g <= FillStages; g++)
            {
                if (name.StartsWith(GroupPrefixes[g - 1], StringComparison.Ordinal))
                {
                    saved.Add((el, el.FacesResolved));
                    el.FacesResolved = new ShapeElementFace[6];
                    break;
                }
            }
            BlankGroupsAbove(el.Children, keepStages, saved);
        }
    }

    // Blank every element (and child) whose name starts with prefix, saving originals for restore (see PotMesh).
    static void BlankByPrefix(ShapeElement[]? els, string prefix, List<(ShapeElement, ShapeElementFace[]?)> saved)
    {
        if (els == null) return;
        foreach (ShapeElement el in els)
        {
            if ((el.Name ?? "").StartsWith(prefix, StringComparison.Ordinal))
            {
                saved.Add((el, el.FacesResolved));
                el.FacesResolved = new ShapeElementFace[6];
            }
            BlankByPrefix(el.Children, prefix, saved);
        }
    }
}

/// <summary>
/// Texture source that resolves an authored shape's OWN baked texture keys (its <c>textures</c> dict, full asset paths)
/// against the block atlas — for tesselating an overlay shape (the birch fill) whose texture keys the host block does
/// not declare. Mirrors <see cref="LogTexSource"/>'s atlas lookup: try the atlas, else load textures/&lt;path&gt;.png and
/// insert. An unrecognised key returns the atlas' unknown position rather than throwing — a KeyNotFoundException on the
/// tesselation path would crash the render loop.
/// </summary>
internal sealed class ShapeTexSource : ITexPositionSource
{
    readonly ICoreClientAPI capi;
    readonly Dictionary<string, AssetLocation> textures;

    public ShapeTexSource(ICoreClientAPI capi, Dictionary<string, AssetLocation> textures)
    {
        this.capi = capi;
        this.textures = textures;
    }

    public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

    public TextureAtlasPosition this[string textureCode]
    {
        get
        {
            if (textures.TryGetValue(textureCode, out AssetLocation? path)) return GetOrInsert(path);
            // An element referencing a key we never mapped: it renders with the atlas' unknown ("?") texture.
            TarDbg.Log(capi, $"ShapeTexSource UNMAPPED-KEY '{textureCode}' -> unknown(?) texture");
            return capi.BlockTextureAtlas.UnknownTexturePosition;
        }
    }

    TextureAtlasPosition GetOrInsert(AssetLocation path)
    {
        ITextureAtlasAPI atlas = capi.BlockTextureAtlas;
        TextureAtlasPosition? pos = atlas[path];
        if (pos != null) return pos;

        // NOT already in the block atlas. This is the suspect path for colours only vanilla declares (brown/black/...)
        // versus the blue/fire/red our own blocktypes declare — and inserting off the tesselation thread may fail.
        IAsset? asset = capi.Assets.TryGet(path.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"));
        bool inAtlas = false;
        TextureAtlasPosition? inserted = null;
        if (asset != null)
        {
            inAtlas = atlas.GetOrInsertTexture(path, out _, out TextureAtlasPosition ins, () => asset.ToBitmap(capi));
            inserted = ins;
        }
        TarDbg.Log(capi, $"ShapeTexSource ATLAS-MISS {path} assetFound={asset != null} inserted={inAtlas}"
                       + $" thread={Environment.CurrentManagedThreadId} main={RuntimeEnv.MainThreadId}");
        if (asset != null && inAtlas && inserted != null) return inserted;
        return atlas.UnknownTexturePosition;
    }
}
