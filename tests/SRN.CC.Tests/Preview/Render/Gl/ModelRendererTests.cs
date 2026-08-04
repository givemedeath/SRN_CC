using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Preview.Render;
using SRN.CC.Preview.Render.Gl;

namespace SRN.CC.Tests.Preview.Render.Gl;

[TestFixture]
public class ModelRendererTests
{
    private static RenderCamera CreateCamera() => RenderCamera.Frame(new System.Numerics.Vector3(-1f), new System.Numerics.Vector3(1f), 1.5f);

    // -----------------------------------------------------------------------------------------
    // Lifecycle / leak safety
    // -----------------------------------------------------------------------------------------

    [Test]
    public void DisposeAfterUpload_LeavesLiveResourcesEmpty()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);

        renderer.Initialize(device.Capabilities).IsSupported.Should().BeTrue();
        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());
        device.LiveResources.Should().NotBeEmpty("the scene should have created buffers, textures, and a program");

        renderer.Dispose();

        device.LiveResources.Should().BeEmpty();
        renderer.State.Should().Be(RendererState.Disposed);
    }

    [Test]
    public void FiftyUploadDisposeCycles_NeverLeakLiveResources()
    {
        using var device = new FakeGlDevice();

        for (int i = 0; i < 50; i++)
        {
            var renderer = new ModelRenderer(device);
            renderer.Initialize(device.Capabilities);
            renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());
            renderer.Dispose();

            device.LiveResources.Should().BeEmpty($"cycle {i} must not leak any GL resource");
        }
    }

    [Test]
    public void ReUpload_ReleasesThePreviouslyUploadedGeometryAndMaterials()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities);

        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());
        int liveAfterFirstUpload = device.LiveResources.Count;

        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());
        int liveAfterSecondUpload = device.LiveResources.Count;

        liveAfterSecondUpload.Should().Be(liveAfterFirstUpload, "re-uploading must release the prior geometry/materials, not accumulate them");
    }

    // -----------------------------------------------------------------------------------------
    // Context loss / abandon
    // -----------------------------------------------------------------------------------------

    [Test]
    public void SimulateContextLossThenAbandon_IssuesZeroDeviceCallsAndLeavesLiveResourcesEmpty()
    {
        var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities);
        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());

        device.SimulateContextLoss();
        int callsAfterLoss = device.Calls.Count;

        renderer.Abandon();

        device.Calls.Should().HaveCount(callsAfterLoss, "Abandon must issue zero device calls");
        device.LiveResources.Should().BeEmpty();
        renderer.State.Should().Be(RendererState.Stale);
    }

    [Test]
    public void Abandon_ThenRenderAndUpload_AreSafeNoOps()
    {
        var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities);
        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());

        device.SimulateContextLoss();
        renderer.Abandon();

        Action act = () =>
        {
            renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());
            renderer.Render(0, CreateCamera(), 100, 100, showWalkmesh: true);
        };

        act.Should().NotThrow();
        renderer.LastFrameDrawCallCount.Should().Be(0);
    }

    // -----------------------------------------------------------------------------------------
    // Cross-context guard
    // -----------------------------------------------------------------------------------------

    [Test]
    public void RendererBuiltOnDeviceA_UsedAgainstDeviceB_IssuesZeroCallsOnBAndReportsStale()
    {
        var deviceA = new FakeGlDevice();
        var renderer = new ModelRenderer(deviceA);
        renderer.Initialize(deviceA.Capabilities);
        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());

        var deviceB = new FakeGlDevice();
        deviceB.ContextId.Should().NotBe(deviceA.ContextId);
        renderer.AttachDeviceForTesting(deviceB);

        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());
        deviceB.Calls.Should().BeEmpty("the renderer's captured ContextId no longer matches deviceB's");
        renderer.State.Should().Be(RendererState.Stale);

        renderer.Render(0, CreateCamera(), 100, 100, showWalkmesh: true);
        deviceB.Calls.Should().BeEmpty();
        renderer.LastFrameDrawCallCount.Should().Be(0);
    }

    // -----------------------------------------------------------------------------------------
    // Initialize: unsupported paths never throw
    // -----------------------------------------------------------------------------------------

    [Test]
    public void Initialize_WithFailProgramLink_ReturnsUnsupportedWithReason_NeverThrows()
    {
        using var device = new FakeGlDevice(failProgramLink: true);
        var renderer = new ModelRenderer(device);

        RendererInitResult result = default;
        Action act = () => result = renderer.Initialize(device.Capabilities);

        act.Should().NotThrow();
        result.IsSupported.Should().BeFalse();
        result.Reason.Should().NotBeNullOrWhiteSpace();
        renderer.State.Should().Be(RendererState.Unsupported);
        renderer.UnsupportedReason.Should().Be(result.Reason);
    }

    [Test]
    public void Initialize_WithMaxTextureSizeBelowMinimum_ReturnsUnsupportedWithReason()
    {
        var lowCapabilities = new GlCapabilities(
            IsEmbeddedProfile: true,
            MajorVersion: 3,
            MinorVersion: 0,
            HasVertexArrayObjects: true,
            HasNonPowerOfTwoTextures: true,
            MaxTextureSize: 64,
            GlslVersionDirective: "#version 300 es",
            RendererName: "test-low-cap-renderer");
        using var device = new FakeGlDevice(lowCapabilities);
        var renderer = new ModelRenderer(device);

        RendererInitResult result = renderer.Initialize(lowCapabilities);

        result.IsSupported.Should().BeFalse();
        result.Reason.Should().NotBeNullOrWhiteSpace();
        renderer.State.Should().Be(RendererState.Unsupported);
        device.Calls.Should().BeEmpty("a capability shortfall must be caught before any shader compile call");
    }

    [Test]
    public void Initialize_WithSufficientCapabilities_Succeeds()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);

        RendererInitResult result = renderer.Initialize(device.Capabilities);

        result.IsSupported.Should().BeTrue();
        result.Reason.Should().BeNull();
        renderer.State.Should().Be(RendererState.Ready);
    }

    // -----------------------------------------------------------------------------------------
    // Semantic draw-call assertions
    // -----------------------------------------------------------------------------------------

    [Test]
    public void Render_WithShowWalkmeshFalse_DrawsOnlyArtworkMeshes()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities);
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        renderer.Upload(scene);

        renderer.Render(0, CreateCamera(), 200, 150, showWalkmesh: false);

        renderer.LastFrameDrawCallCount.Should().Be(scene.ArtworkMeshes.Count);
        renderer.LastFrameBoundTextureNames.Should().HaveCount(scene.ArtworkMeshes.Count, "both artwork meshes reference textured materials");
        renderer.LastFrameBoundTextureNames.Should().Contain(["redmat", "bluemat"]);
    }

    [Test]
    public void Render_WithShowWalkmeshTrue_DrawsArtworkAndWalkmeshMeshes()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities);
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        renderer.Upload(scene);

        renderer.Render(0, CreateCamera(), 200, 150, showWalkmesh: true);

        renderer.LastFrameDrawCallCount.Should().Be(scene.ArtworkMeshes.Count + scene.WalkmeshMeshes.Count);
    }

    [Test]
    public void Render_BoundTextureNames_ExcludeUntexturedMaterials()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities);
        // Walkmesh mesh (materialIndex 2) references the untextured material — it must never
        // contribute a name to LastFrameBoundTextureNames.
        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());

        renderer.Render(0, CreateCamera(), 200, 150, showWalkmesh: true);

        renderer.LastFrameBoundTextureNames.Should().HaveCount(2);
        renderer.LastFrameBoundTextureNames.Should().NotContain(string.Empty);
    }

    [Test]
    public void Render_IssuesDrawCallsOnTheDevice()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities);
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        renderer.Upload(scene);
        int callsBeforeRender = device.Calls.Count;

        renderer.Render(0, CreateCamera(), 200, 150, showWalkmesh: true);

        int drawCallsRecorded = device.Calls.Skip(callsBeforeRender).Count(c => c.Method == "Draw");
        drawCallsRecorded.Should().Be(scene.ArtworkMeshes.Count + scene.WalkmeshMeshes.Count);
    }

    // -----------------------------------------------------------------------------------------
    // Construction / basic state machine
    // -----------------------------------------------------------------------------------------

    [Test]
    public void ContextId_IsCapturedFromTheDeviceAtConstruction()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);

        renderer.ContextId.Should().Be(device.ContextId);
    }

    [Test]
    public void InitialState_IsUninitialized()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);

        renderer.State.Should().Be(RendererState.Uninitialized);
        renderer.UnsupportedReason.Should().BeNull();
        renderer.LastFrameDrawCallCount.Should().Be(0);
        renderer.LastFrameBoundTextureNames.Should().BeEmpty();
    }

    [Test]
    public void UploadBeforeInitialize_IsANoOp()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);

        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());

        renderer.State.Should().Be(RendererState.Uninitialized);
        device.Calls.Should().BeEmpty();
    }
}
