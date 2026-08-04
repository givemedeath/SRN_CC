using Avalonia;
using Avalonia.Headless.NUnit;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;

namespace SRN.CC.Tests.App;

/// <summary>
/// Covers slice S12 (milestone-5 UI debt: image content zoom/pan). Bitmap-construction assertions
/// need the headless Avalonia platform registered (<see cref="Avalonia.Media.Imaging.WriteableBitmap"/>
/// requires <c>IPlatformRenderInterface</c>), so those use <see cref="AvaloniaTestAttribute"/>; the
/// zoom/pan/dispose-safety assertions construct the view model with a null/malformed payload so no
/// real bitmap is ever created, and use plain <see cref="TestAttribute"/>.
/// </summary>
[TestFixture]
public class ImageContentViewModelTests
{
    [AvaloniaTest]
    public void Constructor_ValidBgraPayload_ProducesBitmapWithExpectedPixelSize()
    {
        // 2x2 straight BGRA, 4 bytes/pixel, packed rows (matches TextureDecoder's DecodedTexture shape).
        byte[] bgra = new byte[2 * 2 * 4];
        var result = CreateResult(bgra, "2:2");

        using var vm = new ImageContentViewModel(result);

        vm.Bitmap.Should().NotBeNull();
        vm.Bitmap!.PixelSize.Should().Be(new PixelSize(2, 2));
    }

    [AvaloniaTest]
    public void Dispose_CalledMultipleTimesAfterRealBitmap_DoesNotThrow()
    {
        byte[] bgra = new byte[2 * 2 * 4];
        var result = CreateResult(bgra, "2:2");
        var vm = new ImageContentViewModel(result);
        vm.Bitmap.Should().NotBeNull();

        Action act = () =>
        {
            vm.Dispose();
            vm.Dispose();
            vm.Dispose();
        };

        act.Should().NotThrow();
    }

    [Test]
    public void Constructor_MissingFormattedContent_LeavesBitmapNull()
    {
        var result = CreateResult(new byte[16], formattedContent: null);

        using var vm = new ImageContentViewModel(result);

        vm.Bitmap.Should().BeNull();
    }

    [Test]
    public void Constructor_MalformedFormattedContent_LeavesBitmapNull()
    {
        var result = CreateResult(new byte[16], "not-a-dimension-string");

        using var vm = new ImageContentViewModel(result);

        vm.Bitmap.Should().BeNull();
    }

    [Test]
    public void Constructor_NullRawPayload_LeavesBitmapNull()
    {
        var result = CreateResult(null, "4:4");

        using var vm = new ImageContentViewModel(result);

        vm.Bitmap.Should().BeNull();
    }

    [Test]
    public void Constructor_PayloadShorterThanDeclaredDimensions_LeavesBitmapNull()
    {
        // Declares 4x4 (64 bytes needed) but only supplies 8 bytes.
        var result = CreateResult(new byte[8], "4:4");

        using var vm = new ImageContentViewModel(result);

        vm.Bitmap.Should().BeNull();
    }

    [Test]
    public void Constructor_Default_ZoomScaleIsOneAndPanIsZero()
    {
        using var vm = new ImageContentViewModel(CreateResult(null, null));

        vm.ZoomScale.Should().Be(1.0);
        vm.PanX.Should().Be(0);
        vm.PanY.Should().Be(0);
    }

    [Test]
    public void ZoomInCommand_RepeatedInvocation_NeverExceedsMaxZoom()
    {
        using var vm = new ImageContentViewModel(CreateResult(null, null));

        for (int i = 0; i < 100; i++)
        {
            vm.ZoomInCommand.Execute(null);
        }

        vm.ZoomScale.Should().BeLessThanOrEqualTo(ImageContentViewModel.MaxZoomScale);
        vm.ZoomScale.Should().BePositive();
    }

    [Test]
    public void ZoomOutCommand_RepeatedInvocation_NeverReachesZeroOrNegative()
    {
        using var vm = new ImageContentViewModel(CreateResult(null, null));

        for (int i = 0; i < 100; i++)
        {
            vm.ZoomOutCommand.Execute(null);
        }

        vm.ZoomScale.Should().BeGreaterThanOrEqualTo(ImageContentViewModel.MinZoomScale);
        vm.ZoomScale.Should().BePositive();
    }

    [Test]
    public void ZoomBy_NonPositiveOrNonFiniteFactor_IsIgnored()
    {
        using var vm = new ImageContentViewModel(CreateResult(null, null));
        double before = vm.ZoomScale;

        vm.ZoomBy(0);
        vm.ZoomBy(-2);
        vm.ZoomBy(double.NaN);
        vm.ZoomBy(double.PositiveInfinity);

        vm.ZoomScale.Should().Be(before);
    }

    [Test]
    public void PanBy_AccumulatesDeltasAcrossCalls()
    {
        using var vm = new ImageContentViewModel(CreateResult(null, null));

        vm.PanBy(10, -5);
        vm.PanBy(3, 2);

        vm.PanX.Should().Be(13);
        vm.PanY.Should().Be(-3);
    }

    [Test]
    public void ResetViewCommand_AfterZoomAndPan_RestoresDefaults()
    {
        using var vm = new ImageContentViewModel(CreateResult(null, null));
        vm.ZoomInCommand.Execute(null);
        vm.PanBy(50, 50);

        vm.ResetViewCommand.Execute(null);

        vm.ZoomScale.Should().Be(1.0);
        vm.PanX.Should().Be(0);
        vm.PanY.Should().Be(0);
    }

    [Test]
    public void Dispose_WithoutBitmap_CanBeCalledMultipleTimesSafely()
    {
        var vm = new ImageContentViewModel(CreateResult(null, null));

        Action act = () =>
        {
            vm.Dispose();
            vm.Dispose();
        };

        act.Should().NotThrow();
    }

    [Test]
    public void Family_IsAlwaysImage()
    {
        using var vm = new ImageContentViewModel(CreateResult(null, null));

        vm.Family.Should().Be(PreviewFamily.Image);
    }

    private static PreviewResult CreateResult(byte[]? bgra, string? formattedContent) => new(
        Occurrence: CreateOccurrence(),
        Family: PreviewFamily.Image,
        IsSuccess: true,
        MetadataText: null,
        RawPayload: bgra,
        FormattedContent: formattedContent,
        ErrorMessage: null,
        Diagnostics: Array.Empty<string>());

    private static AssetOccurrence CreateOccurrence() => new(
        identity: new AssetIdentity("test_image", 3001),
        sourceId: Guid.NewGuid(),
        locator: new HakEntryLocator(1),
        originalName: "test_image.tga",
        size: 16,
        validationState: ValidationState.Valid,
        extensionMetadata: null,
        sha256: null);
}
