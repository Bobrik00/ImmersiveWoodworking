using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

/// <summary>
/// Plays the chop swing while RMB is held with an axe on the stand, timed so the axe-impact frame lands
/// exactly on ChopSeconds — the same instant the chop resolves and the hit sound plays.
///
/// This class manually drives the animator's per-swing state, on purpose: each piece defeats a specific
/// engine easing/blend artifact. Before "simplifying" the logic, know the bugs it exists to prevent (all
/// observed in-game and fixed here):
///   1. swing not restarting from frame 0 (a re-press resuming a half-finished / easing-out swing)
///   2. swing playing twice — a "ghost" wind-up replayed after a tap/release, or the axe bouncing back
///   3. wrong ease-in/ease-out: a one-frame drop to idle, incl. when chopping out of the raised
///      "axe ready" two-handed stance
///   4. the very first swing after a world/game load not playing (cold-start activation)
///   5. the swing not playing to completion
/// All of this is COSMETIC: the authoritative chop (BlockEntityChoppingBlock.Chop) is server-side and
/// HasContent-guarded, independent of the animation, and every animator reach-in below is null-guarded.
/// The carry logic relies on a few VS-internal animation invariants (noted at ResetChopAnim); they are the
/// price of a smooth single swing and are sensitive to engine updates.
/// </summary>
public class BlockChoppingBlock : Block
{
    // Player animation from patches/woodchop_animations.json; the engine auto-plays the "-fp" variant in 1st person.
    const string ChopAnimCode = "woodchop";

    // The two animation variants the per-swing animator manipulation must cover: the 3rd-person body swing and
    // the first-person ("-fp") hands the engine auto-plays for the holder. Named once so ResetChopAnim /
    // CancelChopAnim (and the debug trace) iterate the same set and can't fall out of step when a variant is
    // added. NOT the same list as EntityBehaviorChopAnim.CustomAnimCodes, which also carries the saw animations
    // for keyframe backfill — iterating that here would wrongly touch "sawing".
    static readonly string[] ChopAnimVariants = { ChopAnimCode, ChopAnimCode + "-fp" };

    // Impact = the LAST authored keyframe of the swing (onAnimationEnd=Hold freezes that pose). Anims advance
    // at 30 fps * AnimationSpeed, so StartChopAnimation inverts that to put the impact frame exactly on
    // ChopSeconds. We read it live off the loaded animation (GetImpactFrame) so patches/woodchop_animations.json
    // is the single source of truth — a re-export can never silently desync a hard-coded constant.
    // FallbackImpactFrame is used only when the animation can't be read off the entity (e.g. a non-seraph
    // player shape that doesn't define woodchop).
    const int FallbackImpactFrame = 19;

    // Cached impact frame (= last keyframe of woodchop), resolved lazily once from the asset. impactFrame is an
    // instance field on the single (variant-less) chopping-block instance — the wood species is a stack attribute
    // now, not a variantgroup, so there's just one Block instance and one cached copy (resolves to 19). -1 = not
    // yet resolved. We only ever cache a real reading, never the fallback, so a first chopper whose shape lacks
    // the animation can't poison the cache.
    int impactFrame = -1;

    // Read live (not snapshotted) so a client always uses the SERVER's authoritative ChopSeconds: the mod
    // system swaps in a new Config object on sync and we go through it each time. Drives both the chop timer
    // and the swing speed, keeping the visual impact and the resolve in lockstep (no MP audio/visual drift).
    ImmersiveWoodworkingModSystem modSystem = null!;
    float ChopSeconds => modSystem.Config.ChopSeconds;
    // Pause between auto-restocked chops (AutoRestock continuous loop), live from the server-synced config.
    float ChopRestockDelaySeconds => modSystem.Config.ChopRestockDelaySeconds;

    // Interaction-help shown on the block: which set depends on whether the stand holds something.
    // Empty -> "place wood to chop"; occupied -> "chop" + "take". The heavy stack lists (every choppable /
    // every axe) are resolved once client-side and cached process-wide (ObjectCacheUtil);
    // GetPlacedBlockInteractionHelp just picks a set by content state.
    // The two CONFIG-FREE sets are built in OnLoaded; the empty-stand set reads the config and is built lazily on
    // first use instead — see EmptyInteractions() (AuditFixPlan T02.05 / F-L3-10). null there = not built yet.
    WorldInteraction[]? emptyInteractions;
    WorldInteraction[] contentInteractions = System.Array.Empty<WorldInteraction>();
    // Stand with an axe stuck in it: Ctrl+RMB pulls the axe back out (chopping is disabled meanwhile).
    WorldInteraction[] toolInteractions = System.Array.Empty<WorldInteraction>();
    // The axe accept-list, shared by the chop hint (occupied stand) and the stick-the-axe hint (empty stand).
    // A field rather than an OnLoaded local because EmptyInteractions() needs it later than OnLoaded.
    ItemStack[] axeStacks = System.Array.Empty<ItemStack>();

    // T03.01: per-species GPU meshes for the held / GUI / dropped item, keyed by "{domain}:{wood}". Held in a
    // process-wide ObjectCacheUtil entry, lazily built+uploaded in OnBeforeRender and freed in OnUnloaded. We
    // GetOrCreate it fresh each OnBeforeRender rather than caching a field reference — the exact
    // BlockShapeFromAttributes idiom, with no field-vs-cache divergence to reason about.
    //
    // GetOrCreate is NOT an atomic or thread-safe synchronisation point, whatever this comment used to claim
    // (AuditFixPlan T05.01 / F-L5-07, verified 2026-08-28): api.ObjectCache is a plain Dictionary<string, object>
    // (ICoreAPI.cs:122) and GetOrCreate is a bare TryGetValue → create → assign with nothing between them (vsapi
    // Util/ObjectCacheUtil.cs), so two threads racing it can both build, and a write concurrent with any read can
    // corrupt the dictionary outright. What makes THIS use safe is the CALLER, not the helper: OnBeforeRender is
    // main-thread, as is every other GetOrCreate in the mod EXCEPT the two static-mesh lookups in
    // BlockEntitySawmill/BlockEntityChopper.OnTesselation, which run on the tesselation thread — those two are what
    // F-L5-07 actually reports and they are still open. So: put a new GetOrCreate on the main thread, and do not read
    // the two off-thread ones as precedent.
    const string HeldMeshCacheKey = "iwChoppingBlockHeldMeshes";

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        modSystem = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();

        // T03.02: replace the JSON single creative entry with one stack per wood species (each carrying the wood
        // attribute), so the creative menu and — via the default GetHandBookStacks — the handbook show every species.
        // Both sides: api.World is ready here; the list is read client-side but setting it server-side is harmless.
        BuildCreativeStacks(api);

        // Interaction help is purely client-side; nothing on the server needs it. (The held-mesh cache is created
        // lazily in OnBeforeRender, so there is nothing to set up for it here.)
        if (api is not ICoreClientAPI capi) return;

        // The axe accept-list, resolved once and shared by the chop hint (occupied stand) and the stick hint (empty
        // stand). Hoisted out of contentInteractions so both can reuse it. An axe override from another mod shouldn't
        // be able to crash load, so each GetHandBookStacks is guarded. Config-free, so building it here is safe.
        axeStacks = ObjectCacheUtil.GetOrCreate(api, "iwChoppingBlockAxes", () =>
        {
            List<ItemStack> list = new List<ItemStack>();
            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                if (obj.Tool != EnumTool.Axe) continue;
                List<ItemStack>? stacks;
                try { stacks = obj.GetHandBookStacks(capi); }
                catch { continue; }
                if (stacks != null) list.AddRange(stacks);
            }
            return list.ToArray();
        });

        // The EMPTY-stand set is NOT built here — it reads the config, and on the client the server's config has not
        // arrived yet at OnLoaded. See EmptyInteractions() below (AuditFixPlan T02.05 / F-L3-10).

        // Occupied stand: hold right-click with an axe to chop; Ctrl+right-click to take the item back.
        contentInteractions = ObjectCacheUtil.GetOrCreate(api, "iwChoppingBlockContentInteractions", () =>
            new WorldInteraction[]
            {
                new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-choppingblock-chop",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = axeStacks
                },
                new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-choppingblock-take",
                    HotKeyCode = "ctrl",
                    MouseButton = EnumMouseButton.Right
                }
            });

        // Stand with an axe stuck in it: Ctrl+right-click pulls the axe back out (chopping is disabled meanwhile).
        toolInteractions = ObjectCacheUtil.GetOrCreate(api, "iwChoppingBlockToolInteractions", () =>
            new WorldInteraction[]
            {
                new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-choppingblock-take-axe",
                    HotKeyCode = "ctrl",
                    MouseButton = EnumMouseButton.Right
                }
            });
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        BlockEntityChoppingBlock? be = GetBE(world, selection);
        WorldInteraction[] own = be != null && be.HasTool ? toolInteractions
                               : be != null && be.HasContent ? contentInteractions
                               : EmptyInteractions();
        return own.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
    }

    /// <summary>The EMPTY-stand help set ("place wood to chop", plus Shift+RMB to park the axe in the top), built on
    /// FIRST USE rather than in <see cref="OnLoaded"/> and then cached process-wide like the other two sets and
    /// memoized in <see cref="emptyInteractions"/>.
    ///
    /// The lateness IS the fix (AuditFixPlan T02.05 / F-L3-10). The advertised "place" stacks depend on
    /// <see cref="ImmersiveWoodworkingConfig.AllowFirewoodToSticks"/> — with it off, firewood can't be laid and must
    /// not be suggested — and on the client the server's config arrives over the network AFTER Block.OnLoaded has run.
    /// A set baked there therefore advertised the client's LOCAL DEFAULT forever, offering firewood on a server that
    /// forbids it. By the time a player can look at a placed block the config is in, so the first call here reads the
    /// authoritative value. Help is still not re-evaluated later (memory vs-interaction-help-no-poll), which costs
    /// nothing: the config does not change again within a session.
    ///
    /// The put-stacks are the exact accept-list (<see cref="BlockEntityChoppingBlock.IsChoppable"/>), minus our own
    /// block so the help isn't cluttered by the chopping block itself (placing one on another is allowed but pointless
    /// to suggest). Client-only — the server never draws help, and gets an empty array.</summary>
    WorldInteraction[] EmptyInteractions()
    {
        if (emptyInteractions != null) return emptyInteractions;
        if (api is not ICoreClientAPI capi) return System.Array.Empty<WorldInteraction>();

        return emptyInteractions = ObjectCacheUtil.GetOrCreate(api, "iwChoppingBlockEmptyInteractions", () =>
        {
            // The server-synced config, read HERE (first help query) rather than at load — see the method doc.
            ImmersiveWoodworkingConfig config = modSystem.Config;
            List<ItemStack> choppables = new List<ItemStack>();
            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                if (obj is BlockChoppingBlock) continue;
                // Cheap code-part gate FIRST (vanilla idiom): only resolve handbook stacks for plausible
                // choppables, so we never call GetHandBookStacks on unrelated items — some (e.g. vanilla
                // ItemPressedMash) throw inside it during the block-load phase, which would crash mod load.
                if (!BlockEntityChoppingBlock.CouldBeChoppable(obj)) continue;
                List<ItemStack>? stacks;
                try { stacks = obj.GetHandBookStacks(capi); }
                catch { continue; } // stay resilient if a (modded) choppable still throws in GetHandBookStacks
                if (stacks == null) continue;
                foreach (ItemStack stack in stacks)
                {
                    if (BlockEntityChoppingBlock.IsChoppable(stack, config)) choppables.Add(stack);
                }
            }

            return new WorldInteraction[]
            {
                new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-choppingblock-put",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = choppables.ToArray()
                },
                new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-choppingblock-stick-axe",
                    // "shift", NOT "sneak". They are two separately rebindable hotkeys, and the gate in
                    // OnBlockInteractStart reads Controls.ShiftKey, which SystemPlayerControl feeds from `shift`
                    // (`sneak` feeds Controls.Sneak, a different flag). The HUD resolves this code to the player's
                    // CURRENT mapping, so naming the wrong one advertises the wrong key the moment a player separates
                    // the two in the controls settings. vsapi says which to use in as many words
                    // (EntityControls.cs:239-240: "Use Controls.ShiftKey instead for mouse interaction modifiers, as
                    // it is a separable control"), and vanilla agrees 66 to 1 across vssurvivalmod.
                    HotKeyCode = "shift",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = axeStacks
                }
            };
        });
    }

    // ───────────────────────────── E03: inventory surfaces (held / GUI / drop) + per-species name ─────────────────────────────

    /// <summary>
    /// Render the stand with its stored species' textures in EVERY item surface — in hand (1st/3rd person), in the
    /// GUI/inventory/creative icon, and as a dropped item — by swapping <paramref name="renderinfo"/>.ModelRef to a
    /// per-species GPU mesh (T03.01). Same idiom as vanilla BlockShapeFromAttributes.OnBeforeRender: lazily build +
    /// upload one MultiTextureMeshRef per species into a process-wide cache (freed in <see cref="OnUnloaded"/>). The
    /// mesh is the SAME geometry the in-world stand uses (<see cref="BlockEntityChoppingBlock.BuildStandMesh"/>, M1
    /// bark / M2 end-grain), so held and placed look identical; only the upload differs (the world mesh is baked into
    /// the chunk terrain mesh instead). A bare / legacy stack (no recorded species) keeps the block's own default-oak
    /// mesh + default transforms from base. Runs on the main render thread, where capi.Tesselator and the GPU upload
    /// are both legal — the engine drives item OnBeforeRender there, exactly as BlockShapeFromAttributes relies on.
    /// </summary>
    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);

        string? wood = itemstack.Attributes.GetString("wood", null);
        if (wood == null) return; // bare / legacy stack → keep base's default (oak) mesh + the default transforms

        string woodDomain = itemstack.Attributes.GetString("woodDomain", "game");
        string key = woodDomain + ":" + wood;

        Dictionary<string, MultiTextureMeshRef> cache =
            ObjectCacheUtil.GetOrCreate(capi, HeldMeshCacheKey, () => new Dictionary<string, MultiTextureMeshRef>());

        if (!cache.TryGetValue(key, out MultiTextureMeshRef? meshref))
        {
            MeshData? mesh = BlockEntityChoppingBlock.BuildStandMesh(capi, wood, woodDomain);
            if (mesh == null) return; // shape missing → leave base's default mesh; retry on the next render
            meshref = capi.Render.UploadMultiTextureMesh(mesh);
            cache[key] = meshref;
        }

        // Same geometry as the default block mesh, so base's per-target transform (gui/ground/hand) already fits;
        // we only swap which textured mesh is drawn.
        renderinfo.ModelRef = meshref;
    }

    // ───────────────────────────── T04.06: particles show the stand's own wood ─────────────────────────────
    //
    // The species is painted onto the MESH at tesselation and never reaches Block.Textures, which stays the JSON
    // default (oak) for every stand — so everything the engine colours off that dictionary showed oak regardless of
    // the wood: the burst when a stand is mined or broken, and the chips the automated chopper throws while chopping
    // a stand fed to it as a workpiece. Chips are particles, so this is fixed with COLOUR, not with a texture swap
    // (Block.Textures can only be re-pointed in OnCollectTextures, long before any species is known).
    //
    // Both engine seams are overridden because they answer different questions: the WORLD one is asked about a
    // position (block-breaking / block-broken particles, vsapi BlockBreakingParticleProps.GetRgbaColor), the STACK one
    // about a loose stack (the chopper's chip burst, which stands at the MACHINE's position — asking the world there
    // would find a chopper, not a stand). Both fall back to base when no species is recorded, so a bare / legacy stand
    // keeps the default look it already renders with.

    /// <summary>Particle colour for a stand in the WORLD: a random pixel of the stored species' bark
    /// (<see cref="BlockEntityChoppingBlock.BarkTex"/>), so a birch stand throws pale chips and an oak one dark. Falls
    /// back to base for a bare / legacy stand, or if the block entity is already gone. It is not: the engine spawns
    /// both bursts BEFORE clearing the block (vsapi <c>Block.OnBlockBroken</c> → SpawnBlockBrokenParticles → SetBlock),
    /// and evaluates the colour synchronously at spawn, so the stand is still standing when we are asked.</summary>
    public override int GetRandomColor(ICoreClientAPI capi, BlockPos pos, BlockFacing facing, int rndIndex = -1)
    {
        if (capi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityChoppingBlock be && be.Wood != null)
        {
            return SpeciesColor(capi, be.Wood, be.WoodDomain, rndIndex);
        }
        return base.GetRandomColor(capi, pos, facing, rndIndex);
    }

    /// <summary>Particle colour for a stand as a loose STACK — the species off the stack's own attributes, the same
    /// pair the held/GUI mesh reads. Used by the automated chopper's chip burst for a fed stand, and by the engine's
    /// stack-based cube particles. Falls back to base (the default-oak block colour) for a bare / legacy stack.</summary>
    public override int GetRandomColor(ICoreClientAPI capi, ItemStack stack)
    {
        if (BlockEntityChoppingBlock.StackSpecies(stack) is { } species)
        {
            return SpeciesColor(capi, species.wood, species.domain, -1);
        }
        return base.GetRandomColor(capi, stack);
    }

    /// <summary>Sample a random pixel of one species' bark out of the block atlas — the same load-or-insert the stand
    /// mesh's texture source uses (<see cref="ChoppingBlockTexSource"/>.GetOrCreate), so the chips can never be cut
    /// from a different image than the paint. A species whose texture is missing resolves to the atlas' "unknown"
    /// position there and is coloured from it, exactly as it is drawn.</summary>
    static int SpeciesColor(ICoreClientAPI capi, string wood, string woodDomain, int rndIndex)
    {
        TextureAtlasPosition texPos = ChoppingBlockTexSource.GetOrCreate(
            capi, BlockEntityChoppingBlock.BarkTex(capi, wood, woodDomain), woodDomain + ":" + wood);
        return capi.BlockTextureAtlas.GetRandomColor(texPos, rndIndex);
    }

    /// <summary>Free the per-species GPU meshes built by <see cref="OnBeforeRender"/> on world unload/reload, so the
    /// MultiTextureMeshRefs don't leak. TryGet (never GetOrCreate, which would re-create an empty dict) → Dispose each
    /// → Delete the cache entry, exactly as BlockShapeFromAttributes.OnUnloaded does. Client-only GPU resource.</summary>
    public override void OnUnloaded(ICoreAPI api)
    {
        base.OnUnloaded(api);
        if (api is not ICoreClientAPI capi) return;

        Dictionary<string, MultiTextureMeshRef>? cache = ObjectCacheUtil.TryGet<Dictionary<string, MultiTextureMeshRef>>(capi, HeldMeshCacheKey);
        if (cache != null)
        {
            foreach (MultiTextureMeshRef meshref in cache.Values) meshref.Dispose();
            ObjectCacheUtil.Delete(capi, HeldMeshCacheKey);
        }
    }

    /// <summary>
    /// Per-species held name (T03.03). The PLACED name comes for free through base.GetPlacedBlockName → OnPickBlock
    /// (which stamps the species onto the stack) → ItemStack.GetName → here, so this one override covers hand / GUI /
    /// creative / handbook / dropped AND the in-world block. A bare / legacy stack (no species) shows the generic name.
    /// </summary>
    public override string GetHeldItemName(ItemStack itemStack)
    {
        string? wood = itemStack.Attributes.GetString("wood", null);
        if (wood == null) return Lang.Get("immersivewoodworking:block-choppingblock"); // generic fallback
        return ChoppingBlockName(wood, itemStack.Attributes.GetString("woodDomain", "game"));
    }

    /// <summary>"&lt;Species&gt; chopping block" from the per-language template (block-choppingblock-name, {0}=species).
    /// The resolver behind it lives in <see cref="WoodName"/> — the half-log item and the half-log slab name
    /// themselves through the same one, each with its own template key.</summary>
    static string ChoppingBlockName(string wood, string woodDomain)
        => WoodName.InTemplate("immersivewoodworking:block-choppingblock-name", wood, woodDomain);

    /// <summary>
    /// Generate one creative-inventory stack per wood species (each carrying the wood + domain attribute) and assign
    /// them to <see cref="Block.CreativeInventoryStacks"/>, so the creative menu shows every species (T03.02). The
    /// handbook does NOT follow that field here — <see cref="GetHandBookStacks"/> collapses it to one generic page.
    /// Mirrors BlockClutter.LoadTypes: build JsonItemStacks of the same block code with a per-species
    /// attribute, Resolve each, wrap in a CreativeTabAndStackList. If species discovery turns up nothing, the JSON
    /// `creativeinventory` (the bare block) is left as the fallback rather than emptying the creative/handbook entry.
    ///
    /// <para><b>And the JSON field is CLEARED on success</b> (AuditFixPlan T04.04). The two sources are ADDED, not
    /// overridden: InventoryPlayerCreative.GatherTabStacks walks <c>CreativeInventoryTabs</c> and
    /// <c>CreativeInventoryStacks</c> in two independent branches, with no <c>else</c> between them and no dedupe. So
    /// the JSON <c>creativeinventory: ["*"]</c> kept minting one extra entry off the BARE block — a thirteenth
    /// "Chopping Block" with no species standing next to the twelve named ones on vanilla — and a player who took it
    /// placed a species-less block that neither middle-click nor breaking would ever turn into an oak one. Clearing
    /// happens only INSIDE the success branch, and that ordering is load-bearing: the field is filled at InitBlock,
    /// before any <c>OnLoaded</c>, so clearing it unconditionally would erase the block from creative entirely on a
    /// world where discovery found nothing. An empty array rather than null, so nothing downstream has to be
    /// null-safe. The handbook page does not ride on this field — see <see cref="GetHandBookStacks"/>.
    /// </summary>
    void BuildCreativeStacks(ICoreAPI api)
    {
        IReadOnlyList<(string wood, string domain)> woods = GetCreativeWoods();
        if (woods.Count == 0) return; // keep the JSON creativeinventory fallback if discovery failed

        List<JsonItemStack> stacks = new List<JsonItemStack>(woods.Count);
        foreach ((string wood, string domain) in woods)
        {
            JsonItemStack jstack = new JsonItemStack
            {
                Code = Code,
                Type = EnumItemClass.Block,
                Attributes = new JsonObject(new JObject { ["wood"] = wood, ["woodDomain"] = domain })
            };
            if (jstack.Resolve(api.World, "immersivewoodworking chopping block species")) stacks.Add(jstack);
        }

        if (stacks.Count > 0)
        {
            CreativeInventoryStacks = new CreativeTabAndStackList[]
            {
                new CreativeTabAndStackList { Stacks = stacks.ToArray(), Tabs = new string[] { "general" } }
            };
            CreativeInventoryTabs = System.Array.Empty<string>();
        }
    }

    /// <summary>
    /// The wood species offered in creative/handbook, as (species, domain) pairs: the all-domain set the mod system
    /// discovered once from every <c>worldproperties/block/wood</c> (vanilla + wildcrafttree + any wood mod), so every
    /// mod's species gets a creative/handbook entry with no per-mod patch (E04/T04.01). The SAME set backs the generic
    /// grid recipes (T04.02), so "what shows in creative" and "what can be crafted" can't drift. Empty only if discovery
    /// found nothing — <see cref="BuildCreativeStacks"/> then keeps the JSON <c>creativeinventory</c> fallback.
    /// </summary>
    IReadOnlyList<(string wood, string domain)> GetCreativeWoods() => modSystem.WoodSpecies;

    /// <summary>
    /// Collapse the handbook to ONE generic "Chopping Block" page instead of one page per wood species, so the
    /// woodworking overview (and any other text) can deep-link to a single stable page
    /// (<c>block-immersivewoodworking:choppingblock</c>) exactly like the sawhorse and the sawmill frame — uniform
    /// cross-linking, the same way vanilla's pulverizer shows one frame page for all its cap-metal variants. The
    /// creative menu still lists every species: that comes from <see cref="Block.CreativeInventoryStacks"/> (set in
    /// <see cref="BuildCreativeStacks"/>), which this does NOT touch. UNCONDITIONAL, and that is what keeps this page
    /// alive now that <see cref="BuildCreativeStacks"/> clears <see cref="CollectibleObject.CreativeInventoryTabs"/>:
    /// the base implementation returns null for anything in no creative tab (vsapi Collectible.cs) and this override
    /// consults neither field. A bare (attribute-less) stack's handbook page
    /// code is just <c>block-&lt;domain&gt;:choppingblock</c> (GuiHandbookItemStackPage.PageCodeForStack, else-branch),
    /// and the per-species look/name still resolves everywhere else — held, placed, dropped (OnBeforeRender /
    /// GetHeldItemName key off the stack's wood attribute, which the bare handbook stack simply lacks → default oak +
    /// the generic "Chopping Block" name). Handbook is a client concern, but returning the same single stack on the
    /// server is harmless.
    /// </summary>
    public override List<ItemStack> GetHandBookStacks(ICoreClientAPI capi)
    {
        return new List<ItemStack> { new ItemStack(this) };
    }

    // ───────────────────────────── T01.03: carry the wood species through the stack ─────────────────────────────
    //
    // The block is now a single data-block, so its species lives in the BlockEntity (BlockEntityChoppingBlock.wood
    // / woodDomain) rather than the block code. These two overrides stamp that species onto any ItemStack the stand
    // produces (middle-click pick + break drop), so "dig an oak stand → get an oak stand → place an oak stand"
    // round-trips. The place-side read (stack attribute → BE field) lives in BlockEntityChoppingBlock.OnBlockPlaced,
    // which closes the loop. Idiom mirrors vanilla BlockShapeFromAttributes.OnPickBlock + the "drop self with data"
    // GetDrops used by typed/display blocks.

    /// <summary>Middle-click pick: hand back a stand stack carrying this stand's species (so a creative pick or
    /// any code reading the picked stack keeps it). Reads already-synced BE fields, so it's side-safe; a bare /
    /// legacy stand (no species) picks as a plain choppingblock and falls back to the default look + generic name.</summary>
    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        ItemStack stack = base.OnPickBlock(world, pos);
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityChoppingBlock be && be.Wood != null)
        {
            // Domain rides along only with a species, mirroring BlockEntityChoppingBlock.ToTreeAttributes.
            stack.Attributes.SetString("wood", be.Wood);
            stack.Attributes.SetString("woodDomain", be.WoodDomain);
        }
        return stack;
    }

    /// <summary>Break drop: the broken stand drops a choppingblock that still knows its species — the "drop self
    /// with data" idiom (reuse OnPickBlock, which already stamps the attributes). Base GetDrops would return a
    /// species-less self-drop. A bare / legacy stand falls through to base so the vanilla static-Drops path (and
    /// its dropQuantityMultiplier) still applies. Server-side; the BE still exists here. The stand's CONTENT
    /// (a laid log/firewood) is spilled separately by BlockEntityChoppingBlock.OnBlockRemoved (E06), not by this path.</summary>
    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityChoppingBlock be && be.Wood != null)
        {
            return new[] { OnPickBlock(world, pos) };
        }
        return base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
    }

    /// <summary>
    /// Fallback capture of the wood species of the log the stand was crafted from. Since T04.02 the generic recipes
    /// (ImmersiveWoodworkingModSystem.RegisterChoppingBlockRecipes) BAKE `wood` / `woodDomain` straight onto the
    /// recipe output, so the crafted stack already carries the right species — this override then re-derives the SAME
    /// pair from the grid's log-placed-{wood}-{rot} block and re-stamps it, which is idempotent (same recipe → same
    /// log → same values). It's kept as a belt-and-suspenders for any non-standard craft path that yields a
    /// choppingblock WITHOUT baked attributes (e.g. a third-party patch adding its own recipe), so such a result
    /// still picks up its species from the consumed log rather than falling back to the default-oak look. The
    /// BlockEntity reads `wood` / `woodDomain` back on place (T01.03).
    ///
    /// Mutating the per-craft output stack here sticks and syncs: the engine clones a fresh output stack per craft
    /// and calls this on it (RecipeBase.GenerateOutputStack), so the written attributes reach the player's result
    /// and the client. base runs first so block behaviors execute; our two keys are then authoritative.
    /// </summary>
    public override void OnCreatedByCrafting(ItemSlot[] allInputslots, ItemSlot outputSlot, IRecipeBase byRecipe)
    {
        base.OnCreatedByCrafting(allInputslots, outputSlot, byRecipe);

        ItemStack? output = outputSlot?.Itemstack;
        if (output == null) return; // engine sets it before calling us; guard defensively

        // allInputslots holds EVERY grid cell — empty slots AND the axe — so identify the log by its code shape
        // (log-placed-{wood}-{rot}, the recipe input), never by position or "first non-empty". The domain comes
        // from the log's own code, so a modded log stamps its own domain (E04) and vanilla logs stamp "game".
        // NOTE: this log-placed-{wood} parse intentionally mirrors the sawhorse's narrow contract
        // (BlockEntitySawhorse.IsSawableLog / GetWoodSpecies); kept a local copy on purpose (different subsystem,
        // and here we also need the domain), but if the vanilla log-placed code shape ever changes, update BOTH.
        // (This is NOT the chopping block's broader chop accept-list, LogCodeParts — that's a different, looser set.)
        foreach (ItemSlot slot in allInputslots)
        {
            Block? log = slot?.Itemstack?.Block;
            if (log?.Code == null) continue;
            if (log.FirstCodePart() != "log" || log.FirstCodePart(1) != "placed") continue;

            // Stamp nothing rather than a BLANK species (AuditFixPlan T04.06 / F-L6-28). FirstCodePart(2) returns null
            // for a log whose code stops short of a species part — vanilla's never does, but a third-party recipe
            // feeding a bare `log-placed` would — and SetString(key, null) does NOT leave the key absent: the engine
            // writes "" for a null value on the way out (StringAttribute.ToBytes), so the first save/sync turns it into
            // a key that is PRESENT and EMPTY. Every reader then takes the "species known" branch and resolves
            // `block/wood/bark/` with a hole in it. Skipping keeps the stack bare, which is a state the whole line
            // already handles (default oak look + the generic name).
            string? species = log.FirstCodePart(2);
            if (string.IsNullOrEmpty(species)) continue;

            output.Attributes.SetString("wood", species);
            output.Attributes.SetString("woodDomain", log.Code.Domain);
            break;
        }
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        // Shift+RMB (no Ctrl) with an axe in hand on an empty stand -> stick the axe into the top (a parked tool that
        // also disables chopping). Shift+Ctrl is deliberately NOT us: that's the axe's vanilla ground-storage "lean it
        // against the block" (GroundStorable, ctrlKey), which we leave alone. Anything else on Shift falls through to
        // vanilla place-a-block-on-top (TryStickTool self-guards on axe / empty stand, so non-axes never match).
        if (byPlayer.Entity.Controls.ShiftKey)
        {
            BlockEntityChoppingBlock? standBe = GetBE(world, blockSel);
            if (!byPlayer.Entity.Controls.CtrlKey && standBe != null
                && standBe.TryStickTool(byPlayer.InventoryManager.ActiveHotbarSlot, byPlayer))
            {
                return true;
            }
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        BlockEntityChoppingBlock? be = GetBE(world, blockSel);
        if (be == null) return base.OnBlockInteractStart(world, byPlayer, blockSel);

        Dbg(world, byPlayer, $"START content={be.HasContent} tool={byPlayer.InventoryManager.ActiveTool}");

        // Ctrl+RMB = eject back to the player: a stuck axe first (it can't coexist with content, but keep the priority
        // explicit), otherwise the stored choppable. Full inventory -> dropped in world by the BE.
        if (byPlayer.Entity.Controls.CtrlKey)
        {
            if (be.HasTool) { be.TryPullTool(byPlayer); return true; }
            if (be.HasContent) { be.TryTake(byPlayer); return true; }
        }

        // Chop: axe + content on the stand, and no axe parked in it -> begin the timed swing (continues in Step/Stop).
        // ContentIsChoppable also guards the live AllowFirewoodToSticks gate: a firewood workpiece left on the stand from
        // before that flag was turned off won't start a (no-op) hold — the player takes it back with Ctrl+RMB instead.
        if (be.HasContent && !be.HasTool && be.ContentIsChoppable() && byPlayer.InventoryManager.ActiveTool == EnumTool.Axe)
        {
            // Speed the swing to ChopSeconds, then start it on BOTH sides (client so the holder sees it; server
            // so it replicates in 3rd person). CancelHeldReadyAnim + ResetChopAnim force a clean frame-0 swing
            // and carry it in at full weight so it never resumes a half-done swing or flashes through idle
            // (bugs 1, 3, 4). A re-press while held must NOT bounce — see OnBlockInteractStep.
            //
            // Remember the exact axe this swing starts with so the wear lands on it even if the player swaps
            // their active hotbar slot before impact (consumed in BlockEntityChoppingBlock.Chop). EDGE-02.
            // swiftsaw (T04.02): capture the holder's effective per-swing seconds ONCE for this hold (Config.ChopSeconds,
            // shortened by their swiftsaw tier, capped; == ChopSeconds without xSkills). Both sides compute the same value
            // off the same synced tier, so the swing + impact sound stay in lockstep. The first STRIKE impact lands at it.
            //
            // A REFUSAL means another player's hold already owns this stand (AuditFixPlan T02.02): swallow the click
            // with no swing, no axe capture and — note the order — no re-facing of the log either. Turning the running
            // chopper's workpiece toward the interloper is the one VISIBLE half of the aliasing this task closes
            // (F-L4-03), so FaceContentTo has to sit AFTER the claim, not before it. The server has already told the
            // refused player why (BlockEntityChoppingBlock.RefuseChopHold); their client hears it a packet later.
            if (!be.BeginChopHold(modSystem.EffectiveChopSeconds(byPlayer, ChopSeconds), byPlayer)) return true;

            be.FaceContentTo(byPlayer);     // E05: turn the laid log to face THIS chopper (server writes + syncs the facing)
            be.CapturePendingAxe(byPlayer);

            Dbg(world, byPlayer, "CHOP-A before start/reset");
            StartChopAnimation(byPlayer, be.HeldChopSeconds(byPlayer));
            Dbg(world, byPlayer, "CHOP-C after StartChopAnimation");
            bool readyPoseWasVisible = CancelHeldReadyAnim(byPlayer);
            ResetChopAnim(byPlayer, readyPoseWasVisible);
            Dbg(world, byPlayer, "CHOP-D after ResetChopAnim");
            return true;
        }

        // Put: a choppable in hand + the stand is empty.
        ItemSlot active = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (!active.Empty && !be.HasContent && be.TryPut(active, byPlayer))
        {
            return true;
        }

        // Plain RMB otherwise: swallow the click so no block gets placed on top.
        return true;
    }

    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        // Make the swing clock honest before ANYTHING reads it (AuditFixPlan T02.03). The engine's `secondsUsed` is
        // ~0.14 s ahead of real time on the server — it replays the frames each 0.15 s packet covered, adding 0.02 s a
        // step (ImmersiveWoodworkingModSystem.HonestHoldSeconds). ChopTick both COMPARES against it and RE-BASES its
        // next threshold on it, so unclamped the server sits permanently ahead of the client that is drawing the
        // swing: firewood appeared ~0.14 s before the axe visibly landed, and on release the leftover replay burst
        // pushed one MORE strike through — wearing the axe, dropping the product and paying XP after the button was
        // already up (F-L9-02). The client's own value is already honest and passes through untouched, so both sides
        // now pace off the same real seconds.
        secondsUsed = ImmersiveWoodworkingModSystem.HonestHoldSeconds(world, byPlayer, secondsUsed);

        BlockEntityChoppingBlock? be = GetBE(world, blockSel);

        // Abort only if the stand vanished or the axe left the hand. Do NOT end the interaction at impact:
        // returning false while RMB is still held was OBSERVED IN-GAME to re-fire Start and replay the wind-up
        // (the "axe bounces back" glitch, bug 2). The client interaction driver isn't in the public API, so this
        // is empirical, not a documented contract — the API only says false "stops the interaction". Either way
        // the safe move is the same: once started, the swing lives until the player releases.
        if (be == null || byPlayer.InventoryManager.ActiveTool != EnumTool.Axe)
        {
            // The axe left the hand (or the stand vanished): the chop hold is over. End this hold's pacing + drop the
            // axe capture DEFENSIVELY here, in case the engine ends the interaction via this false-return WITHOUT also
            // firing Stop/Cancel. That matters since the progress-flicker fix (2026-07-24): a stuck chop hold would
            // pin the chopper's client display to a frozen prediction and make FromTreeAttributes ignore later syncs.
            // Idempotent with the EndChopHold/ClearPendingAxe that Stop/Cancel also call (both just set state / Remove),
            // and owner-scoped since AuditFixPlan T02.02 — a refused second player bailing out here ends nothing.
            // Cutting the swing is OUR job here, not the following Stop's (AuditFixPlan T02.04): this is an abort, so it
            // gets the hard cut whatever the swing's phase, and ending the hold below is precisely what tells that Stop
            // the swing has already been dealt with.
            EndChopSwing(secondsUsed, world, byPlayer, be, abort: true, "STEP-ABORT (return false)");
            return false;
        }

        Dbg(world, byPlayer, "STEP", secondsUsed);

        // Drive the discrete-strike chop (E03). ChopTick advances the server-side, HasContent-guarded progress bar at
        // each swing impact (dropping + emptying only on the strike that reaches 100%) and tells us how to pace the
        // cosmetic swing:
        //  • None — keep the current swing as-is. Returned only on the FINAL strike of the final log (bar hit 100% and
        //    nothing left to restock): the swing holds its impact pose (onAnimationEnd=Hold) until release — exactly the
        //    original one-swing-per-press finish.
        //  • Otherwise the chop works the whole log/stack as strike → breather → (relaunch | place-next) → strike,
        //    signalling each transition so the swing reads as a rhythm instead of freezing at the impact pose:
        //      – EnterPause: a strike just landed and another swing is coming (more strikes on this log, or the next
        //        one) → ease the swing OUT from its impact pose (StopAnimation, onActivityStopped=EaseOut) — the same
        //        clean finish a post-impact release uses — so the breather looks like the axe relaxing, not hanging.
        //      – Relaunch: the breather elapsed → start a FRESH swing easing in from the relaxed pose (RestartSwingFresh:
        //        cold frame-0, zero weight, no forced full-weight snap) on the same log (mid-chop) or the next one just
        //        auto-laid. Because woodchop has EaseAnimationSpeed=false the impact frame still lands exactly ChopSeconds
        //        later, in lockstep with the strike timer. CapturePendingAxe re-arms the wear for the coming impact.
        switch (be.ChopTick(byPlayer, secondsUsed, ChopRestockDelaySeconds)) // paces off the swiftsaw-effective seconds captured at BeginChopHold (T04.02)
        {
            case BlockEntityChoppingBlock.ChopTickAction.EnterPause:
                byPlayer.Entity.StopAnimation(ChopAnimCode); // eases out both variants (the engine eases out the -fp too)
                Dbg(world, byPlayer, "STEP-PAUSE (ease swing out for breather)", secondsUsed);
                break;

            case BlockEntityChoppingBlock.ChopTickAction.Relaunch:
                be.CapturePendingAxe(byPlayer);
                StartChopAnimation(byPlayer, be.HeldChopSeconds(byPlayer)); // queue the fresh swing + set its per-impact speed on both variants
                RestartSwingFresh(byPlayer);  // ...and (re)start it cold at frame 0 / zero weight so it eases back in
                Dbg(world, byPlayer, "STEP-CHOP (relaunch swing after breather)", secondsUsed);
                break;
        }

        // Hold the interaction for the whole press: each swing plays once (0 -> impact); when nothing remains to
        // chop the last swing holds its impact pose (onAnimationEnd=Hold) until release. We never return false
        // mid-hold (that re-fires Start = the wind-up ghost, bug 2); the loop above drives any repeat explicitly.
        return true;
    }

    public override void OnBlockInteractStop(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        // The player let go: NOT an abort, so EndChopSwing may let a landed swing ease out. On the SERVER this is the
        // only callback a normal release produces (the client sends StopBlockUse) and therefore the one that ends the
        // hold; on the CLIENT the engine has already run OnBlockInteractCancel(ReleasedMouse) this same frame, which
        // finished the release in full, so EndChopSwing finds no hold left and leaves that decision standing
        // (AuditFixPlan T02.04).
        //
        // Honest clock, same as Step (T02.03) — and this is the second place F-L9-02 bit: the release burst the engine
        // replays before calling Stop inflates the argument by up to 0.16 s, so a release up to that far BEFORE the
        // impact used to read as "post-impact" and ease the swing out instead of cutting it.
        float rawSecondsUsed = secondsUsed;
        secondsUsed = ImmersiveWoodworkingModSystem.HonestHoldSeconds(world, byPlayer, secondsUsed);
#if IW_DEBUG
        // T02.03 measurement, one line per release. The BLOCK path is the one that cannot self-correct: unlike held
        // items (ServerSystemInventory registers OnUsingTick, which advances UsingCount in real time and leaves the
        // replay loop with nothing to catch up), ServerSystemBlockSimulation has NO tick listener — the replay loop is
        // the only thing that moves UsingCount, so it must run the whole packet's worth of frames. `raw` minus `held`
        // on the SERVER is therefore the inflation itself, measured rather than derived. On the client the two are
        // equal by construction (HonestHoldSeconds is a no-op there), which is what makes the pair readable.
        world.Logger.Notification("[IW-DBG] chophold side={0} pos={1} raw={2:0.000} held={3:0.000} inflation={4:0.000}",
            world.Side, blockSel.Position, rawSecondsUsed, secondsUsed, rawSecondsUsed - secondsUsed);
#endif

        EndChopSwing(secondsUsed, world, byPlayer, GetBE(world, blockSel), abort: false, "STOP");
    }

    public override bool OnBlockInteractCancel(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, EnumItemUseCancelReason cancelReason)
    {
        // NOT all of these are aborts. Releasing the button reaches us here too — on the client the engine fires
        // Cancel(ReleasedMouse) and only THEN OnBlockInteractStop, so hard-cancelling every reason meant the chopper's
        // own swing was already dead by the time Stop's ease-out branch ran: everyone but the person chopping saw the
        // post-impact swing relax (the server, whose animation observers see, is only ever sent StopBlockUse and so
        // never takes this path on a normal release). That is F-L9-09; the fix is to treat a release as a release and
        // let EndChopSwing make the same pre/post-impact decision Stop would.
        //
        // Every OTHER reason (looked away, changed slot, dropped the axe, died) is a genuine abort and still gets the
        // hard cut with no wind-up ghost. Looking away is the one that actually fires in play, and note that it is NOT
        // followed by a Stop on either side — the client returns straight out of its interaction handler and sends
        // CancelBlockUse.
        //
        // The teardown inside EndChopSwing runs either way, which is what makes this safe on BOTH release paths: the
        // usual one hands off to Stop a moment later, but the client ALSO cancels straight out of its game tick when
        // the mouse stops being grabbed (a GUI opened mid-chop) — carrying reason ReleasedMouse, not OpenedGui, and
        // with no Stop ever following. Deciding AND ending here means that path needs nothing extra, and the Stop that
        // normally follows the usual one finds the hold gone and leaves the easing-out swing alone.
        //
        // Honest clock, same as Step/Stop (T02.03): the server runs the identical replay loop before CancelBlockUse.
        // Held in a LOCAL, not written back over the parameter — the base call below hands the argument back to the
        // engine, which owns its own units (same convention as BlockSawhorse.OnBlockInteractCancel).
        float held = ImmersiveWoodworkingModSystem.HonestHoldSeconds(world, byPlayer, secondsUsed);
        EndChopSwing(held, world, byPlayer, GetBE(world, blockSel),
                     abort: cancelReason != EnumItemUseCancelReason.ReleasedMouse, $"CANCEL reason={cancelReason}");
        return base.OnBlockInteractCancel(secondsUsed, world, byPlayer, blockSel, cancelReason);
    }

    /// <summary>
    /// The one place a chop hold ends, however it ended — a release (<see cref="OnBlockInteractStop"/> on the server,
    /// <see cref="OnBlockInteractCancel"/> with <see cref="EnumItemUseCancelReason.ReleasedMouse"/> on the client), a
    /// genuine abort (any other cancel reason) or the axe leaving the hand mid-hold (<see cref="OnBlockInteractStep"/>).
    /// Added by AuditFixPlan T02.04, which is also why the animation decision is gated: ONE client release arrives as
    /// two callbacks, and the second must not re-cut a swing the first set easing out.
    /// <para><paramref name="abort"/> false = the player let go, so a swing that has already landed its impact eases out
    /// of the impact pose (onActivityStopped=EaseOut) for the natural finish. Otherwise — and for a wind-up that hasn't
    /// landed yet — the swing is cut hard: plain StopAnimation only fades the WEIGHT while the engine keeps advancing
    /// the FRAME at full speed, so an unfinished wind-up would play out as a fading ghost (bug 2).</para>
    /// <para><paramref name="secondsUsed"/> must already be the HONEST hold clock (T02.03). The pre/post-impact verdict
    /// comes from <see cref="BlockEntityChoppingBlock.SwingHasLanded"/>, which knows which swing of the hold is on
    /// screen — over an AutoRestock hold, "total seconds past one swing's duration" would call every later release
    /// post-impact, ghost wind-up and all.</para>
    /// </summary>
    static void EndChopSwing(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockEntityChoppingBlock? be,
                             bool abort, string tag)
    {
        // No live hold of ours = no swing of ours on screen: either this release was already finished by the callback
        // ahead of us, or it was a NON-chop interaction (put / take / stick-tool, which the engine Steps and Stops
        // exactly the same). Either way, leave the animator alone. A vanished stand has no hold to ask and is an abort
        // by nature, so it still gets the hard cut.
        if (be == null || be.HasChopHold(byPlayer))
        {
            if (!abort && be != null && be.SwingHasLanded(byPlayer, secondsUsed))
            {
                byPlayer.Entity.StopAnimation(ChopAnimCode); // eases out both variants (the engine eases out the -fp too)
                Dbg(world, byPlayer, $"{tag} -> ease out (post-impact)", secondsUsed);
            }
            else
            {
                CancelChopAnim(byPlayer);
                Dbg(world, byPlayer, $"{tag} -> hard cut", secondsUsed);
            }
        }
        else Dbg(world, byPlayer, $"{tag} -> swing already settled", secondsUsed);

        // Drop any axe capture not consumed by a resolved chop (e.g. a pre-impact tap). No-op post-chop. Also end this
        // hold's chop pacing so a FOLLOWING non-chop interaction (which also drives Step with an axe in hand) can't run
        // the loop off this hold's stale pacing and phantom-strike (F-S06-01). Both are idempotent and owner-scoped
        // (AuditFixPlan T02.02): a refused second player letting go used to stall the real chopper (F-L4-03), and the
        // second callback of one release finds nothing left to do.
        be?.ClearPendingAxe(byPlayer);
        be?.EndChopHold(byPlayer);
    }

    // [IW-DBG] dev trace: dumps every active/running animation (frame/easing/blended weight/iterations/flags)
    // for the player. [Conditional("IW_DEBUG")] strips every call (args included) unless IW_DEBUG is defined,
    // so Release pays nothing; the dev build (tools/apply-animation.ps1 -> -p:IwDebug=true) turns it on.
#if IW_DEBUG
    // Master toggle for the VERBOSE per-frame animation trace below (Dbg/DbgLog). OFF by default so the chop/tier
    // diagnostics ([iw-chop], [iw-tier]) stay findable in the log; flip to true to bring the animation trace back.
    static readonly bool AnimTraceEnabled = false;
#endif
    [Conditional("IW_DEBUG")]
    static void Dbg(IWorldAccessor world, IPlayer byPlayer, string tag, float t = -1f)
    {
#if IW_DEBUG
        if (!AnimTraceEnabled) return;
        IAnimator? animator = byPlayer.Entity.AnimManager?.Animator;
        string a = "";
        if (animator != null)
        {
            // 1) chop states explicitly, even when inactive — always shows their frame/weight
            foreach (string code in ChopAnimVariants)
            {
                RunningAnimation? ra = animator.GetAnimationState(code);
                a += ra == null
                    ? $" {code}[NULL]"
                    : $" {code}[f={ra.CurrentFrame:0.0} ease={ra.EasingFactor:0.00} bw={ra.BlendedWeight:0.00} it={ra.Iterations} {(ra.Active ? "A" : "-")}{(ra.Running ? "R" : "-")}]";
            }
            // 2) any OTHER active/running anim that could also be posing the arms
            foreach (RunningAnimation ra in animator.Animations)
            {
                if (ra?.Animation == null || (!ra.Active && !ra.Running)) continue;
                string code = ra.Animation.Code;
                if (code == ChopAnimCode || code == ChopAnimCode + "-fp") continue;
                a += $" {code}[f={ra.CurrentFrame:0.0} ease={ra.EasingFactor:0.00} bw={ra.BlendedWeight:0.00}]";
            }
        }
        string tt = t >= 0 ? $" t={t:0.000}" : "";
        world.Logger.Notification($"[IW-DBG] {world.Side} anim#{animator?.GetHashCode()} {tag}{tt}{a}");
#endif
    }

    // Companion one-line [IW-DBG] trace used by StartChopAnimation/ResetChopAnim. Same IW_DEBUG gating as Dbg.
    [Conditional("IW_DEBUG")]
    static void DbgLog(IPlayer byPlayer, string msg)
    {
#if IW_DEBUG
        if (!AnimTraceEnabled) return;
        IWorldAccessor world = byPlayer.Entity.World;
        world.Logger.Notification($"[IW-DBG] {world.Side} {msg}");
#endif
    }

    /// <summary>
    /// The impact frame = the last authored keyframe of woodchop, read live off the loaded animation so
    /// patches/woodchop_animations.json is the single source of truth (a re-export can't desync a constant).
    /// Cached after the first real reading; falls back to <see cref="FallbackImpactFrame"/> only when the
    /// animation isn't present on the entity's shape (and is NOT cached then, so a later valid shape resolves).
    /// </summary>
    int GetImpactFrame(IPlayer byPlayer)
    {
        if (impactFrame > 0) return impactFrame;

        Animation? anim = byPlayer.Entity.AnimManager?.Animator?.GetAnimationState(ChopAnimCode)?.Animation;
        AnimationKeyFrame[]? kfs = anim?.KeyFrames;
        if (kfs == null || kfs.Length == 0) return FallbackImpactFrame;

        int last = 0;
        foreach (AnimationKeyFrame kf in kfs) if (kf.Frame > last) last = kf.Frame;
        return impactFrame = last;
    }

    /// <summary>
    /// Starts the chop swing on this entity with its playback speed timed so the impact frame (see
    /// <see cref="GetImpactFrame"/>) lands exactly on ChopSeconds. Runs on client and server so the speed the
    /// server replicates to other players matches what the holder sees.
    ///
    /// Properties.Client.AnimationsByMetaCode is PER-ENTITY, not a shared template: every entity carries its own
    /// clone of EntityProperties, and EntityClientProperties.Clone() rebuilds this dictionary with a fresh
    /// per-entity Clone() of each AnimationMetaData (vsapi EntityProperties.cs:533-571; the AnimationMetaData
    /// docs note that scalar fields like AnimationSpeed are fresh per-entity copies — "that's the reason why we
    /// clone"). So writing AnimationSpeed onto either variant's meta affects ONLY this player; there is NO
    /// cross-player aliasing to defend against (an earlier comment here wrongly called it a shared template).
    ///
    /// The 3rd-person variant ("woodchop") is still started from a per-entity Clone() rather than by mutating
    /// the meta the engine stored in AnimationsByMetaCode — purely defensive hygiene: StartAnimation(meta) puts
    /// the passed object into THIS entity's ActiveAnimationsByAnimCode (which the server serializes out to other
    /// clients), and we'd rather hand it our own clone than poke the engine's stored meta in place. The
    /// 1st-person variant ("woodchop-fp") is auto-started by the engine for the holder's own (local-only)
    /// first-person view, which we don't get to intercept, so we set AnimationSpeed on its (already per-entity)
    /// meta directly. Both are correct and per-entity-safe; the only difference is that we can't clone the fp
    /// start.
    /// </summary>
    void StartChopAnimation(IPlayer byPlayer, float chopSeconds)
    {
        int impact = GetImpactFrame(byPlayer);
        // Clamp OverallSpeedMultiplier off zero: a temporal-storm slow-mo drives it to exactly 0, which would
        // make speed = impact/0 = Infinity and corrupt the swing frame to NaN (Progress only guards ==0). The
        // chop resolves on the real-time chopSeconds timer (OnBlockInteractStep), so this only guards the
        // cosmetic swing from a one-press glitch in that exotic state. chopSeconds is the swiftsaw-effective
        // per-swing duration captured for this hold (BeginChopHold → HeldChopSeconds, T04.02), so the impact
        // frame lands on it in lockstep with the strike timer — with or without a swiftsaw bonus.
        float speedMult = System.Math.Max(0.0001f, GlobalConstants.OverallSpeedMultiplier);
        float speed = impact / (30f * chopSeconds * speedMult);

        var byMeta = byPlayer.Entity.Properties.Client.AnimationsByMetaCode;

        // 1st person: this meta is per-entity (see summary), so set its speed directly. The engine auto-plays
        // this variant for the holder's own first-person view; we can't intercept that start to clone it.
        byMeta.TryGetValue(ChopAnimCode + "-fp", out AnimationMetaData? fpMeta);
        if (fpMeta != null) fpMeta.AnimationSpeed = speed;

        // 3rd person: hand StartAnimation our own clone rather than mutating the engine-stored meta in place
        // (defensive hygiene — the meta is already per-entity, so this is not about avoiding cross-player leaks).
        if (byMeta.TryGetValue(ChopAnimCode, out AnimationMetaData? tpTemplate) && tpTemplate != null)
        {
            AnimationMetaData tp = tpTemplate.Clone();
            tp.AnimationSpeed = speed;
            byPlayer.Entity.AnimManager.StartAnimation(tp);
        }
        else
        {
            // No template on this shape (e.g. a non-seraph player shape lacking woodchop): start by code so the
            // swing still fires where it exists. Nothing renders if the shape has no keyframes anyway.
            byPlayer.Entity.StartAnimation(ChopAnimCode);
        }

        DbgLog(byPlayer, $"START-ANIM speed={speed:0.000} impact={impact} chopS={chopSeconds:0.000} mult={GlobalConstants.OverallSpeedMultiplier:0.000} fpMeta={fpMeta != null}");
    }

    /// <summary>
    /// Restarts the swing at frame 0 on every press (impact timing depends on starting at 0). If a pose is
    /// already on screen — a still-Running swing (back-to-back chops) or a just-cleared "axe ready" stance —
    /// carry the new frame-0 pose in at FULL weight (EasingFactor=1) so the arms blend straight out of it with
    /// no one-frame idle dip (bugs 1, 3). For a still-Running swing also re-assert Active/Running so the engine
    /// keeps it instead of running its ease-out teardown. A genuine cold start (variant fully Stopped) is left
    /// to ease in from idle — forcing Active there makes the engine skip activation and the swing never plays
    /// (bugs 4, 5). Covers the 3rd-person and first-person (-fp) variants.
    ///
    /// Relies on VS-internal invariants (sensitive to engine updates): the engine only writes EasingFactor in
    /// Progress/EaseOut (never on deferred re-activation), AnimNowActive only sets Running/Active/meta and
    /// CurrentFrame = meta.StartFrameOnce, and Stop() is the only thing that clears Running — so the synchronous
    /// writes below survive the next tick. (The cold-start CurrentFrame=0 below is only safe because
    /// StartFrameOnce defaults to 0; on enrollment AnimNowActive would otherwise overwrite our frame with it.)
    /// </summary>
    static void ResetChopAnim(IPlayer byPlayer, bool readyPoseWasVisible)
    {
        IAnimator? animator = byPlayer.Entity.AnimManager?.Animator;
        if (animator == null) { DbgLog(byPlayer, "RESET animator NULL"); return; }

        foreach (string code in ChopAnimVariants)
        {
            RunningAnimation? ras = animator.GetAnimationState(code);
            if (ras == null) { DbgLog(byPlayer, $"RESET {code} state NULL"); continue; }
#if IW_DEBUG
            float before = ras.CurrentFrame;
            int beforeIt = ras.Iterations;
            float beforeEase = ras.EasingFactor;
#endif

            // Running == a prior swing of this variant is still on screen and still enrolled in the animator's
            // run list (only Stop() clears Running), so we can re-assert its state in place below.
            bool priorSwingLive = ras.Running;

            // Carry the new frame-0 swing in at full weight whenever there's a pose to blend out of: a live
            // prior swing (back-to-back) or a just-cleared ready stance. Otherwise ease in from idle as normal.
            bool carry = priorSwingLive || readyPoseWasVisible;

            // Restart at frame 0 (HARD CONSTRAINT for impact timing); Iterations=0 so the next Progress() takes
            // the ease-IN branch, not ease-out.
            ras.CurrentFrame = 0;
            ras.Iterations = 0;

            if (carry) ras.EasingFactor = 1f; // pins to full weight next tick (survives AnimNowActive)

            if (priorSwingLive)
            {
                // Already enrolled: re-assert Active so the engine skips its re-activation pass + ease-out
                // teardown and just keeps running our frame-0 pose at full weight.
                ras.Active = true;
                ras.Running = true;
            }
            // else FRESH start (variant fully Stopped): leave Active/Running false so AnimNowActive enrolls the
            // swing next frame. Forcing Active here makes the engine skip activation and the swing never plays
            // (the bug 4/5 regression). A carried EasingFactor=1 still survives; a cold idle start eases up gently.

#if IW_DEBUG
            DbgLog(byPlayer, $"RESET {code} frame {before:0.00}->0 it {beforeIt}->0 ease {beforeEase:0.00}->{ras.EasingFactor:0.00} carry={carry} ready={readyPoseWasVisible} active={ras.Active} running={ras.Running} bw={ras.BlendedWeight:0.00}");
#endif
        }
    }

    /// <summary>
    /// (Re)starts the swing at frame 0 from a RELAXED pose — the auto-restock relaunch counterpart to
    /// <see cref="ResetChopAnim"/>. Where ResetChopAnim carries the first swing in at FULL weight (to avoid an idle dip
    /// when chopping out of the held "axe ready" stance), the relaunch wants the OPPOSITE: the previous swing was eased
    /// OUT over the breather (<see cref="BlockEntityChoppingBlock.ChopTickAction.EnterPause"/>), so we introduce the new
    /// frame-0 pose at ZERO weight and let it ease in (easeInSpeed=10 → full weight within ~0.15s, well before the
    /// frame-19 impact at ChopSeconds). Starting cold is what hides the frame-19→0 jump: at the instant of the jump the
    /// swing carries ~no weight, so the arms read as winding up for the next strike instead of snapping back. This is
    /// what replaced the old "carry the held swing in at full weight" relaunch, which produced that visible snap.
    ///
    /// Frame 0 / Iterations 0 are the only hard requirement (impact timing depends on starting at 0); since woodchop has
    /// EaseAnimationSpeed=false the easing never affects WHEN the impact frame is reached, only the blend weight. We set
    /// the fields directly on both variants' RunningAnimation so the restart is correct regardless of whether the eased-
    /// out swing has fully Stopped yet or the engine re-enrolls it via AnimNowActive next frame (which only ever rewrites
    /// CurrentFrame = StartFrameOnce = 0, never the easing). Covers the 3rd-person and first-person (-fp) variants.
    /// </summary>
    static void RestartSwingFresh(IPlayer byPlayer)
    {
        IAnimator? animator = byPlayer.Entity.AnimManager?.Animator;
        if (animator == null) { DbgLog(byPlayer, "RESTART animator NULL"); return; }

        foreach (string code in ChopAnimVariants)
        {
            RunningAnimation? ras = animator.GetAnimationState(code);
            if (ras == null) { DbgLog(byPlayer, $"RESTART {code} state NULL"); continue; }

            ras.CurrentFrame = 0;   // HARD CONSTRAINT for impact timing
            ras.Iterations = 0;     // next Progress() takes the ease-IN branch
            ras.EasingFactor = 0f;  // introduce the frame-0 pose at zero weight → the 19→0 jump is masked, then eases in

            DbgLog(byPlayer, $"RESTART {code} -> frame 0, ease 0 (cold ease-in)");
        }
    }

    /// <summary>
    /// Hard-cancels the swing with no ease-out tail. StopAnimation alone keeps advancing the frame while the weight
    /// fades, so the wind-up plays as a ghost (bug 2); Stop() on each running variant zeroes the weight next frame,
    /// so a tap reads as "nothing happened". Clears the 3rd-person and -fp variants. Also called from
    /// <see cref="BlockEntityChoppingBlock"/> when the server refuses a second chopper (AuditFixPlan T02.02), to cut
    /// the swing that client had already started optimistically.
    /// </summary>
    internal static void CancelChopAnim(IPlayer byPlayer)
    {
        byPlayer.Entity.StopAnimation(ChopAnimCode);

        // The holder's currently-visible animator (fp hands in 1st person, the 3rd-person body otherwise).
        IAnimator? animator = byPlayer.Entity.AnimManager?.Animator;
        if (animator == null) return;

        foreach (string code in ChopAnimVariants)
        {
            animator.GetAnimationState(code)?.Stop();
        }
    }

    /// <summary>
    /// Hard-clears the vanilla held "axe ready" pose the instant a swing starts (the engine only EASES it out,
    /// which muddies the wind-up), and reports whether it was actually on screen. Hard-stopping it alone would
    /// leave a one-frame window where both the ready pose (now 0) and the not-yet-eased swing have zero weight
    /// → a one-tick idle flash (bug 3); the caller carries the swing in at full weight when this returns true.
    /// Returns false for a chop straight out of idle, where the gentle ease-in already looks right. Back-to-back
    /// chops never hit this: while RMB is held the engine won't re-arm the ready pose (EntityPlayer's
    /// shouldRightReadyStack is gated on !RightMouseDown); it re-arms by itself once RMB is released.
    /// </summary>
    /// <returns>True if a held-ready pose was visible (eased in) and has now been hard-cleared.</returns>
    static bool CancelHeldReadyAnim(IPlayer byPlayer)
    {
        ItemSlot slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        string? readyAnim = slot?.Itemstack?.Collectible?.GetHeldReadyAnimation(slot, byPlayer.Entity, EnumHand.Right);
        if (readyAnim == null) return false;

        RunningAnimation? ras = byPlayer.Entity.AnimManager?.Animator?.GetAnimationState(readyAnim);
        if (ras == null) return false;

        // Visible = eased in past a sliver. Running alone isn't enough (it can be Running while fading at ~0).
        bool wasVisible = ras.Running && ras.EasingFactor > 0.1f;

        ras.Stop(); // hard-stop now so its fade can't overlap the swing's ease-in
        return wasVisible;
    }

    static BlockEntityChoppingBlock? GetBE(IWorldAccessor world, BlockSelection blockSel)
    {
        if (blockSel == null) return null;
        return world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityChoppingBlock;
    }
}
