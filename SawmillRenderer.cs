using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// E07 — the automated sawmill's custom renderer, modelled 1:1 on vanilla <c>HelveHammerRenderer</c>: it draws the
/// machine's MOVING parts (the static frame is baked into the chunk mesh by
/// <see cref="BlockEntitySawmill.OnTesselation"/>, variant B). Each mover part is a separate
/// <see cref="MultiTextureMeshRef"/> and, per frame, gets its OWN model matrix from the master BE
/// (<see cref="BlockEntitySawmill.GetPartMatrices"/> — the procedural slider-crank + ratchet-feed assembler driven by
/// the mechanical-network angle and feed progress, T07.02/T07.03).
///
/// Like the helve hammer, the phase comes entirely from the drive angle (no local timer): a frozen angle leaves the
/// machine at its authored rest pose, a turning network reciprocates the sash. Opaque uses the <c>"tex"</c> sampler
/// (NOT <c>"tex2d"</c> — memory vs-rendermultitexturemesh-sampler-name); the shadow stages use <c>"tex2d"</c>, exactly
/// like <c>HelveHammerRenderer</c>.
/// </summary>
public class SawmillRenderer : IRenderer
{
    readonly ICoreClientAPI api;
    readonly BlockEntitySawmill be;
    readonly BlockPos pos;
    readonly MultiTextureMeshRef?[] meshes;
    readonly int[] ruleIdx; // meshes[i] belongs to SawmillParts.Rules[ruleIdx[i]] — the matrix to apply

    // Whether ANY mover mesh actually uploaded. Fixed for the renderer's lifetime: meshes is readonly and the BE
    // rebuilds the whole renderer (UpdateRenderer) when the install mask changes, so this is decided once in the ctor
    // rather than re-scanned every frame. False on a bare frame (nothing installed → BuildMoverMeshes returns an empty
    // array) and on the degenerate case where every mesh failed to build. See MachineRenderGate.NothingToDraw.
    readonly bool hasMoverMesh;

    readonly Matrixf modelMat = new Matrixf();  // camera-relative block origin (rebuilt per frame)
    readonly Matrixf partMat = new Matrixf();   // base · part animation matrix (rebuilt per mesh)
    readonly Matrixf shadowMvpMat = new Matrixf();

    // Per-part animation matrices, rebuilt ONCE per frame (on the opaque stage) and reused by both shadow stages —
    // see OnRenderFrame. Null until the first opaque pass builds them (the meshes/pivots may also be unbuilt).
    float[]?[]? frameMats;

    public double RenderOrder => 0.5;
    /// <summary>Inherited from <c>HelveHammerRenderer</c> and, like there, decoration: the API documents
    /// <see cref="IRenderer.RenderRange"/> as "currently not used!", so the engine culls nothing by it. The real
    /// visibility rule is <see cref="MachineRenderGate"/> (AuditFixPlan T05.06).</summary>
    public int RenderRange => 24;

    public SawmillRenderer(ICoreClientAPI capi, BlockEntitySawmill be, MeshData?[] partMeshes, int[] ruleIdx)
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
        // AuditFixPlan T05.06 / F-L5-13 — nothing installed and no log loaded: return before ANY GL state, shader or
        // matrix work. A bare frame is registered on all three stages from Initialize, so without this it paid the full
        // per-frame price to draw an empty mesh array. Restores the helve hammer's opening guard; the reasoning for why
        // skipping the opaque state advance too is a behavioural no-op is in MachineRenderGate.NothingToDraw.
        if (MachineRenderGate.NothingToDraw(hasMoverMesh, be.HasLogMesh)) return;

        // Rebuild the per-part animation matrices ONCE per frame, on the opaque stage, not on every render stage. The
        // renderer is registered for 3 stages (opaque + 2 shadow) and the matrix tree (BuildMatrices + the slider-crank
        // rods) is a whole tree of work, so recomputing the identical tree 3× per frame was pure CPU waste. The shadow
        // stages reuse this cached set; the feed advance (clientProgress) and the timing-critical motion audio also run
        // here off the SAME matrices, so the opaque pass always draws the freshest pose and the shadows lag it by at
        // most one frame (imperceptible). frameMats stays null until the first opaque build, so a shadow stage that runs
        // before any opaque draws the rest pose (PartModel falls back to the block origin). Since T05.07 the build
        // writes into the machine's own buffers and allocates nothing.
        if (stage == EnumRenderStage.Opaque)
        {
            be.AdvanceClientFeed(deltaTime); // smooth client feed, once per frame (T07.03)
            frameMats = be.GetPartMatrices(deltaTime); // native-frame part matrices; null until the meshes/pivots are built. deltaTime feeds the Nyquist-hybrid shaft step (ModDbBugfix T02.03 / SpeedDecoupling T04.02)
            be.UpdateMotionAudio(frameMats); // one-shots (sash taps / ratchet / feed-step) land on the visible event
            be.SyncLogMesh();                // re-merge the workpiece only when a slice has gone through (T05.07)
        }

        // AuditFixPlan T05.06 / F-L5-13 — off-screen: skip the DRAW only, and only after the state advance above, so a
        // machine at your back keeps its sash taps, its ratchet click and its carriage feed running. Opaque stage only
        // (see MachineRenderGate.OutOfView).
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
            // Workpiece log (T07.03): the body plus the working slices the saw hasn't fed through yet, merged into ONE
            // mesh by be.SyncLogMesh — the progressive cut is a merge-time toggle now (T05.07 / F-L10-06: 25 draw calls
            // per stage became 1). The whole workpiece rides the carriage matrix.
            MultiTextureMeshRef? logMesh = be.LogMeshRef;
            if (logMesh != null)
            {
                prog.ModelMatrix = PartModel(mats, be.LogRuleIndex);
                rpi.RenderMultiTextureMesh(logMesh, "tex");
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
            MultiTextureMeshRef? logMesh = be.LogMeshRef;
            if (logMesh != null)
            {
                shadowMvpMat.Set(rpi.CurrentProjectionMatrix).Mul(rpi.CurrentModelviewMatrix).Mul(PartModel(mats, be.LogRuleIndex));
                rpi.CurrentActiveShader.UniformMatrix("mvpMatrix", shadowMvpMat.Values);
                rpi.CurrentActiveShader.Uniform("origin", 0f, 0f, 0f);
                rpi.RenderMultiTextureMesh(logMesh, "tex2d");
            }
        }
    }

    /// <summary>The model matrix for the part at <paramref name="ruleIndex"/> (into <see cref="SawmillParts.Rules"/> /
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
