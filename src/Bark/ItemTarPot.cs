using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// The SEALED / FIRED tar pot as a carriable product (BarkPlan/E04, T04.02; mesh override added 2026-07-29).
///
/// WHY THIS CLASS EXISTS. Until now this was a class-less item, so it rendered with the plain JSON mesh of the
/// <c>pot-combined</c> shape. That was harmless while the birch lived in a SEPARATE overlay shape — but the
/// 2026-07-28 model merge (D41) folded the four cumulative birch groups INTO <c>pot-combined</c>, so the default
/// JSON mesh became "pot + lid + ALL FOUR birch stages at once": 127 birch elements wrapping a pot that is supposed
/// to be sealed shut. D41 recorded exactly this as its one unverified visual and named this fix as the remedy;
/// the playtest then confirmed the sealed pot renders wrong (the rig BEFORE sealing renders correctly — verified by
/// the [IW-TAR] mesh trace — and the breakage appears only at the moment of sealing).
///
/// What a sealed pot must look like: the clay shell with its LID and daub on, and NO birch showing — the bark is
/// packed inside, under the lid. That is <c>PotMesh(stage: 0, withLid: true)</c>, the same builder the rig and the
/// standalone perforated pot already use, so all three vessels stay visually consistent and share one mesh cache.
///
/// COLOUR rides the CODE (<c>tarpot-{color}-{perfcolor}-sealed</c> / <c>-fired</c>), not attributes, because the pit
/// kiln clones the JSON SmeltedStack and drops item attributes when it fires (D11/D23). Since 2026-07-29 BOTH vessels'
/// colours are carried (the <c>perfcolor</c> axis), so the sealed pot renders TWO-TONE like the rig it came from — top
/// body + lid in the perforated pot's colour, bottom in the receiver's — and opening hands each vessel back in its own
/// colour. That retires the D39 sacrifice, where sealing forgot the perforated pot's colour entirely.
/// </summary>
public class ItemTarPot : Item, IContainedMeshSource, IHandBookPageCodeProvider
{
    // The merged two-vessel shape: shell + lid/daub + the four birch groups (all of which we hide here, stage 0).
    static readonly AssetLocation PotShape = new AssetLocation("immersivewoodworking", "shapes/block/pot-combined.json");

    ICoreClientAPI? capi;
    // Per-colour meshes, uploaded once and disposed in OnUnloaded (the vanilla per-stack held-mesh idiom).
    readonly Dictionary<string, MultiTextureMeshRef?> heldMeshes = new();

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        capi = api as ICoreClientAPI;
    }

    /// <summary>Whether this is the FIRED state (<c>tarpot-{color}-{perfcolor}-fired</c>) rather than the merely sealed one.</summary>
    bool IsFired => LastCodePart() == "fired";

    /// <summary>
    /// Lower (receiver) body texture: the claypot texture of the RECEIVER's colour, carried in the code
    /// (<c>tarpot-{color}-{perfcolor}-{state}</c> → part 1), in its CLEAN or SOOTY form. Blue is the mod-wide degenerate
    /// fallback colour.
    ///
    /// <para>Soot, and why it is split between a code and an attribute (2026-07-29). The receiver may be a clean
    /// <c>claypot</c> or an already-used <c>dirtyclaypot</c> — the unsealed rig renders that difference
    /// (<see cref="BlockTarPot"/>), and until now sealing threw it away, so a rig built on a sooty pot repainted itself
    /// pristine the moment you capped it. A FIRED pot needs no data to answer this: it has just distilled tar, and opening
    /// it hands back a <c>dirtyclaypot</c> whatever went in (<see cref="BehaviorTarPotOpen"/>, D8), so it is
    /// unconditionally sooty — which is also the only form that survives the pit kiln, since firing clones the JSON
    /// SmeltedStack and drops attributes (D11/D23). A SEALED pot is the one real question, and it never crosses the kiln,
    /// so it reads the <c>recvDirty</c> flag <see cref="BlockTarPot.SealRig"/> stamps onto the stack. Net effect: soot
    /// carries end to end without a fourth code axis (which would have doubled 60 codes to 120 and broken every saved
    /// pot), and the only appearance change is the honest one — a clean pot darkens when it is FIRED, not when it is
    /// capped.</para>
    /// </summary>
    AssetLocation ReceiverTex(ItemStack? stack)
    {
        bool dirty = IsFired || (stack?.Attributes.GetBool("recvDirty", false) ?? false);
        return new AssetLocation("game", "block/clay/pot/" + (dirty ? "dirty" : "") + (Variant?["color"] ?? "blue"));
    }

    /// <summary>Upper (perforated) body + lid texture: the fired claypot texture of the PERFORATED pot's colour, code
    /// part 2 (<c>perfcolor</c> axis, 2026-07-29). The lid is the clay cap put on THAT vessel, so it follows it. The
    /// perforated pot held bark and char, never liquid, so it has no sooty form (D8) and needs no stack.</summary>
    AssetLocation PerforatedTex() => new AssetLocation("game", "block/clay/pot/" + (Variant?["perfcolor"] ?? "blue"));

    // Sealed shut: no birch visible (stage 0), lid + daub ON. TWO-TONE, exactly like the unsealed rig it was folded from —
    // upper body + lid in the perforated pot's colour, lower body in the receiver's (sooty per ReceiverTex). That is what
    // makes the preserved perforated-pot colour visible: before the perfcolor axis the pot went monochrome at sealing and
    // the player could not tell which pot was inside (nor get it back).
    MeshData? SealedMesh(ICoreClientAPI capi, ItemStack? stack)
    {
        AssetLocation top = PerforatedTex();
        return TarRig.PotMesh(capi, PotShape, top, ReceiverTex(stack), 0, true, top);
    }

    // ── Ground-storage quadrant (a sealed pot sits in the quadrant it was sealed in, and stacks into a pit kiln) ──

    public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
    {
        TarDbg.Log(capi, $"SEALEDPOT GenMesh @{atBlockPos} top={PerforatedTex()} bot={ReceiverTex(slot.Itemstack)} stack={TarDbg.Describe(slot.Itemstack)}");
        // The display base mutates the mesh it is handed (applyDefaultTranforms), so hand it a CLONE of the cache.
        MeshData? m = capi != null ? SealedMesh(capi, slot.Itemstack) : null;
        if (m == null) TarDbg.Log(capi, "SEALEDPOT GenMesh -> NULL mesh - returning EMPTY 4/3 mesh");
        return m != null ? m.Clone() : new MeshData(4, 3);
    }

    // Both colours AND the receiver's soot are in the key: two sealed pots differing only in which perforated pot is
    // inside — or only in whether their receiver was already used — must NOT share a mesh. ReceiverTex already folds the
    // soot into the texture path (…/pot/blue vs …/pot/dirtyblue), so the key needs no extra term.
    public string GetMeshCacheKey(ItemSlot slot) => "iw-tarpot-sealed|" + PerforatedTex() + "|" + ReceiverTex(slot.Itemstack);

    // ── Held / GUI / dropped ──

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
        string key = PerforatedTex() + "|" + ReceiverTex(itemstack);
        if (!heldMeshes.TryGetValue(key, out MultiTextureMeshRef? mref))
        {
            MeshData? md = SealedMesh(capi, itemstack);
            mref = md != null ? capi.Render.UploadMultiTextureMesh(md) : null;
            heldMeshes[key] = mref;
        }
        if (mref != null) renderinfo.ModelRef = mref;
    }

    public override void OnUnloaded(ICoreAPI api)
    {
        foreach (MultiTextureMeshRef? m in heldMeshes.Values) m?.Dispose();
        heldMeshes.Clear();
        base.OnUnloaded(api);
    }

    // ── Handbook: sixty codes, two pages ──

    /// <summary>
    /// Send every colour pairing to the page of its STATE: all thirty <c>-sealed</c> codes to the sealed pot's page,
    /// all thirty <c>-fired</c> codes to the fired pot's. Only <c>tarpot-blue-blue-{state}</c> keeps a page; the rest
    /// carry <c>handbook.exclude</c> (itemtypes/tarpot.json). Both colours ride the code because that is the only
    /// channel that survives the pit kiln, so the handbook would otherwise have to answer for sixty variants that read
    /// identically to the player — the two names ("Sealed tar pot", "Fired tar pot") do not mention colour at all.
    ///
    /// <para>This is the only seam that fixes BOTH symptoms. The engine routes the H key
    /// (<c>SurvivalHandbook.OnKeyDown</c>) and every clickable stack icon on another page
    /// (<c>CollectibleBehaviorHandbookTextAndExtraInfo.getPageCodeForStack</c>) through this interface, and the second
    /// one is what the "Fires into" arrow on the sealed pot's page uses — its target is the smelted stack, i.e. a
    /// <c>-fired</c> code that has no page of its own.</para>
    ///
    /// <para>It also disarms a second, quieter miss: the default page code appends a JSON dump of the stack's
    /// remaining attributes (<c>GuiHandbookItemStackPage.PageCodeForStack</c>), so a sealed pot carrying
    /// <c>recvDirty</c> asks for a page code no page ever had. A fixed string cannot.</para>
    /// </summary>
    public string HandbookPageCodeForStack(IWorldAccessor world, ItemStack stack)
        => "item-" + Code.Domain + ":tarpot-blue-blue-" + (IsFired ? "fired" : "sealed");
}
