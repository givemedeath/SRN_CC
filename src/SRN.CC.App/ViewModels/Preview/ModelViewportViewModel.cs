using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Preview;
using SRN.CC.Preview.Render;

namespace SRN.CC.App.ViewModels.Preview;

/// <summary>
/// Content VM for a successful model preview (architecture decision A3). Holds the CPU-side
/// <see cref="RenderScene"/> plus the mutable camera/toggle state that <c>ModelViewportControl</c>
/// (the sibling Avalonia+GL file, per architecture decision A4) reads on every frame and writes in
/// response to pointer input. This VM owns no GPU resources itself — those belong to
/// <c>ModelRenderer</c>/<c>IGlDevice</c>, constructed and disposed entirely inside the control per its
/// own <c>OpenGlControlBase</c> lifecycle callbacks.
/// </summary>
public sealed partial class ModelViewportViewModel : PreviewContentViewModel
{
    public override PreviewFamily Family => PreviewFamily.Model;

    /// <summary>The CPU-side scene this viewport renders. Immutable for the VM's lifetime.</summary>
    public RenderScene Scene { get; }

    /// <summary>
    /// Auto-framed on construction via <see cref="RenderCamera.Frame"/>; every subsequent value comes
    /// from <see cref="Orbit"/>/<see cref="Pan"/>/<see cref="Dolly"/>/<see cref="ResetCommand"/>, all of
    /// which route through <see cref="RenderCamera"/>'s own immutable-struct math (no camera math is
    /// duplicated here).
    /// </summary>
    [ObservableProperty]
    private RenderCamera _camera;

    /// <summary>
    /// Toggles the surface-id-coloured walkmesh overlay (architecture decision A6), excluded from the
    /// artwork draw list. Off by default so the artwork-only view is what a slot shows first.
    /// </summary>
    [ObservableProperty]
    private bool _showWalkmesh;

    /// <summary>
    /// Non-null exactly when <c>ModelViewportControl</c> could not (or was not allowed to) render this
    /// scene — a GPU capability shortfall, a shader link failure, or the
    /// <c>ModelViewportRegistry</c> concurrency backstop refusing a fourth concurrent viewport. The
    /// control sets this property directly (it holds the VM via its DataContext); nothing in this
    /// slice reacts to it. A later, small <c>PreviewSlotViewModel</c> change observes this property's
    /// <see cref="ObservableObject.PropertyChanged"/> notification and, when it becomes non-null,
    /// flips the slot's <c>Content</c> back to a <c>TextContentViewModel</c> wrapping the preview's
    /// existing <c>FormattedContent</c>, folding this reason into <c>DiagnosticsText</c>. Null means
    /// rendering is (or may still become) available.
    /// </summary>
    [ObservableProperty]
    private string? _renderUnavailableReason;

    public ModelViewportViewModel(RenderScene scene)
    {
        Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _camera = RenderCamera.Frame(scene.BoundsMinimum, scene.BoundsMaximum, scene.Radius);
    }

    /// <summary>
    /// Orbits the camera by a mouse-drag-style yaw/pitch delta, in radians. Delegates entirely to
    /// <see cref="RenderCamera.Orbit"/>, which clamps pitch short of the poles.
    /// </summary>
    public void Orbit(float deltaYaw, float deltaPitch) => Camera = Camera.Orbit(deltaYaw, deltaPitch);

    /// <summary>Translates the orbit target by a world-space delta. Delegates to <see cref="RenderCamera.Pan"/>.</summary>
    public void Pan(Vector3 worldDelta) => Camera = Camera.Pan(worldDelta);

    /// <summary>
    /// Scales the camera distance by <paramref name="factor"/> (less than 1 zooms in, greater than 1
    /// zooms out). Delegates to <see cref="RenderCamera.Dolly"/>, which clamps to a sane, always-positive range.
    /// </summary>
    public void Dolly(float factor) => Camera = Camera.Dolly(factor);

    /// <summary>Reframes the camera on the scene's bounds via <see cref="RenderCamera.Frame"/>, discarding all orbit/pan/dolly state.</summary>
    [RelayCommand]
    private void Reset() => Camera = RenderCamera.Frame(Scene.BoundsMinimum, Scene.BoundsMaximum, Scene.Radius);

    /// <summary>
    /// No GPU resources or external subscriptions are owned at this level — <see cref="Scene"/> is
    /// plain CPU data, and <c>ModelViewportControl</c> owns (and disposes, per architecture decision
    /// A4) the actual GL device/renderer. Overridden explicitly to keep this VM's disposal story
    /// documented rather than implicit.
    /// </summary>
    public override void Dispose() => base.Dispose();
}
