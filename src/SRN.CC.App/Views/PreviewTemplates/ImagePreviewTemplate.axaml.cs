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
        _image.Bind(Image.SourceProperty, new Binding(nameof(ImageContentViewModel.Bitmap)));
        _scaleTransform.Bind(ScaleTransform.ScaleXProperty, new Binding(nameof(ImageContentViewModel.ZoomScale)));
        _scaleTransform.Bind(ScaleTransform.ScaleYProperty, new Binding(nameof(ImageContentViewModel.ZoomScale)));
        _translateTransform.Bind(TranslateTransform.XProperty, new Binding(nameof(ImageContentViewModel.PanX)));
        _translateTransform.Bind(TranslateTransform.YProperty, new Binding(nameof(ImageContentViewModel.PanY)));

        Child = _image;
    }

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
