using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// AuditFixPlan T05.06 / F-L5-13 — the ONE per-frame visibility rule shared by both powered-machine renderers
/// (<see cref="ChopperRenderer"/> and <see cref="SawmillRenderer"/>, twins by design). It lives here so the rule
/// exists once instead of drifting into two versions, exactly like <see cref="AutoRestockUtil"/> holds the one
/// auto-feed rule for both manual stations.
///
/// Both renderers are registered from <c>Initialize</c> UNCONDITIONALLY, on three stages (opaque + the two shadow
/// passes), long before a single part is fitted into the frame. Without a gate an empty frame paid, every frame,
/// for three <c>GlDisableCullFace</c> calls and three model-matrix rebuilds, plus (on opaque)
/// <c>PreparedStandardShader</c> — which walks the block accessor for the light RGBs and uploads the whole uniform
/// set — and <c>prog.Stop()</c>, all to iterate an EMPTY mesh array. Vanilla's <c>HelveHammerRenderer</c>, the model
/// both are built on, opens with <c>if (meshref == null || be.HammerStack == null) return;</c>; that line is what
/// <see cref="NothingToDraw"/> restores.
///
/// There is deliberately NO distance cut. <see cref="IRenderer.RenderRange"/> is documented in the API as
/// "currently not used!" — the engine never culls a custom renderer by it, so the <c>RenderRange =&gt; 24</c> both
/// renderers inherited from the helve hammer is decoration. Adding a real 24-block cut would be visibly WRONG here:
/// the machine's static frame is baked into the CHUNK mesh and keeps drawing at any distance, so cutting the movers
/// would leave a skeleton with no axe, no gears, no carriage and no workpiece log. Vanilla agrees — no comparable
/// machine renderer (helve hammer, pulverizer, quern) carries a distance gate; only small props do
/// (<c>ChestLabelRenderer</c> at 20 blocks, <c>GroundStorageRenderer</c> at its RenderRange). Author decision
/// 2026-08-28.
/// </summary>
internal static class MachineRenderGate
{
    // Bounding sphere of the drawn volume, in the master block's own frame. Both machines occupy the same footprint
    // extent — local X ∈ [-1,1], Y ∈ [0,3], Z ∈ [-1,1] around the master (BlockEntity{Chopper,Sawmill}.Footprint) —
    // so the drawn geometry spans about [-1,2] × [0,4] × [-1,2] from the block origin. Centre it and take a radius
    // with slack for the workpiece log overhanging the bed/carriage: a sphere that is too LARGE only costs a draw
    // that would have been skipped, one that is too small pops the machine out of frame edges.
    const double CentreX = 0.5, CentreY = 2.0, CentreZ = 0.5;
    const double CullRadius = 4.0;

    /// <summary>True when the renderer has no geometry at all this frame and must return before touching GL state:
    /// no mover mesh was uploaded (nothing installed yet — the static frame is in the chunk mesh, not here) AND no
    /// workpiece surface is loaded (the sawmill's merged workpiece log, the chopper's billet and species-painted bed).
    ///
    /// Skipping the whole frame also skips the opaque state advance (<c>GetPartMatrices</c>, the effects/audio pump,
    /// the sawmill's client feed), which is a behavioural no-op here: every one of those gates internally on
    /// <c>IsComplete</c> via <c>IsMechanismTurning()</c>/<c>CanSaw()</c>, and a machine with no movers is by
    /// definition not complete. The one deferred-state case — the chopper holding a consumed billet across
    /// <c>pendingChopVanish</c> — cannot be starved either: holding the billet is exactly what keeps
    /// <c>LogMeshRef</c> non-null, so <paramref name="hasWorkpiece"/> is true and the frame runs.</summary>
    public static bool NothingToDraw(bool hasMoverMesh, bool hasWorkpiece) => !hasMoverMesh && !hasWorkpiece;

    /// <summary>True when the machine is outside the camera frustum and this frame's DRAW can be skipped — the
    /// machine behind the player's back or off the screen edge, the case F-L5-13 names. Same test vanilla's
    /// <c>SignRenderer</c> uses (<c>DefaultFrustumCuller.SphereInFrustum</c>, six plane tests) and, like it, applied
    /// on the OPAQUE stage only: the shadow passes render from the light's point of view, so the camera frustum is
    /// the wrong question there and culling by it would delete the shadows of everything just off screen.
    ///
    /// Callers must run their per-frame STATE advance (audio one-shots, particle phase, client feed) BEFORE asking
    /// this, and gate only the drawing on it. A working machine must keep sounding and keep its carriage advancing
    /// while you look away.
    ///
    /// <paramref name="pos"/> is read with the same plain <c>Y</c> the renderers put in their model matrix (as the
    /// helve hammer does), not <c>InternalY</c>, so the test and the draw can never disagree about where the machine
    /// is.</summary>
    public static bool OutOfView(ICoreClientAPI api, BlockPos pos, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Opaque) return false;
        return !api.Render.DefaultFrustumCuller.SphereInFrustum(pos.X + CentreX, pos.Y + CentreY, pos.Z + CentreZ, CullRadius);
    }
}
