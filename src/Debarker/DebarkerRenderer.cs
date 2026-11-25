using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// Draws one debarker's spindle and installed movers (AutoDebarkerPlan T07.02; the frame is in the chunk mesh), each
/// with its matrix from <see cref="BlockEntityDebarker.GetPartMatrices"/> (T07.03), and the loaded log, whose mesh the
/// master keeps (<see cref="BlockEntityDebarker.LogMeshRef"/>, T07.04). The master rebuilds the renderer whenever the
/// installed parts change. Opaque samples <c>"tex"</c>, the shadow passes <c>"tex2d"</c>, as vanilla's
/// <c>HelveHammerRenderer</c> and <see cref="ChopperRenderer"/> (memory vs-rendermultitexturemesh-sampler-name).
/// <see cref="RenderRange"/> is not read by the engine; visibility is <see cref="MachineRenderGate"/>.
/// </summary>
internal sealed class DebarkerRenderer : IRenderer
{
    static readonly EnumRenderStage[] Stages = { EnumRenderStage.Opaque, EnumRenderStage.ShadowFar, EnumRenderStage.ShadowNear };
    readonly ICoreClientAPI api;
    readonly BlockEntityDebarker be;
    readonly BlockPos renderPos;
    readonly List<(int Rule, MultiTextureMeshRef Mesh)> meshes = new();
    readonly Matrixf origin = new();
    readonly Matrixf partModel = new();
    readonly Matrixf shadowMvp = new();
    float[][]? frameMatrices;
    int registeredStages;

    public double RenderOrder => 0.5;
    public int RenderRange => 24;

    internal DebarkerRenderer(ICoreClientAPI api, BlockEntityDebarker be, List<(int Rule, MeshData Mesh)> movers)
    {
        this.api = api;
        this.be = be;
        // CameraPos already includes the dimension offset (memory vs-camerapos-includes-dimension-offset), so the internal
        // Y goes into Y here, and the draw, the light and the frustum gate all read that one field.
        renderPos = new BlockPos(be.Pos.X, be.Pos.InternalY, be.Pos.Z, 0);
        try
        {
            foreach (var (rule, mesh) in movers)
                meshes.Add((rule, api.Render.UploadMultiTextureMesh(mesh)));
            foreach (var stage in Stages)
            {
                api.Event.RegisterRenderer(this, stage, "debarker");
                registeredStages++;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (MachineRenderGate.NothingToDraw(meshes.Count > 0, be.HasLogMesh)) return;
        // Shadows reuse the previous opaque pose (the engine draws them before Opaque), so the pose is built once a frame.
        // The log is re-merged after the pose has moved its progress, and before the view gate: the shadows draw it too.
        if (stage == EnumRenderStage.Opaque)
        {
            frameMatrices = be.GetPartMatrices(deltaTime);
            be.SyncLogMesh();
        }
        if (MachineRenderGate.OutOfView(api, renderPos, stage)) return;
        MultiTextureMeshRef? log = be.LogMeshRef;

        var render = api.Render;
        Vec3d camera = api.World.Player.Entity.CameraPos;
        var pos = renderPos;
        origin.Identity().Translate(pos.X - camera.X, pos.Y - camera.Y, pos.Z - camera.Z);
        render.GlDisableCullFace();

        if (stage == EnumRenderStage.Opaque)
        {
            var shader = render.PreparedStandardShader(pos.X, pos.Y, pos.Z);
            shader.ViewMatrix = render.CameraMatrixOriginf;
            shader.ProjectionMatrix = render.CurrentProjectionMatrix;
            foreach (var (rule, mesh) in meshes)
            {
                shader.ModelMatrix = ModelMatrix(rule);
                render.RenderMultiTextureMesh(mesh, "tex");
            }
            if (log != null)
            {
                shader.ModelMatrix = ModelMatrix(DebarkerParts.LogIndex);
                render.RenderMultiTextureMesh(log, "tex");
            }
            shader.Stop();
        }
        else
        {
            var shader = render.CurrentActiveShader;
            shader.Uniform("origin", 0f, 0f, 0f);
            foreach (var (rule, mesh) in meshes)
            {
                shadowMvp.Set(render.CurrentProjectionMatrix).Mul(render.CurrentModelviewMatrix).Mul(ModelMatrix(rule));
                shader.UniformMatrix("mvpMatrix", shadowMvp.Values);
                render.RenderMultiTextureMesh(mesh, "tex2d");
            }
            if (log != null)
            {
                shadowMvp.Set(render.CurrentProjectionMatrix).Mul(render.CurrentModelviewMatrix).Mul(ModelMatrix(DebarkerParts.LogIndex));
                shader.UniformMatrix("mvpMatrix", shadowMvp.Values);
                render.RenderMultiTextureMesh(log, "tex2d");
            }
        }
    }

    // Null before the first opaque frame, whose shadow passes come first. The length check keeps the rest pose instead
    // of throwing on the render thread should the matrices ever fall short of the rules, as ChopperRenderer does.
    float[] ModelMatrix(int rule) => frameMatrices is { } mats && rule < mats.Length
        ? partModel.Set(origin.Values).Mul(mats[rule]).Values
        : origin.Values;

    public void Dispose()
    {
        while (registeredStages > 0) api.Event.UnregisterRenderer(this, Stages[--registeredStages]);
        foreach (var (_, mesh) in meshes) mesh.Dispose();
        meshes.Clear();
    }
}
