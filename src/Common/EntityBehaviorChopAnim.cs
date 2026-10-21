using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace ImmersiveWoodworking;

/// <summary>
/// Makes the mod's custom player animations — the woodchop swing (woodchop/-fp), the log-rip saw loop
/// (sawing/-fp) AND the bark-strip loops (debarking-barkspud/-fp, debarking-axe/-fp, debarking-knife/-fp) — play on EVERY player body
/// shape, not just the vanilla seraph-faceless one. (Registered as "iw-chopanim"; the name is historical — it now
/// backfills the saw and debark animations too.)
///
/// Why this exists: patches/woodchop_animations.json, patches/sawing_animations.json and
/// patches/debarking_animations.json register the animation METADATA on the player entity
/// (game:entities/humanoid/player), which is universal, but a JSON patch can only add the actual KEYFRAMES to ONE
/// named shape file — seraph-faceless. Any mod that swaps the player's base shape to its own file (a reskin that
/// keeps the seraph bone skeleton) loads a shape that has no woodchop/sawing/debarking keyframes, so
/// StartAnimation("woodchop"/"sawing"/"debarking-*") finds nothing and the arms don't move (chopping/sawing/
/// debarking itself still works — it's resolved server-side — only the visible motion is missing).
///
/// This client-side behavior backfills the keyframes at shape-build time: in OnTesselation it checks whether the
/// shape the player is about to use already defines our animations and, if not, copies them in from the
/// (always-present, already-patched) vanilla seraph-faceless shape. The keyframes are authored against seraph bone
/// names (UpperArmL, LowerArmR, ItemAnchor, ...), so this works for ANY shape built on the seraph skeleton —
/// vanilla skins and seraph-based reskin mods alike. A genuinely different skeleton (different bone names) still
/// can't play them — that's a hard limit of skeletal animation — but it degrades to "no motion", never an error.
///
/// Engine mechanics (verified against vsapi): when a behavior changes the shape in OnTesselation and sets
/// shapeIsCloned=true, Entity rebuilds the animator via LoadAnimator (the non-cached path), which calls
/// Shape.InitForAnimations and resolves the added animations against the current shape's joints. So the
/// injected swing becomes playable on the freshly built animator with no extra work from us. Runs LAST in
/// the player's client behavior list (patched with /client/behaviors/-), so extraskinnable has already
/// cloned + assembled the shape by the time we append.
/// </summary>
public class EntityBehaviorChopAnim : EntityBehavior
{
    // The keyframes live in the vanilla player base shape, where patches/woodchop_animations.json,
    // patches/sawing_animations.json and patches/debarking_animations.json add them at load. We read them from
    // there (the asset is already patched by the time any tesselation runs), so there is a single source of truth
    // for the animation data and no second copy to keep in sync.
    static readonly AssetLocation SourceShape = new AssetLocation("game", "shapes/entity/humanoid/seraph-faceless.json");
    static readonly string[] CustomAnimCodes =
    {
        "woodchop", "woodchop-fp",
        "sawing", "sawing-fp",
        "debarking-barkspud", "debarking-barkspud-fp", // tier-3 bark spud (driven T03.02)
        "debarking-axe", "debarking-axe-fp",           // tier-2 axe+hammer (driven T03.03)
        "debarking-knife", "debarking-knife-fp",       // tier-1 knife scrape (driven T03.02)
        "driftwood-break", "driftwood-break-fp",       // E07 one-shot driftwood hand-snap (BlockBehaviorFlotsamBreak)
    };

    // Loaded once per client session and reused for every player. Each injection appends a Clone() of the
    // source Animation. Note Animation.Clone() SHALLOW-copies its keyframe-element dicts (vsapi
    // AnimationKeyFrame.Clone), so the injected copies share the cache's keyframe-element objects rather than
    // fully owning them, and the engine re-binds their ForElement on every tesselation — this has been safe
    // because every seraph-based shape has identical bone names, so the per-tesselation re-resolve binds them
    // the same way. (If you ever need true isolation, deep-clone instead.) Lazily filled on the first
    // tesselation that needs it (assets are guaranteed loaded by render time). The check-then-set is
    // unsynchronized; a rare double-load on first use is harmless (identical data), matching the chop-sound
    // cache pattern elsewhere in this mod.
    static Animation[]? sourceAnims;

    public EntityBehaviorChopAnim(Entity entity) : base(entity) { }

    public override string PropertyName() => "iw-chopanim";

    public override void OnTesselation(ref Shape entityShape, string shapePathForLogging, ref bool shapeIsCloned, ref string[] willDeleteElements)
    {
        // Visual-only concern; the behavior is patched into client.behaviors so this is already client-side,
        // but guard defensively in case it's ever added elsewhere.
        if (entity.World?.Side != EnumAppSide.Client) return;

        // An upstream client behavior (e.g. extraskinnable) can leave entityShape null when a referenced
        // skin-part shape asset is missing/errored. Bail rather than NRE on entityShape.Animations below
        // (Entity.OnTesselation would swallow the throw, but it adds a confusing second warning and aborts
        // the rest of the tesselation pipeline for that frame).
        if (entityShape == null) return;

        // Already has all of them (vanilla seraph-faceless, where the patches put them directly) -> nothing to do.
        if (HasAllCustomAnims(entityShape)) return;

        Animation[]? src = GetSourceAnims();
        if (src == null || src.Length == 0) return;

        // We are about to change the shape's animation set. The shape handed to us may be the shared cached
        // base shape; mutating that would corrupt it for every entity. Clone first (unless an earlier behavior
        // such as extraskinnable already cloned it). Setting shapeIsCloned=true also makes Entity take the
        // LoadAnimator path that re-runs InitForAnimations, so the animations we add actually get resolved.
        if (!shapeIsCloned)
        {
            entityShape = entityShape.Clone();
            shapeIsCloned = true;
        }

        List<Animation> anims = entityShape.Animations != null ? entityShape.Animations.ToList() : new List<Animation>();
        foreach (Animation a in src)
        {
            if (a?.Code == null) continue;
            if (anims.Any(x => x.Code == a.Code)) continue; // don't duplicate one the shape already defines
            anims.Add(a.Clone());
        }
        entityShape.Animations = anims.ToArray();
    }

    static bool HasAllCustomAnims(Shape? shape)
    {
        if (shape?.Animations == null) return false;
        foreach (string code in CustomAnimCodes)
        {
            if (!shape.Animations.Any(a => a?.Code == code)) return false;
        }
        return true;
    }

    Animation[]? GetSourceAnims()
    {
        if (sourceAnims != null) return sourceAnims;

        Shape? src = Shape.TryGet(entity.Api, SourceShape);
        if (src?.Animations == null) return null;

        sourceAnims = src.Animations
            .Where(a => a?.Code != null && System.Array.IndexOf(CustomAnimCodes, a.Code) >= 0)
            .ToArray();
        return sourceAnims;
    }
}
