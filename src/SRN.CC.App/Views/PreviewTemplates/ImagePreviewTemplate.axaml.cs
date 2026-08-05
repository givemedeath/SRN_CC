using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SRN.CC.App.ViewModels.Preview;

namespace SRN.CC.App.Views.PreviewTemplates;

/// <summary>
/// The interactive visual for <see cref="ImageContentViewModel"/>, referenced by
/// <c>ImagePreviewTemplate.axaml</c>'s <c>DataTemplate</c>. Built entirely in code (rather than as
/// AXAML with named event handlers on a code-behind-bound <c>ResourceDictionary</c>) so pointer-wheel
/// zoom and drag-to-pan wire up through the standard, unambiguous <see cref="Control"/> pointer-event
/// overrides. <see cref="Control.DataContext"/> is inherited automatically from the
/// <c>ContentControl</c> that hosts this template, per architecture decision A3.
/// </summary>
public sealed class ImagePreviewCanvas : Border
{
    private readonly Image _image;
    private readonly ScaleTransform _scaleTransform = new();
    private readonly TranslateTransform _translateTransform = new();
    private Point? _dragOrigin;

    public ImagePreviewCanvas()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);

        _image = new Image
        {
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new TransformGroup
            {
                Children = { _scaleTransform, _translateTransform },
            },
        };
        // The image is a control, so a plain binding resolves against the DataContext it inherits.
        _image.Bind(Image.SourceProperty, new Binding(nameof(ImageContentViewModel.Bitmap)));

        // Transforms are not. ScaleTransform and TranslateTransform are bare AvaloniaObjects that
        // sit outside the visual tree and have no DataContext of their own, so a plain binding
        // throws "Cannot find a DataContext to bind to" — from this constructor, which means the
        // whole DataTemplate fails to build and the slot renders nothing at all. Routing each one
        // through this canvas's own DataContext keeps the binding declarative and still tracks the
        // slot swapping one view model for another.
        _scaleTransform.Bind(ScaleTransform.ScaleXProperty, CanvasBinding(nameof(ImageContentViewModel.ZoomScale)));
        _scaleTransform.Bind(ScaleTransform.ScaleYProperty, CanvasBinding(nameof(ImageContentViewModel.ZoomScale)));
        _translateTransform.Bind(TranslateTransform.XProperty, CanvasBinding(nameof(ImageContentViewModel.PanX)));
        _translateTransform.Bind(TranslateTransform.YProperty, CanvasBinding(nameof(ImageContentViewModel.PanY)));

        Child = _image;
    }

    /// <summary>
    /// A binding to <paramref name="property"/> on this canvas's data context, usable from an object
    /// that has no data context of its own.
    /// </summary>
    private Binding CanvasBinding(string property) => new($"{nameof(DataContext)}.{property}") { Source = this };

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (DataContext is not ImageContentViewModel vm)
        {
            return;
        }

        double factor = e.Delta.Y > 0 ? ImageContentViewModel.WheelZoomStep : 1.0 / ImageContentViewModel.WheelZoomStep;
        vm.ZoomBy(factor);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragOrigin = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (DataContext is not ImageContentViewModel vm || _dragOrigin is not { } origin)
        {
            return;
        }

        Point current = e.GetPosition(this);
        vm.PanBy(current.X - origin.X, current.Y - origin.Y);
        _dragOrigin = current;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        e.Pointer.Capture(null);
        _dragOrigin = null;
    }
}
