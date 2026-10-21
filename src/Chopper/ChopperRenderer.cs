using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// E07 — the automated chopper's custom renderer, modelled 1:1 on <see cref="SawmillRenderer"/> (in turn vanilla
/// <c>HelveHammerRenderer</c>): it draws the machine's MOVING parts (the static frame is baked into the chunk mesh by
/// <see cref="BlockEntityChopper.OnTesselation"/>, variant B). Each mover part is a separate
/// <see cref="MultiTextureMeshRef"/> and, per frame, gets its OWN model matrix from the master BE
/// (<see cref="BlockEntityChopper.GetPartMatrices"/> — the gear-train → pawls → axe-beam kinematics driven by the
/// mechanical-network angle, T07.02). It returns null only until the movers are tesselated; a frozen (unpowered)
/// angle draws the authored rest pose. Simpler than the sawmill: the workpiece log + species-painted bed (T07.03) are drawn
/// STATIC on the bed (the chopper has no carriage — the log doesn't move), so they ride the plain block origin, not a
/// carriage matrix, and there are no kerf slices to toggle.
///
/// Like the helve hammer the phase comes entirely from the drive angle (no local timer): a frozen angle leaves the
/// machine at rest, a turning network raises and drops the axe. Opaque uses the <c>"tex"</c> sampler (NOT <c>"tex2d"</c>
/// — memory vs-rendermultitexturemesh-sampler-name); the shadow stages use <c>"tex2d"</c>, exactly like
/// <c>HelveHammerRenderer</c>.
/// </summary>
public class ChopperRenderer : IRenderer
{
    readonly ICoreClientAPI api;
    readonly BlockEntityChopper be;
    readonly BlockPos pos;
    readonly MultiTextureMeshRef?[] meshes;
    readonly int[] ruleIdx; // meshes[i] belongs to ChopperParts.Rules[ruleIdx[i]] — the matrix to apply

    // Whether ANY mover mesh actually uploaded. Fixed for the renderer's lifetime: meshes is readonly and the BE
    // rebuilds the whole renderer (UpdateRenderer) when the install mask changes, so this is decided once in the ctor
    // rather than re-scanned every frame. False on a bare frame (nothing installed → BuildMoverMeshes returns an empty
    // array) and on the degenerate case where every mesh failed to build. See MachineRenderGate.NothingToDraw.
    readonly bool hasMoverMesh;

    readonly Matrixf modelMat = new Matrixf();  // camera-relative block origin (rebuilt per frame)
    readonly Matrixf partMat = new Matrixf();   // base · part animation matrix (rebuilt per mesh)
    readonly Matrixf shadowMvpMat = new Matrixf();

    // Per-part animation matrices, rebuilt ONCE per frame (on the opaque stage) and reused by both shadow stages — see
    // OnRenderFrame. Null until the first opaque pass builds them (and whenever GetPartMatrices returns null — an
    // untesselated machine), in which case PartModel falls back to the block origin (identity → authored rest pose).
    float[]?[]? frameMats;

    public double RenderOrder => 0.5;
    /// <summary>Inherited from <c>HelveHammerRenderer</c> and, like there, decoration: the API documents
    /// <see cref="IRenderer.RenderRange"/> as "currently not used!", so the engine culls nothing by it. The real
    /// visibility rule is <see cref="MachineRenderGate"/> (AuditFixPlan T05.06).</summary>
    public int RenderRange => 24;

    public ChopperRenderer(ICoreClientAPI capi, BlockEntityChopper be, MeshData?[] partMeshes, int[] ruleIdx)
    {
        this.api = capi;
        this.be = be;
        this.pos = be.Pos;
        this.ruleIdx = ruleIdx;
        meshes = new MultiTextureMeshRef?[partMeshes.Length];
        for (int i = 0; i < partMeshes.Length; i++)
        {
            meshes[i] = partMeshes[i] == null ? null : capi.Render.UploadMultiTextureMesh(partMeshes[i]);
            if (meshes[i] != null) hasMoverMesh = true;
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        // AuditFixPlan T05.06 / F-L5-13 — nothing installed and nothing on the bed: return before ANY GL state, shader
        // or matrix work. A bare frame is registered on all three stages from Initialize, so without this it paid the
        // full per-frame price to draw an empty mesh array. Restores the helve hammer's opening guard; the reasoning for
        // why skipping the opaque state advance too is a behavioural no-op is in MachineRenderGate.NothingToDraw.
        if (MachineRenderGate.NothingToDraw(hasMoverMesh, be.BedMeshRef != null || be.LogMeshRef != null)) return;

        // Rebuild the per-part animation matrices ONCE per frame, on the opaque stage (the renderer is registered for 3
        // stages); the shadow stages reuse this cached set so the tree isn't rebuilt 3× per frame and the shadow lines
        // up with opaque. frameMats stays null until the first opaque build (or whenever GetPartMatrices returns null,
        // e.g. an untesselated machine), so a shadow stage that runs first just draws the rest pose.
        if (stage == EnumRenderStage.Opaque)
        {
            frameMats = be.GetPartMatrices(deltaTime); // native-frame part matrices; null → rest pose (filled in T07.02). deltaTime feeds the Nyquist-hybrid shaft step (T03.05)
            be.UpdateCutEffects(frameMats);   // T07.04 — chip burst at the strike's bottom point, off the SAME matrices
        }

        // AuditFixPlan T05.06 / F-L5-13 — off-screen: skip the DRAW only, and only after the state advance above, so a
        // machine at your back keeps its gear clacks, its axe thud and its chip phase running. Opaque stage only (see
        // MachineRenderGate.OutOfView).
        if (MachineRenderGate.OutOfView(api, pos, stage)) return;

        float[]?[]? mats = frameMats;
        IRenderAPI rpi = api.Render;
        Vec3d camPos = api.World.Player.Entity.CameraPos;

        rpi.GlDisableCullFace();

        // Camera-relative block origin shared by every part; each part's own animation matrix is post-multiplied below.
        modelMat.Identity().Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z);

        if (stage == EnumRenderStage.Opaque)
        {
            IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
            prog.ViewMatrix = rpi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
            for (int i = 0; i < meshes.Length; i++)
            {
                MultiTextureMeshRef? m = meshes[i];
                if (m == null) continue;
                prog.ModelMatrix = PartModel(mats, ruleIdx[i]);
                rpi.RenderMultiTextureMesh(m, "tex");
            }
            // Species-painted bed + workpiece log (T07.03): both STATIC — the chopper log doesn't move (no carriage) —
            // so they draw at the plain camera-relative block origin (no part matrix; their seating is baked into the
            // mesh). The bed first, then the log on top. modelMat still holds the origin (PartModel writes into partMat,
            // never mutating modelMat).
            MultiTextureMeshRef? bed = be.BedMeshRef;
            MultiTextureMeshRef? log = be.LogMeshRef;
            if (bed != null || log != null)
            {
                prog.ModelMatrix = modelMat.Values;
                if (bed != null) rpi.RenderMultiTextureMesh(bed, "tex");
                if (log != null) rpi.RenderMultiTextureMesh(log, "tex");
            }
            prog.Stop();
        }
        else
        {
            for (int i = 0; i < meshes.Length; i++)
            {
                MultiTextureMeshRef? m = meshes[i];
                if (m == null) continue;
                shadowMvpMat.Set(rpi.CurrentProjectionMatrix).Mul(rpi.CurrentModelviewMatrix).Mul(PartModel(mats, ruleIdx[i]));
                rpi.CurrentActiveShader.UniformMatrix("mvpMatrix", shadowMvpMat.Values);
                rpi.CurrentActiveShader.Uniform("origin", 0f, 0f, 0f);
                rpi.RenderMultiTextureMesh(m, "tex2d");
            }
            // Static bed + log in the shadow stages too (T07.03), at the block origin.
            MultiTextureMeshRef? bed = be.BedMeshRef;
            MultiTextureMeshRef? log = be.LogMeshRef;
            if (bed != null || log != null)
            {
                shadowMvpMat.Set(rpi.CurrentProjectionMatrix).Mul(rpi.CurrentModelviewMatrix).Mul(modelMat.Values);
                rpi.CurrentActiveShader.UniformMatrix("mvpMatrix", shadowMvpMat.Values);
                rpi.CurrentActiveShader.Uniform("origin", 0f, 0f, 0f);
                if (bed != null) rpi.RenderMultiTextureMesh(bed, "tex2d");
                if (log != null) rpi.RenderMultiTextureMesh(log, "tex2d");
            }
        }
    }

    /// <summary>The model matrix for the part at <paramref name="ruleIndex"/> (into <see cref="ChopperParts.Rules"/> /
    /// the matrix array): the camera-relative block origin post-multiplied by that part's animation matrix, or just the
    /// origin when there is no matrix (rest pose). Returns a reused array — valid only until the next call (the shader
    /// uploads it on assignment, before we reuse the buffer).</summary>
    float[] PartModel(float[]?[]? mats, int ruleIndex)
    {
        float[]? pm = (mats != null && ruleIndex >= 0 && ruleIndex < mats.Length) ? mats[ruleIndex] : null;
        if (pm == null) return modelMat.Values;
        return partMat.Set(modelMat.Values).Mul(pm).Values;
    }

    public void Dispose()
    {
        api.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        api.Event.UnregisterRenderer(this, EnumRenderStage.ShadowFar);
        api.Event.UnregisterRenderer(this, EnumRenderStage.ShadowNear);
        for (int i = 0; i < meshes.Length; i++) meshes[i]?.Dispose();
    }
}
