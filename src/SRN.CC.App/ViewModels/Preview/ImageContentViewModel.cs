using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Preview;

namespace SRN.CC.App.ViewModels.Preview;

/// <summary>
/// Image family content: decodes the straight top-left-origin BGRA8 pixel buffer produced by
/// <c>ImagePreviewProvider</c>/<c>TextureDecoder</c> (carried on <see cref="PreviewResult.RawPayload"/>,
/// with dimensions encoded as <c>"{width}:{height}"</c> in <see cref="PreviewResult.FormattedContent"/>
/// via <c>PreviewStreamHelpers.FormatDimensions</c>) into a displayable <see cref="WriteableBitmap"/>,
/// and exposes zoom/pan state for a pannable/zoomable image view.
///
/// This is milestone-5 UI debt (per <c>docs/MILESTONE-5.md</c>'s "Image Canvas with render
/// transforms" / <c>ZoomScale</c>/<c>PanX</c>/<c>PanY</c> spec) finally implemented under
/// architecture decision A3: this VM owns only its own content and is reached via a
/// <c>PreviewContentFactory</c> arm keyed on <see cref="PreviewFamily.Image"/> (there is no
/// dedicated <see cref="IPreviewPayload"/> type for images).
/// </summary>
public sealed partial class ImageContentViewModel : PreviewContentViewModel
{
    /// <summary>Lower clamp for <see cref="ZoomScale"/> - never zero or negative.</summary>
    public const double MinZoomScale = 0.1;

    /// <summary>Upper clamp for <see cref="ZoomScale"/>.</summary>
    public const double MaxZoomScale = 8.0;

    /// <summary>Multiplicative step used by <see cref="ZoomInCommand"/>/<see cref="ZoomOutCommand"/>.</summary>
    public const double ZoomStepFactor = 1.25;

    /// <summary>Multiplicative step a pointer-wheel handler should apply per notch.</summary>
    public const double WheelZoomStep = 1.1;

    private bool _disposed;

    public override PreviewFamily Family => PreviewFamily.Image;

    /// <summary>
    /// The decoded image, ready to bind to <c>&lt;Image Source="{Binding Bitmap}"/&gt;</c>. Null
    /// when the payload was missing, malformed, or the dimensions in
    /// <see cref="PreviewResult.FormattedContent"/> could not be parsed - the template degrades to
    /// showing nothing rather than throwing.
    /// </summary>
    public WriteableBitmap? Bitmap { get; }

    [ObservableProperty]
    private double _zoomScale = 1.0;

    [ObservableProperty]
    private double _panX;

    [ObservableProperty]
    private double _panY;

    public ImageContentViewModel(PreviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Bitmap = TryParseDimensions(result.FormattedContent, out int width, out int height)
            ? TryCreateBitmap(result.RawPayload, width, height)
            : null;
    }

    [RelayCommand]
    private void ZoomIn() => ZoomScale = Math.Clamp(ZoomScale * ZoomStepFactor, MinZoomScale, MaxZoomScale);

    [RelayCommand]
    private void ZoomOut() => ZoomScale = Math.Clamp(ZoomScale / ZoomStepFactor, MinZoomScale, MaxZoomScale);

    [RelayCommand]
    private void ResetView()
    {
        ZoomScale = 1.0;
        PanX = 0;
        PanY = 0;
    }

    /// <summary>Applies a continuous multiplicative zoom factor (e.g. from a pointer-wheel delta), clamped to sane bounds.</summary>
    public void ZoomBy(double factor)
    {
        if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor))
        {
            return;
        }

        ZoomScale = Math.Clamp(ZoomScale * factor, MinZoomScale, MaxZoomScale);
    }

    /// <summary>Applies a continuous pan delta (e.g. from a pointer-drag), typically in view pixels.</summary>
    public void PanBy(double deltaX, double deltaY)
    {
        PanX += deltaX;
        PanY += deltaY;
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Bitmap?.Dispose();
    }

    private static bool TryParseDimensions(string? formattedContent, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (string.IsNullOrEmpty(formattedContent))
        {
            return false;
        }

        string[] parts = formattedContent.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedWidth) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedHeight))
        {
            return false;
        }

        if (parsedWidth <= 0 || parsedHeight <= 0)
        {
            return false;
        }

        width = parsedWidth;
        height = parsedHeight;
        return true;
    }

    private static WriteableBitmap? TryCreateBitmap(byte[]? bgra, int width, int height)
    {
        if (bgra == null || width <= 0 || height <= 0)
        {
            return null;
        }

        long expectedBytes = (long)width * height * 4L;
        if (expectedBytes <= 0 || expectedBytes > int.MaxValue || bgra.LongLength < expectedBytes)
        {
            return null;
        }

        try
        {
            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            using (ILockedFramebuffer frame = bitmap.Lock())
            {
                int sourceStride = width * 4;
                if (frame.RowBytes == sourceStride)
                {
                    Marshal.Copy(bgra, 0, frame.Address, (int)expectedBytes);
                }
                else
                {
                    // The framebuffer stride may include alignment padding the source buffer
                    // (which is packed with no padding, per TextureDecoder) does not have.
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(bgra, y * sourceStride, frame.Address + y * frame.RowBytes, sourceStride);
                    }
                }
            }

            return bitmap;
        }
        catch (Exception)
        {
            // Never throw out of content construction - an undecodable/oversized buffer should
            // degrade to "no bitmap" rather than take down the slot.
            return null;
        }
    }
}
