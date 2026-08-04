using System.Numerics;
using NUnit.Framework;
using SRN.CC.Core.Services;
using SRN.CC.Preview.Render;
using SWLOR.NWN.Formats.Mdl;

namespace SRN.CC.Tests.Preview.Render;

[TestFixture]
public class MdlSceneBuilderTests
{
    private const float Tolerance = 1e-3f;

    // ---------------------------------------------------------------------
    // Vertex/face counts
    // ---------------------------------------------------------------------

    [Test]
    public async Task BuildAsync_FlattensVertexAndFaceCountsFromSourceNode()
    {
        MdlTrimeshNode trimesh = CreateQuadTrimesh("quad");
        MdlModel model = CreateModel(trimesh);

        RenderScene scene = await BuildAsync(model);

        Assert.That(scene.ArtworkMeshes, Has.Count.EqualTo(1));
        RenderMesh mesh = scene.ArtworkMeshes[0];
        Assert.That(mesh.Positions, Has.Length.EqualTo(trimesh.Vertices.Length * 3));
        Assert.That(mesh.Normals, Has.Length.EqualTo(trimesh.Vertices.Length * 3));
        Assert.That(mesh.TexCoords, Has.Length.EqualTo(trimesh.Vertices.Length * 2));
        Assert.That(mesh.Indices, Has.Length.EqualTo(trimesh.Faces.Length * 3));
    }

    // ---------------------------------------------------------------------
    // Three-level world-transform composition
    // ---------------------------------------------------------------------

    [Test]
    public async Task BuildAsync_ComposesWorldTransformThroughThreeLevelHierarchy()
    {
        Quaternion rootOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        Quaternion childOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        Quaternion grandchildOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);

        MdlNode root = new()
        {
            Name = "root",
            Position = new Vector3(10f, 0f, 0f),
            Orientation = rootOrientation,
            Scale = 1f,
        };

        MdlNode child = new()
        {
            Name = "child",
            Parent = root,
            Position = new Vector3(0f, 5f, 0f),
            Orientation = childOrientation,
            Scale = 2f,
        };
        root.Children.Add(child);

        MdlTrimeshNode grandchild = CreateQuadTrimesh("grandchild");
        grandchild.Parent = child;
        grandchild.Position = new Vector3(1f, 0f, 0f);
        grandchild.Orientation = grandchildOrientation;
        grandchild.Scale = 0.5f;
        child.Children.Add(grandchild);

        MdlModel model = new() { Name = "hierarchy", GeometryRoot = root };

        RenderScene scene = await BuildAsync(model);

        Assert.That(scene.ArtworkMeshes, Has.Count.EqualTo(1));
        Matrix4x4 actual = scene.ArtworkMeshes[0].WorldTransform;

        // Independently reconstructed expected matrix: standard TRS composition
        // (scale, rotate, translate per node) chained root-to-leaf.
        static Matrix4x4 Local(MdlNode node) =>
            Matrix4x4.CreateScale(node.Scale)
            * Matrix4x4.CreateFromQuaternion(node.Orientation)
            * Matrix4x4.CreateTranslation(node.Position);

        Matrix4x4 expected = Local(grandchild) * Local(child) * Local(root);

        AssertMatrixEqual(expected, actual);

        Vector3 knownPoint = new(1f, 2f, 3f);
        Vector3 expectedTransformed = Vector3.Transform(knownPoint, expected);
        Vector3 actualTransformed = Vector3.Transform(knownPoint, actual);
        Assert.That(actualTransformed.X, Is.EqualTo(expectedTransformed.X).Within(Tolerance));
        Assert.That(actualTransformed.Y, Is.EqualTo(expectedTransformed.Y).Within(Tolerance));
        Assert.That(actualTransformed.Z, Is.EqualTo(expectedTransformed.Z).Within(Tolerance));
    }

    // ---------------------------------------------------------------------
    // Walkmesh / artwork partition
    // ---------------------------------------------------------------------

    [Test]
    public async Task BuildAsync_PartitionsWalkmeshAndArtworkNodesIntoSeparateLists()
    {
        MdlTrimeshNode artwork = CreateQuadTrimesh("artwork", isWalkmesh: false);
        MdlTrimeshNode walkmesh = CreateQuadTrimesh("walk", isWalkmesh: true);

        MdlNode root = new() { Name = "root" };
        root.Children.Add(artwork);
        root.Children.Add(walkmesh);
        artwork.Parent = root;
        walkmesh.Parent = root;

        MdlModel model = new() { Name = "partition", GeometryRoot = root };

        RenderScene scene = await BuildAsync(model);

        Assert.That(scene.ArtworkMeshes, Has.Count.EqualTo(1));
        Assert.That(scene.ArtworkMeshes[0].NodeName, Is.EqualTo("artwork"));
        Assert.That(scene.ArtworkMeshes[0].IsWalkmesh, Is.False);

        Assert.That(scene.WalkmeshMeshes, Has.Count.EqualTo(1));
        Assert.That(scene.WalkmeshMeshes[0].NodeName, Is.EqualTo("walk"));
        Assert.That(scene.WalkmeshMeshes[0].IsWalkmesh, Is.True);
        Assert.That(scene.WalkmeshMeshes[0].FaceSurfaceIds, Has.Length.EqualTo(walkmesh.Faces.Length));
        Assert.That(scene.ArtworkMeshes[0].FaceSurfaceIds, Is.Empty);
    }

    // ---------------------------------------------------------------------
    // Material dedup
    // ---------------------------------------------------------------------

    [Test]
    public async Task BuildAsync_DeduplicatesMaterialsSharingBitmapLightmapAndDiffuse()
    {
        MdlTrimeshNode first = CreateQuadTrimesh("first", bitmap: "Brick01", diffuse: new Vector3(1f, 1f, 1f));
        MdlTrimeshNode second = CreateQuadTrimesh("second", bitmap: "brick01", diffuse: new Vector3(1f, 1f, 1f));
        MdlTrimeshNode third = CreateQuadTrimesh("third", bitmap: "stone02", diffuse: new Vector3(1f, 1f, 1f));

        MdlNode root = new() { Name = "root" };
        foreach (MdlTrimeshNode node in new[] { first, second, third })
        {
            node.Parent = root;
            root.Children.Add(node);
        }

        MdlModel model = new() { Name = "dedup", GeometryRoot = root };

        RenderScene scene = await BuildAsync(model);

        Assert.That(scene.Materials, Has.Count.EqualTo(2), "brick01/Brick01 must collapse to one material");

        int firstIndex = scene.ArtworkMeshes.Single(m => m.NodeName == "first").MaterialIndex;
        int secondIndex = scene.ArtworkMeshes.Single(m => m.NodeName == "second").MaterialIndex;
        int thirdIndex = scene.ArtworkMeshes.Single(m => m.NodeName == "third").MaterialIndex;

        Assert.That(secondIndex, Is.EqualTo(firstIndex));
        Assert.That(thirdIndex, Is.Not.EqualTo(firstIndex));
        Assert.That(scene.Materials[firstIndex].DiffuseColor, Is.EqualTo(new Vector3(1f, 1f, 1f)));
    }

    // ---------------------------------------------------------------------
    // Budget-exceeded degradation
    // ---------------------------------------------------------------------

    [Test]
    public async Task BuildAsync_WithTinyCpuBudget_DegradesWithoutThrowingAndEmitsDiagnostic()
    {
        MdlNode root = new() { Name = "root" };
        for (int i = 0; i < 5; i++)
        {
            MdlTrimeshNode node = CreateLargeTrimesh($"mesh{i}", vertexCount: 200);
            node.Parent = root;
            root.Children.Add(node);
        }

        MdlModel model = new() { Name = "big", GeometryRoot = root };
        SceneBuildBudget tinyBudget = new(CpuBytes: 64, TextureBytes: 1024, MaxTextures: 8, MaxDrawCalls: 4096);

        RenderScene? scene = null;
        Assert.DoesNotThrowAsync(async () =>
            scene = await new MdlSceneBuilder().BuildAsync(model, isAsciiSource: true, textures: null, tinyBudget));

        Assert.That(scene, Is.Not.Null);
        Assert.That(scene!.ArtworkMeshes.Count, Is.LessThan(5), "budget should have omitted at least one mesh");
        Assert.That(scene.Diagnostics, Is.Not.Empty);
        Assert.That(scene.Diagnostics.Any(d => d.Contains("budget", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task BuildAsync_WithTinyDrawCallBudget_DegradesWithoutThrowingAndEmitsDiagnostic()
    {
        MdlNode root = new() { Name = "root" };
        for (int i = 0; i < 3; i++)
        {
            MdlTrimeshNode node = CreateQuadTrimesh($"mesh{i}");
            node.Parent = root;
            root.Children.Add(node);
        }

        MdlModel model = new() { Name = "manyDraws", GeometryRoot = root };
        SceneBuildBudget budget = DefaultBudget() with { MaxDrawCalls = 1 };

        RenderScene scene = await new MdlSceneBuilder().BuildAsync(model, isAsciiSource: true, textures: null, budget);

        Assert.That(scene.ArtworkMeshes, Has.Count.EqualTo(1));
        Assert.That(scene.Diagnostics.Any(d => d.Contains("Draw-call", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    // ---------------------------------------------------------------------
    // Mid-walk cancellation
    // ---------------------------------------------------------------------

    [Test]
    public void BuildAsync_WithPreCancelledToken_ThrowsOperationCanceledException()
    {
        MdlTrimeshNode trimesh = CreateQuadTrimesh("quad");
        MdlModel model = CreateModel(trimesh);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await new MdlSceneBuilder().BuildAsync(model, isAsciiSource: true, textures: null, DefaultBudget(), cts.Token));
    }

    // ---------------------------------------------------------------------
    // Null ITextureSource
    // ---------------------------------------------------------------------

    [Test]
    public async Task BuildAsync_WithNullTextureSource_ProducesUntexturedSceneWithDiagnostics()
    {
        MdlTrimeshNode trimesh = CreateQuadTrimesh("quad", bitmap: "mytexture");
        MdlModel model = CreateModel(trimesh);

        RenderScene scene = await new MdlSceneBuilder().BuildAsync(model, isAsciiSource: true, textures: null, DefaultBudget());

        Assert.That(scene.Materials, Has.Count.EqualTo(1));
        RenderMaterial material = scene.Materials[0];
        Assert.That(material.Diffuse, Is.Null);
        Assert.That(material.Lightmap, Is.Null);
        Assert.That(material.UnresolvedTextures, Does.Contain("mytexture"));
        Assert.That(scene.Diagnostics, Is.Not.Empty);
    }

    // ---------------------------------------------------------------------
    // Unsupported features
    // ---------------------------------------------------------------------

    [Test]
    public async Task BuildAsync_PopulatesUnsupportedFeaturesForSkinmeshEmitterAndAnimations()
    {
        MdlSkinmeshNode skinmesh = new()
        {
            Name = "skin",
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            Normals = Array.Empty<Vector3>(),
            TextureCoordinates = new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY },
            Faces = new[] { new MdlFace { VertexIndex0 = 0, VertexIndex1 = 1, VertexIndex2 = 2 } },
        };

        MdlEmitterNode emitter = new() { Name = "emit" };

        MdlNode root = new() { Name = "root" };
        skinmesh.Parent = root;
        emitter.Parent = root;
        root.Children.Add(skinmesh);
        root.Children.Add(emitter);

        MdlModel model = new() { Name = "unsupported", GeometryRoot = root };
        model.Animations.Add(new MdlAnimation { Name = "walk" });

        RenderScene scene = await BuildAsync(model);

        Assert.That(scene.UnsupportedFeatures.Any(f => f.Contains("skinmesh", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(scene.UnsupportedFeatures.Any(f => f.Contains("emitter", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(scene.UnsupportedFeatures.Any(f => f.Contains("animation", StringComparison.OrdinalIgnoreCase)), Is.True);

        // A skinmesh node is still a trimesh: it should still render at rest pose.
        Assert.That(scene.ArtworkMeshes.Any(m => m.NodeName == "skin"), Is.True);
    }

    // ---------------------------------------------------------------------
    // Empty geometry root
    // ---------------------------------------------------------------------

    [Test]
    public async Task BuildAsync_WithNullGeometryRoot_ProducesEmptyScene()
    {
        MdlModel model = new() { Name = "empty", GeometryRoot = null };

        RenderScene scene = await BuildAsync(model);

        Assert.That(scene.ArtworkMeshes, Is.Empty);
        Assert.That(scene.WalkmeshMeshes, Is.Empty);
        Assert.That(scene.Materials, Is.Empty);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task<RenderScene> BuildAsync(MdlModel model, ITextureSource? textures = null)
        => await new MdlSceneBuilder().BuildAsync(model, isAsciiSource: true, textures, DefaultBudget());

    private static SceneBuildBudget DefaultBudget() => new(
        CpuBytes: 48L * 1024 * 1024,
        TextureBytes: 32L * 1024 * 1024,
        MaxTextures: 64,
        MaxDrawCalls: 4096);

    private static MdlModel CreateModel(MdlTrimeshNode root) => new() { Name = "model", GeometryRoot = root };

    private static MdlTrimeshNode CreateQuadTrimesh(
        string name,
        bool isWalkmesh = false,
        string bitmap = "",
        string lightmap = "",
        Vector3? diffuse = null,
        int tileFade = 0)
    {
        return new MdlTrimeshNode
        {
            Name = name,
            IsWalkmesh = isWalkmesh,
            Bitmap = bitmap,
            Lightmap = lightmap,
            Diffuse = diffuse ?? Vector3.One,
            TileFade = tileFade,
            Vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(1f, 1f, 0f),
            },
            Normals = Array.Empty<Vector3>(),
            TextureCoordinates = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
            },
            Faces = new[]
            {
                new MdlFace { SurfaceId = 1, VertexIndex0 = 0, VertexIndex1 = 1, VertexIndex2 = 2 },
                new MdlFace { SurfaceId = 2, VertexIndex0 = 1, VertexIndex1 = 3, VertexIndex2 = 2 },
            },
        };
    }

    private static MdlTrimeshNode CreateLargeTrimesh(string name, int vertexCount)
    {
        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i] = new Vector3(i, i * 0.5f, 0f);
            uvs[i] = new Vector2(0f, 0f);
        }

        int faceCount = vertexCount / 3;
        MdlFace[] faces = new MdlFace[faceCount];
        for (int i = 0; i < faceCount; i++)
        {
            int baseIndex = i * 3;
            faces[i] = new MdlFace
            {
                SurfaceId = 0,
                VertexIndex0 = (ushort)baseIndex,
                VertexIndex1 = (ushort)(baseIndex + 1),
                VertexIndex2 = (ushort)(baseIndex + 2),
            };
        }

        return new MdlTrimeshNode
        {
            Name = name,
            Vertices = vertices,
            Normals = Array.Empty<Vector3>(),
            TextureCoordinates = uvs,
            Faces = faces,
        };
    }

    private static void AssertMatrixEqual(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.That(actual.M11, Is.EqualTo(expected.M11).Within(Tolerance));
        Assert.That(actual.M12, Is.EqualTo(expected.M12).Within(Tolerance));
        Assert.That(actual.M13, Is.EqualTo(expected.M13).Within(Tolerance));
        Assert.That(actual.M14, Is.EqualTo(expected.M14).Within(Tolerance));
        Assert.That(actual.M21, Is.EqualTo(expected.M21).Within(Tolerance));
        Assert.That(actual.M22, Is.EqualTo(expected.M22).Within(Tolerance));
        Assert.That(actual.M23, Is.EqualTo(expected.M23).Within(Tolerance));
        Assert.That(actual.M24, Is.EqualTo(expected.M24).Within(Tolerance));
        Assert.That(actual.M31, Is.EqualTo(expected.M31).Within(Tolerance));
        Assert.That(actual.M32, Is.EqualTo(expected.M32).Within(Tolerance));
        Assert.That(actual.M33, Is.EqualTo(expected.M33).Within(Tolerance));
        Assert.That(actual.M34, Is.EqualTo(expected.M34).Within(Tolerance));
        Assert.That(actual.M41, Is.EqualTo(expected.M41).Within(Tolerance));
        Assert.That(actual.M42, Is.EqualTo(expected.M42).Within(Tolerance));
        Assert.That(actual.M43, Is.EqualTo(expected.M43).Within(Tolerance));
        Assert.That(actual.M44, Is.EqualTo(expected.M44).Within(Tolerance));
    }
}
