using System.ComponentModel;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.Preview.Render;
using SRN.CC.Preview.Render.Gl;

namespace SRN.CC.App.Views;

/// <summary>
/// The only file in this milestone that sees both Avalonia and GL types (architecture decisions A1
/// and A4): it couples the two exclusively through <c>gl.GetProcAddress</c> — a
/// <c>Func&lt;string, IntPtr&gt;</c> — handed to <see cref="SilkGlDevice"/>'s constructor. Everything
/// else (mesh/material data, camera math, the renderer, the device abstraction) lives in the
/// Avalonia-free <c>SRN.CC.Preview</c> render layer.
/// </summary>
/// <remarks>
/// Hosted via <c>Views/PreviewTemplates/ModelPreviewTemplate.axaml</c>'s <c>DataTemplate</c>, whose
/// <see cref="global::Avalonia.Controls.ContentControl"/> sets this control's
/// <see cref="global::Avalonia.StyledElement.DataContext"/> to the bound
/// <see cref="ModelViewportViewModel"/> — this control reads <see cref="ModelViewportViewModel.Scene"/>,
/// <see cref="ModelViewportViewModel.Camera"/> and <see cref="ModelViewportViewModel.ShowWalkmesh"/> on
/// every frame and writes <see cref="ModelViewportViewModel.RenderUnavailableReason"/> whenever GPU
/// work cannot proceed.
///
/// Redraw strategy: this control does NOT call <see cref="OpenGlControlBase.RequestNextFrameRendering"/>
/// unconditionally from <see cref="OnOpenGlRender"/> — nothing in this milestone's MVP scope animates
/// (no skinning, no emitters, no animation playback; see the plan's Deferred list), so a continuous
/// render loop would burn GPU/CPU for a static frame. Instead a redraw is requested precisely when
/// something that affects the picture changes: after a successful upload, after the bound
/// <see cref="ModelViewportViewModel"/>'s <see cref="ModelViewportViewModel.Camera"/> or
/// <see cref="ModelViewportViewModel.ShowWalkmesh"/> property changes (observed via
/// <see cref="INotifyPropertyChanged.PropertyChanged"/>), and directly from the pointer handlers below.
/// </remarks>
public sealed class ModelViewportControl : OpenGlControlBase
{
    // Chosen so a full-width drag (a few hundred pixels) covers roughly one full turn; matches the
    // "mouse-drag-style" orbit the plan describes without needing per-platform DPI awareness.
    private const double OrbitRadiansPerPixel = 0.01;

    // Scaled by the camera's current distance in OnPointerMoved so panning feels consistent whether
    // the camera is close to or far from the model.
    private const double PanUnitsPerPixelAtUnitDistance = 0.0025;

    // Applied once per wheel-delta unit: a positive delta (scroll up / zoom in) yields a factor below
    // 1, a negative delta (scroll down / zoom out) yields a factor above 1.
    private const double DollyFactorPerWheelDeltaUnit = 0.9;

    /// <summary>
    /// The app-lifetime <see cref="ModelViewportRegistry"/> every <see cref="ModelViewportControl"/>
    /// instance shares. Public and settable so a headless test's <c>[SetUp]</c> can swap in a fresh
    /// instance and get isolated counter state between test runs instead of accumulating across the
    /// whole test assembly's lifetime.
    /// </summary>
    public static ModelViewportRegistry SharedRegistry { get; set; } = new();

    private IGlDevice? _device;
    private ModelRenderer? _renderer;
    private ModelViewportViewModel? _boundViewModel;
    private bool _registryAcquired;
    private string? _pendingUnavailableReason;
    private Point? _lastPointerPosition;
    private bool _isPanning;

    /// <summary>
    /// True once <see cref="OnOpenGlInit"/> has actually run (as opposed to merely being scheduled).
    /// Test-observable diagnostic: headless test hosts have no GL backend, so this must stay false
    /// through an entire attach/detach cycle there — asserting that is the cheapest way to prove this
    /// control never does GPU work it does not need to.
    /// </summary>
    public bool HasAttemptedGlInit { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _registryAcquired = SharedRegistry.TryAcquire();
        if (!_registryAcquired)
        {
            ReportUnavailable("Model viewport limit reached: at most " + ModelViewportRegistry.MaxConcurrent
                + " concurrent 3D previews are allowed.");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _boundViewModel = null;
        }

        if (_registryAcquired)
        {
            SharedRegistry.Release();
            _registryAcquired = false;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DataContextProperty)
        {
            OnDataContextChanged();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        HasAttemptedGlInit = true;

        if (!_registryAcquired)
        {
            // The ModelViewportRegistry backstop already refused this viewport (a fourth concurrent
            // instance); per architecture decision A4 that means no GPU work at all, not even a probe.
            return;
        }

        var device = new SilkGlDevice(gl.GetProcAddress);
        var renderer = new ModelRenderer(device);
        RendererInitResult initResult = renderer.Initialize(device.Capabilities);
        if (!initResult.IsSupported)
        {
            device.Dispose();
            ReportUnavailable(initResult.Reason ?? "model rendering is not supported on this device.");
            return;
        }

        _device = device;
        _renderer = renderer;

        if (_boundViewModel is { } viewModel)
        {
            renderer.Upload(viewModel.Scene);
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_renderer is not { State: RendererState.Ready } renderer || _boundViewModel is not { } viewModel)
        {
            return;
        }

        renderer.Render(fb, viewModel.Camera, (int)Bounds.Width, (int)Bounds.Height, viewModel.ShowWalkmesh);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        // Normal teardown: delete every GL name this control's renderer/device created.
        _renderer?.Dispose();
        _device?.Dispose();
        _renderer = null;
        _device = null;

        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlLost()
    {
        // Context loss (architecture decision A4): the driver already invalidated every name in this
        // context, so Abandon/MarkLost only forget state — neither issues a single GL call.
        _renderer?.Abandon();
        _device?.MarkLost();
        _renderer = null;
        _device = null;

        base.OnOpenGlLost();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            return;
        }

        _lastPointerPosition = point.Position;
        _isPanning = point.Properties.IsRightButtonPressed;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_lastPointerPosition is not { } last || _boundViewModel is not { } viewModel)
        {
            return;
        }

        Point current = e.GetPosition(this);
        Avalonia.Vector delta = current - last;
        _lastPointerPosition = current;

        if (delta.X == 0 && delta.Y == 0)
        {
            return;
        }

        if (_isPanning)
        {
            viewModel.Pan(ScreenDeltaToWorldPan(delta, viewModel.Camera));
        }
        else
        {
            viewModel.Orbit(
                (float)(delta.X * OrbitRadiansPerPixel),
                (float)(-delta.Y * OrbitRadiansPerPixel));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _lastPointerPosition = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (_boundViewModel is not { } viewModel || e.Delta.Y == 0)
        {
            return;
        }

        float factor = MathF.Pow((float)DollyFactorPerWheelDeltaUnit, (float)e.Delta.Y);
        viewModel.Dolly(factor);
    }

    /// <summary>
    /// Converts a screen-space pointer delta into a world-space pan offset along the camera's current
    /// right/up basis (derived the same way <see cref="RenderCamera"/>'s eye offset is), scaled by
    /// distance so panning feels consistent at any zoom level. Screen-right drags the target left
    /// (natural "grab and drag the world" feel), matching how <c>Orbit</c>'s sign is chosen above.
    /// </summary>
    private static Vector3 ScreenDeltaToWorldPan(Avalonia.Vector delta, RenderCamera camera)
    {
        float cosPitch = MathF.Cos(camera.Pitch);
        var eyeDirection = new Vector3(
            cosPitch * MathF.Sin(camera.Yaw),
            MathF.Sin(camera.Pitch),
            cosPitch * MathF.Cos(camera.Yaw));

        Vector3 forward = Vector3.Normalize(-eyeDirection);
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 up = Vector3.Cross(forward, right);

        float scale = camera.Distance * (float)PanUnitsPerPixelAtUnitDistance;
        return (right * (float)-delta.X + up * (float)delta.Y) * scale;
    }

    private void OnDataContextChanged()
    {
        if (!ReferenceEquals(_boundViewModel, DataContext))
        {
            if (_boundViewModel is not null)
            {
                _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _boundViewModel = DataContext as ModelViewportViewModel;

            if (_boundViewModel is not null)
            {
                _boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        if (_boundViewModel is not { } viewModel)
        {
            return;
        }

        if (_pendingUnavailableReason is { } reason)
        {
            viewModel.RenderUnavailableReason = reason;
            return;
        }

        if (_renderer is { State: RendererState.Ready } renderer)
        {
            renderer.Upload(viewModel.Scene);
            RequestNextFrameRendering();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelViewportViewModel.Camera) or nameof(ModelViewportViewModel.ShowWalkmesh))
        {
            RequestNextFrameRendering();
        }
    }

    private void ReportUnavailable(string reason)
    {
        _pendingUnavailableReason = reason;
        if (_boundViewModel is { } viewModel)
        {
            viewModel.RenderUnavailableReason = reason;
        }
    }
}
