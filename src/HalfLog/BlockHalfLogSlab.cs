using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// The half-log building slab (HalfLogBlockPlan E03): <c>halflogslab-{wood}-{skin}-{rot}-{run}-{face}</c>, one block
/// variant per wood species, surface state and orientation, NO BlockEntity (a floor is laid by the hundred, so
/// per-cell state is not affordable). All this class does is make the species' REAL textures reach the atlas,
/// including when the species belongs to another mod's domain.
///
/// <para><c>skin</c> (<c>barked</c> / <c>debarked</c>, AuditFixPlan T04.01) is the surface state the half-log was
/// split with, carried into the world so a stripped log stays stripped once it is built into a wall. It is a variant
/// axis rather than a stack attribute for the same reason the species is: the slab has no BlockEntity, so the block
/// code is the only place a placed cell can remember anything at all.
///
/// <para><b>Why the paint has to happen in <see cref="OnCollectTextures"/> and nowhere else.</b> The blocktype asset
/// pins its three texture paths to <c>game:</c> (see the comment in <c>blocktypes/halflogslab.json</c>), which is right
/// for the 12 vanilla species and a 404 for every wood mod's species. Fixing that means changing
/// <c>CompositeTexture.Base</c> BEFORE the atlas bakes it, and the bake is one-shot on both levels:
/// <c>CompositeTexture.Bake</c> is a no-op once <c>Baked != null</c> (vsapi CompositeTexture.cs:291) and
/// <c>TextureDictionary.BakeAndCollect</c> latches <c>alreadyBaked</c> (vsapi TextureDictionary.cs:25). The engine
/// calls THIS method to drive the bake ("Called by the texture atlas manager when building up the block atlas",
/// vsapi Block.cs:2438-2443), so rewriting here and then delegating to <c>base</c> is the only ordering that works.
/// Both hooks the task originally proposed are too late: on the client <c>Block.OnLoaded</c> runs AFTER the atlas is
/// composed — the base class proves it against itself, since <c>Block.OnLoaded</c> calls
/// <c>LoadTextureSubIdForBlockColor()</c> on the client side (Block.cs:480-482) and that method dereferences
/// <c>compoTex.Baked.TextureSubId</c> UNGUARDED (Block.cs:494, 501), which can only survive post-bake — and a
/// ModSystem's <c>AssetsFinalize</c> is later still.
///
/// <para><b>Why this alone is enough, and why <c>ITexPositionSource</c> is NOT implemented here.</b> With
/// <c>Base</c> correct before the bake, every consumer downstream is served by the ordinary pipeline: the chunk mesh,
/// the inventory/handbook icon, the chisel microblock, the break particles and the map colour all read
/// <c>block.Textures</c> / <c>block.TexturesInventory</c>. An <c>ITexPositionSource</c> indexer would reach none of
/// them: <c>TesselateBlock(Block, out MeshData)</c> takes no texture source at all (vsapi ITesselatorAPI.cs:136 is the
/// only declaration) and nothing in the engine type-tests a Block for the interface — it is seen only where the caller
/// hands itself in, which is what vanilla <c>BlockSlabSnowRemove</c> does for its EXTRA snow mesh, not for its own
/// block mesh. <see cref="LogTexSource"/> stays untouched and keeps serving <see cref="ItemHalfLog"/>, where the mesh
/// IS tesselated by hand and a source can be passed.
/// </summary>
/// <remarks>Partial: the wrench (E05), the world-edit / schematic transforms and their self-check live in
/// <c>BlockHalfLogSlab.Wrench.cs</c>, which is also where <c>IWrenchOrientable</c> is declared on this class. Splitting
/// by FEATURE rather than by member kind, the same way the chopper and the sawhorse are split.</remarks>
public partial class BlockHalfLogSlab : Block
{
    // The species textures, in the same layout the item and the sawn-log meshes use: the round side, tree-ring end
    // grain on the cut caps, bare wood on the split plane. The blocktype declares NINE keys over these three
    // surfaces — the three the shape names its materials by, and the six world-face codes the chisel reads (T04.02) —
    // and the paint pass below answers all nine from four WoodTexFamily values. Material keys are matched by PREFIX
    // exactly as LogTexSource does (wood_corner BEFORE wood, since it also starts with "wood"), so a renamed or added
    // key in the generated shapes still lands somewhere sane instead of silently keeping the game:-pinned path; face
    // codes go through SurfaceOf, because their names say a direction and not a surface.
    //
    // FOUR families for three slots, because the `skin` axis (T04.01) moves TWO of them: a debarked slab takes its round
    // side off the bare-wood family and its END CAPS off vanilla's separate treetrunk/debarked — the tree-ring
    // texture without the rim of bark. Leaving the caps on plain treetrunk would paint bark back onto the cut face,
    // which is the very thing this axis exists to stop. The split plane is bare wood on both skins. Identical pairing
    // to LogTexSource.ForSpecies, which draws whole logs and half-log items the same way.

    // Species we have already complained about, so a wood mod that ships a species with NO wood textures at all costs
    // one log line and not 24 (one per orientation). Process-wide but cleared in OnUnloaded, so leaving a world and
    // loading another one with a different mod set reports again. Lock-free: OnCollectTextures runs off the main
    // thread and the engine gives no ordering guarantee between block instances.
    static readonly ConcurrentDictionary<string, byte> warnedSpecies = new ConcurrentDictionary<string, byte>();

    // Species whose variant lookup already failed once, split by WHY (see <see cref="Get"/>). Separate from warnedSpecies on
    // purpose: that one means "this species has no TEXTURES", these mean "this species has no BLOCK". And separate
    // from EACH OTHER because they are different failures with different owners — "the species has no variants at
    // all" is a fact about the world, "that orientation does not exist" is a bug in the calling code — so sharing
    // one key would let a single probe of an impossible orientation silence the real warning for that species for
    // the rest of the session.
    static readonly ConcurrentDictionary<string, byte> missingSpecies = new ConcurrentDictionary<string, byte>();
    static readonly ConcurrentDictionary<string, byte> missingState = new ConcurrentDictionary<string, byte>();

    /// <summary>
    /// Point every one of this variant's texture slots at the domain that actually SHIPS it, then let base bake them.
    /// Runs once per block instance while the atlas is being assembled, off the main thread — asset reads only, no
    /// render API.
    /// </summary>
    public override void OnCollectTextures(ICoreAPI api, ITextureLocationDictionary textureDict)
    {
        RetargetSpeciesTextures(api);
        base.OnCollectTextures(api, textureDict);
    }

    /// <summary>Clear the log-once sets on world unload (see <see cref="warnedSpecies"/>), so leaving a world and
    /// loading another one with a different mod set reports again instead of staying silent for the process.</summary>
    public override void OnUnloaded(ICoreAPI api)
    {
        base.OnUnloaded(api);
        warnedSpecies.Clear();
        missingSpecies.Clear();
        missingState.Clear();
        missingItem.Clear();
#if IW_DEBUG
        loggedSpecies.Clear();
#endif
    }

    // ───────────────────────────── species and state resolution (T03.03) ─────────────────────────────
    //
    // ONE place that turns a block variant into the (species, domain) pair the half-log ITEM stores in its stack
    // attributes, and back into a block. Three later call sites lean on it and must not each grow their own copy:
    // placement (T04.02) needs a block from the held stack's species, drop / pick block (T04.04) needs a stack from
    // the placed block, and the wrench (T05.01, T05.02) needs to step to a neighbouring orientation without dropping
    // the species on the way.
    //
    // The variant code is built from ONE formula (CodeFor) for both directions, rather than from the instance's own
    // Variant dictionary via CodeWithVariants: the species direction has no block instance to read a dictionary off,
    // and having the two directions share a single literal shape is what makes them unable to disagree. Writing the
    // shape by hand costs little, because release will freeze it anyway — the chisel stores the hewn material as a
    // RAW block id and re-resolves it by code only on the schematic paths (BEMicroBlock.OnLoadCollectibleMappings on
    // import, OnTransformed on rotate), so from the first SHIPPED world on, halflogslab-{wood}-{skin}-{rot}-{run}-{face}
    // cannot change regardless of what this file does (T03.01). No world has shipped yet — 1.3.0 never reached ModDB —
    // which is exactly what let T04.01 widen the shape by a part; the freeze starts at the first release that carries
    // this block, not at the first release that mentioned it. Until then the shape CAN still move, which is why
    // SelfCheck below exists to catch the formula drifting from the asset.

    const string ModDomain = "immersivewoodworking";
    const string BaseCode = "halflogslab";

    /// <summary>The two states of the <c>skin</c> variant axis (T04.01). Spelled once here and read from here by
    /// everything that builds or reads a code, so the asset and the formula cannot disagree on the wording — the
    /// same discipline <see cref="CodeFor"/> imposes on the shape of the code as a whole.</summary>
    internal const string SkinBarked = "barked";
    internal const string SkinDebarked = "debarked";

    /// <summary>The axis state for a half-log stack's debarked flag. The ONE place the boolean the item carries turns
    /// into the string the block code carries, so a placement and a drop cannot spell it differently.</summary>
    internal static string SkinFor(bool debarked) => debarked ? SkinDebarked : SkinBarked;

    /// <summary>
    /// This variant's BARE wood species code ("oak"), or null when the variant carries none. Bare by necessity: a
    /// variant part cannot hold a colon, so the code names the species and NOT the domain that ships it — see
    /// <see cref="WoodDomain"/> for the other half of the pair.
    ///
    /// <para>The <c>placeholder</c> species (what survives when <c>InjectWoodVariants</c> found nothing) is
    /// deliberately NOT special-cased, for the same reason <see cref="RetargetSpeciesTextures"/> doesn't special-case
    /// it: that world is already broken and already warned about at atlas-build time, and a magic string here would
    /// be a second copy of a name that lives in the asset.
    /// </summary>
    public string? WoodCode
    {
        get
        {
            string? wood = Variant?["wood"];
            return string.IsNullOrEmpty(wood) ? null : wood;
        }
    }

    /// <summary>
    /// The domain that actually OWNS this variant's species — the half of the pair the code cannot carry, and the
    /// value a dropped half-log stack stores as <c>woodDomain</c>. Reads the mod system's collapsed index
    /// (<see cref="ImmersiveWoodworkingModSystem.WoodDomains"/>), which is built on BOTH sides in <c>AssetsLoaded</c>,
    /// i.e. before any <c>Block.OnLoaded</c>, so it is always populated by the time anything can hold this block.
    /// Falls back to <c>game</c> — the same guess the pinned paths in the asset make — for a species nobody
    /// discovered.
    ///
    /// <para>Valid from <c>OnLoaded</c> onwards, which covers every real caller (a drop, a placement, a wrench turn,
    /// a name lookup) because all of them need a loaded block to start with. Earlier than that it answers <c>game</c>
    /// for everything, because the inherited <c>api</c> field this reads is assigned in <c>OnLoadedNative</c> and the
    /// engine does not promise loaded blocks before <c>GameReady</c> — see <see cref="SelfCheck"/>, which learned it
    /// the hard way.
    ///
    /// <para>This is the COLLAPSED domain, which is not necessarily the one the stack that placed the block carried:
    /// where two mods ship the same bare species code, <c>BuildWoodDomainIndex</c> picks one and every variant of
    /// that species wears it. So a drop built from this pair can name a different <c>woodDomain</c> than the half-log
    /// that was placed. That is the accepted cost of putting the species in the variant code (T01.02: a shared
    /// species code means the same tree), and it costs nothing on any install measured so far — vanilla plus
    /// wildcrafttree plus herbarium plus FloralZones is 57 species in 3 domains with ZERO shared codes.
    /// </summary>
    public string WoodDomain => DomainOf(api, WoodCode);

    /// <summary>This variant's <c>skin</c> axis state, <c>barked</c> when the variant carries none (T04.01).
    ///
    /// <para>Defaulting rather than answering null is deliberate and is the opposite call from <see cref="WoodCode"/>.
    /// A species-less variant has nothing to place, name or paint, so null is the honest answer there and every caller
    /// has a branch for it. A skin-less variant, by contrast, can only come from an asset edited to drop the axis, and
    /// the pre-T04.01 behaviour of every one of those blocks was "barked" — so the default keeps a mis-edited asset
    /// looking exactly like the mod did before the axis existed instead of failing in a second, unrelated way.
    /// <see cref="SelfCheck"/> is what notices the axis went missing.</summary>
    public string SkinCode
    {
        get
        {
            string? skin = Variant?["skin"];
            return string.IsNullOrEmpty(skin) ? SkinBarked : skin!;
        }
    }

    /// <summary>Whether this variant is one of the debarked ones — the block-side reading of the same fact the
    /// half-log stack carries as <see cref="ItemHalfLog.DebarkedAttr"/>.</summary>
    public bool IsDebarkedVariant => SkinCode == SkinDebarked;

    /// <summary>Bare species code → its owning domain, per the mod system's index; <c>game</c> when there is nothing
    /// to go on. Shared by <see cref="WoodDomain"/> and by the texture pass, which cannot use the property because it
    /// runs BEFORE <c>OnLoaded</c> sets the instance's <c>api</c> and must be handed one.</summary>
    static string DomainOf(ICoreAPI? api, string? wood)
    {
        if (api == null || string.IsNullOrEmpty(wood)) return "game";
        ImmersiveWoodworkingModSystem? modSystem = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        return modSystem != null && modSystem.WoodDomains.TryGetValue(wood!, out string? found) ? found : "game";
    }

    /// <summary>This variant's species in another orientation, as a code. Null when the variant has no species — the
    /// caller then has nothing to place, which is the honest answer.
    ///
    /// <para>Does NOT validate the orientation: the 12 impossible pairs live in the asset's <c>skipVariants</c> and a
    /// second copy of that table here would be the exact duplication this class exists to prevent. That makes
    /// knowing the valid set the CALLER's job (T04.01 and T05.01 both have it in their DoD) — probing is not the
    /// supported way to find out, because an unregistered state costs one <see cref="Get"/> warning per species in a
    /// SHIPPED build, not just under IW_DEBUG. If a caller ever does need the set at runtime, derive it by sweeping
    /// the registry for this class (the way <see cref="SelfCheck"/> does) rather than restating skipVariants.
    ///
    /// <para>The <c>skin</c> axis is CARRIED, not chosen: this block's own state goes into the new code. That is what
    /// keeps the wrench (which reaches every orientation through here) from quietly re-growing bark on a debarked
    /// wall, and it is why the wrench needed no edit of its own for T04.01.</summary>
    public AssetLocation? CodeWithState(string rot, string run, string face) => CodeFor(WoodCode, SkinCode, rot, run, face);

    /// <summary>This variant's species in another orientation, as a block — the wrench's step (T05.01, T05.02). Null
    /// when that state does not exist, with the same log-once behaviour as <see cref="Get"/>.</summary>
    public Block? WithState(IWorldAccessor world, string rot, string run, string face)
        => Get(world, WoodCode, SkinCode, rot, run, face);

    /// <summary>
    /// The half-log slab block for a given species and orientation, or null if there is none — placement's entry point
    /// (T04.02), where the species comes from the held stack's <c>wood</c> attribute rather than from a block.
    ///
    /// <para>Takes the species alone, NOT the (species, domain) pair the stack stores, because there is no block to
    /// select with a domain: the variant code has no slot for one, and <c>InjectWoodVariants</c> registers exactly one
    /// variant per BARE species. Passing the domain would suggest a choice that does not exist. Where two mods ship
    /// the same species name, the mod system's collapse (<c>game</c> wins, else the ordinally smaller domain) has
    /// already decided which textures that one block wears, so a stack from the losing domain places the same block —
    /// the mod treats a shared species code as the same tree.
    ///
    /// <para>Never throws on an unknown species: the block registry is the authority (on a client it IS the server's
    /// registry, received over the wire), so a miss here means the species genuinely has no block — or a caller
    /// asking for one of the 12 orientations <c>skipVariants</c> removes. Vanilla's own <c>aged</c>, <c>veryaged</c>,
    /// <c>rotten</c> and <c>veryrotten</c> used to be the shipped example of the first case, since they have logs and
    /// appear in no <c>worldproperties/block/wood.json</c>; the axis now covers them (AuditFixPlan T04.05,
    /// <c>ImmersiveWoodworkingModSystem.DiscoverLogOnlySpecies</c>), so a null here no longer has a known cause.
    ///
    /// <para><b>A world whose wood mods were REMOVED does not land here, despite the intuition</b> (measured
    /// 2026-08-05, T07.02). The savegame remembers every block code it ever registered, and the load-time remapper
    /// installs a placeholder <c>Block</c> with <c>IsMissing = true</c> under that same code in <c>BlocksByCode</c> —
    /// so <c>world.GetBlock</c> returns a NON-null block for the codes of an uninstalled wood mod, and the null check
    /// below never sees them. Placing such a stack therefore lays the frozen placeholder and spends the half-log.
    /// That is deliberate and left alone: it is exactly what happens to every other mod's blocks, the cell becomes
    /// the real slab again the moment the wood mod is reinstalled, and the alternative (refusing on
    /// <c>IsMissing</c>) would make this mod behave unlike the rest of the game for no gain.
    /// </summary>
    public static Block? Get(IWorldAccessor world, string? wood, string skin, string rot, string run, string face)
        => Get(world, wood, skin, rot, run, face, quiet: false);

    /// <summary>As <see cref="Get"/>, with <paramref name="quiet"/> to suppress the log-once warning. Only
    /// <see cref="SelfCheck"/>'s deliberate negative probes pass true: they must not write a real-looking warning on
    /// every IW_DEBUG start, and the earlier trick of claiming the log keys first and releasing them afterwards was
    /// the only NON-monotonic write to these process-wide sets — which the client and the integrated server share in
    /// single-player, so a release could have un-suppressed or swallowed the other side's genuine warning.</summary>
    static Block? Get(IWorldAccessor world, string? wood, string skin, string rot, string run, string face, bool quiet)
    {
        AssetLocation? code = CodeFor(wood, skin, rot, run, face);
        Block? block = code == null ? null : world.GetBlock(code);
        if (block != null || quiet) return block;

        // Say WHICH of the two failures this is, and key the log-once accordingly. They have different owners: a
        // species with no variants at all is a fact about the world, whereas an orientation outside the registered 24
        // is a bug in the calling code. Note what is NOT in that list: a removed wood mod. Its codes still resolve to
        // engine placeholders, so they never reach this branch at all (see the remark on Get above). One shared key
        // would let a single probe of an impossible orientation swallow the species warning for the rest of the
        // session. One line per species per KIND, so the bound stays two lines per species even if a caller hammers
        // it. The PLAYER is told separately, at the placement itself (ItemHalfLog.OnHeldInteractStart, T04.05) — a
        // log line is for whoever ships the assets, not for whoever is holding the half-log.
        bool known = !string.IsNullOrEmpty(wood) && KnownSpecies(world, wood!);
        if (known)
        {
            if (missingState.TryAdd(wood!, 0))
                world.Logger.Warning(
                    "[immersivewoodworking] Half-log slab '{0}' is not a registered state; nothing will be placed or turned. The 24 valid states are the ones left after skipVariants, and this is not one of them.",
                    code);
        }
        else if (missingSpecies.TryAdd(wood ?? "", 0))
        {
            world.Logger.Warning(
                "[immersivewoodworking] No half-log slab block for wood species '{0}'; nothing will be placed. The slab's species axis is built at startup from every worldproperties/block/wood.json plus the wood states of every log and debarkedlog blocktype, and this species came from neither.",
                string.IsNullOrEmpty(wood) ? "(none)" : wood);
        }

        return null;
    }

    /// <summary>Whether this species HAS variants at all, i.e. whether a failed lookup is a bad orientation rather
    /// than an absent species. Used only to classify a failure for the log, never to gate the lookup itself.
    ///
    /// <para>Asks the block REGISTRY, by probing the one state that is valid for every species — the same
    /// <c>down-we-split</c> reference <see cref="SelfCheck"/> probes, valid because <c>skipVariants</c> only ever
    /// removes a rot/run pair that shares an axis. It used to ask the mod system's species index instead, and that
    /// answer stopped being the same one: the slab's axis is now the UNION of the worldproperties species and the
    /// species only a log blocktype declares (AuditFixPlan T04.05), so vanilla's <c>aged</c> has a variant while the
    /// index — which stays keyed on worldproperties, because the chopping block's own content is built from it — does
    /// not list it. The registry is the authority on both sides (on a client it IS the server's registry, received
    /// over the wire), so this cannot disagree with the <see cref="Get"/> it is explaining.</para></summary>
    static bool KnownSpecies(IWorldAccessor world, string wood)
    {
        AssetLocation? reference = CodeFor(wood, SkinBarked, "down", "we", "split");
        return reference != null && world.GetBlock(reference) != null;
    }

    /// <summary>The variant code for a species, surface state and orientation. The single formula both directions are
    /// built from; null for an absent species, so callers never construct <c>halflogslab--barked-down-we-split</c>.
    /// The <c>skin</c> part sits directly after the species and not at the end, matching the asset: every
    /// <c>*ByType</c> key in <c>halflogslab.json</c> is anchored either on the species slot or on <c>rot</c> with two
    /// parts behind it, and only a TAIL-anchored key (the creative filter) can tell the difference — put the axis
    /// last and that one silently matches both skins.</summary>
    static AssetLocation? CodeFor(string? wood, string skin, string rot, string run, string face)
        => string.IsNullOrEmpty(wood)
            ? null
            : new AssetLocation(ModDomain, BaseCode + "-" + wood + "-" + skin + "-" + rot + "-" + run + "-" + face);

    // ───────────────────────────── per-species name (T03.04) ─────────────────────────────
    //
    // ONE template per language and ZERO per-species strings: the species word comes from the vanilla
    // `material-{wood}` entry, which every wood mod already ships for its own trees, so a wood mod nobody has
    // written yet is still named correctly with no content from us. Vanilla itself does the opposite for the same
    // shape of block — `block-plankslab-oak-*`, `block-plankslab-birch-*`, 14 hand-written lines for its 12 species
    // plus `aged`/`veryaged` — which is affordable for a closed set and is not for a set that any mod can extend.
    //
    // Same shape as ItemHalfLog.HalfLogName and BlockChoppingBlock.ChoppingBlockName, with ONE difference that
    // matters: those two read the species off the STACK's attributes, and this reads it off the BLOCK VARIANT. That
    // is forced, not stylistic — a half-log slab stack carries no wood attribute at all (the species IS the variant),
    // and three of the callers build the stack themselves out of the block alone: the handbook (list entry and search
    // title, GuiHandbookItemStackPage.cs:41,70; page heading, CollectibleBehaviorHandbookTextAndExtraInfo.cs:147 —
    // all `stack.GetName()`), the creative list, and the chisel, which names a hewn block with
    // `majorityBlock.GetHeldItemName(new ItemStack(majorityBlock))` (BEMicroBlock.cs:414-421) — the path a player
    // reaches through canChisel:true. Keying off the stack would name every one of those with no species.
    //
    // BOTH overrides can run OFF the main thread: the handbook builds its page list in a pool task
    // (GuiDialogHandbook.QueueTask -> LoadPages_Async -> SurvivalHandbook -> stack.GetName()), which is ~one call per
    // species here. Safe by construction, not by luck — Lang reads dictionaries that were filled before the world
    // started, and the mod system's species -> domain index is assigned once in AssetsLoaded and never mutated after,
    // so a background reader sees a finished snapshot. Nothing here allocates render state or touches the world.

    /// <summary>
    /// The name of a STACK of this block: hand, inventory tooltip, creative list, handbook page title, and the name
    /// the chisel gives a block hewn out of this one. Mirrors <see cref="CollectibleObject.GetHeldItemName"/> but for
    /// the first append and the base's <c>Code == null</c> guard, which is redundant here because
    /// <see cref="WoodCode"/> already answers null for a code-less block.
    ///
    /// <para>The behaviour loop is kept rather than dropped, and that is not defensive padding: a third-party patch
    /// can attach a CollectibleBehavior to our block, and one on this very disk already appends through this hook —
    /// Toolsmith's <c>CollectibleBehaviorToolHandle</c> adds its "handle is wet" suffix here. Vanilla uses the hook
    /// only in its door and trapdoor behaviours. Swallowing that suffix would be a real bug with no symptom here to
    /// point at.
    /// </summary>
    public override string GetHeldItemName(ItemStack itemStack)
    {
        StringBuilder sb = new StringBuilder(SpeciesName());
        foreach (CollectibleBehavior bh in CollectibleBehaviors) bh.GetHeldItemName(sb, itemStack);
        return sb.ToString();
    }

    /// <summary>
    /// The name the block info HUD shows for a PLACED slab. This override is LOAD-BEARING as of T04.04 and must not
    /// be deleted as redundant: the base builds the name from <c>OnPickBlock(world, pos)?.GetName()</c> (vsapi
    /// Block.cs:2251-2262), and <see cref="OnPickBlock"/> now hands back the half-log ITEM, so without this the HUD
    /// would call a placed slab "Oak Half-log". It also keeps the two names independent on purpose: the thing in the
    /// world and the thing in the hand are different objects, and only one of them is a slab. Behaviour loop mirrored
    /// for the same reason as above.
    /// </summary>
    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        StringBuilder sb = new StringBuilder(SpeciesName());
        foreach (BlockBehavior bh in BlockBehaviors) bh.GetPlacedBlockName(sb, world, pos);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// "&lt;Species&gt; half-log slab" for this variant's species, from the per-language template
    /// (<c>block-halflogslab-name</c>, {0} = species); the resolver itself is <see cref="WoodName"/>, shared with the
    /// half-log item and the chopping block. A species that ships no <c>material-*</c> name anywhere reads as its own
    /// capitalized code and never as a blank.
    ///
    /// <para>One template per THING, authored per LANGUAGE — the species word is substituted, the word order is not
    /// assumed. The mod's existing templates prove why that matters: en puts the species first ("{0} Half-log"),
    /// ru and de append it in parentheses ("Половинка бревна ({0})"), zh-cn prefixes it with no space and pt-br
    /// joins it with "de". So the cost of this scheme is one line per language per thing, NOT an ungrammatical
    /// species-as-adjective; T06.01 owns the four translations of this block's line.
    ///
    /// <para>A variant with no species falls back to the generic name, which must stay the EXACT key
    /// <c>block-halflogslab</c> and cannot lean on the <c>block-halflogslab-*</c> net: <c>Lang.Get</c> is gated on
    /// <c>HasTranslation(key, findWildcarded: false)</c> and then looks <c>entryCache</c> up exactly, so wildcards
    /// never rescue it. The asset cannot produce a species-less variant — <c>wood</c> is the first variant group and
    /// is never empty — but <see cref="WoodCode"/> is nullable and a name is the wrong place to find out.
    /// </summary>
    string SpeciesName()
    {
        string? wood = WoodCode;
        if (wood == null) return Lang.Get("immersivewoodworking:block-halflogslab");

        // One template per skin (T04.01), the block-side twin of ItemHalfLog.HalfLogName. Two visibly different
        // blocks that place from two different items get two names, the way vanilla separates log from debarkedlog.
        // The species-less fallback above stays ONE string for both, for the reason given there.
        return WoodName.InTemplate(
            IsDebarkedVariant
                ? "immersivewoodworking:block-halflogslab-debarked-name"
                : "immersivewoodworking:block-halflogslab-name",
            wood, WoodDomain);
    }

    // ───────────────────────────── drop and pick block (T04.04) ─────────────────────────────
    //
    // A broken slab gives back the ITEM it was placed from, of the same species — closing the loop T04.02 opened.
    // Two engine seams need it, and both want the SAME stack:
    //
    //   GetDrops     break   -> the half-log WITH this variant's species
    //   OnPickBlock  middle  -> the same stack, so the picked item places the same block back
    //
    // There was a THIRD, `GetDropsForHandbook`, and it wanted the opposite stack — species-less, so that the handbook
    // would match it against the half-log's own bare page stack and grow an "Obtained by breaking" section with every
    // species collapsed into one cycling icon. AuditFixPlan T04.04 removed it along with the slab's handbook page:
    // the book assembles that section by sweeping `allStacks`, which is built from GetHandBookStacks over every
    // collectible (SurvivalHandbook.SetupBehaviorAndGetItemStacks), and a block in no creative tab is not in it. With
    // no caller left, the override was deleted rather than kept as a comment claiming a consumer it no longer has,
    // and `HalfLogStack` lost the `withIdentity: false` half it existed to serve — the factory below now has one
    // shape because both remaining seams want the same one.
    //
    // `drops` is deliberately absent from blocktypes/halflogslab.json rather than written there, because a JSON drop
    // cannot stamp the wood/woodDomain attributes; the engine's substituted self-drop stays in the `Drops` FIELD and
    // is now read by nothing we own. `"drops": []` would be behaviourally IDENTICAL for this block — the tool-hint
    // story that suggests otherwise is wrong, because the substituted self-drop carries no `Tool` (BlockType.
    // CreateBlock writes only Code/Type/Quantity) and Block.GetPlacedBlockInteractionHelp skips every drop whose
    // Tool is null (Block.cs:2203, 2212). Silence is kept for the honest reason instead: an empty array is an
    // ASSERTION that this block drops nothing, which is false, whereas an absent key says "the JSON path is not
    // used here" and leaves the field as the engine's own default for anything third-party that reads it.

    static readonly AssetLocation HalfLogItemCode = new AssetLocation(ModDomain, "halflog");

    /// <summary>The half-log item, warned about once if it is missing (see <see cref="warnedSpecies"/> for why the
    /// log-once sets are shaped this way). Separate from the other two sets: they are about a SPECIES, this is about
    /// the item asset being gone entirely, which is one message for the whole world and not one per species.</summary>
    static readonly ConcurrentDictionary<string, byte> missingItem = new ConcurrentDictionary<string, byte>();

    /// <summary>
    /// One half-log stack, the single factory behind both seams above. Shared rather than written twice
    /// because the failure of writing it twice is silent and nasty: <c>TreeAttribute</c> compares the whole KEY SET,
    /// so a stack carrying <c>wood</c> without <c>woodDomain</c> — or an empty string instead of an absent key —
    /// would refuse to merge with the half-logs the chopping block produces
    /// (<see cref="BlockEntityChoppingBlock"/> stamps exactly these two keys, or neither), and a player would end up
    /// with two stacks of the same thing. Vanilla has the anti-precedent on disk: <c>BlockTapestry</c> writes one
    /// attribute in <c>GetDrops</c> and two in <c>OnPickBlock</c>, and its picked stack does not stack with its
    /// dropped one.
    ///
    /// <para>ALWAYS stamped, since AuditFixPlan T04.04. There used to be a <c>withIdentity: false</c> shape that
    /// stripped both stamps for <c>GetDropsForHandbook</c>, whose comparison ran against a completely bare page
    /// stack; that override and its consumer are gone with the slab's handbook page, and a bool parameter with one
    /// live value is worse than none — it reads as a choice the caller has.
    ///
    /// <para>Exactly ONE item: placing spends exactly one (<c>ItemHalfLog.OnHeldInteractStart</c>), so breaking
    /// returns exactly one. This is a building block being taken apart, not a resource being harvested.
    /// </summary>
    ItemStack? HalfLogStack(IWorldAccessor world)
    {
        Item? halflog = world.GetItem(HalfLogItemCode);
        if (halflog == null)
        {
            if (missingItem.TryAdd("", 0))
            {
                world.Logger.Warning(
                    "[immersivewoodworking] The item '{0}' is not registered, so a broken half-log slab drops nothing and middle-clicking one hands back the block instead. The itemtypes asset is missing or was renamed.",
                    HalfLogItemCode);
            }
            return null;
        }

        ItemStack stack = new ItemStack(halflog, 1);

        string? wood = WoodCode;
        if (wood != null)
        {
            // Both keys or neither, in this order, matching the chop-time stamp exactly.
            stack.Attributes.SetString("wood", wood);
            stack.Attributes.SetString("woodDomain", WoodDomain);
        }

        // The surface state, through the item's own stamper so "absent means barked" is written in exactly one place
        // (T04.01). Independent of the species branch above on purpose: the two facts have different sources — one is
        // the variant's wood, the other its skin — and a species-less variant, if the asset ever produced one, would
        // still be honest about its bark.
        ItemHalfLog.StampDebarked(stack, IsDebarkedVariant);

        return stack;
    }

    /// <summary>
    /// What a broken slab leaves behind: one half-log of this variant's species.
    ///
    /// <para>This is <c>Block.GetDrops</c> verbatim (vsapi Block.cs:1276-1308) with ONE step replaced — "read the
    /// <c>Drops</c> field" becomes "build the half-log". The <c>BlockBehaviors</c> loop is kept for the same reason
    /// <see cref="GetHeldItemName"/> and <see cref="GetPlacedBlockName"/> keep theirs: <c>BlockBehaviors</c> is a
    /// public array a third-party patch can append to — this mod does exactly that to a vanilla BLOCK, appending
    /// <c>iw-flotsambreak</c> to <c>game:blocktypes/aquatic/flotsam.json</c> through <c>op: Add, path:
    /// "/behaviors/-"</c> — and a behaviour that means to add or replace a drop must still be able to.
    ///
    /// <para><b>Nothing here may read the world at <paramref name="pos"/></b>, and that is a live constraint rather
    /// than a style note: an explosion clears the cell BEFORE calling this (Block.cs:2536 runs ahead of :2542), a
    /// falling block calls it for a position it has already left, and the handbook calls it with the PLAYER's
    /// position (Block.cs:1256). The species is this block's own variant, so the constraint is met by construction —
    /// keep it that way. For the same reason <paramref name="byPlayer"/> can be null (explosions, decor, falling
    /// blocks), and the array must never contain a null (the drop path clones entries unchecked, Block.cs:1129).
    ///
    /// <para><paramref name="dropQuantityMultiplier"/> is deliberately not applied. One placed half-log is one
    /// half-log back, whatever the tool or the server event says — this is dismantling, not harvesting. Vanilla's own
    /// slab does the same (<c>BlockSlab.GetDrops</c> builds a bare <c>new ItemStack(block)</c>,
    /// vssurvivalmod Block/BlockSlab.cs:36-40), and so does this mod's chopping block on its attribute-carrying
    /// branch. The cost is real and bounded: <c>IServerEventAPI.BreakBlock</c>'s multiplier, and any mod that
    /// multiplies drops through it, does not act on this block. The multiplier is still passed BY REFERENCE into the
    /// behaviour loop, so a behaviour that wants to use it still can.
    ///
    /// <para>Creative mode and the client/server split are not tested here: the break path already refuses to spawn
    /// drops in creative before it ever calls this (Block.cs:1117).
    /// </summary>
    public override ItemStack[]? GetDrops(IWorldAccessor world, BlockPos pos, IPlayer? byPlayer, float dropQuantityMultiplier = 1f)
    {
        bool preventDefault = false;
        List<ItemStack> stacks = new List<ItemStack>();

        foreach (BlockBehavior behavior in BlockBehaviors)
        {
            EnumHandling handled = EnumHandling.PassThrough;

            // Nullable return, mirroring base: a PreventSubsequent behaviour is allowed to answer null, and the
            // engine's break path reads that as "no drops" (Block.cs:1121). Not coerced to an empty array here,
            // because being byte-identical to base is the whole point of reproducing this loop. `byPlayer` is
            // annotated nullable for the same honesty — the engine passes null on the explosion and decor paths
            // (Block.cs:2542, :1173), so a non-nullable parameter would only be lying to the analyser.
            ItemStack[]? fromBehavior = behavior.GetDrops(world, pos, byPlayer, ref dropQuantityMultiplier, ref handled);
            if (fromBehavior != null) stacks.AddRange(fromBehavior);
            if (handled == EnumHandling.PreventSubsequent) return fromBehavior;
            if (handled == EnumHandling.PreventDefault) preventDefault = true;
        }

        if (preventDefault) return stacks.ToArray();

        // Ours first, then the behaviours' — the order base uses for the JSON drops it is standing in for.
        ItemStack? half = HalfLogStack(world);
        if (half != null) stacks.Insert(0, half);

        return stacks.ToArray();
    }

    /// <summary>
    /// Middle-click: hand back the half-log ITEM, not the slab, because the item is what the player placed and what
    /// places it again. Base's own behaviour loop is reproduced around it (vsapi Block.cs:1319-1341) for the same
    /// reason <see cref="GetDrops"/> keeps its loop; only the final "a stack of myself" line is replaced.
    ///
    /// <para>Two limits worth knowing rather than discovering. In SURVIVAL the engine does not mint the picked stack
    /// at all — it looks for a matching one already in the hotbar or backpacks and quietly does nothing if there is
    /// none — so this only ever moves a half-log the player already has, which is the intended behaviour and is why
    /// the stack must merge with the ones the chopping block makes (see <see cref="HalfLogStack"/>). And in CREATIVE
    /// the picked item can no longer reproduce one exact orientation: the item always places <c>face = split</c> with
    /// rot and run re-derived from the aim, so a <c>bark</c>-out slab picks as an ordinary half-log. That is the
    /// wrench's axis by the plan's own decision (E05), not a regression this override can avoid — the signature takes
    /// no player, so it cannot branch on game mode without reading client state and becoming impure.
    /// </summary>
    public override ItemStack? OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        EnumHandling handled = EnumHandling.PassThrough;
        ItemStack? stack = null;

        foreach (BlockBehavior behavior in BlockBehaviors)
        {
            EnumHandling bhHandled = EnumHandling.PassThrough;
            ItemStack bhStack = behavior.OnPickBlock(world, pos, ref bhHandled);

            if (bhHandled != EnumHandling.PassThrough)
            {
                stack = bhStack;
                handled = bhHandled;
            }

            if (handled == EnumHandling.PreventSubsequent) return stack;
        }

        if (handled == EnumHandling.PreventDefault) return stack;

        // Falls back to a stack of the block itself, exactly as base would, if the item asset is gone — an empty hand
        // would be a worse answer than the wrong one.
        return HalfLogStack(world) ?? new ItemStack(this, 1);
    }

#if IW_DEBUG
    /// <summary>
    /// Round-trip every registered variant through the helpers above and report the result in one line (T03.03).
    /// Called from the mod system on both sides: server at <c>EnumServerRunPhase.GameReady</c>, client at
    /// <c>LevelFinalize</c>. Both, because the species → domain index is built per side from that side's own assets
    /// and is never sent over the wire, so only a client run can show that the client agrees with the server.
    ///
    /// <para><b>Not from AssetsFinalize, which is where it was first written and where it lied.</b> The block
    /// REGISTRY is complete there — the sweep found all 1368 variants and every <c>world.GetBlock</c> hit — but
    /// <c>Block.OnLoadedNative</c> has not run yet, so every instance's inherited <c>api</c> field is still null and
    /// <see cref="WoodDomain"/> silently returned its <c>game</c> fallback for all 57 species while the mod system's
    /// index held three domains. The engine says as much in its own phase list: <c>AssetsFinalize</c> is phase 4 and
    /// "all blocks are loaded" is only promised at <c>GameReady</c> (phase 6, vsapi Server/EnumServerRunPhase.cs).
    /// Anything reading a Block's own <c>api</c> — this class's species/domain pair included — has to wait for that.
    ///
    /// <para>This exists because the helpers have no caller yet — placement, drop and the wrench are E04/E05 — so
    /// without it the task would ship on reasoning alone. It stays afterwards as a regression guard on the ONE thing
    /// here that can silently rot: <see cref="CodeFor"/> writes the variant code by hand, so if a variant group is
    /// ever added, renamed or reordered in the asset, the formula and the engine's own code would drift and EVERY
    /// lookup would fail. Not silently — <see cref="Get"/> warns — but one line per species at the moment a player
    /// right-clicks is a worse place to learn it than a named count at startup, and the warning would read as "this
    /// species is missing" when in fact all of them are.
    ///
    /// <para>Stripped entirely from a shipped build (the whole method and its call site are inside IW_DEBUG), so the
    /// registry sweep costs players nothing.
    /// </summary>
    internal static void SelfCheck(ICoreAPI api)
    {
        int variants = 0, codeMismatch = 0, lookupMiss = 0, wrongBlock = 0, noSpecies = 0, stateMiss = 0, noSkin = 0;
        System.Collections.Generic.HashSet<string> species = new System.Collections.Generic.HashSet<string>();
        System.Collections.Generic.HashSet<string> skins = new System.Collections.Generic.HashSet<string>();
        System.Collections.Generic.HashSet<string> domains = new System.Collections.Generic.HashSet<string>();
        // One representative variant per species AND SKIN, for the name and drop sweeps below: neither a name nor a
        // drop carries an orientation, so all 24 states of one skin answer the same thing and checking them all would
        // only say it 24 times. The skin, unlike the orientation, DOES change both (T04.01) — keying this by species
        // alone would have left every debarked variant's name and drop unchecked.
        System.Collections.Generic.SortedDictionary<string, BlockHalfLogSlab> oneOfEach =
            new System.Collections.Generic.SortedDictionary<string, BlockHalfLogSlab>(System.StringComparer.Ordinal);
        // Creative / handbook exposure (AuditFixPlan T04.04), gathered in the same sweep: which variants got a creative
        // tab — the answer must be NONE — and how many carry one of the two keys that would put the family back into
        // the menu or the book behind the asset's back. Sorted by species so the failure report reads the same on
        // every run.
        System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<string>> exposedStates =
            new System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<string>>(System.StringComparer.Ordinal);
        int creativeStackVariants = 0, handbookInclude = 0, noHandbookExclude = 0;

        foreach (Block block in api.World.Blocks)
        {
            if (block is not BlockHalfLogSlab slab || slab.Code == null) continue;
            variants++;

            string? wood = slab.WoodCode;
            if (wood == null) { noSpecies++; continue; }
            species.Add(wood);
            domains.Add(slab.WoodDomain);

            // The axis read straight off the variant dictionary, NOT through SkinCode: the property defaults a
            // missing axis to `barked` so a mis-edited asset still renders, and the whole point here is to notice
            // that the axis went missing rather than to inherit the property's kindness.
            string rawSkin = slab.Variant["skin"] ?? "";
            if (rawSkin.Length == 0) noSkin++;
            string skin = slab.SkinCode;
            skins.Add(skin);
            string kind = wood + "|" + skin;
            if (!oneOfEach.ContainsKey(kind)) oneOfEach[kind] = slab;

            string rot = slab.Variant["rot"], run = slab.Variant["run"], face = slab.Variant["face"];

            // The formula must reproduce the engine's own code for this block, byte for byte.
            if (!slab.Code.Equals(slab.CodeWithState(rot, run, face))) codeMismatch++;

            // And the registry must hand the very same instance back from (species, skin, state).
            Block? found = Get(api.World, wood, skin, rot, run, face);
            if (found == null) lookupMiss++;
            else if (!ReferenceEquals(found, slab)) wrongBlock++;

            // Stepping to another state (what the wrench does) must land on a real block too. `down-we-split` is
            // valid for every species and is the state the creative menu ships, so it is always registered. This also
            // proves the step CARRIES the skin: WithState feeds the instance's own SkinCode back into the formula, so
            // a debarked variant can only find a block here if the debarked half of the axis registered as well.
            if (slab.WithState(api.World, "down", "we", "split") == null) stateMiss++;

            // Read the RESOLVED field rather than re-reading the asset's wildcard, because that field is what both
            // the creative menu and the handbook actually consume, and it is the one thing an edit to the wildcard
            // can change without changing anything else visible from here. The state is recorded with its SKIN so
            // that a failure names the exact variant that leaked back in, not just its species.
            if (slab.CreativeInventoryTabs != null && slab.CreativeInventoryTabs.Length > 0)
            {
                if (!exposedStates.TryGetValue(wood, out System.Collections.Generic.List<string>? states))
                    exposedStates[wood] = states = new System.Collections.Generic.List<string>();
                states.Add(skin + "-" + rot + "-" + run + "-" + face);
            }
            if (slab.CreativeInventoryStacks != null && slab.CreativeInventoryStacks.Length > 0) creativeStackVariants++;

            Vintagestory.API.Datastructures.JsonObject? handbookAttr = slab.Attributes?["handbook"];
            if (handbookAttr?["include"].AsBool(false) == true) handbookInclude++;
            if (handbookAttr?["exclude"].AsBool(false) != true) noHandbookExclude++;
        }

        // Negative cases: an unknown species and an absent one must both answer null and not throw. Quiet, so an
        // IW_DEBUG start does not print two real-looking [Warning] lines that are only this check talking to itself.
        bool unknownIsNull = Get(api.World, "iw_selfcheck_not_a_wood", SkinBarked, "down", "we", "split", quiet: true) == null;
        bool nullIsNull = Get(api.World, null, SkinBarked, "down", "we", "split", quiet: true) == null;

        // INDEPENDENT ORACLE. Everything above is self-referential to a degree: WoodDomain reads the same index the
        // comparison uses, and a species with no variant has no block to sweep, so the loop cannot see what is
        // missing. The set of species that can actually reach a half-log is the set of LOG blocks
        // (BlockEntityChoppingBlock.IsFullLogBlock — the same predicate the split uses, and LogBlockSpecies the same
        // call that stamps the stack).
        //
        // That set used to be WIDER than the injected one, and this is where it was reported: vanilla's log.json
        // carries a literal `aged` state and log-debarked.json `aged, veryaged, rotten, veryrotten`, none of which
        // appear in any worldproperties/block/wood.json, so those four had a half-log item and no slab at all. E04
        // decided (AuditFixPlan T04.05) and the injection now reads the log assets too
        // (ImmersiveWoodworkingModSystem.DiscoverLogOnlySpecies), so `noSlab` below is EXPECTED TO BE EMPTY on any
        // install, and a non-empty one is a real finding, reported as a Warning. It has fired for real once: with In
        // Dappled Groves or Wildcraft Trees installed, their patches turned vanilla's log files into one-line JSON
        // that the first, line-anchored log-asset probe could not see (2026-09-10).
        //
        // One caveat the domain comparison inherits: the species -> domain index stays keyed on worldproperties,
        // because it must answer IDENTICALLY on both sides (blocktypes are server-only) and because it also drives
        // the chopping block's own content. So a log-only species resolves to domain `game`, which is right for all
        // four vanilla ones and a graceful degrade for a hypothetical modded one — which domainDisagrees would then
        // name here. That is the intended report, not a false alarm: it is exactly the case worth knowing about.
        // Species -> EVERY domain that ships a log of it. A set, not a last-wins single value: a species carried by two
        // domains is the collapse case, which is a design decision, not a fault, and must not be reported as one.
        System.Collections.Generic.Dictionary<string, System.Collections.Generic.SortedSet<string>> logSpecies =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.SortedSet<string>>();
        foreach (Block block in api.World.Blocks)
        {
            if (!BlockEntityChoppingBlock.IsFullLogBlock(block)) continue;   // null-safe: it reads block?.FirstCodePart()
            // A savegame that once ran a wood mod keeps that mod's codes as IsMissing placeholders under their real
            // names. Their species left with the mod, so no axis could cover them, and counting them reported every
            // species of an uninstalled wood mod as a coverage gap.
            if (block.IsMissing) continue;
            (string? logWood, string logDomain) = BlockEntityChoppingBlock.LogBlockSpecies(block);
            if (string.IsNullOrEmpty(logWood)) continue;
            if (!logSpecies.TryGetValue(logWood!, out System.Collections.Generic.SortedSet<string>? domainsOfLog))
                logSpecies[logWood!] = domainsOfLog = new System.Collections.Generic.SortedSet<string>(System.StringComparer.Ordinal);
            domainsOfLog.Add(logDomain);
        }

        System.Collections.Generic.List<string> noSlab = new System.Collections.Generic.List<string>();
        System.Collections.Generic.List<string> collapsed = new System.Collections.Generic.List<string>();
        System.Collections.Generic.List<string> domainDisagrees = new System.Collections.Generic.List<string>();
        foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.SortedSet<string>> kv in logSpecies)
        {
            if (!species.Contains(kv.Key)) { noSlab.Add(kv.Key); continue; }

            Block? any = Get(api.World, kv.Key, SkinBarked, "down", "we", "split");
            if (any is not BlockHalfLogSlab slab) continue;

            if (kv.Value.Count > 1)
            {
                // Two mods, one species name. WoodDomains picked one and every variant wears it, so the drop in T04.04
                // will re-stamp a stack from the losing domain with the winner. Documented and accepted (T01.02), but
                // it happens SILENTLY everywhere else, so name it here — this is the only place that can.
                collapsed.Add(kv.Key + " (logs in " + string.Join("/", kv.Value) + ", slab uses " + slab.WoodDomain + ")");
            }
            else if (!kv.Value.Contains(slab.WoodDomain))
            {
                // One domain ships the logs and the slab named a different one: not the collapse, an actual fault in
                // the species -> domain index. The pair the block reports would not be the pair its own logs carry.
                domainDisagrees.Add(kv.Key + " (log " + string.Join("/", kv.Value) + " vs slab " + slab.WoodDomain + ")");
            }
        }
        noSlab.Sort(System.StringComparer.Ordinal);
        collapsed.Sort(System.StringComparer.Ordinal);
        domainDisagrees.Sort(System.StringComparer.Ordinal);

        if (noSlab.Count > 0)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log slab coverage: {0} of {1} species with logs have no slab variant [{2}]. A half-log of those can be carried but not placed: the species axis missed their log blocktypes (see DiscoverLogOnlySpecies).",
                noSlab.Count, logSpecies.Count, string.Join(", ", noSlab));
        }
        if (collapsed.Count > 0)
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log slab species collapse: {0} species are shipped by more than one domain [{1}]. Expected (a variant code has no domain slot); a stack from the losing domain places the same block and drops re-stamped to the winner.",
                collapsed.Count, string.Join(", ", collapsed));
        }
        if (domainDisagrees.Count > 0)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log slab domain disagreement on {0} species [{1}]: only one domain ships those logs and the slab named a different one, so the block would drop a stack naming a domain its own logs never had.",
                domainDisagrees.Count, string.Join(", ", domainDisagrees));
        }

        // CREATIVE AND HANDBOOK (AuditFixPlan T04.04). ZERO creative entries and — both ride on the SAME field — zero
        // handbook pages: `GetHandBookStacks` returns null for anything that is in no creative tab and carries no
        // `handbook.include` (vsapi Collectible.cs:2480-2485), so a variant with no tab mints no page. That shared
        // field is also the only way to count pages from HERE at all: the handbook is client-only
        // (ModSystemSurvivalHandbook.ShouldLoad == Client), while this check has to hold on a headless server.
        //
        // WHY ZERO, when the same check asserted "one per species" until 2026-08-28. A slab is not a thing the player
        // owns: the thing is the half-log ITEM, which is what the menu offers, what places this block and what
        // breaking it hands back. Listing both put 13 entries on vanilla and 58 on a full modpack against one thing,
        // and the block-stack taken from the menu behaved differently from the item — it placed one fixed state
        // regardless of aim, because orientation is derived in ItemHalfLog and TryPlaceBlock is not overridden here.
        // The price is named rather than discovered: handbook SEARCH by species is gone for slabs (FilterItems drops
        // hidden pages before matching), and the item's "Obtained by breaking" section is gone too, because that
        // section sweeps `allStacks`, which is exactly the union of GetHandBookStacks over every collectible.
        //
        // The oracle is the RESOLVED field on every variant, not a second read of the asset's wildcard. Three engine
        // escape hatches would put the family back silently, so all three are asserted rather than assumed:
        //   - a creative tab on ANY variant (the wildcard edited back to something that matches);
        //   - `creativeinventoryStacks`, which is not variant-filtered at all — BlockType.CreateBlock clones it onto
        //     every variant, 2736 copies here since the skin axis doubled the count — and names its tabs itself,
        //     bypassing the wildcard entirely;
        //   - `handbook.include`, which overrides the creative-tab test inside GetHandBookStacks, so an unqualified
        //     `include: true` would put all 48 states of every species back into the book. Not hypothetical:
        //     chopper/frame.json and sawmill/frame.json carried exactly that until 2026-08-01 — each printed one page
        //     per facing instead of one — found by this check's reasoning and fixed by dropping the key.
        // And `handbook.exclude` is asserted PRESENT on every variant, the one positive of the four: it is the
        // explicit half of the pair that hides this family, and losing it would leave the hiding resting on the
        // creative wildcard alone, where the next edit to that wildcard silently mints 57 pages again.
        System.Collections.Generic.List<string> creativeWrong = new System.Collections.Generic.List<string>();
        System.Collections.Generic.List<string> speciesSorted = new System.Collections.Generic.List<string>(species);
        speciesSorted.Sort(System.StringComparer.Ordinal);
        foreach (string wood in speciesSorted)
        {
            if (!exposedStates.TryGetValue(wood, out System.Collections.Generic.List<string>? states)) continue;

            states.Sort(System.StringComparer.Ordinal);
            creativeWrong.Add(wood + " (" + string.Join("+", states) + ")");
        }

        if (creativeWrong.Count > 0 || creativeStackVariants > 0 || handbookInclude > 0 || noHandbookExclude > 0)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log slab creative/handbook exposure FAILED: {0} of {1} species are in a creative tab and should be in none [{2}]; creativeinventoryStacks on {3} variants, handbook.include on {4}, handbook.exclude MISSING on {5} of {6}. Each of those puts the slab back next to the half-log item it is placed from, up to 48 entries and pages per species.",
                creativeWrong.Count, speciesSorted.Count, string.Join(", ", creativeWrong),
                creativeStackVariants, handbookInclude, noHandbookExclude, variants);
        }
        else
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log slab creative/handbook: none of {0} variants ({1} species) is in a creative tab, none carries creativeinventoryStacks or handbook.include, and all carry handbook.exclude, so the slab mints zero creative entries and zero handbook pages; the half-log item owns both.",
                variants, speciesSorted.Count);
        }

        // *ByType COVERAGE (T04.01). Every key in blocktypes/halflogslab.json is a wildcard over the WHOLE code
        // string, and the skin axis made that string one part longer. A key that stops matching does not fail loudly:
        // the variant silently keeps the ENGINE default — a full-cube collision box, a block solid on all six sides,
        // AO bleeding out of a thin panel — and five of the six below are invisible until a player walks into one.
        //
        // The oracle is the GEOMETRY, not a second reading of the globs: a slab lying flush against `rot` fills the
        // half of the cell on that side, is solid on that face alone, and (only when it lies flat) casts AO through
        // it. Anything the asset says that contradicts that is either a lost match or a wrong one, and both are
        // reported here rather than discovered in a wall.
        int boxWrong = 0, solidWrong = 0, aoWrong = 0, attachWrong = 0, barrierWrong = 0, burnWrong = 0;
        string firstByTypeBad = "";
        int faceSlotMissing = 0, faceSlotWrong = 0, materialSlotMissing = 0, noTextures = 0;
        string firstTexBad = "";
        foreach (Block block in api.World.Blocks)
        {
            if (block is not BlockHalfLogSlab slab || slab.Code == null || slab.WoodCode == null) continue;

            BlockFacing? rotFacing = BlockFacing.FromCode(slab.Variant["rot"]);
            if (rotFacing == null) continue;   // no rot to reason from; the code-shape checks above own that case

            Cuboidf want = HalfCellFor(rotFacing);
            bool flat = rotFacing == BlockFacing.UP || rotFacing == BlockFacing.DOWN;

            if (!IsOnlyBox(slab.CollisionBoxes, want) || !IsOnlyBox(slab.SelectionBoxes, want))
            {
                boxWrong++;
                if (firstByTypeBad.Length == 0) firstByTypeBad = slab.Code + " collision/selection box";
            }

            // sideSolidOpaqueAoByType seeds THREE dictionaries at once; SideSolid is the one a neighbour's tesselator
            // reads, so it stands for all three. Exactly the supporting face, and nothing else.
            for (int i = 0; i < 6; i++)
            {
                if (slab.SideSolid[i] == (i == rotFacing.Index)) continue;
                solidWrong++;
                if (firstByTypeBad.Length == 0) firstByTypeBad = slab.Code + " sideSolid[" + BlockFacing.ALLFACES[i].Code + "]";
                break;
            }

            // emitSideAoByType: the flat ones shadow the neighbour they rest on, the wall ones shadow nothing (a thin
            // panel throwing AO sideways draws dark stripes). Its catch-all "*" is what the wall rots depend on, and
            // a catch-all placed FIRST would swallow the two specific keys — so this is also the check that the
            // ordering rule the asset's comment states is still being followed.
            // EmitSideAo is a BITMASK here, not a bool array like SideSolid one field over — read it through each
            // facing's own Flag rather than an index, so the two spellings can never be confused.
            for (int i = 0; i < 6; i++)
            {
                bool emits = (slab.EmitSideAo & BlockFacing.ALLFACES[i].Flag) != 0;
                if (emits == (flat && i == rotFacing.Index)) continue;
                aoWrong++;
                if (firstByTypeBad.Length == 0) firstByTypeBad = slab.Code + " emitSideAo[" + BlockFacing.ALLFACES[i].Code + "]";
                break;
            }

            // The two keys that live INSIDE `attributes` are read as attributes, which is also the honest test of
            // whether the byType resolved onto this variant at all.
            if (slab.Attributes?["partialAttachable"].AsBool(false) != flat)
            {
                attachWrong++;
                if (firstByTypeBad.Length == 0) firstByTypeBad = slab.Code + " partialAttachable";
            }

            // Only the BOTTOM half holds water to y=0.5 with non-solid sides; every other rot is left to the engine's
            // own derivation from SideSolid.
            bool wantsBarrier = rotFacing == BlockFacing.DOWN;
            if ((slab.Attributes?["liquidBarrierOnSides"].Exists == true) != wantsBarrier)
            {
                barrierWrong++;
                if (firstByTypeBad.Length == 0) firstByTypeBad = slab.Code + " liquidBarrierOnSides";
            }

            // combustiblePropsByType is the one key anchored on the SPECIES slot rather than on rot, so it is the one
            // the skin axis could have shifted out from under. Petrified wood is stone and must not burn; everything
            // else must.
            bool shouldBurn = slab.WoodCode != "petrified";
            if ((slab.CombustibleProps != null) != shouldBurn)
            {
                burnWrong++;
                if (firstByTypeBad.Length == 0) firstByTypeBad = slab.Code + " combustibleProps";
            }

            // TEXTURE SLOTS (T04.02). Nine of them: the three surfaces the shape names its materials by, and the six
            // world-face codes the chisel reads. The assertion is deliberately NOT "this path is the bark one" — that
            // would be a second reading of the asset and would go red the moment a wood mod's species moves one slot
            // to its own domain — but "each face carries the SAME texture as the material slot for the surface that
            // face IS": round -> wood, split -> debarked_wood, end -> wood_corner. That holds on both skins, in every
            // domain, and through the degrade ladder in RetargetSpeciesTextures, because both sides of the comparison
            // are written from the same three resolved locations.
            //
            // What it catches is the defect the task fixed and the one way it can come back: an undeclared face code
            // makes VoxelMaterial.FromBlock hand that face block.Textures.First() instead, so a hewn half-log comes
            // out bark on all six sides — no rings, no split plane — and nothing anywhere says a word.
            if (slab.Textures == null || slab.Textures.Count == 0) { noTextures++; continue; }

            for (int i = 0; i < 6; i++)
            {
                BlockFacing side = BlockFacing.ALLFACES[i];
                if (!slab.Textures.TryGetValue(side.Code, out CompositeTexture? faceTex) || faceTex?.Base == null)
                {
                    faceSlotMissing++;
                    if (firstTexBad.Length == 0) firstTexBad = slab.Code + " has no texture code '" + side.Code + "'";
                    continue;
                }

                string material = SurfaceOf(rotFacing, slab.Variant["run"], slab.Variant["face"], side) switch
                {
                    SlabSurface.End => "wood_corner",
                    SlabSurface.Split => "debarked_wood",
                    _ => "wood",
                };
                if (!slab.Textures.TryGetValue(material, out CompositeTexture? wantTex) || wantTex?.Base == null)
                {
                    materialSlotMissing++;
                    if (firstTexBad.Length == 0) firstTexBad = slab.Code + " has no material slot '" + material + "'";
                    continue;
                }

                if (faceTex.Base.Equals(wantTex.Base)) continue;
                faceSlotWrong++;
                if (firstTexBad.Length == 0)
                    firstTexBad = slab.Code + " face '" + side.Code + "' is " + faceTex.Base + " but its surface is "
                                + material + " = " + wantTex.Base;
            }
        }

        int byTypeWrong = boxWrong + solidWrong + aoWrong + attachWrong + barrierWrong + burnWrong;
        if (byTypeWrong > 0)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log slab *ByType coverage FAILED over {0} variants (first: {1}): {2} wrong collision/selection box, {3} wrong sideSolid, {4} wrong emitSideAo, {5} wrong partialAttachable, {6} wrong liquidBarrierOnSides, {7} wrong combustibleProps. A byType key stopped matching and the engine default took over silently.",
                variants, firstByTypeBad, boxWrong, solidWrong, aoWrong, attachWrong, barrierWrong, burnWrong);
        }
        else
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log slab *ByType coverage: all {0} variants carry the box, sideSolid, emitSideAo, partialAttachable, liquidBarrier and combustible values their rot and species call for.",
                variants);
        }

        if (faceSlotMissing + faceSlotWrong + materialSlotMissing > 0)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log slab texture slots FAILED over {0} variants (first: {1}): {2} face codes absent, {3} pointing at the wrong surface, {4} material slots absent. Every absent code costs the CHISEL one face, which then takes block.Textures.First() instead — the round side on all six sides of a hewn slab.",
                variants, firstTexBad, faceSlotMissing, faceSlotWrong, materialSlotMissing);
        }
        else if (noTextures > 0)
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log slab texture slots: not checked, {0} of {1} variants carry no texture dictionary on this side.",
                noTextures, variants);
        }
        else
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log slab texture slots: all {0} variants declare six face codes, each resolving to the same texture as the material slot for the surface it is (round, split plane or sawn end).",
                variants);
        }

        // HANDBOOK ANCHOR: gone with the pages it guarded (AuditFixPlan T04.04). `halflog-handbook-text` and
        // `craftinginfo-woodworking-text` used to link straight AT one species page —
        // `handbook://block-immersivewoodworking:halflogslab-oak-barked-down-we-split` in all five lang files —
        // because a handbook page code IS the block code (GuiHandbookItemStackPage.PageCodeForStack) and the slab
        // minted one page per species, so a link had to name one of them. This probe existed for the failure MODE,
        // not the likelihood: a dead handbook anchor is COMPLETELY silent in game (GuiDialogHandbook
        // .OpenDetailPageFor misses the dictionary, returns false, and SurvivalHandbook.onHandBookLinkClicked
        // discards the bool). The slab now has no page for anything to link to, so both texts name the slab in plain
        // words instead and there is no anchor left to guard.
        //
        // The reason it is worth ten lines of gravestone rather than a silent delete is that the probe never actually
        // read those lang strings — it re-stated the literal in C# and asked the block registry (F-L1-09). Anything
        // that brings the link back must read the SHIPPED string, not a fourth copy of it: pull every
        // `handbook://block-immersivewoodworking:…` out of Lang.Get of those two keys with a regex and resolve each
        // extracted page code. Otherwise the guard passes while the link is dead, which is what it did here.

        // NAMES (T03.04). Two failures live here and NEITHER is visible without a running client otherwise. First,
        // a missing or renamed template key: Lang.Get hands the KEY back when NEITHER the current locale nor the
        // default one has it (vsapi Lang.cs:191-196 -> TranslationService.cs:461-464, key returned at :488), so every
        // menu would call the block "immersivewoodworking:block-halflogslab-name" and nothing would log. The `-*` net
        // cannot mask that, because Get is gated on HasTranslation(key, findWildcarded: false) and then reads
        // entryCache exactly — which is precisely what makes this probe valid. Note the default-locale leg: with the
        // template shipped in en.json only, this catches a rename today; after T06.01 adds four languages it stops
        // catching a key renamed in a TRANSLATED file while en still has it. Second, a species with no material-*
        // entry in any domain — legal, and the raw code is the intended fallback, but it is the one naming defect a
        // wood mod can hand us that we cannot fix from here, so it should be named rather than discovered in a
        // screenshot. The oracle is the composed name string, not the lookup: whatever chain SpeciesName walked, the
        // finished name must CONTAIN the species word the lang files actually hold, which fails loudly if the
        // template loses its {0}, if the override stops firing (the generic `block-halflogslab-*` net would answer
        // "Half-log Slab" and look perfectly healthy), or if the species is dropped on the way.
        System.Collections.Generic.SortedSet<string> rawCodeNamed =
            new System.Collections.Generic.SortedSet<string>(System.StringComparer.Ordinal);
        int nameNoSpecies = 0, placedNameThrew = 0;
        string sampleName = "";
        BlockPos anyPos = new BlockPos(0, 0, 0);       // placed names ignore it: only a BlockBehavior would read it
        foreach (System.Collections.Generic.KeyValuePair<string, BlockHalfLogSlab> kv in oneOfEach)
        {
            // The species off the BLOCK, not off the dictionary key: since T04.01 that key is "{wood}|{skin}", and
            // both skins are swept because they name themselves from two DIFFERENT templates. A set, not a list, so a
            // species with no material-* entry is reported once rather than once per skin.
            string wood = kv.Value.WoodCode ?? kv.Key;

            // A SECOND copy of the lookup chain on purpose, and the only place in the mod that keeps one: an oracle
            // assembled out of WoodName — the code under test — would agree with itself no matter what it did. All
            // THREE steps, ours last, because the sweep reaches woods the mod itself names: T04.05 folds the log-only
            // species into the slab and ships material-rotten / material-veryrotten for the two woods vanilla
            // leaves unnamed. A two-step oracle fell back to the raw code for exactly those, which happens to match
            // the finished English name and matches no other language — so a Russian dev client reported a naming
            // FAILURE that belonged to the oracle.
            string? word = Lang.GetIfExists("material-" + wood)
                        ?? Lang.GetIfExists(kv.Value.WoodDomain + ":material-" + wood)
                        ?? Lang.GetIfExists("immersivewoodworking:material-" + wood);

            // Case exactly where the resolver puts it (AuditFixPlan T09.05): a translated word is compared as the
            // lang file wrote it and ONLY the raw-code fallback is capitalised. An oracle that capitalised both
            // would hold "Дуб" against a name that correctly reads "(дуб)" and fail every Russian species. That is
            // what the old copy did, which is why it could neither catch nor refute the defect F-L3-14 named.
            if (string.IsNullOrEmpty(word)) { rawCodeNamed.Add(wood); word = char.ToUpperInvariant(wood[0]) + wood.Substring(1); }

            // BOTH names, because they are two different overrides serving two different HUDs (stack tooltip and
            // block info) and only one of them can be reached from a hand. The placed one is probed with a throwaway
            // position, which our override provably never reads (it reaches only the BlockBehaviors loop, and this
            // block declares no behaviours) — but BlockBehaviors is a public array a third-party patch can append to,
            // and at GameReady there is no block entity at 0,0,0, so a patched behaviour that dereferences one would
            // take a DEV BUILD down inside a self-check. Counted instead of thrown: coverage survives E05 adding the
            // wrench, and a hostile behaviour becomes a number in the report.
            string held = kv.Value.GetHeldItemName(new ItemStack(kv.Value));
            string placed;
            try { placed = kv.Value.GetPlacedBlockName(api.World, anyPos); }
            catch (System.Exception) { placedNameThrew++; placed = held; }

            if (held.IndexOf(word, System.StringComparison.Ordinal) < 0
                || placed.IndexOf(word, System.StringComparison.Ordinal) < 0) nameNoSpecies++;

            if (sampleName.Length == 0) sampleName = held;
        }

        // The raw-code fallback has fired on NO install measured so far — every species that is actually DISCOVERED
        // ships a material-* name: vanilla 12 of 12, wildcrafttree 44 of 44 (all under game:), floralzones 1 of 1,
        // and the two log-only woods the slab folds in (rotten / veryrotten) out of our own domain since T04.05 —
        // so probe it directly instead of shipping an unproven branch. (The install also carries a wood code with no
        // material-* at all, floralzones' `cecropiaobtusifolia`, but it lives as a literal state in a log blocktype
        // and not in any worldproperties, so DiscoverWoodSpecies never sees it. It is exactly the shape of species
        // that WOULD take this branch if discovery were ever widened.) Doubles as the template-key check: a missing
        // block-halflogslab-name in any locale would make Lang.Get hand the key back, and the key contains no
        // species code.
        string fallbackProbe = WoodName.InTemplate("immersivewoodworking:block-halflogslab-name", "iw_selfcheck_not_a_wood", "game");
        bool fallbackNames = fallbackProbe.IndexOf("Iw_selfcheck_not_a_wood", System.StringComparison.Ordinal) >= 0;

        if (nameNoSpecies > 0 || !fallbackNames || placedNameThrew > 0)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log slab names FAILED: {0} of {1} species/skin pairs produce a name without their own species word in it, {2} threw while naming the placed block, unnamed-species fallback {3}. Check that lang keys block-halflogslab-name and block-halflogslab-debarked-name exist and still contain their {4} placeholder.",
                nameNoSpecies, oneOfEach.Count, placedNameThrew, fallbackNames ? "ok" : "BROKEN", "{0}");
        }
        else if (rawCodeNamed.Count > 0)
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log slab names: {0} species/skin pairs named from their templates (e.g. \"{1}\"), {2} species fall back to the raw species code because no material-* entry exists in their own domain or in game [{3}].",
                oneOfEach.Count, sampleName, rawCodeNamed.Count, string.Join(", ", rawCodeNamed));
        }
        else
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log slab names: all {0} species/skin pairs named from their templates (e.g. \"{1}\"), none fell back to a raw code; a species with no material-* entry names as \"{2}\".",
                oneOfEach.Count, sampleName, fallbackProbe);
        }

        // DROP AND PICK BLOCK (T04.04). Two claims, neither checkable without breaking a block of every species.
        //
        // 1. The break drop ROUND-TRIPS: the species it stamps, fed back through the placement lookup, lands on this
        //    very block. That is the Definition of Done ("place → break → place keeps the species") written as a
        //    machine test, and the oracle is the block REGISTRY rather than a second copy of the stamping code.
        // 2. Middle-click yields a stack EQUAL to the drop, attributes included. Not cosmetic: an unequal stack does
        //    not MERGE with the half-logs the chopping block produces, and the player ends up carrying two piles of
        //    the same thing. Merging compares with Equals too, but through GlobalConstants.IgnoredStackAttributes
        //    (Collectible.GetMergableQuantity); this check is deliberately STRICTER — it passes no ignore list —
        //    because the two stacks here should be identical, not merely mergeable.
        //
        // A THIRD claim stood here until AuditFixPlan T04.04: that the handbook drop was species-less and compared
        // equal to the half-log's own page stack, without which the "obtained by breaking" section silently never
        // appeared. It went with the override it guarded — the slab is out of the handbook, so no section can be
        // built from it at all, and the claim would have been checking base's substituted self-drop against a probe.
        //
        // A throwaway position again (see the naming sweep above): neither claim reads the world at pos, and that is
        // a constraint GetDrops documents and the engine relies on.
        Item? halfLogItem = api.World.GetItem(HalfLogItemCode);

        int dropWrong = 0, dropNoRoundTrip = 0, pickMismatch = 0;
        string firstDropBad = "";

        foreach (System.Collections.Generic.KeyValuePair<string, BlockHalfLogSlab> kv in oneOfEach)
        {
            BlockHalfLogSlab slab = kv.Value;
            // null player on purpose: that is what the explosion and decor paths pass, so it is the harder case.
            ItemStack[]? drops = slab.GetDrops(api.World, anyPos, null);

            if (drops == null || drops.Length != 1 || drops[0]?.Collectible == null
                || !ReferenceEquals(drops[0].Collectible, halfLogItem))
            {
                dropWrong++;
                if (firstDropBad.Length == 0)
                    firstDropBad = kv.Key + " drops " + (drops == null ? "null" : drops.Length + " stack(s)");
                continue;
            }

            // BOTH stamps are fed back, and both come off the DROPPED STACK rather than off the block: the species
            // through the `wood` attribute, the skin through the debarked flag turned back into an axis state by the
            // same helper a placement uses. That is the whole loop the player walks (break -> carry -> place), so a
            // flag that failed to stamp, or an axis state spelled differently on the two sides, lands on a different
            // block or on none and is counted here (T04.01).
            string? droppedWood = drops[0].Attributes.GetString("wood", null);
            string droppedSkin = SkinFor(ItemHalfLog.IsDebarked(drops[0]));
            Block? backAgain = Get(api.World, droppedWood, droppedSkin, slab.Variant["rot"], slab.Variant["run"], slab.Variant["face"]);
            if (!ReferenceEquals(backAgain, slab))
            {
                dropNoRoundTrip++;
                if (firstDropBad.Length == 0)
                    firstDropBad = kv.Key + " stamps '" + (droppedWood ?? "(none)") + "'/" + droppedSkin + ", which resolves back to " + (backAgain?.Code?.ToString() ?? "nothing");
            }

            ItemStack? picked = slab.OnPickBlock(api.World, anyPos);
            if (picked == null || !picked.Equals(api.World, drops[0])) pickMismatch++;
        }

        if (dropWrong > 0 || dropNoRoundTrip > 0 || pickMismatch > 0)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log slab drops FAILED over {0} species/skin pairs: {1} do not drop exactly one half-log (first: {2}), {3} stamp a species+skin that does not resolve back to the same block, {4} pick a stack the drop would not merge with.",
                oneOfEach.Count, dropWrong, firstDropBad.Length == 0 ? "none" : firstDropBad, dropNoRoundTrip, pickMismatch);
        }
        else
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log slab drops: all {0} species/skin pairs drop one half-log whose species and skin resolve back to the same block, and middle-click matches the drop.",
                oneOfEach.Count);
        }

        // Name the domains rather than count them: "3 domain(s)" would have looked healthy while every species
        // silently fell back to `game`, which is precisely the failure this check exists to notice.
        System.Collections.Generic.List<string> domainList = new System.Collections.Generic.List<string>(domains);
        domainList.Sort(System.StringComparer.Ordinal);
        string domainNames = string.Join(", ", domainList);

        // Same reasoning one axis over: naming the skins that actually registered is what catches "the axis is in the
        // asset but every variant came out barked", which a count of 2 would hide.
        System.Collections.Generic.List<string> skinList = new System.Collections.Generic.List<string>(skins);
        skinList.Sort(System.StringComparer.Ordinal);

        // What the mod system itself holds, so a disagreement points at the property rather than at the index.
        ImmersiveWoodworkingModSystem? mods = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        System.Collections.Generic.HashSet<string> indexDomains = new System.Collections.Generic.HashSet<string>();
        if (mods != null) foreach (System.Collections.Generic.KeyValuePair<string, string> kv in mods.WoodDomains) indexDomains.Add(kv.Value);

        int broken = codeMismatch + lookupMiss + wrongBlock + noSpecies + stateMiss + noSkin;
        if (broken == 0 && unknownIsNull && nullIsNull && domains.SetEquals(indexDomains))
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log slab self-check: {0} variants, {1} species x {2} skins [{3}], domains [{4}], all round-trip; unknown and absent species both resolve to null.",
                variants, species.Count, skins.Count, string.Join("/", skinList), domainNames);
        }
        else if (broken == 0 && unknownIsNull && nullIsNull)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log slab self-check: {0} variants round-trip, but the blocks report domains [{1}] while the species index holds {2} domain(s). The (species, domain) pair is being read wrong.",
                variants, domainNames, indexDomains.Count);
        }
        else
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log slab self-check FAILED over {0} variants: {1} code mismatch, {2} lookup miss, {3} wrong block, {4} without species, {5} without a skin axis state, {6} state step miss; unknown->null {7}, absent->null {8}.",
                variants, codeMismatch, lookupMiss, wrongBlock, noSpecies, noSkin, stateMiss, unknownIsNull, nullIsNull);
        }
    }

    /// <summary>The half of the cell a slab lying flush against <paramref name="rot"/> fills: the unit cube halved
    /// along that face's own axis, on that face's side. Derived from the facing rather than transcribed from the
    /// asset's six boxes, so it is an independent oracle and not a copy of what it is checking.</summary>
    static Cuboidf HalfCellFor(BlockFacing rot)
    {
        Cuboidf c = new Cuboidf(0f, 0f, 0f, 1f, 1f, 1f);
        Vec3i n = rot.Normali;
        if (n.X > 0) c.X1 = 0.5f; else if (n.X < 0) c.X2 = 0.5f;
        if (n.Y > 0) c.Y1 = 0.5f; else if (n.Y < 0) c.Y2 = 0.5f;
        if (n.Z > 0) c.Z1 = 0.5f; else if (n.Z < 0) c.Z2 = 0.5f;
        return c;
    }

    /// <summary>Whether <paramref name="boxes"/> is exactly one box equal to <paramref name="want"/>. Tolerance
    /// because the values travel through JSON as doubles; 1/1024 of a block is far below anything the asset writes
    /// (its smallest step is 0.5) and far above float noise.</summary>
    static bool IsOnlyBox(Cuboidf[]? boxes, Cuboidf want)
    {
        if (boxes == null || boxes.Length != 1 || boxes[0] == null) return false;
        Cuboidf b = boxes[0];
        const float Eps = 0.001f;
        return System.Math.Abs(b.X1 - want.X1) < Eps && System.Math.Abs(b.Y1 - want.Y1) < Eps && System.Math.Abs(b.Z1 - want.Z1) < Eps
            && System.Math.Abs(b.X2 - want.X2) < Eps && System.Math.Abs(b.Y2 - want.Y2) < Eps && System.Math.Abs(b.Z2 - want.Z2) < Eps;
    }
#endif

    /// <summary>The three surfaces a half-log has. Not a texture and not a path: the QUESTION "what is this face
    /// made of", which the species and the skin then answer differently.</summary>
    internal enum SlabSurface { Round, Split, End }

    /// <summary>The world axis the log lies along, or null for a <c>run</c> nobody named. Spelled out rather than
    /// derived from <see cref="BlockFacing"/>, because <c>we</c> / <c>ns</c> / <c>ud</c> are the ASSET's words for
    /// the three axes and the engine has never heard of them.</summary>
    static EnumAxis? RunAxis(string? run) => run switch
    {
        "we" => EnumAxis.X,
        "ud" => EnumAxis.Y,
        "ns" => EnumAxis.Z,
        _ => (EnumAxis?)null,
    };

    /// <summary>
    /// Which surface a given world face of a given state is made of — the code side's ONE statement of the half-log's
    /// geometry, and the mirror of the rule <c>tools/gen-halflogslab-shape.py</c> bakes into the 24 shapes and prints
    /// into the blocktype's six face texture codes (T04.02).
    ///
    /// <para>The rule, read straight off the generator's <c>base_box</c> and <c>rotation</c>: the two faces along the
    /// log's own axis (<c>run</c>) are the sawn ends; the one face whose outward normal is the split plane's normal is
    /// the split; the remaining three are the round side. That normal is <c>rot</c>'s OPPOSITE on a <c>split</c>
    /// variant — the flat face looks AWAY from whatever the slab rests against — and <c>rot</c> itself on a
    /// <c>bark</c> one. <c>skin</c> does not enter: it changes what the round side and the ends are MADE of, never
    /// which face is which.
    ///
    /// <para>Two callers, deliberately not one: <see cref="RetargetSpeciesTextures"/> uses it to paint, and
    /// <c>SelfCheck</c> uses it to audit the asset. A third copy of this rule anywhere is a bug.
    /// </summary>
    internal static SlabSurface SurfaceOf(BlockFacing rot, string? run, string? face, BlockFacing side)
    {
        if (RunAxis(run) is EnumAxis axis && side.Axis == axis) return SlabSurface.End;

        BlockFacing splitFace = face == "split" ? rot.Opposite : rot;
        return side == splitFace ? SlabSurface.Split : SlabSurface.Round;
    }

    /// <summary>
    /// Resolve round side / split plane / end-grain for this variant's species AND skin and write them into every
    /// slot of <c>Textures</c> and <c>TexturesInventory</c> — the three shape materials and the six face codes alike.
    /// A no-op for every variant whose textures already live under <c>game:</c> at the paths the asset names — i.e.
    /// for a vanilla-only install nothing here changes a single byte, on either skin and on any face, because the
    /// asset already names the right family in all nine slots.
    /// </summary>
    void RetargetSpeciesTextures(ICoreAPI api)
    {
        // RelaxedReadOnlyDictionary: a missing key is null, never a throw. `placeholder` is NOT special-cased: if
        // InjectWoodVariants failed, the on-disk placeholder state survives and falls straight through to the
        // "nothing resolves" branch below, which is exactly what we want — ONE warning naming it, and a legible
        // aged-wood block, instead of 72 engine 404s for game:block/wood/bark/placeholder.
        string? wood = Variant?["wood"];
        if (string.IsNullOrEmpty(wood)) return;

        // The species' own domain, as decided once by the mod system (`game` wins, else the lexicographically smaller
        // domain). Absent = a species nobody discovered; `game` is then the only sensible guess and also what the
        // asset already says, so we quietly change nothing. Shared with WoodDomain (T03.03) so the paint and the
        // dropped stack can never name different domains for the same species; passed the api explicitly because this
        // runs before OnLoaded, when the instance's own `api` is still null.
        string domain = DomainOf(api, wood);

        // WHICH FAMILY, decided by the skin axis (T04.01) — and this pass is the AUTHORITY, not the asset. The
        // texturesByType in halflogslab.json says the same thing for a vanilla species, but this method rewrites
        // every slot it is given regardless of what it found there, so a debarked variant whose paths were only
        // corrected in JSON would be silently painted back into bark on the very next line. Both halves have to
        // agree; the JSON is what a reader sees, this is what the atlas gets.
        bool debarked = IsDebarkedVariant;

        // PER TEXTURE, not per species (WoodTextures). Wildcraft Trees is the proof this matters: it ships bark and
        // treetrunk in its OWN domain for all 44 of its species, but debarked for 43 of them only under game: — so
        // "the species came from domain X, therefore all three are X:" loses the split face on nearly every modded
        // wood, and dropping the game: leg loses bark and end grain on all of them.
        AssetLocation? round = WoodTextures.Find(api, domain, wood!, debarked ? WoodTexFamily.Debarked : WoodTexFamily.Bark);
        AssetLocation? split = WoodTextures.Find(api, domain, wood!, WoodTexFamily.Debarked);

        // The caps step DOWN one rung before they degrade sideways: vanilla ships treetrunk/debarked for most species
        // but not all (mossy and mahogany have a debarked side and no debarked cap), and a modded wood is likelier
        // still to ship only the ordinary one. A cap with a rim of bark on it is a smaller lie than a cap painted in
        // side grain, so the ordinary treetrunk is tried before the generic ladder below takes over.
        AssetLocation? end = debarked
            ? WoodTextures.Find(api, domain, wood!, WoodTexFamily.TreetrunkDebarked)
                ?? WoodTextures.Find(api, domain, wood!, WoodTexFamily.Treetrunk)
            : WoodTextures.Find(api, domain, wood!, WoodTexFamily.Treetrunk);

        // Degrade INSIDE the species before leaving it: a species that ships only one of the three is drawn in it
        // everywhere, which reads as wood. Measured coverage is 0 gaps across vanilla + wildcrafttree + herbarium, so
        // this is for the wood mod nobody has written yet, not for anything on disk today.
        round ??= end ?? split;
        split ??= round;
        end ??= round;

        if (round == null)
        {
            // Nothing at all for this species. Vanilla aged wood is always present (the game is always installed) and
            // is plain enough to read as "some wood", which beats the white unknown texture on a whole floor. The
            // fallback follows the skin too — falling back to BARK on a debarked slab would re-introduce the very
            // thing the axis removes, and vanilla ships debarked/aged as reliably as bark/aged.
            if (warnedSpecies.TryAdd(wood!, 0))
            {
                api.Logger.Warning(
                    "[immersivewoodworking] No bark, debarked or treetrunk texture for wood species '{0}' in domain '{1}', in game or on its own log block; half-log slabs of that species fall back to aged wood.",
                    wood, domain);
            }
            round = split = end = new AssetLocation("game", WoodTextures.PathOf(debarked ? WoodTexFamily.Debarked : WoodTexFamily.Bark, "aged"));
        }

        // WHICH SLOT GETS WHICH FAMILY — two questions, because the nine keys are named two different ways. The
        // three MATERIAL keys say what they carry, so their own name answers it (wood_corner tested BEFORE wood,
        // since it also starts with "wood"). The six FACE keys (T04.02) say only a direction, and what a direction
        // carries is a property of the STATE, so those go through SurfaceOf — the same rule the shape generator bakes
        // into the shapes and prints into the asset. Anything else falls to the round side, which is what every face
        // got before the codes existed: a degraded look on a mis-edited asset, never a magenta one.
        BlockFacing? rotFacing = BlockFacing.FromCode(Variant?["rot"]);
        string? run = Variant?["run"], faceState = Variant?["face"];

        AssetLocation Want(string key)
        {
            if (key.StartsWith("wood_corner")) return end!;
            if (key.StartsWith("debarked")) return split!;

            BlockFacing? side = BlockFacing.FromCode(key);
            if (side == null || rotFacing == null) return round!;

            return SurfaceOf(rotFacing, run, faceState, side) switch
            {
                SlabSurface.End => end!,
                SlabSurface.Split => split!,
                _ => round!,
            };
        }

        int changed = Retarget(Textures, Want) + Retarget(TexturesInventory, Want);

#if IW_DEBUG
        // One line per SPECIES AND SKIN (not per orientation — that would be 24 identical lines each), and only for
        // the ones we actually moved off game:. This is the positive proof the seam fired at all: a headless server
        // never calls OnCollectTextures, so a client run with -p:IwDebug=true is the only place it can be observed.
        if (changed > 0 && loggedSpecies.TryAdd(wood! + "|" + SkinCode, 0))
        {
            api.Logger.Notification("[immersivewoodworking] Half-log slab '{0}' ({1}): round={2}, split={3}, end={4}.",
                wood, SkinCode, round, split, end);
        }
#endif
    }

#if IW_DEBUG
    static readonly ConcurrentDictionary<string, byte> loggedSpecies = new ConcurrentDictionary<string, byte>();
#endif

    /// <summary>
    /// Replace the SLOTS in <paramref name="dict"/> whose resolved path differs from the one
    /// <paramref name="want"/> names for that key.
    ///
    /// <para>Slot replacement with a <c>Clone()</c>, never an in-place <c>Base = …</c>: the atlas manager de-duplicates
    /// plain <c>CompositeTexture</c>s across the WHOLE block list, so the object sitting in our <c>game:block/wood/bark/oak</c>
    /// slot is very likely the same instance vanilla's <c>log-oak</c> holds — mutating it would repaint vanilla logs.
    /// <c>Clone()</c> copies Base/Rotation/Alpha/overlays/alternates but NOT <c>Baked</c> (vsapi CompositeTexture.cs),
    /// so the replacement re-bakes while the shared original is left alone.
    ///
    /// <para>Key ORDER is preserved, and it still matters — though since T04.02 no longer to the chisel, which now
    /// finds all six face codes declared and never reaches its <c>block.Textures.First()</c> fallback. What still
    /// reads the first key is everything that wants "a texture for this block" with no face to ask about. We only
    /// ever assign to an EXISTING key, and <c>FastSmallDictionary</c>'s setter overwrites in place (vsapi
    /// FastSmallDictionary.cs) — no key is added, removed or moved, and the dictionary OBJECT is never swapped (base
    /// hard-casts it to <c>TextureDictionary</c>).
    ///
    /// <para>The classification itself lives in the CALLER, not here: it needs the variant's state, and this loop is
    /// run twice over two dictionaries that must be painted identically. One selector, two passes.
    /// </summary>
    static int Retarget(IDictionary<string, CompositeTexture>? dict, System.Func<string, AssetLocation> want)
    {
        if (dict == null || dict.Count == 0) return 0;

        int changed = 0;
        List<string> keys = new List<string>(dict.Keys);        // snapshot: we assign into the dictionary while reading it
        foreach (string key in keys)
        {
            AssetLocation target = want(key);

            CompositeTexture? current = dict[key];
            if (current?.Base == null || current.Base.Equals(target)) continue; // already right (every vanilla species)

            CompositeTexture replacement = current.Clone();
            replacement.Base = target.Clone();
            dict[key] = replacement;
            changed++;
        }

        return changed;
    }

}
