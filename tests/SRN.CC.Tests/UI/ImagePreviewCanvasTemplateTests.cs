using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.App.Views.PreviewTemplates;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;

namespace SRN.CC.Tests.UI;

/// <summary>
/// <see cref="ImagePreviewCanvas"/> must be constructible, because a throw here is invisible in the
/// worst way: it happens while the <c>DataTemplate</c> is being built, so the slot shows no image,
/// no error text and writes no log line — every image preview in the application renders blank.
/// </summary>
/// <remarks>
/// The regression this guards: <see cref="Avalonia.Media.ScaleTransform"/> and
/// <see cref="Avalonia.Media.TranslateTransform"/> are bare <c>AvaloniaObject</c>s outside the
/// visual tree, so they have no data context. Binding zoom and pan straight onto them threw
/// "Cannot find a DataContext to bind to" from the constructor. The bindings now resolve through
/// the canvas's own data context instead.
/// </remarks>
[TestFixture]
public class ImagePreviewCanvasTemplateTests
{
    [AvaloniaTest]
    public void Constructing_DoesNotThrow()
    {
        Action construct = () => _ = new ImagePreviewCanvas();

        construct.Should().NotThrow(
            "a throw here happens during template construction, which blanks the slot with no "
            + "error text and no log record");
    }

    [AvaloniaTest]
    public void AnImageContentViewModel_ReachesAnImageControlWithItsBitmap()
    {
        ImageContentViewModel content = OpaqueRedContent(width: 4, height: 4);
        content.Bitmap.Should().NotBeNull("the fixture must exercise a real decoded image");

        ImagePreviewCanvas canvas = new() { DataContext = content };
        Window window = new() { Content = canvas, Width = 200, Height = 200 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Image image = canvas.GetVisualDescendants().OfType<Image>().Single();
        image.Source.Should().BeOfType<WriteableBitmap>();
        ((WriteableBitmap)image.Source!).PixelSize.Width.Should().Be(4);
        ((WriteableBitmap)image.Source!).PixelSize.Height.Should().Be(4);
    }

    [AvaloniaTest]
    public void ZoomAndPanChanges_FlowThroughToTheRenderTransform()
    {
        ImageContentViewModel content = OpaqueRedContent(width: 4, height: 4);
        ImagePreviewCanvas canvas = new() { DataContext = content };
        Window window = new() { Content = canvas, Width = 200, Height = 200 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        content.ZoomBy(2.0);
        content.PanBy(15, -7);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Image image = canvas.GetVisualDescendants().OfType<Image>().Single();
        var group = (Avalonia.Media.TransformGroup)image.RenderTransform!;
        var scale = (Avalonia.Media.ScaleTransform)group.Children[0];
        var translate = (Avalonia.Media.TranslateTransform)group.Children[1];

        // Routing the bindings through the canvas's data context must not quietly disconnect them.
        scale.ScaleX.Should().Be(content.ZoomScale);
        scale.ScaleY.Should().Be(content.ZoomScale);
        translate.X.Should().Be(content.PanX);
        translate.Y.Should().Be(content.PanY);
    }

    [AvaloniaTest]
    public void ReplacingTheDataContext_RebindsToTheNewViewModel()
    {
        ImagePreviewCanvas canvas = new() { DataContext = OpaqueRedContent(4, 4) };
        Window window = new() { Content = canvas, Width = 200, Height = 200 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // A slot showing one asset and then another reuses the same canvas.
        ImageContentViewModel second = OpaqueRedContent(width: 8, height: 8);
        second.ZoomBy(3.0);
        canvas.DataContext = second;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Image image = canvas.GetVisualDescendants().OfType<Image>().Single();
        ((WriteableBitmap)image.Source!).PixelSize.Width.Should().Be(8);

        var group = (Avalonia.Media.TransformGroup)image.RenderTransform!;
        ((Avalonia.Media.ScaleTransform)group.Children[0]).ScaleX.Should().Be(second.ZoomScale);
    }

    private static ImageContentViewModel OpaqueRedContent(int width, int height)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 2] = 255;
            bgra[i + 3] = 255;
        }

        return new ImageContentViewModel(new PreviewResult(
            Occurrence: new AssetOccurrence(
                new AssetIdentity("probe", 3), Guid.NewGuid(), new HakEntryLocator(0), "probe.tga", bgra.Length),
            Family: PreviewFamily.Image,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: bgra,
            FormattedContent: $"{width}:{height}",
            ErrorMessage: null,
            Diagnostics: Array.Empty<string>()));
    }
}
