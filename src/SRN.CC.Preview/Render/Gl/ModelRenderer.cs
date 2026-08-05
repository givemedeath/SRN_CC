using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("SRN.CC.Tests")]

namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// GPU-side renderer for one uploaded <see cref="RenderScene"/>. Owns every GL resource it creates
/// through its <see cref="IGlDevice"/> and is the only place the milestone-6 render layer issues
/// draw calls. See architecture decision A4 for the full lifetime story:
/// <c>ModelViewportControl</c> (App layer) creates one <see cref="ModelRenderer"/> per
/// <c>OnOpenGlInit</c>, calls <see cref="Dispose"/> from <c>OnOpenGlDeinit</c> (normal teardown,
/// deletes every GL name), and calls <see cref="Abandon"/> from <c>OnOpenGlLost</c> (context loss —
/// forgets everything, issues zero GL calls, because the driver already freed those names).
/// </summary>
public sealed class ModelRenderer : IDisposable
{
    /// <summary>
    /// Textures larger than the scene needs are fine; a context that cannot even hit this floor
    /// cannot usefully render MDL materials (per-model texture budgets top out well above this).
    /// </summary>
    private const int MinimumRequiredTextureSize = 512;

    private const int FloatsPerVertex = 3 + 3 + 2; // position + normal + texcoord

    private IGlDevice _device;
    private readonly List<GlHandle> _ownedResources = [];
    private List<UploadedMaterial> _materials = [];
    private List<UploadedMesh> _artworkMeshes = [];
    private List<UploadedMesh> _walkmeshMeshes = [];
    private GlHandle _program;

    public ModelRenderer(IGlDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ContextId = device.ContextId;
    }

    /// <summary>The context this renderer was built for, captured once at construction.</summary>
    public GlContextId ContextId { get; }

    public RendererState State { get; private set; } = RendererState.Uninitialized;

    /// <summary>Non-null exactly when <see cref="State"/> is <see cref="RendererState.Unsupported"/>.</summary>
    public string? UnsupportedReason { get; private set; }

    /// <summary>Number of draw calls issued by the most recent <see cref="Render"/>. Semantic assertion hook.</summary>
    public int LastFrameDrawCallCount { get; private set; }

    /// <summary>Names of every textured material actually drawn by the most recent <see cref="Render"/>. Semantic assertion hook.</summary>
    public IReadOnlyList<string> LastFrameBoundTextureNames { get; private set; } = [];

    /// <summary>
    /// Probes shader compilation and the minimum capability floor. Never throws: a capability
    /// shortfall or shader link failure is reported through the returned
    /// <see cref="RendererInitResult"/>, and <see cref="State"/> becomes
    /// <see cref="RendererState.Unsupported"/> so callers can degrade to the existing MDL text
    /// preview (architecture decision A4).
    /// </summary>
    public RendererInitResult Initialize(GlCapabilities capabilities)
    {
        if (IsCrossContextOrLost())
        {
            State = RendererState.Stale;
            return new RendererInitResult(false, "device is lost or belongs to a different context");
        }

        if (capabilities.MaxTextureSize < MinimumRequiredTextureSize)
        {
            UnsupportedReason =
                $"GL_MAX_TEXTURE_SIZE ({capabilities.MaxTextureSize}) is below the minimum required " +
                $"for model previews ({MinimumRequiredTextureSize}).";
            State = RendererState.Unsupported;
            return new RendererInitResult(false, UnsupportedReason);
        }

        (string vertexSource, string fragmentSource) = ShaderSources.Select(capabilities);
        GlHandle program = _device.CreateProgram(vertexSource, fragmentSource, out string? linkLog);
        if (program.IsZero)
        {
            UnsupportedReason = linkLog ?? "shader program failed to compile or link.";
            State = RendererState.Unsupported;
            return new RendererInitResult(false, UnsupportedReason);
        }

        _program = program;
        _ownedResources.Add(program);
        UnsupportedReason = null;
        State = RendererState.Ready;
        return new RendererInitResult(true, null);
    }

    /// <summary>
    /// Creates GPU buffers/textures for every mesh and material in <paramref name="scene"/>. A no-op
    /// (state becomes <see cref="RendererState.Stale"/>) when the device is lost or belongs to a
    /// different context than <see cref="ContextId"/>; a no-op (state unchanged) when
    /// <see cref="State"/> is not <see cref="RendererState.Ready"/> — <see cref="Initialize"/> must
    /// succeed first.
    /// </summary>
    public void Upload(RenderScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (IsCrossContextOrLost())
        {
            State = RendererState.Stale;
            return;
        }

        if (State != RendererState.Ready)
        {
            return;
        }

        ReleaseUploadedGeometryAndMaterials();

        _materials = new List<UploadedMaterial>(scene.Materials.Count);
        foreach (RenderMaterial material in scene.Materials)
        {
            GlHandle? textureHandle = null;
            TextureImage? diffuse = material.Diffuse;
            if (diffuse is { Width: > 0, Height: > 0 } && diffuse.Bgra.Length > 0)
            {
                GlHandle created = _device.CreateTexture2D(diffuse.Width, diffuse.Height, diffuse.Bgra, diffuse.HasAlpha);
                if (!created.IsZero)
                {
                    textureHandle = created;
                    _ownedResources.Add(created);
                }
            }

            _materials.Add(new UploadedMaterial(
                textureHandle,
                string.IsNullOrEmpty(diffuse?.Name) ? material.Name : diffuse!.Name,
                material.DiffuseColor,
                material.BlendMode,
                material.AlphaTestThreshold));
        }

        _artworkMeshes = UploadMeshes(scene.ArtworkMeshes);
        _walkmeshMeshes = UploadMeshes(scene.WalkmeshMeshes);
    }

    /// <summary>
    /// Draws every artwork mesh, plus every walkmesh mesh when <paramref name="showWalkmesh"/> is
    /// true (excluded entirely otherwise — architecture decision A6). A no-op (state becomes
    /// <see cref="RendererState.Stale"/>) under the same cross-context/lost conditions as
    /// <see cref="Upload"/>.
    /// </summary>
    public void Render(int framebuffer, in RenderCamera camera, int pixelWidth, int pixelHeight, bool showWalkmesh)
    {
        LastFrameDrawCallCount = 0;
        LastFrameBoundTextureNames = [];

        if (IsCrossContextOrLost())
        {
            State = RendererState.Stale;
            return;
        }

        if (State != RendererState.Ready || pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        // Before anything is drawn, and unconditionally — including for a scene with no meshes, so a
        // cleared slot does not keep showing the previous model. The depth clear is the load-bearing
        // half: depth testing is enabled for the device's whole lifetime and the host's framebuffer
        // arrives with undefined depth, so without this every fragment can fail the test and the
        // frame comes out empty with all its draw calls issued.
        _device.Clear(framebuffer, pixelWidth, pixelHeight);

        float aspect = (float)pixelWidth / pixelHeight;
        Matrix4x4 viewProjection = camera.View * camera.Projection(aspect);

        List<string> boundTextureNames = [];
        int drawCount = 0;

        foreach (UploadedMesh mesh in _artworkMeshes)
        {
            DrawMesh(mesh, viewProjection, framebuffer, pixelWidth, pixelHeight, boundTextureNames);
            drawCount++;
        }

        if (showWalkmesh)
        {
            foreach (UploadedMesh mesh in _walkmeshMeshes)
            {
                DrawMesh(mesh, viewProjection, framebuffer, pixelWidth, pixelHeight, boundTextureNames);
                drawCount++;
            }
        }

        LastFrameDrawCallCount = drawCount;
        LastFrameBoundTextureNames = boundTextureNames;
    }

    /// <summary>
    /// Forgets every resource this renderer holds without issuing a single device call, then marks
    /// <see cref="State"/> as <see cref="RendererState.Stale"/>. This is the "on context loss" path
    /// (architecture decision A4): the driver has already invalidated every name, so calling
    /// <c>IGlDevice.Delete</c> here would be a GL call against a dead context.
    /// </summary>
    public void Abandon()
    {
        _ownedResources.Clear();
        _materials = [];
        _artworkMeshes = [];
        _walkmeshMeshes = [];
        _program = default;
        LastFrameDrawCallCount = 0;
        LastFrameBoundTextureNames = [];
        UnsupportedReason = null;
        State = RendererState.Stale;
    }

    /// <summary>
    /// Normal teardown: deletes every GL name this renderer created (program, buffers, textures) via
    /// the device, then marks <see cref="State"/> as <see cref="RendererState.Disposed"/>. Safe to
    /// call multiple times. If the device is already lost or belongs to a different context, this
    /// behaves like <see cref="Abandon"/> instead (no calls issued) — there is nothing safe to delete.
    /// </summary>
    public void Dispose()
    {
        if (State == RendererState.Disposed)
        {
            return;
        }

        if (!IsCrossContextOrLost())
        {
            foreach (GlHandle handle in _ownedResources)
            {
                _device.Delete(handle);
            }
        }

        _ownedResources.Clear();
        _materials = [];
        _artworkMeshes = [];
        _walkmeshMeshes = [];
        _program = default;
        LastFrameDrawCallCount = 0;
        LastFrameBoundTextureNames = [];
        State = RendererState.Disposed;
    }

    /// <summary>
    /// Test-only seam: re-points this renderer at a different device while leaving
    /// <see cref="ContextId"/> — the context id captured from the <em>original</em> device at
    /// construction — unchanged. This is exactly how a caller could (incorrectly) reuse a renderer
    /// across two unrelated <see cref="IGlDevice"/> instances, and exists so the cross-context guard
    /// is exercisable without a live driver. Production code never calls this: a real
    /// <c>ModelViewportControl</c> constructs a fresh <see cref="ModelRenderer"/> for every device.
    /// </summary>
    internal void AttachDeviceForTesting(IGlDevice device) => _device = device ?? throw new ArgumentNullException(nameof(device));

    private bool IsCrossContextOrLost() => _device.IsLost || _device.ContextId != ContextId;

    private void ReleaseUploadedGeometryAndMaterials()
    {
        foreach (UploadedMesh mesh in _artworkMeshes)
        {
            DeleteAndForget(mesh.VertexBuffer);
            DeleteAndForget(mesh.IndexBuffer);
        }

        foreach (UploadedMesh mesh in _walkmeshMeshes)
        {
            DeleteAndForget(mesh.VertexBuffer);
            DeleteAndForget(mesh.IndexBuffer);
        }

        foreach (UploadedMaterial material in _materials)
        {
            if (material.TextureHandle is { } handle)
            {
                DeleteAndForget(handle);
            }
        }

        _artworkMeshes = [];
        _walkmeshMeshes = [];
        _materials = [];
    }

    private void DeleteAndForget(GlHandle handle)
    {
        _device.Delete(handle);
        _ownedResources.Remove(handle);
    }

    private List<UploadedMesh> UploadMeshes(IReadOnlyList<RenderMesh> meshes)
    {
        List<UploadedMesh> result = new(meshes.Count);
        foreach (RenderMesh mesh in meshes)
        {
            if (mesh.Indices.Length == 0 || mesh.Positions.Length == 0)
            {
                continue;
            }

            ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes<float>(PackVertices(mesh));
            GlHandle vertexBuffer = _device.CreateBuffer(GlBufferTarget.Vertex, vertexBytes);

            ReadOnlySpan<byte> indexBytes = MemoryMarshal.AsBytes<ushort>(mesh.Indices);
            GlHandle indexBuffer = _device.CreateBuffer(GlBufferTarget.Index, indexBytes);

            if (vertexBuffer.IsZero || indexBuffer.IsZero)
            {
                // The device went lost mid-upload (or a budget/driver failure): don't leave a
                // half-built mesh, and don't leak whichever half did succeed.
                if (!vertexBuffer.IsZero)
                {
                    _device.Delete(vertexBuffer);
                }

                if (!indexBuffer.IsZero)
                {
                    _device.Delete(indexBuffer);
                }

                continue;
            }

            _ownedResources.Add(vertexBuffer);
            _ownedResources.Add(indexBuffer);

            result.Add(new UploadedMesh(vertexBuffer, indexBuffer, mesh.Indices.Length, mesh.MaterialIndex, mesh.WorldTransform));
        }

        return result;
    }

    private static float[] PackVertices(RenderMesh mesh)
    {
        int vertexCount = mesh.Positions.Length / 3;
        float[] interleaved = new float[vertexCount * FloatsPerVertex];

        for (int i = 0; i < vertexCount; i++)
        {
            int positionIndex = i * 3;
            int uvIndex = i * 2;
            int dest = i * FloatsPerVertex;

            interleaved[dest + 0] = mesh.Positions[positionIndex + 0];
            interleaved[dest + 1] = mesh.Positions[positionIndex + 1];
            interleaved[dest + 2] = mesh.Positions[positionIndex + 2];

            if (positionIndex + 2 < mesh.Normals.Length)
            {
                interleaved[dest + 3] = mesh.Normals[positionIndex + 0];
                interleaved[dest + 4] = mesh.Normals[positionIndex + 1];
                interleaved[dest + 5] = mesh.Normals[positionIndex + 2];
            }
            else
            {
                // Defensive fallback only — IMdlSceneBuilder always populates Normals (generating
                // them when the source lacked them), so this path should not be reachable in practice.
                interleaved[dest + 3] = 0f;
                interleaved[dest + 4] = 1f;
                interleaved[dest + 5] = 0f;
            }

            if (uvIndex + 1 < mesh.TexCoords.Length)
            {
                interleaved[dest + 6] = mesh.TexCoords[uvIndex + 0];
                interleaved[dest + 7] = mesh.TexCoords[uvIndex + 1];
            }
            else
            {
                interleaved[dest + 6] = 0f;
                interleaved[dest + 7] = 0f;
            }
        }

        return interleaved;
    }

    private void DrawMesh(
        UploadedMesh mesh,
        Matrix4x4 viewProjection,
        int framebuffer,
        int pixelWidth,
        int pixelHeight,
        List<string> boundTextureNames)
    {
        UploadedMaterial? material = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < _materials.Count
            ? _materials[mesh.MaterialIndex]
            : null;

        GlHandle? texture = material?.TextureHandle;
        Vector3 diffuseColor = material?.DiffuseColor ?? Vector3.One;
        bool alphaTest = material?.BlendMode == MaterialBlendMode.AlphaTest;
        bool alphaBlend = material?.BlendMode == MaterialBlendMode.AlphaBlend;
        float alphaThreshold = material?.AlphaTestThreshold ?? 0f;

        var call = new GlDrawCall(
            _program,
            mesh.VertexBuffer,
            mesh.IndexBuffer,
            mesh.IndexCount,
            texture,
            mesh.WorldTransform,
            viewProjection,
            diffuseColor,
            alphaTest,
            alphaThreshold,
            alphaBlend,
            framebuffer,
            pixelWidth,
            pixelHeight);

        _device.Draw(in call);

        if (texture is { IsZero: false } && !string.IsNullOrEmpty(material?.TextureName))
        {
            boundTextureNames.Add(material!.TextureName!);
        }
    }

    private sealed record UploadedMesh(GlHandle VertexBuffer, GlHandle IndexBuffer, int IndexCount, int MaterialIndex, Matrix4x4 WorldTransform);

    private sealed record UploadedMaterial(GlHandle? TextureHandle, string? TextureName, Vector3 DiffuseColor, MaterialBlendMode BlendMode, float AlphaTestThreshold);
}

/// <summary>Lifecycle state of a <see cref="ModelRenderer"/>.</summary>
public enum RendererState
{
    Uninitialized,
    Ready,
    Unsupported,
    Stale,
    Disposed,
}

/// <summary>Result of <see cref="ModelRenderer.Initialize"/>.</summary>
/// <param name="IsSupported">True when the device/capabilities can render this milestone's MVP shading model.</param>
/// <param name="Reason">Human-readable reason when <paramref name="IsSupported"/> is false; null otherwise.</param>
public readonly record struct RendererInitResult(bool IsSupported, string? Reason);
