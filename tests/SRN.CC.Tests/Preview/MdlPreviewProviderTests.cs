using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;
using SRN.CC.Preview.Render;
using SWLOR.NWN.Formats.Mdl;

namespace SRN.CC.Tests.Preview;

[TestFixture]
public class MdlPreviewProviderTests
{
    /// <summary>
    /// Deliberately degenerate geometry (every vertex at the origin) so the ASCII reader's
    /// bounding-sphere computation lands on an exact, hand-verifiable <c>Radius: 0</c> rather than
    /// an arbitrary floating-point value — this keeps the golden-string assertion in
    /// <see cref="GeneratePreviewAsync_ValidAsciiMdl_ProducesGoldenFormattedContentAndScenePayload"/>
    /// exact instead of approximate.
    /// </summary>
    private const string AsciiFixture = """
        newmodel sample
        beginmodelgeom sample
          node dummy sample
            parent NULL
          endnode
          node trimesh panel
            parent sample
            render 1
            bitmap panel_texture
            verts 3
              0 0 0
              0 0 0
              0 0 0
            faces 1
              0 1 2 0 0 0 0 1
          endnode
        endmodelgeom sample
        donemodel sample
        """;

    // ---------------------------------------------------------------------
    // (a) Valid MDL: golden FormattedContent + non-null ModelScenePayload
    // ---------------------------------------------------------------------

    [Test]
    public async Task GeneratePreviewAsync_ValidAsciiMdl_ProducesGoldenFormattedContentAndScenePayload()
    {
        byte[] bytes = Encoding.ASCII.GetBytes(AsciiFixture);
        RenderScene expectedScene = CreateScene();
        FakeMdlSceneBuilder sceneBuilder = new() { SceneToReturn = expectedScene };
        MdlPreviewProvider provider = new(new StubRegistry(), sceneBuilder, new ModelSceneCache());

        AssetOccurrence occurrence = CreateOccurrence("test_model");
        PreviewRequest request = CreateRequest(occurrence);

        using MemoryStream stream = new(bytes);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True);

        string[] expectedLines =
        {
            "=== MDL PREVIEW ===",
            "Resref: test_model",
            "Format: ASCII MDL",
            "Model Name: sample",
            "SuperModel: ",
            "Model Type: 0",
            "Scale: 1",
            "Radius: 0",
            "Animations: 0",
            "Mesh Blocks: 1",
            "Texture References: 1 unique",
            "  panel_texture x1",
        };
        string expected = string.Join(Environment.NewLine, expectedLines);
        Assert.That(result.FormattedContent, Is.EqualTo(expected));

        Assert.That(result.Payload, Is.InstanceOf<ModelScenePayload>());
        ModelScenePayload payload = (ModelScenePayload)result.Payload!;
        Assert.That(payload.Scene, Is.SameAs(expectedScene));
        Assert.That(payload.PayloadKind, Is.EqualTo("model-scene/v1"));

        Assert.That(sceneBuilder.CallCount, Is.EqualTo(1));
        Assert.That(sceneBuilder.LastIsAsciiSource, Is.True);
        Assert.That(sceneBuilder.LastTextures, Is.Null, "no texture source accessor was supplied");
        Assert.That(sceneBuilder.LastBudget, Is.Not.Null);
        Assert.That(sceneBuilder.LastBudget!.CpuBytes, Is.EqualTo(PreviewStreamHelpers.ModelSceneCpuBudgetBytes));
        Assert.That(sceneBuilder.LastBudget.TextureBytes, Is.EqualTo(PreviewStreamHelpers.ModelTextureSetBudgetBytes));
        Assert.That(sceneBuilder.LastBudget.MaxTextures, Is.EqualTo(PreviewStreamHelpers.ModelMaxTextures));
        Assert.That(sceneBuilder.LastBudget.MaxDrawCalls, Is.EqualTo(PreviewStreamHelpers.ModelMaxDrawCalls));
    }

    // ---------------------------------------------------------------------
    // (b) Header-fallback path: null Payload, fallback status text still present
    // ---------------------------------------------------------------------

    [Test]
    public async Task GeneratePreviewAsync_MalformedBinaryMdl_FallsBackToHeaderMetadataWithNullPayload()
    {
        // Same shape as MdlReaderTests.BinaryModelHeaderMustFitInsideDeclaredModelData: a
        // model-data size (200) that cannot fit inside the declared raw-data size (32), which the
        // primary binary reader rejects with NwnFormatException while the header fallback parser
        // can still recover partial metadata (the buffer is >= 200 bytes and mostly zero, which
        // satisfies the fallback parser's own minimum-length and bounds checks).
        byte[] bytes = new byte[12 + 232];
        WriteUInt32(bytes, 4, 200);
        WriteUInt32(bytes, 8, 32);

        FakeMdlSceneBuilder sceneBuilder = new() { SceneToReturn = CreateScene() };
        MdlPreviewProvider provider = new(new StubRegistry(), sceneBuilder, new ModelSceneCache());

        AssetOccurrence occurrence = CreateOccurrence("broken_model");
        PreviewRequest request = CreateRequest(occurrence);

        using MemoryStream stream = new(bytes);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.FormattedContent, Does.Contain("Status: Partial metadata extracted from header fallback."));
        Assert.That(result.Payload, Is.Null);
        Assert.That(sceneBuilder.CallCount, Is.EqualTo(0), "the header-fallback path must never attempt a scene build");
    }

    // ---------------------------------------------------------------------
    // (c) Three concurrent requests for the same occurrence build the scene once
    // ---------------------------------------------------------------------

    [Test]
    public async Task GeneratePreviewAsync_ThreeConcurrentRequestsForSameOccurrence_BuildSceneExactlyOnce()
    {
        byte[] bytes = Encoding.ASCII.GetBytes(AsciiFixture);
        RenderScene expectedScene = CreateScene();
        TaskCompletionSource<bool> gate = new();
        FakeMdlSceneBuilder sceneBuilder = new() { SceneToReturn = expectedScene, Gate = gate };
        ModelSceneCache cache = new();
        MdlPreviewProvider provider = new(new StubRegistry(), sceneBuilder, cache);

        AssetOccurrence occurrence = CreateOccurrence("test_model");
        PreviewRequest request = CreateRequest(occurrence);

        Task<PreviewResult> t1 = provider.GeneratePreviewAsync(request, new MemoryStream(bytes));
        Task<PreviewResult> t2 = provider.GeneratePreviewAsync(request, new MemoryStream(bytes));
        Task<PreviewResult> t3 = provider.GeneratePreviewAsync(request, new MemoryStream(bytes));

        Assert.That(sceneBuilder.CallCount, Is.EqualTo(1),
            "the first requester's build must already be registered as in-flight before the gate is released");

        gate.SetResult(true);
        PreviewResult[] results = await Task.WhenAll(t1, t2, t3);

        Assert.That(results, Has.All.Matches<PreviewResult>(r => r.IsSuccess));
        Assert.That(sceneBuilder.CallCount, Is.EqualTo(1), "only one scene build must have happened for the shared occurrence");
        foreach (PreviewResult result in results)
        {
            ModelScenePayload payload = (ModelScenePayload)result.Payload!;
            Assert.That(payload.Scene, Is.SameAs(expectedScene));
        }
    }

    // ---------------------------------------------------------------------
    // (d) Scene-builder exception degrades gracefully: text preview stays the source of truth
    // ---------------------------------------------------------------------

    [Test]
    public async Task GeneratePreviewAsync_SceneBuilderThrows_StaysSuccessfulWithNullPayloadAndDiagnostic()
    {
        byte[] bytes = Encoding.ASCII.GetBytes(AsciiFixture);
        FakeMdlSceneBuilder sceneBuilder = new() { ExceptionToThrow = new InvalidOperationException("simulated scene-build failure") };
        MdlPreviewProvider provider = new(new StubRegistry(), sceneBuilder, new ModelSceneCache());

        AssetOccurrence occurrence = CreateOccurrence("test_model");
        PreviewRequest request = CreateRequest(occurrence);

        using MemoryStream stream = new(bytes);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.FormattedContent, Is.Not.Null.And.Not.Empty);
        Assert.That(result.FormattedContent, Does.Contain("Model Name: sample"));
        Assert.That(result.Payload, Is.Null);
        Assert.That(result.Diagnostics.Any(d => d.Contains("Scene build failed", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static AssetOccurrence CreateOccurrence(string resref, Guid? sourceId = null) => new(
        identity: new AssetIdentity(resref, 2002),
        sourceId: sourceId ?? Guid.NewGuid(),
        locator: new HakEntryLocator(1),
        originalName: $"{resref}.mdl",
        size: 100,
        validationState: ValidationState.Valid,
        extensionMetadata: null,
        sha256: null);

    private static PreviewRequest CreateRequest(AssetOccurrence occurrence) =>
        new(occurrence, AssetSource.CreateHak("c:/test/source.hak", 0), PreviewFamily.Model);

    private static RenderScene CreateScene() => new()
    {
        ModelName = "sample",
        SuperModel = string.Empty,
        IsAsciiSource = true,
        BoundsMinimum = Vector3.Zero,
        BoundsMaximum = Vector3.Zero,
        Radius = 0f,
        ArtworkMeshes = Array.Empty<RenderMesh>(),
        WalkmeshMeshes = Array.Empty<RenderMesh>(),
        Materials = Array.Empty<RenderMaterial>(),
        UnsupportedFeatures = Array.Empty<string>(),
        Diagnostics = Array.Empty<string>(),
        ApproximateByteSize = 0,
    };

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private sealed class StubRegistry : IResourceTypeRegistry
    {
        public bool TryGetExtension(ushort typeId, out string extension)
        {
            extension = "mdl";
            return true;
        }

        public bool TryGetType(string extension, out ushort typeId)
        {
            typeId = 2002;
            return true;
        }
    }

    private sealed class FakeMdlSceneBuilder : IMdlSceneBuilder
    {
        private int _callCount;

        public int CallCount => _callCount;

        public RenderScene? SceneToReturn { get; set; }

        public Exception? ExceptionToThrow { get; set; }

        /// <summary>When set, <see cref="BuildAsync"/> suspends until this gate completes, letting tests observe an in-flight build before it finishes.</summary>
        public TaskCompletionSource<bool>? Gate { get; set; }

        public bool LastIsAsciiSource { get; private set; }

        public ITextureSource? LastTextures { get; private set; }

        public SceneBuildBudget? LastBudget { get; private set; }

        public async Task<RenderScene> BuildAsync(
            MdlModel model,
            bool isAsciiSource,
            ITextureSource? textures,
            SceneBuildBudget budget,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            LastIsAsciiSource = isAsciiSource;
            LastTextures = textures;
            LastBudget = budget;

            if (Gate != null)
            {
                await Gate.Task.ConfigureAwait(false);
            }

            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            return SceneToReturn ?? throw new InvalidOperationException("SceneToReturn was not configured.");
        }
    }
}
