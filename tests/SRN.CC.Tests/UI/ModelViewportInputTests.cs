using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.App.Views;
using SRN.CC.Preview.Render;

namespace SRN.CC.Tests.UI;

/// <summary>
/// The model viewport must actually receive pointer input.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia hit-tests a bare <see cref="Control"/> against what it rendered, and this control's
/// picture comes from GL via a custom draw operation that the hit test cannot see. Without
/// <c>ICustomHitTest</c>, <c>InputHitTest</c> returned null over the viewport despite correct bounds
/// and <c>IsHitTestVisible == true</c>, so every pointer handler on it was unreachable: orbit, pan
/// and dolly silently did nothing and the camera could never be moved.
/// </para>
/// <para>
/// These drive real pointer input through the headless window rather than calling the view model's
/// camera methods directly. Calling those directly passes whether or not the control is reachable,
/// which is precisely the bug that shipped.
/// </para>
/// </remarks>
[TestFixture]
public class ModelViewportInputTests
{
    [AvaloniaTest]
    public void TheViewportIsHitTestable()
    {
        (ModelViewportControl control, _, _) = ShowViewport();

        control.InputHitTest(Centre(control)).Should().BeSameAs(
            control, "a viewport the input system cannot see receives no pointer events at all");
    }

    [AvaloniaTest]
    public void ADragOrbitsTheCamera()
    {
        (ModelViewportControl control, ModelViewportViewModel vm, Window window) = ShowViewport();
        float yaw = vm.Camera.Yaw;
        float pitch = vm.Camera.Pitch;

        Drag(window, Centre(control), dx: 60, dy: 20, MouseButton.Left);

        vm.Camera.Yaw.Should().NotBe(yaw);
        vm.Camera.Pitch.Should().NotBe(pitch);
    }

    [AvaloniaTest]
    public void AWheelNotchDolliesTheCamera()
    {
        (ModelViewportControl control, ModelViewportViewModel vm, Window window) = ShowViewport();
        float distance = vm.Camera.Distance;

        window.MouseWheel(Centre(control), new Avalonia.Vector(0, 1));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        vm.Camera.Distance.Should().BeLessThan(distance, "a positive wheel delta zooms in");
    }

    [AvaloniaTest]
    public void ARightDragPansTheCamera()
    {
        (ModelViewportControl control, ModelViewportViewModel vm, Window window) = ShowViewport();
        Vector3 target = vm.Camera.Target;

        Drag(window, Centre(control), dx: 40, dy: 0, MouseButton.Right);

        vm.Camera.Target.Should().NotBe(target);
    }

    private static Point Centre(Control control) =>
        new(control.Bounds.Width / 2, control.Bounds.Height / 2);

    private static void Drag(Window window, Point from, double dx, double dy, MouseButton button)
    {
        Point to = new(from.X + dx, from.Y + dy);
        window.MouseDown(from, button);
        window.MouseMove(to);
        window.MouseUp(to, button);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static (ModelViewportControl Control, ModelViewportViewModel ViewModel, Window Window) ShowViewport()
    {
        ModelViewportViewModel vm = new(Scene());
        ModelViewportControl control = new() { DataContext = vm };
        Window window = new() { Content = control, Width = 400, Height = 300 };
        window.Show();

        // Hit testing needs real bounds, and those only exist once layout has run. Capturing a
        // frame is the headless way to force a full layout and render pass; without it the viewport
        // is legitimately unhittable at (0,0,0,0) and these tests would turn on timing rather than
        // on the behaviour under test.
        window.CaptureRenderedFrame();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        control.Bounds.Width.Should().BeGreaterThan(0, "the fixture must lay the viewport out");

        return (control, vm, window);
    }

    private static RenderScene Scene() => new()
    {
        ModelName = "input-probe",
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
