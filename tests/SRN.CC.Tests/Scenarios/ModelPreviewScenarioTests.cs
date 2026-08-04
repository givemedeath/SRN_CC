using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Preview;
using SRN.CC.Preview.Render;
using SRN.CC.Preview.Render.Gl;

namespace SRN.CC.Tests.Scenarios;

/// <summary>
/// End-to-end integration coverage for the milestone-6 gate ("representative one-to-three-slot
/// corpus previews survive unsupported features and context failures without leaks or cross-context
/// handles"), proving the seams between the REAL pieces actually connect: real <see cref="MdlReader"/>
/// (via <see cref="MdlPreviewProvider"/>), real <see cref="MdlSceneBuilder"/>, real
/// <see cref="ModelSceneCache"/>, real <see cref="PreviewContentFactory"/>, and real
/// <see cref="ModelViewportViewModel"/>. Each has its own unit tests elsewhere (S6b, S9, S10, S11);
/// none of those prove the pipeline actually connects end to end, which is this file's entire job.
/// </summary>
/// <remarks>
/// The only test doubles used anywhere in this file are <see cref="FakeGlDevice"/> (a real GPU
/// context cannot exist in this unit-test tier — see <c>tests/SRN.CC.CorpusTests/Render/</c> for the
/// opt-in, real-GPU-adjacent tier) and a minimal in-memory <see cref="ITextureSource"/> (a real
/// curated workspace cannot exist here either). Every geometry/material/scene/VM type is the real,
/// production implementation.
/// </remarks>
[TestFixture]
public class ModelPreviewScenarioTests
{
    // -----------------------------------------------------------------------------------------
    // ASCII MDL fixtures (real MdlReader grammar; see MdlReaderTests.cs and
    // MdlPreviewProviderTests.cs for the same grammar shapes this file reuses).
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Two artwork trimesh nodes with distinct bitmaps ("tex_a"/"tex_b") plus one <c>aabb</c>
    /// walkmesh node, all children of a dummy root. Parameterized by model name so the
    /// "three different occurrences" scenario can build three independent, distinguishable scenes
    /// from the same shape.
    /// </summary>
    private static string TwoMaterialWalkmeshAsciiFixture(string modelName) => $$"""
        newmodel {{modelName}}
        beginmodelgeom {{modelName}}
          node dummy {{modelName}}
            parent NULL
          endnode
          node trimesh panel_a
            parent {{modelName}}
            render 1
            bitmap tex_a
            verts 4
              0 0 0
              1 0 0
              0 1 0
              1 1 0
            tverts 4
              0 0 0
              1 0 0
              0 1 0
              1 1 0
            faces 2
              0 1 2 1 0 1 2 1
              1 3 2 1 1 3 2 1
          endnode
          node trimesh panel_b
            parent {{modelName}}
            render 1
            bitmap tex_b
            verts 3
              2 0 0
              3 0 0
              2 1 0
            tverts 3
              0 0 0
              1 0 0
              0 1 0
            faces 1
              0 1 2 1 0 1 2 1
          endnode
          node aabb walkmesh
            parent {{modelName}}
            verts 3
              0 0 0
              1 0 0
              0 1 0
            faces 1
              0 1 2 1 0 1 2 1
          endnode
        endmodelgeom {{modelName}}
        donemodel {{modelName}}
        """;

    /// <summary>
    /// Verbatim (same shape as <c>MdlReaderTests.ReadsAsciiSkinEmitterAndAnimationControllers</c>):
    /// a skinmesh node, an emitter node, and one animation block — the three feature categories
    /// <see cref="MdlSceneBuilder"/> reports as <see cref="RenderScene.UnsupportedFeatures"/> rather
    /// than failing the preview.
    /// </summary>
    private const string SkinEmitterAnimationAsciiFixture = """
        newmodel sample
        beginmodelgeom sample
          node dummy sample
            parent NULL
          endnode
          node skin cloth
            parent sample
            render 1
            bitmap cloth
            verts 3
              0 0 0
              1 0 0
              0 1 0
            tverts 3
              0 0 0
              1 0 0
              0 1 0
            faces 1
              0 1 2 0 0 1 2 1
            weights 3
              sample 1
              sample .5 arm .5
              arm 1
          endnode
          node emitter sparks
            parent sample
            texture fx_spark
            xgrid 4
            ygrid 2
            update Explosion
            render Normal
            blend Lighten
            loop 1
            twosidedtex 1
          endnode
        endmodelgeom sample
        newanim open sample
          length 1
          transtime .25
          node dummy sample
            parent NULL
            positionkey 2
              0 0 0 0
              1 4 5 6
            orientationkey
              0 0 0 0 0
              1 0 0 1 3.14159265
            endlist
            scalekey 1
              0 1
          endnode
        doneanim open sample
        donemodel sample
        """;

    // ---------------------------------------------------------------------
    // 1. Full pipeline: provider -> real ModelScenePayload -> real content VM -> auto-framed camera
    //    -> artwork/walkmesh partition
    // ---------------------------------------------------------------------

    [Test]
    public async Task FullPipeline_ValidMdlWithArtworkAndWalkmesh_FlowsThroughToRealModelViewportViewModel()
    {
        MdlPreviewProvider provider = CreateProvider(textureSourceAccessor: null);
        AssetOccurrence occurrence = CreateOccurrence("scene_a", Guid.NewGuid());
        PreviewRequest request = CreateRequest(occurrence);
        byte[] bytes = Encoding.ASCII.GetBytes(TwoMaterialWalkmeshAsciiFixture("scene_a"));

        PreviewResult result = await provider.GeneratePreviewAsync(request, new MemoryStream(bytes));

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Payload, Is.InstanceOf<ModelScenePayload>(), "a successful MDL parse must attach a real scene payload");
        ModelScenePayload payload = (ModelScenePayload)result.Payload!;
        RenderScene scene = payload.Scene;

        Assert.That(scene.ArtworkMeshes, Has.Count.EqualTo(2), "panel_a and panel_b are both artwork");
        Assert.That(scene.ArtworkMeshes.Select(m => m.NodeName), Is.EquivalentTo(new[] { "panel_a", "panel_b" }));
        Assert.That(scene.ArtworkMeshes, Has.All.Matches<RenderMesh>(m => !m.IsWalkmesh));

        Assert.That(scene.WalkmeshMeshes, Has.Count.EqualTo(1), "the aabb node is walkmesh, not artwork");
        Assert.That(scene.WalkmeshMeshes[0].NodeName, Is.EqualTo("walkmesh"));
        Assert.That(scene.WalkmeshMeshes[0].IsWalkmesh, Is.True);
        Assert.That(scene.WalkmeshMeshes[0].FaceSurfaceIds, Is.Not.Empty);

        // panel_a ("tex_a"), panel_b ("tex_b"), and the untextured walkmesh node each resolve to
        // their own distinct material (walkmesh nodes are trimesh nodes too, so they go through
        // material resolution exactly like artwork does, just with an empty bitmap name).
        Assert.That(scene.Materials, Has.Count.EqualTo(3), "two distinct artwork bitmaps plus one untextured walkmesh material");

        // Real PreviewContentFactory dispatch -> real ModelViewportViewModel.
        PreviewContentViewModel content = PreviewContentFactory.Create(result);
        Assert.That(content, Is.InstanceOf<ModelViewportViewModel>());
        ModelViewportViewModel viewportViewModel = (ModelViewportViewModel)content;
        Assert.That(viewportViewModel.Scene, Is.SameAs(scene));

        // The VM's camera must be auto-framed from the scene's own bounds via the same
        // RenderCamera.Frame math the VM constructor calls internally.
        RenderCamera expectedCamera = RenderCamera.Frame(scene.BoundsMinimum, scene.BoundsMaximum, scene.Radius);
        Assert.That(viewportViewModel.Camera, Is.EqualTo(expectedCamera));

        content.Dispose();
    }

    // ---------------------------------------------------------------------
    // 2. One-to-three-slot representative scenario
    // ---------------------------------------------------------------------

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task ConcurrentRequestsForSameOccurrence_ProduceExactlyOneSceneBuild_RegardlessOfSlotCount(int slotCount)
    {
        MdlPreviewProvider provider = CreateProvider(textureSourceAccessor: null);
        AssetOccurrence occurrence = CreateOccurrence("shared_model", Guid.NewGuid());
        byte[] bytes = Encoding.ASCII.GetBytes(TwoMaterialWalkmeshAsciiFixture("shared_model"));

        // Simulates `slotCount` comparison slots all previewing the SAME occurrence concurrently
        // (e.g. the same MDL opened side-by-side) — the ModelSceneCache single-flight path must
        // deduplicate the parse+build across all of them, not just within its own unit tests.
        Task<PreviewResult>[] tasks = new Task<PreviewResult>[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            PreviewRequest request = CreateRequest(occurrence);
            tasks[i] = provider.GeneratePreviewAsync(request, new MemoryStream(bytes));
        }

        PreviewResult[] results = await Task.WhenAll(tasks);

        Assert.That(results, Has.All.Matches<PreviewResult>(r => r.IsSuccess));
        RenderScene[] scenes = results.Select(r => ((ModelScenePayload)r.Payload!).Scene).ToArray();
        Assert.That(scenes, Has.All.Matches<RenderScene>(s => ReferenceEquals(s, scenes[0])),
            $"all {slotCount} concurrent requests for the same occurrence must observe the exact same cached RenderScene instance");
    }

    [Test]
    public async Task ThreeDifferentOccurrences_BuildThreeIndependentScenesThatDoNotInterfere()
    {
        MdlPreviewProvider provider = CreateProvider(textureSourceAccessor: null);

        (string ModelName, Guid SourceId)[] occurrenceSpecs =
        {
            ("model_one", Guid.NewGuid()),
            ("model_two", Guid.NewGuid()),
            ("model_three", Guid.NewGuid()),
        };

        Task<PreviewResult>[] tasks = occurrenceSpecs.Select(spec =>
        {
            AssetOccurrence occurrence = CreateOccurrence(spec.ModelName, spec.SourceId);
            PreviewRequest request = CreateRequest(occurrence);
            byte[] bytes = Encoding.ASCII.GetBytes(TwoMaterialWalkmeshAsciiFixture(spec.ModelName));
            return provider.GeneratePreviewAsync(request, new MemoryStream(bytes));
        }).ToArray();

        PreviewResult[] results = await Task.WhenAll(tasks);

        Assert.That(results, Has.All.Matches<PreviewResult>(r => r.IsSuccess));
        RenderScene[] scenes = results.Select(r => ((ModelScenePayload)r.Payload!).Scene).ToArray();

        // Each of the three scenes must carry its own occurrence's model name and must not share a
        // RenderScene instance with either sibling — three independent builds, not accidental reuse.
        for (int i = 0; i < occurrenceSpecs.Length; i++)
        {
            Assert.That(scenes[i].ModelName, Is.EqualTo(occurrenceSpecs[i].ModelName));
            Assert.That(scenes[i].ArtworkMeshes, Has.Count.EqualTo(2));
            Assert.That(scenes[i].WalkmeshMeshes, Has.Count.EqualTo(1));
        }

        Assert.That(scenes[0], Is.Not.SameAs(scenes[1]));
        Assert.That(scenes[1], Is.Not.SameAs(scenes[2]));
        Assert.That(scenes[0], Is.Not.SameAs(scenes[2]));
    }

    // ---------------------------------------------------------------------
    // 3. Unsupported-features survival
    // ---------------------------------------------------------------------

    [Test]
    public async Task SkinmeshEmitterAndAnimationFixture_StillProducesValidSceneWithUnsupportedFeaturesPopulated()
    {
        MdlPreviewProvider provider = CreateProvider(textureSourceAccessor: null);
        AssetOccurrence occurrence = CreateOccurrence("skin_emit_anim", Guid.NewGuid());
        PreviewRequest request = CreateRequest(occurrence);
        byte[] bytes = Encoding.ASCII.GetBytes(SkinEmitterAnimationAsciiFixture);

        PreviewResult result = await provider.GeneratePreviewAsync(request, new MemoryStream(bytes));

        Assert.That(result.IsSuccess, Is.True, "unsupported features must degrade gracefully, never fail the preview");
        Assert.That(result.Payload, Is.InstanceOf<ModelScenePayload>());
        RenderScene scene = ((ModelScenePayload)result.Payload!).Scene;

        Assert.That(scene.UnsupportedFeatures.Any(f => f.Contains("skinmesh", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(scene.UnsupportedFeatures.Any(f => f.Contains("emitter", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(scene.UnsupportedFeatures.Any(f => f.Contains("animation", StringComparison.OrdinalIgnoreCase)), Is.True);

        // A skinmesh is still a trimesh: it renders at rest pose rather than being dropped.
        Assert.That(scene.ArtworkMeshes.Any(m => m.NodeName == "cloth"), Is.True);
    }

    // ---------------------------------------------------------------------
    // 4. Degradation path: header-fallback trips, Payload stays null, text preview is the truth
    // ---------------------------------------------------------------------

    [Test]
    public async Task MalformedBinaryMdl_DegradesToHeaderFallback_WithNullPayloadAndFallbackStatusText()
    {
        // Same shape as MdlPreviewProviderTests.GeneratePreviewAsync_MalformedBinaryMdl_...: a
        // declared model-data size (200) that cannot fit inside the declared raw-data size (32),
        // which the primary binary MdlReader rejects while the header-fallback parser can still
        // recover partial metadata from the (mostly zero, >= 200 byte) buffer.
        byte[] bytes = new byte[12 + 232];
        WriteUInt32(bytes, 4, 200);
        WriteUInt32(bytes, 8, 32);

        MdlPreviewProvider provider = CreateProvider(textureSourceAccessor: null);
        AssetOccurrence occurrence = CreateOccurrence("broken_model", Guid.NewGuid());
        PreviewRequest request = CreateRequest(occurrence);

        PreviewResult result = await provider.GeneratePreviewAsync(request, new MemoryStream(bytes));

        Assert.That(result.IsSuccess, Is.True, "the header-fallback path is a supported outcome, not a failure");
        Assert.That(result.Payload, Is.Null, "the fallback path has no real geometry, so it must never attach a scene payload");
        Assert.That(result.FormattedContent, Does.Contain("Status: Partial metadata extracted from header fallback."));

        // The degraded result must still flow through the real content factory to a text VM (the
        // "text preview stays the reliable degradation target" contract from architecture decision A4).
        PreviewContentViewModel content = PreviewContentFactory.Create(result);
        Assert.That(content, Is.InstanceOf<TextContentViewModel>());
        content.Dispose();
    }

    // ---------------------------------------------------------------------
    // 5. CPU scene -> GPU upload seam, using a REAL scene from the pipeline above
    // ---------------------------------------------------------------------

    [Test]
    public async Task RealSceneFromPipeline_UploadsAndRendersThroughRealModelRenderer_WithMatchingDrawCallsAndTextureNames()
    {
        FakeTexturedSource textureSource = new();
        textureSource.AddTga("tex_a.tga", width: 2, height: 2);
        textureSource.AddTga("tex_b.tga", width: 3, height: 3);

        MdlPreviewProvider provider = CreateProvider(() => textureSource);
        AssetOccurrence occurrence = CreateOccurrence("textured_scene", Guid.NewGuid());
        PreviewRequest request = CreateRequest(occurrence);
        byte[] bytes = Encoding.ASCII.GetBytes(TwoMaterialWalkmeshAsciiFixture("textured_scene"));

        PreviewResult result = await provider.GeneratePreviewAsync(request, new MemoryStream(bytes));
        Assert.That(result.IsSuccess, Is.True);
        RenderScene scene = ((ModelScenePayload)result.Payload!).Scene;

        // panel_a/panel_b's materials plus the untextured walkmesh material (see the comment in
        // FullPipeline_... above). The two artwork materials must have actually resolved a real
        // decoded texture through the real TextureResolver/TextureDecoder ladder — otherwise the
        // bound-texture-name assertion below would be vacuously true.
        Assert.That(scene.Materials, Has.Count.EqualTo(3));
        int[] artworkMaterialIndices = scene.ArtworkMeshes.Select(m => m.MaterialIndex).Distinct().ToArray();
        Assert.That(artworkMaterialIndices, Has.Length.EqualTo(2));
        Assert.That(artworkMaterialIndices.Select(i => scene.Materials[i]), Has.All.Matches<RenderMaterial>(m => m.Diffuse != null));

        using FakeGlDevice device = new();
        using ModelRenderer renderer = new(device);

        RendererInitResult init = renderer.Initialize(device.Capabilities);
        Assert.That(init.IsSupported, Is.True);

        renderer.Upload(scene);

        RenderCamera camera = RenderCamera.Frame(scene.BoundsMinimum, scene.BoundsMaximum, scene.Radius);
        renderer.Render(0, camera, 256, 256, showWalkmesh: true);

        int expectedDrawCalls = scene.ArtworkMeshes.Count + scene.WalkmeshMeshes.Count;
        Assert.That(renderer.LastFrameDrawCallCount, Is.EqualTo(expectedDrawCalls),
            "draw-call count must match the real scene's actual artwork+walkmesh mesh count");
        Assert.That(renderer.LastFrameBoundTextureNames, Is.EquivalentTo(new[] { "tex_a", "tex_b" }),
            "bound texture names must match the real scene's actual resolved material textures");

        renderer.Render(0, camera, 256, 256, showWalkmesh: false);
        Assert.That(renderer.LastFrameDrawCallCount, Is.EqualTo(scene.ArtworkMeshes.Count),
            "showWalkmesh: false must exclude every WalkmeshMeshes draw call");
    }

    // ---------------------------------------------------------------------
    // 6. No leaks across the full one-to-three-slot lifecycle
    // ---------------------------------------------------------------------

    [Test]
    public async Task ThreeConcurrentModelPreviews_UploadRenderDisposeLifecycle_LeavesEveryDeviceLiveResourcesEmpty()
    {
        MdlPreviewProvider provider = CreateProvider(textureSourceAccessor: null);

        (string ModelName, Guid SourceId)[] occurrenceSpecs =
        {
            ("slot_one", Guid.NewGuid()),
            ("slot_two", Guid.NewGuid()),
            ("slot_three", Guid.NewGuid()),
        };

        List<RenderScene> scenes = new();
        foreach ((string modelName, Guid sourceId) in occurrenceSpecs)
        {
            AssetOccurrence occurrence = CreateOccurrence(modelName, sourceId);
            PreviewRequest request = CreateRequest(occurrence);
            byte[] bytes = Encoding.ASCII.GetBytes(TwoMaterialWalkmeshAsciiFixture(modelName));
            PreviewResult result = await provider.GeneratePreviewAsync(request, new MemoryStream(bytes));
            Assert.That(result.IsSuccess, Is.True);
            scenes.Add(((ModelScenePayload)result.Payload!).Scene);
        }

        Assert.That(scenes, Has.Count.EqualTo(3));

        RenderCamera camera = RenderCamera.Frame(scenes[0].BoundsMinimum, scenes[0].BoundsMaximum, scenes[0].Radius);

        foreach (RenderScene scene in scenes)
        {
            using FakeGlDevice device = new();
            using ModelRenderer renderer = new(device);

            renderer.Initialize(device.Capabilities);
            renderer.Upload(scene);
            renderer.Render(0, camera, 128, 128, showWalkmesh: true);

            Assert.That(device.LiveResources, Is.Not.Empty, "the upload should have created live GL resources before disposal");

            renderer.Dispose();

            Assert.That(device.LiveResources, Is.Empty, $"device for '{scene.ModelName}' must not leak any GL resource after Dispose");
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static MdlPreviewProvider CreateProvider(Func<ITextureSource?>? textureSourceAccessor) =>
        new(new ResourceTypeRegistry(), new MdlSceneBuilder(), new ModelSceneCache(), textureSourceAccessor);

    private static AssetOccurrence CreateOccurrence(string resref, Guid sourceId) => new(
        identity: new AssetIdentity(resref, 2002),
        sourceId: sourceId,
        locator: new HakEntryLocator(1),
        originalName: $"{resref}.mdl",
        size: 100,
        validationState: ValidationState.Valid,
        extensionMetadata: null,
        sha256: null);

    private static PreviewRequest CreateRequest(AssetOccurrence occurrence) =>
        new(occurrence, AssetSource.CreateHak("c:/test/source.hak", 0), PreviewFamily.Model);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    /// <summary>
    /// Minimal in-memory <see cref="ITextureSource"/> serving uncompressed truecolor TGA bytes,
    /// used only where a real curated workspace genuinely cannot exist in a unit test (the same
    /// shape as <c>TextureResolverTests.FakeTextureSource</c>). Feeds real bytes through the real
    /// <see cref="TextureResolver"/>/<see cref="TextureDecoder"/> ladder so
    /// <see cref="RenderMaterial.Diffuse"/> is a genuinely decoded texture, not a stub.
    /// </summary>
    private sealed class FakeTexturedSource : ITextureSource
    {
        private readonly Dictionary<string, byte[]> _entries = new(StringComparer.OrdinalIgnoreCase);

        public void AddTga(string resrefWithExtension, ushort width, ushort height) =>
            _entries[resrefWithExtension] = BuildTga(width, height);

        public Task<TextureLookupResult?> OpenTextureAsync(
            string resref, TextureKind kind, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_entries.TryGetValue(resref, out byte[]? bytes))
            {
                return Task.FromResult<TextureLookupResult?>(null);
            }

            AssetIdentity identity = new(StripExtension(resref), 0);
            TextureLookupResult result = new(identity, 0, new MemoryStream(bytes), TextureOrigin.Workspace);
            return Task.FromResult<TextureLookupResult?>(result);
        }

        private static string StripExtension(string name)
        {
            int lastDot = name.LastIndexOf('.');
            return lastDot > 0 ? name[..lastDot] : name;
        }

        /// <summary>Uncompressed truecolor, 24bpp, top-left origin TGA — matches TextureDecoder's supported subset.</summary>
        private static byte[] BuildTga(ushort width, ushort height)
        {
            byte[] header = new byte[18];
            header[2] = 2; // image type: uncompressed truecolor
            BitConverter.GetBytes(width).CopyTo(header, 12);
            BitConverter.GetBytes(height).CopyTo(header, 14);
            header[16] = 24;
            header[17] = 0x20;

            byte[] body = new byte[width * height * 3];
            byte[] bytes = new byte[header.Length + body.Length];
            header.CopyTo(bytes, 0);
            body.CopyTo(bytes, header.Length);
            return bytes;
        }
    }
}
