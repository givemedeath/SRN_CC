using System.ComponentModel;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.Core.Logging;
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
public sealed class ModelViewportControl : OpenGlControlBase, ICustomHitTest
{
    /// <summary>
    /// Makes the viewport hit-testable across its whole area.
    /// </summary>
    /// <remarks>
    /// Without this the control is invisible to the input system. Avalonia hit-tests a bare
    /// <see cref="Control"/> against what it actually rendered, and this one's picture is produced by
    /// GL through a custom draw operation rather than by anything the hit test can see — so
    /// <c>InputHitTest</c> returns null over the viewport even though its bounds are correct and
    /// <c>IsHitTestVisible</c> is true. Every pointer handler below was therefore dead code: orbit,
    /// pan and dolly silently did nothing. Controls that paint their own background solve this by
    /// having one; a GL surface has to say so explicitly.
    /// </remarks>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

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

    /// <summary>Log category used for the one GPU capability record this control emits.</summary>
    public const string LogCategory = nameof(ModelViewportControl);

    /// <summary>
    /// Application logger, assigned once during composition. A control is constructed by the XAML
    /// runtime and cannot be given constructor dependencies, so this is a static seam rather than
    /// an injected one; it defaults to the no-op logger for the designer and for headless tests.
    /// </summary>
    public static IAppLogger Logger { get; set; } = NullAppLogger.Instance;

    private static int _capabilitiesLogged;

    private IGlDevice? _device;
    private ModelRenderer? _renderer;
    private ModelViewportViewModel? _boundViewModel;
    private bool _registryAcquired;
    private string? _pendingUnavailableReason;
    private Point? _lastPointerPosition;
    private bool _isPanning;

    /// <summary>Guards <see cref="LogFrameOnce"/>; cleared whenever a new scene is uploaded.</summary>
    private bool _frameDiagnosticLogged;

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
        LogCapabilitiesOnce(device.Capabilities);

        var renderer = new ModelRenderer(device);
        RendererInitResult initResult = renderer.Initialize(device.Capabilities);
        if (!initResult.IsSupported)
        {
            device.Dispose();
            string reason = initResult.Reason ?? "model rendering is not supported on this device.";

            // An unsupported device is reported to the slot as text, but the reason never reached
            // the log, which is where an operator looks when a viewport comes up empty.
            _frameDiagnosticLogged = false;
            LogFrameOnce($"renderer initialization refused: {reason}");

            ReportUnavailable(reason);
            return;
        }

        _device = device;
        _renderer = renderer;

        if (_boundViewModel is { } viewModel)
        {
            renderer.Upload(viewModel.Scene);
            _frameDiagnosticLogged = false;
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_renderer is not { State: RendererState.Ready } renderer || _boundViewModel is not { } viewModel)
        {
            LogFrameOnce(
                $"render skipped: renderer={(_renderer is null ? "null" : _renderer.State.ToString())}, "
                + $"viewModel={(_boundViewModel is null ? "null" : "bound")}.");
            return;
        }

        // OpenGlControlBase hands over a framebuffer sized in physical pixels, but Bounds is in
        // device-independent pixels. On any display with scaling the two differ, and passing the DIP
        // size sets a viewport covering only part of the framebuffer.
        (int pixelWidth, int pixelHeight) = FramebufferPixelSize();

        renderer.Render(fb, viewModel.Camera, pixelWidth, pixelHeight, viewModel.ShowWalkmesh);

        LogFrameOnce(
            $"rendered: drawCalls={renderer.LastFrameDrawCallCount}, "
            + $"boundTextures={renderer.LastFrameBoundTextureNames.Count}, "
            + $"pixels={pixelWidth}x{pixelHeight}, dips={(int)Bounds.Width}x{(int)Bounds.Height}, "
            + $"scaling={TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0:0.##}, "
            + $"meshes={viewModel.Scene.ArtworkMeshes.Count}, "
            + $"walkmesh={viewModel.ShowWalkmesh}, "
            + $"camera=(distance {viewModel.Camera.Distance:0.###}, near {viewModel.Camera.NearPlane:0.####}, "
            + $"far {viewModel.Camera.FarPlane:0.#}), model='{viewModel.Scene.ModelName}'.");
    }

    /// <summary>
    /// The framebuffer's size in physical pixels: the control's device-independent bounds scaled by
    /// the render scaling of the window hosting it.
    /// </summary>
    private (int Width, int Height) FramebufferPixelSize()
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scaling <= 0 || double.IsNaN(scaling) || double.IsInfinity(scaling))
        {
            scaling = 1.0;
        }

        return ((int)Math.Round(Bounds.Width * scaling), (int)Math.Round(Bounds.Height * scaling));
    }

    /// <summary>
    /// Emits one record describing the outcome of the first frame after each upload. Per-frame
    /// logging would flood the drawer during an orbit drag; one record per scene is what makes an
    /// empty viewport diagnosable at all, since every state this reports — renderer readiness, draw
    /// call count, viewport size — is otherwise invisible from outside the GL context.
    /// </summary>
    private void LogFrameOnce(string message)
    {
        if (_frameDiagnosticLogged)
        {
            return;
        }

        _frameDiagnosticLogged = true;

        try
        {
            Logger.Log(LogLevel.Info, LogCategory, message);
        }
        catch (Exception)
        {
            // A viewport must not fail because a log sink did.
        }
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

        // Nothing else re-triggers OnOpenGlInit after a context loss, so without reporting here the
        // slot would keep showing a dead, stale frame with no diagnostic (matching the degrade-to-text
        // behavior already applied to init failure and registry refusal, above).
        ReportUnavailable("The 3D rendering context was lost.");

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
            _frameDiagnosticLogged = false;
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

    /// <summary>
    /// Records the probed GPU capabilities exactly once per process, the first time a device is
    /// constructed. The startup preflight deliberately reports GPU capability as deferred rather
    /// than probing it — creating a GL context at launch would turn a driver bug into a launch
    /// failure — so this is where the real answer finally reaches the log.
    /// </summary>
    private static void LogCapabilitiesOnce(GlCapabilities capabilities)
    {
        if (Interlocked.Exchange(ref _capabilitiesLogged, 1) != 0)
        {
            return;
        }

        try
        {
            Logger.Log(LogLevel.Info, LogCategory, $"render-gpu: {capabilities}");
        }
        catch (Exception)
        {
            // A logger is contractually forbidden from throwing, but a diagnostic must never be
            // the reason a 3D preview fails to initialise.
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
