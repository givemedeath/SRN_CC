using System.Numerics;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Preview.Render;
using SRN.CC.Preview.Render.Gl;

namespace SRN.CC.Tests.Preview.Render.Gl;

/// <summary>
/// Every frame must clear the framebuffer before it draws.
/// </summary>
/// <remarks>
/// <para>
/// The regression this exists for produced no error of any kind. The host supplies a framebuffer
/// whose depth attachment holds undefined content, and <c>SilkGlDevice</c> enables depth testing for
/// its whole lifetime, so with no depth clear the test ran against stale values and discarded every
/// fragment. On the machine that reproduced it, a model logged 21 draw calls and 20 bound textures
/// into a correctly sized viewport and displayed nothing at all.
/// </para>
/// <para>
/// Nothing above the device can observe that: the draw calls are issued and accepted, and the result
/// is only visible on a real GPU. <c>FakeGlDevice.ClearCount</c> is the seam that makes it assertable
/// without one.
/// </para>
/// </remarks>
[TestFixture]
public class ModelRendererClearTests
{
    private static RenderCamera CreateCamera() =>
        RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1.5f);

    [Test]
    public void Render_ClearsBeforeDrawing()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities).IsSupported.Should().BeTrue();
        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());

        renderer.Render(framebuffer: 7, CreateCamera(), 640, 480, showWalkmesh: false);

        device.ClearCount.Should().Be(
            1,
            "an uncleared depth buffer can reject every fragment, which looks exactly like a model "
            + "that was never drawn");
        renderer.LastFrameDrawCallCount.Should().BeGreaterThan(0, "the fixture scene has artwork meshes");
    }

    [Test]
    public void Render_ClearsTheFramebufferItWasGiven()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities).IsSupported.Should().BeTrue();
        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());

        renderer.Render(framebuffer: 42, CreateCamera(), 640, 480, showWalkmesh: false);

        device.LastClearFramebuffer.Should().Be(
            42, "clearing the default framebuffer instead would wipe the wrong surface");
    }

    [Test]
    public void EveryFrame_ClearsAgain()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities).IsSupported.Should().BeTrue();
        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());

        for (int i = 0; i < 5; i++)
        {
            renderer.Render(framebuffer: 1, CreateCamera(), 640, 480, showWalkmesh: false);
        }

        device.ClearCount.Should().Be(
            5, "depth accumulates across frames; clearing only the first would break every orbit step");
    }

    [Test]
    public void ASceneWithNoMeshes_StillClears()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities).IsSupported.Should().BeTrue();
        renderer.Upload(EmptyScene());

        renderer.Render(framebuffer: 1, CreateCamera(), 640, 480, showWalkmesh: false);

        device.ClearCount.Should().Be(
            1, "without a clear the slot would keep showing the previous model's last frame");
        renderer.LastFrameDrawCallCount.Should().Be(0);
    }

    [Test]
    public void AZeroSizedViewport_DoesNotClear()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities).IsSupported.Should().BeTrue();
        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());

        renderer.Render(framebuffer: 1, CreateCamera(), 0, 0, showWalkmesh: false);

        device.ClearCount.Should().Be(0, "there is no surface to clear before layout has run");
    }

    [Test]
    public void ALostDevice_IssuesNoClear()
    {
        using var device = new FakeGlDevice();
        var renderer = new ModelRenderer(device);
        renderer.Initialize(device.Capabilities).IsSupported.Should().BeTrue();
        renderer.Upload(TestSceneFactory.CreateSceneWithArtworkAndWalkmesh());

        device.MarkLost();
        renderer.Render(framebuffer: 1, CreateCamera(), 640, 480, showWalkmesh: false);

        device.ClearCount.Should().Be(
            0, "a clear is a GL call, and a lost context must receive none (architecture decision A4)");
    }

    private static RenderScene EmptyScene() => new()
    {
        ModelName = "empty",
        SuperModel = string.Empty,
        IsAsciiSource = true,
        BoundsMinimum = new Vector3(-1f),
        BoundsMaximum = new Vector3(1f),
        Radius = 1.5f,
        ArtworkMeshes = [],
        WalkmeshMeshes = [],
        Materials = [],
        UnsupportedFeatures = [],
        Diagnostics = [],
        ApproximateByteSize = 0,
    };
}
