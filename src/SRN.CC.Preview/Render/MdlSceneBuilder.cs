using System.Numerics;
using SRN.CC.Core.Services;
using SWLOR.NWN.Formats.Mdl;

namespace SRN.CC.Preview.Render;

/// <summary>
/// First-party builder that turns a parsed <see cref="MdlModel"/> into a GPU-agnostic
/// <see cref="RenderScene"/>: rest-pose world transforms, walkmesh/artwork partitioning, material
/// deduplication, generated normals, and budget-aware degradation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Texture decoding reuses <see cref="TextureResolver"/> (slice S7) rather than duplicating its
/// ladder.</b> When <c>textures</c> is non-null this builder constructs exactly one
/// <see cref="TextureResolver"/> per <see cref="BuildAsync"/> call and calls
/// <see cref="TextureResolver.ResolveAsync"/> once per unique material's bitmap/lightmap name, so
/// <see cref="SceneBuildBudget.MaxTextures"/>/<see cref="SceneBuildBudget.TextureBytes"/> are
/// enforced scene-wide by that single resolver instance rather than per-material. The resolver
/// already performs the MTR-override, DDS/TGA/PLT, and TXI-envmap ladder and hands back decoded
/// <see cref="TextureImage"/> instances (via <see cref="TextureDecoder"/>) plus any discovered
/// environment-map name (report-only, per architecture decision A7 — never shaded here). A name
/// that fails to resolve, or that is skipped because a budget is already spent, lands in
/// <see cref="RenderMaterial.UnresolvedTextures"/> with a matching diagnostic. When
/// <c>textures</c> is null, every non-empty bitmap/lightmap name is treated as unresolved and no
/// resolver is constructed.
/// </para>
/// </remarks>
public sealed class MdlSceneBuilder : IMdlSceneBuilder
{
    private const float DefaultAlphaTestThreshold = 0.5f;

    public async Task<RenderScene> BuildAsync(
        MdlModel model,
        bool isAsciiSource,
        ITextureSource? textures,
        SceneBuildBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(budget);

        cancellationToken.ThrowIfCancellationRequested();

        List<string> diagnostics = new();
        List<string> unsupportedFeatures = new();
        List<(MdlTrimeshNode Node, Matrix4x4 World)> collected = new();

        if (model.GeometryRoot != null)
        {
            WalkNode(model.GeometryRoot, Matrix4x4.Identity, collected, unsupportedFeatures, cancellationToken);
        }

        if (model.Animations.Count > 0)
        {
            unsupportedFeatures.Add(
                $"{model.Animations.Count} animation(s) present on model '{model.Name}' (animation playback is unsupported).");
        }

        List<RenderMesh> artworkMeshes = new();
        List<RenderMesh> walkmeshMeshes = new();
        List<RenderMaterial> materials = new();
        Dictionary<MaterialKey, int> materialLookup = new();
        TextureResolver? resolver = textures != null ? new TextureResolver(textures) : null;

        long approximateBytes = 0;
        int drawCallCount = 0;
        bool cpuBudgetExceededReported = false;
        bool drawCallBudgetExceededReported = false;

        foreach ((MdlTrimeshNode node, Matrix4x4 world) in collected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (drawCallCount >= budget.MaxDrawCalls)
            {
                if (!drawCallBudgetExceededReported)
                {
                    diagnostics.Add(
                        $"Draw-call budget ({budget.MaxDrawCalls}) reached; remaining meshes were omitted from the scene.");
                    drawCallBudgetExceededReported = true;
                }

                continue;
            }

            float[] positions = FlattenVector3(node.Vertices);
            float[] texCoords = FlattenTexCoords(node.TextureCoordinates, node.Vertices.Length);
            float[] normals = node.Normals is { Length: > 0 } sourceNormals && sourceNormals.Length == node.Vertices.Length
                ? FlattenVector3(sourceNormals)
                : GenerateNormals(node.Vertices, node.Faces);
            ushort[] indices = FlattenIndices(node.Faces, node.Vertices.Length, node.Name, diagnostics);
            int[] faceSurfaceIds = node.IsWalkmesh ? ExtractSurfaceIds(node.Faces) : Array.Empty<int>();

            long meshBytes = (long)positions.Length * sizeof(float)
                + (long)normals.Length * sizeof(float)
                + (long)texCoords.Length * sizeof(float)
                + (long)indices.Length * sizeof(ushort)
                + (long)faceSurfaceIds.Length * sizeof(int);

            if (approximateBytes + meshBytes > budget.CpuBytes)
            {
                if (!cpuBudgetExceededReported)
                {
                    diagnostics.Add(
                        $"CPU scene budget ({budget.CpuBytes:N0} bytes) reached; remaining meshes were omitted from the scene.");
                    cpuBudgetExceededReported = true;
                }

                continue;
            }

            approximateBytes += meshBytes;
            drawCallCount++;

            int materialIndex = await ResolveMaterialIndexAsync(
                node,
                resolver,
                budget,
                materials,
                materialLookup,
                diagnostics,
                cancellationToken).ConfigureAwait(false);

            RenderMesh mesh = new()
            {
                NodeName = node.Name,
                WorldTransform = world,
                Positions = positions,
                Normals = normals,
                TexCoords = texCoords,
                Indices = indices,
                MaterialIndex = materialIndex,
                IsWalkmesh = node.IsWalkmesh,
                FaceSurfaceIds = faceSurfaceIds,
                TileFade = node.TileFade,
            };

            if (node.IsWalkmesh)
            {
                walkmeshMeshes.Add(mesh);
            }
            else
            {
                artworkMeshes.Add(mesh);
            }
        }

        return new RenderScene
        {
            ModelName = model.Name,
            SuperModel = model.SuperModel,
            IsAsciiSource = isAsciiSource,
            BoundsMinimum = model.BoundsMinimum,
            BoundsMaximum = model.BoundsMaximum,
            Radius = model.Radius,
            ArtworkMeshes = artworkMeshes,
            WalkmeshMeshes = walkmeshMeshes,
            Materials = materials,
            UnsupportedFeatures = unsupportedFeatures,
            Diagnostics = diagnostics,
            ApproximateByteSize = approximateBytes,
        };
    }

    /// <summary>
    /// Recursively walks the node tree from <paramref name="parentWorld"/>, composing each node's
    /// rest-pose world transform (scale, then rotation, then translation, then the parent's world
    /// transform — standard row-vector TRS composition) and collecting trimesh nodes (including
    /// skinmesh nodes, rendered at rest pose) along with unsupported-feature diagnostics for
    /// skinmesh/emitter nodes.
    /// </summary>
    private static void WalkNode(
        MdlNode node,
        Matrix4x4 parentWorld,
        List<(MdlTrimeshNode Node, Matrix4x4 World)> collected,
        List<string> unsupportedFeatures,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Matrix4x4 local = Matrix4x4.CreateScale(node.Scale)
            * Matrix4x4.CreateFromQuaternion(node.Orientation)
            * Matrix4x4.CreateTranslation(node.Position);
        Matrix4x4 world = local * parentWorld;

        switch (node)
        {
            case MdlSkinmeshNode skinmesh:
                unsupportedFeatures.Add(
                    $"skinmesh node '{skinmesh.Name}' (bone deformation is unsupported; rendered at rest pose).");
                collected.Add((skinmesh, world));
                break;
            case MdlTrimeshNode trimesh:
                collected.Add((trimesh, world));
                break;
            case MdlEmitterNode emitter:
                unsupportedFeatures.Add($"emitter node '{emitter.Name}' (particle emitters are unsupported).");
                break;
        }

        foreach (MdlNode child in node.Children)
        {
            WalkNode(child, world, collected, unsupportedFeatures, cancellationToken);
        }
    }

    /// <summary>
    /// Returns the index of the deduplicated <see cref="RenderMaterial"/> for <paramref name="node"/>,
    /// building and resolving (via <see cref="TextureResolver"/>, when available) a new one on first
    /// sight of its material identity (bitmap + lightmap + diffuse colour).
    /// </summary>
    private static async Task<int> ResolveMaterialIndexAsync(
        MdlTrimeshNode node,
        TextureResolver? resolver,
        SceneBuildBudget budget,
        List<RenderMaterial> materials,
        Dictionary<MaterialKey, int> materialLookup,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        bool hasBitmap = !string.IsNullOrEmpty(node.Bitmap);
        bool hasLightmap = !string.IsNullOrEmpty(node.Lightmap);
        string bitmapLower = hasBitmap ? node.Bitmap.ToLowerInvariant() : string.Empty;
        string lightmapLower = hasLightmap ? node.Lightmap.ToLowerInvariant() : string.Empty;

        MaterialKey key = new(bitmapLower, lightmapLower, node.Diffuse.X, node.Diffuse.Y, node.Diffuse.Z);

        if (materialLookup.TryGetValue(key, out int existingIndex))
        {
            return existingIndex;
        }

        List<string> unresolvedTextures = new();
        TextureImage? diffuseImage = null;
        TextureImage? lightmapImage = null;
        string? environmentMapName = null;

        if (resolver != null)
        {
            if (hasBitmap)
            {
                TextureResolution diffuseResolution = await resolver
                    .ResolveAsync(node.Bitmap, TextureKind.Diffuse, budget, cancellationToken)
                    .ConfigureAwait(false);
                diffuseImage = diffuseResolution.Image;
                environmentMapName ??= diffuseResolution.EnvironmentMapName;
                unresolvedTextures.AddRange(diffuseResolution.UnresolvedTextures);
                diagnostics.AddRange(diffuseResolution.Diagnostics);
            }

            if (hasLightmap)
            {
                TextureResolution lightmapResolution = await resolver
                    .ResolveAsync(node.Lightmap, TextureKind.Lightmap, budget, cancellationToken)
                    .ConfigureAwait(false);
                lightmapImage = lightmapResolution.Image;
                environmentMapName ??= lightmapResolution.EnvironmentMapName;
                unresolvedTextures.AddRange(lightmapResolution.UnresolvedTextures);
                diagnostics.AddRange(lightmapResolution.Diagnostics);
            }
        }
        else
        {
            if (hasBitmap)
            {
                unresolvedTextures.Add(bitmapLower);
                diagnostics.Add($"Texture '{bitmapLower}' left unresolved: no texture source is available.");
            }

            if (hasLightmap)
            {
                unresolvedTextures.Add(lightmapLower);
                diagnostics.Add($"Texture '{lightmapLower}' left unresolved: no texture source is available.");
            }
        }

        MaterialBlendMode blendMode = diffuseImage is { HasAlpha: true }
            ? MaterialBlendMode.AlphaTest
            : MaterialBlendMode.Opaque;

        RenderMaterial material = new()
        {
            Name = bitmapLower,
            DiffuseColor = node.Diffuse,
            Diffuse = diffuseImage,
            Lightmap = lightmapImage,
            EnvironmentMapName = environmentMapName,
            BlendMode = blendMode,
            AlphaTestThreshold = DefaultAlphaTestThreshold,
            UnresolvedTextures = unresolvedTextures,
        };

        int newIndex = materials.Count;
        materials.Add(material);
        materialLookup.Add(key, newIndex);
        return newIndex;
    }

    private static float[] FlattenVector3(Vector3[] values)
    {
        float[] result = new float[values.Length * 3];
        for (int i = 0; i < values.Length; i++)
        {
            result[(i * 3) + 0] = values[i].X;
            result[(i * 3) + 1] = values[i].Y;
            result[(i * 3) + 2] = values[i].Z;
        }

        return result;
    }

    private static float[] FlattenTexCoords(Vector2[] uvs, int vertexCount)
    {
        float[] result = new float[vertexCount * 2];
        int count = Math.Min(uvs.Length, vertexCount);
        for (int i = 0; i < count; i++)
        {
            result[(i * 2) + 0] = uvs[i].X;
            result[(i * 2) + 1] = uvs[i].Y;
        }

        return result;
    }

    /// <summary>
    /// Flattens face indices for the GL index buffer, collapsing any face whose vertex index is
    /// beyond <paramref name="vertexCount"/> to a degenerate (0,0,0) triangle instead of passing the
    /// raw, unvalidated value through — a malformed binary MDL can contain out-of-range indices (see
    /// <see cref="GenerateNormals"/>'s equivalent guard), and an unvalidated index reaching
    /// <c>glDrawElements</c> is an out-of-bounds GPU vertex-buffer read.
    /// </summary>
    private static ushort[] FlattenIndices(MdlFace[] faces, int vertexCount, string nodeName, List<string> diagnostics)
    {
        if (vertexCount == 0)
        {
            if (faces.Length > 0)
            {
                diagnostics.Add($"Node '{nodeName}': {faces.Length} face(s) reference vertices but the node has none; faces were dropped.");
            }

            return Array.Empty<ushort>();
        }

        ushort[] result = new ushort[faces.Length * 3];
        int outOfRangeCount = 0;
        for (int i = 0; i < faces.Length; i++)
        {
            MdlFace face = faces[i];
            bool valid = face.VertexIndex0 < vertexCount && face.VertexIndex1 < vertexCount && face.VertexIndex2 < vertexCount;
            if (!valid)
            {
                outOfRangeCount++;
            }

            result[(i * 3) + 0] = valid ? face.VertexIndex0 : (ushort)0;
            result[(i * 3) + 1] = valid ? face.VertexIndex1 : (ushort)0;
            result[(i * 3) + 2] = valid ? face.VertexIndex2 : (ushort)0;
        }

        if (outOfRangeCount > 0)
        {
            diagnostics.Add(
                $"Node '{nodeName}': {outOfRangeCount} face(s) referenced an out-of-range vertex index and were collapsed to a degenerate triangle.");
        }

        return result;
    }

    private static int[] ExtractSurfaceIds(MdlFace[] faces)
    {
        int[] result = new int[faces.Length];
        for (int i = 0; i < faces.Length; i++)
        {
            result[i] = faces[i].SurfaceId;
        }

        return result;
    }

    /// <summary>Per-face normals, accumulated and averaged per vertex, used when a node lacks authored normals.</summary>
    private static float[] GenerateNormals(Vector3[] vertices, MdlFace[] faces)
    {
        Vector3[] accumulated = new Vector3[vertices.Length];

        foreach (MdlFace face in faces)
        {
            if (face.VertexIndex0 >= vertices.Length
                || face.VertexIndex1 >= vertices.Length
                || face.VertexIndex2 >= vertices.Length)
            {
                continue;
            }

            Vector3 a = vertices[face.VertexIndex0];
            Vector3 b = vertices[face.VertexIndex1];
            Vector3 c = vertices[face.VertexIndex2];
            Vector3 faceNormal = Vector3.Cross(b - a, c - a);

            accumulated[face.VertexIndex0] += faceNormal;
            accumulated[face.VertexIndex1] += faceNormal;
            accumulated[face.VertexIndex2] += faceNormal;
        }

        float[] result = new float[vertices.Length * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 normal = accumulated[i].LengthSquared() > 1e-12f
                ? Vector3.Normalize(accumulated[i])
                : Vector3.UnitZ;
            result[(i * 3) + 0] = normal.X;
            result[(i * 3) + 1] = normal.Y;
            result[(i * 3) + 2] = normal.Z;
        }

        return result;
    }

    private readonly record struct MaterialKey(
        string BitmapLower,
        string LightmapLower,
        float DiffuseX,
        float DiffuseY,
        float DiffuseZ);
}
