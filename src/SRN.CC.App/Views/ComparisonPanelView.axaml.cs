using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SRN.CC.App.ViewModels;

namespace SRN.CC.App.Views;

public partial class ComparisonPanelView : UserControl
{
    private readonly Dictionary<IPointer, (Point Start, double PanX, double PanY)> _drags = new();

    public ComparisonPanelView()
    {
        InitializeComponent();
    }

    private void ImageSurface_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not PreviewSlotViewModel slot)
        {
            return;
        }

        double factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        slot.SetZoomPan(slot.ZoomScale * factor, slot.PanX, slot.PanY);
        e.Handled = true;
    }

    private void ImageSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not PreviewSlotViewModel slot)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _drags[e.Pointer] = (point.Position, slot.PanX, slot.PanY);
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void ImageSurface_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not PreviewSlotViewModel slot)
        {
            return;
        }

        if (!_drags.TryGetValue(e.Pointer, out var drag))
        {
            return;
        }

        var current = e.GetCurrentPoint(control).Position;
        double deltaX = current.X - drag.Start.X;
        double deltaY = current.Y - drag.Start.Y;
        slot.SetZoomPan(slot.ZoomScale, drag.PanX + deltaX, drag.PanY + deltaY);
        e.Handled = true;
    }

    private void ImageSurface_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drags.Remove(e.Pointer))
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}
