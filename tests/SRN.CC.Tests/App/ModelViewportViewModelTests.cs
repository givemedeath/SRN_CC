using System.Numerics;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.Core.Preview;
using SRN.CC.Preview.Render;
using SRN.CC.Tests.Preview.Render.Gl;

namespace SRN.CC.Tests.App;

/// <summary>
/// Covers slice S11's pure VM logic: <see cref="ModelViewportViewModel"/> has no Avalonia or GL
/// dependency at all (those belong to <c>ModelViewportControl</c>, per architecture decision A4), so
/// every assertion here is a plain NUnit test with no headless platform needed.
///
/// Uses <see cref="TestSceneFactory"/> (already exercising <c>ModelRenderer</c> in
/// <c>tests/SRN.CC.Tests/Preview/Render/Gl</c>) to avoid duplicating a synthetic
/// <see cref="RenderScene"/> builder for this slice's fixture.
/// </summary>
[TestFixture]
public class ModelViewportViewModelTests
{
    [Test]
    public void Constructor_AutoFramesCameraFromSceneBounds()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        RenderCamera expected = RenderCamera.Frame(scene.BoundsMinimum, scene.BoundsMaximum, scene.Radius);

        using var vm = new ModelViewportViewModel(scene);

        vm.Camera.Should().Be(expected);
    }

    [Test]
    public void Constructor_NullScene_Throws()
    {
        Action act = () => new ModelViewportViewModel(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Orbit_DelegatesToRenderCameraOrbit()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        using var vm = new ModelViewportViewModel(scene);
        RenderCamera before = vm.Camera;

        vm.Orbit(0.3f, -0.1f);

        vm.Camera.Should().Be(before.Orbit(0.3f, -0.1f));
    }

    [Test]
    public void Pan_DelegatesToRenderCameraPan()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        using var vm = new ModelViewportViewModel(scene);
        RenderCamera before = vm.Camera;
        var worldDelta = new Vector3(1f, 2f, 3f);

        vm.Pan(worldDelta);

        vm.Camera.Should().Be(before.Pan(worldDelta));
    }

    [Test]
    public void Dolly_DelegatesToRenderCameraDolly()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        using var vm = new ModelViewportViewModel(scene);
        RenderCamera before = vm.Camera;

        vm.Dolly(0.5f);

        vm.Camera.Should().Be(before.Dolly(0.5f));
    }

    [Test]
    public void ResetCommand_AfterOrbitPanAndDolly_ReframesCameraToInitialValue()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        using var vm = new ModelViewportViewModel(scene);
        RenderCamera initial = vm.Camera;

        vm.Orbit(1.2f, 0.4f);
        vm.Pan(new Vector3(5f, 5f, 5f));
        vm.Dolly(3f);
        vm.Camera.Should().NotBe(initial);

        vm.ResetCommand.Execute(null);

        vm.Camera.Should().Be(initial);
    }

    [Test]
    public void ShowWalkmesh_DefaultsFalse_AndToggles()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        using var vm = new ModelViewportViewModel(scene);

        vm.ShowWalkmesh.Should().BeFalse();

        vm.ShowWalkmesh = true;

        vm.ShowWalkmesh.Should().BeTrue();
    }

    [Test]
    public void RenderUnavailableReason_DefaultsNull_AndIsSettableByTheControl()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        using var vm = new ModelViewportViewModel(scene);

        vm.RenderUnavailableReason.Should().BeNull();

        vm.RenderUnavailableReason = "shader link failed";

        vm.RenderUnavailableReason.Should().Be("shader link failed");
    }

    [Test]
    public void RenderUnavailableReason_Change_RaisesPropertyChanged()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        using var vm = new ModelViewportViewModel(scene);
        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.RenderUnavailableReason = "capability shortfall";

        raisedProperties.Should().Contain(nameof(ModelViewportViewModel.RenderUnavailableReason));
    }

    [Test]
    public void Family_IsAlwaysModel()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        using var vm = new ModelViewportViewModel(scene);

        vm.Family.Should().Be(PreviewFamily.Model);
    }

    [Test]
    public void Scene_IsTheConstructorArgument()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        using var vm = new ModelViewportViewModel(scene);

        vm.Scene.Should().BeSameAs(scene);
    }

    [Test]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        var vm = new ModelViewportViewModel(scene);

        Action act = () =>
        {
            vm.Dispose();
            vm.Dispose();
        };

        act.Should().NotThrow();
    }
}
