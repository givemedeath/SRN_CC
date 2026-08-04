using System.Numerics;
using SRN.CC.Core.Services;
using SRN.CC.Preview.Render;

namespace SRN.CC.Tests.Preview.Render.Gl;

/// <summary>
/// Builds small, valid synthetic <see cref="RenderScene"/> fixtures for <c>ModelRenderer</c> tests —
/// no MDL parsing, no textures beyond simple in-memory BGRA blocks. One triangle per mesh is enough
/// to exercise upload/draw semantics.
/// </summary>
internal static class TestSceneFactory
{
    /// <summary>
    /// A scene with three materials (two textured, one untextured-by-color), two artwork meshes
    /// (one per textured material) and one walkmesh mesh referencing the untextured material.
    /// </summary>
    public static RenderScene CreateSceneWithArtworkAndWalkmesh()
    {
        RenderMaterial texturedRed = CreateMaterial("redmat", withTexture: true);
        RenderMaterial texturedBlue = CreateMaterial("bluemat", withTexture: true);
        RenderMaterial untextured = CreateMaterial("", withTexture: false);

        RenderMesh artworkA = CreateTriangleMesh("artwork_a", materialIndex: 0, isWalkmesh: false);
        RenderMesh artworkB = CreateTriangleMesh("artwork_b", materialIndex: 1, isWalkmesh: false);
        RenderMesh walkmesh = CreateTriangleMesh("walk_a", materialIndex: 2, isWalkmesh: true, surfaceId: 3);

        return new RenderScene
        {
            ModelName = "test_model",
            SuperModel = string.Empty,
            IsAsciiSource = true,
            BoundsMinimum = new Vector3(-1f),
            BoundsMaximum = new Vector3(1f),
            Radius = 1.5f,
            ArtworkMeshes = [artworkA, artworkB],
            WalkmeshMeshes = [walkmesh],
            Materials = [texturedRed, texturedBlue, untextured],
            UnsupportedFeatures = [],
            Diagnostics = [],
            ApproximateByteSize = 1024,
        };
    }

    public static RenderMaterial CreateMaterial(string name, bool withTexture)
    {
        TextureImage? diffuse = withTexture
            ? new TextureImage(name, 4, 4, new byte[4 * 4 * 4], HasAlpha: false, TextureOrigin.Workspace)
            : null;

        return new RenderMaterial
        {
            Name = name,
            DiffuseColor = new Vector3(0.8f, 0.8f, 0.8f),
            Diffuse = diffuse,
            Lightmap = null,
            EnvironmentMapName = null,
            BlendMode = MaterialBlendMode.Opaque,
            AlphaTestThreshold = 0.5f,
            UnresolvedTextures = [],
        };
    }

    public static RenderMesh CreateTriangleMesh(string nodeName, int materialIndex, bool isWalkmesh, int surfaceId = 0)
    {
        return new RenderMesh
        {
            NodeName = nodeName,
            WorldTransform = Matrix4x4.Identity,
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            TexCoords = [0f, 0f, 1f, 0f, 0f, 1f],
            Indices = [0, 1, 2],
            MaterialIndex = materialIndex,
            IsWalkmesh = isWalkmesh,
            FaceSurfaceIds = isWalkmesh ? [surfaceId] : [],
            TileFade = 0,
        };
    }
}
