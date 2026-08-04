using System.Buffers.Binary;
using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.Tests.Preview;

[TestFixture]
public class ImagePreviewProviderTests
{
    private ImagePreviewProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _provider = new ImagePreviewProvider(new StubRegistry("tga"));
    }

    #region TGA Tests

    [Test]
    public async Task GeneratePreviewAsync_Valid32BitTga_ReturnsSuccessWithDimensionsAndExactPixels()
    {
        // Arrange: 2x2 32-bit uncompressed TGA, top-left origin so file order == row-major order.
        // TGA stores pixels as B,G,R,A already, and the provider round-trips RGBA -> BGRA,
        // so the resulting RawPayload should equal the original per-pixel bytes exactly.
        byte[] pixelBytes =
        [
            10, 20, 30, 255, // pixel 0: B,G,R,A
            40, 50, 60, 200, // pixel 1
            70, 80, 90, 150, // pixel 2
            100, 110, 120, 50 // pixel 3
        ];
        byte[] tga = BuildTga(width: 2, height: 2, pixelDepth: 32, pixelData: pixelBytes);
        var provider = new ImagePreviewProvider(new StubRegistry("tga"));
        var request = CreateRequest("image.tga");

        using var stream = new MemoryStream(tga);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Family, Is.EqualTo(PreviewFamily.Image));
        Assert.That(result.FormattedContent, Is.EqualTo("2:2"));
        Assert.That(result.RawPayload, Is.Not.Null);
        Assert.That(result.RawPayload!.Length, Is.EqualTo(2 * 2 * 4));
        Assert.That(result.RawPayload, Is.EqualTo(pixelBytes));
    }

    [Test]
    public async Task GeneratePreviewAsync_Valid24BitTga_ReturnsSuccessWithOpaqueAlpha()
    {
        // 24-bit TGA has no alpha channel in the source; the provider must synthesize A=255.
        byte[] pixelBytes =
        [
            1, 2, 3, // pixel 0: B,G,R
            4, 5, 6 // pixel 1: B,G,R
        ];
        byte[] tga = BuildTga(width: 2, height: 1, pixelDepth: 24, pixelData: pixelBytes);
        var provider = new ImagePreviewProvider(new StubRegistry("tga"));
        var request = CreateRequest("image.tga");

        using var stream = new MemoryStream(tga);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.FormattedContent, Is.EqualTo("2:1"));
        Assert.That(result.RawPayload, Is.Not.Null);
        byte[] expected = [1, 2, 3, 255, 4, 5, 6, 255];
        Assert.That(result.RawPayload, Is.EqualTo(expected));
    }

    [Test]
    public async Task GeneratePreviewAsync_TgaHeaderTooSmall_ReturnsFailure()
    {
        byte[] garbage = new byte[10];
        var provider = new ImagePreviewProvider(new StubRegistry("tga"));
        var request = CreateRequest("broken.tga");

        using var stream = new MemoryStream(garbage);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("TGA header"));
    }

    [Test]
    public async Task GeneratePreviewAsync_TgaDimensionsExceedSafetyLimit_ReturnsFailure()
    {
        // Header-only payload declaring dimensions above PreviewStreamHelpers.MaxImageDimension (4096).
        // The provider rejects based on header dimensions before attempting to decode pixel data.
        byte[] header = new byte[18];
        header[2] = 2; // uncompressed true-color image type
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 5000);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 5000);
        header[16] = 32;
        header[17] = 0x20;

        var provider = new ImagePreviewProvider(new StubRegistry("tga"));
        var request = CreateRequest("huge.tga");

        using var stream = new MemoryStream(header);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("exceed preview safety limits"));
    }

    #endregion

    #region DDS Tests

    [Test]
    public async Task GeneratePreviewAsync_DdsPayloadTooSmall_ReturnsFailure()
    {
        byte[] garbage = new byte[50];
        var provider = new ImagePreviewProvider(new StubRegistry("dds"));
        var request = CreateRequest("broken.dds");

        using var stream = new MemoryStream(garbage);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("too small"));
    }

    [Test]
    public async Task GeneratePreviewAsync_DdsBadMagic_ReturnsFailure()
    {
        byte[] bytes = new byte[128];
        "BAD!"u8.CopyTo(bytes);
        var provider = new ImagePreviewProvider(new StubRegistry("dds"));
        var request = CreateRequest("broken.dds");

        using var stream = new MemoryStream(bytes);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("signature not found"));
    }

    [Test]
    public async Task GeneratePreviewAsync_DdsDimensionsExceedSafetyLimit_ReturnsFailure()
    {
        byte[] header = new byte[128];
        "DDS "u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 5000); // height
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 5000); // width

        var provider = new ImagePreviewProvider(new StubRegistry("dds"));
        var request = CreateRequest("huge.dds");

        using var stream = new MemoryStream(header);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("exceed preview safety limits"));
    }

    [Test]
    public async Task GeneratePreviewAsync_ValidUncompressedRgb24Dds_ReturnsSuccess()
    {
        int width = 2;
        int height = 2;
        byte[] pixelData =
        [
            10, 20, 30, // pixel 0: B,G,R
            40, 50, 60, // pixel 1
            70, 80, 90, // pixel 2
            100, 110, 120 // pixel 3
        ];
        byte[] dds = BuildUncompressedDds(width, height, bitCount: 24, hasAlpha: false, pixelData: pixelData);

        var provider = new ImagePreviewProvider(new StubRegistry("dds"));
        var request = CreateRequest("image.dds");

        using var stream = new MemoryStream(dds);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.FormattedContent, Is.EqualTo($"{width}:{height}"));
        Assert.That(result.RawPayload, Is.Not.Null);
        Assert.That(result.RawPayload!.Length, Is.EqualTo(width * height * 4));
    }

    #endregion

    #region PLT Tests

    [Test]
    public async Task GeneratePreviewAsync_ValidPlt_ReturnsSuccessWithDefaultRecolorDiagnostic()
    {
        // PLT format: 4-byte "PLT " signature, 4-byte "V1  " version, 8 reserved bytes,
        // width (uint32 LE) at offset 16, height (uint32 LE) at offset 20, then per-pixel
        // Intensity+Layer byte pairs (see PltReader.Read).
        byte[] plt = BuildPlt(width: 2, height: 1, pixels:
        [
            (intensity: 128, layer: 0), // Skin
            (intensity: 255, layer: 1) // Hair
        ]);

        var provider = new ImagePreviewProvider(new StubRegistry("plt"));
        var request = CreateRequest("image.plt");

        using var stream = new MemoryStream(plt);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.FormattedContent, Is.EqualTo("2:1"));
        Assert.That(result.RawPayload, Is.Not.Null);
        Assert.That(result.RawPayload!.Length, Is.EqualTo(2 * 1 * 4));
        Assert.That(result.Diagnostics, Has.Some.Contains("default recolor table"));
    }

    [Test]
    public async Task GeneratePreviewAsync_PltPayloadTooSmall_ReturnsFailure()
    {
        byte[] garbage = new byte[10];
        var provider = new ImagePreviewProvider(new StubRegistry("plt"));
        var request = CreateRequest("broken.plt");

        using var stream = new MemoryStream(garbage);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("too small"));
    }

    [Test]
    public async Task GeneratePreviewAsync_PltBadSignature_ReturnsFailure()
    {
        byte[] plt = BuildPlt(width: 1, height: 1, pixels: [(intensity: 1, layer: 0)]);
        plt[0] = (byte)'X'; // corrupt "PLT " signature

        var provider = new ImagePreviewProvider(new StubRegistry("plt"));
        var request = CreateRequest("broken.plt");

        using var stream = new MemoryStream(plt);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("PLT parse failed"));
    }

    #endregion

    #region Unsupported / Garbage Input Tests

    [Test]
    public async Task GeneratePreviewAsync_UnresolvableExtension_ReturnsFailure()
    {
        var provider = new ImagePreviewProvider(new UnresolvedRegistry());
        var request = CreateRequest("no_extension_at_all");

        using var stream = new MemoryStream([1, 2, 3, 4]);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Unsupported image type"));
    }

    [Test]
    public async Task GeneratePreviewAsync_EmptyPayload_ReturnsFailure()
    {
        var provider = new ImagePreviewProvider(new StubRegistry("tga"));
        var request = CreateRequest("empty.tga");

        using var stream = new MemoryStream(Array.Empty<byte>());
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("empty"));
    }

    [Test]
    public void CanPreview_UnsupportedExtension_ReturnsFalse()
    {
        var provider = new ImagePreviewProvider(new StubRegistry("txt"));
        var request = CreateRequest("document.txt");

        Assert.That(provider.CanPreview(request), Is.False);
    }

    [Test]
    public void CanPreview_TgaExtension_ReturnsTrue()
    {
        var provider = new ImagePreviewProvider(new StubRegistry("tga"));
        var request = CreateRequest("image.tga");

        Assert.That(provider.CanPreview(request), Is.True);
    }

    #endregion

    #region Fixture Builders

    private static PreviewRequest CreateRequest(string originalName)
    {
        var identity = new AssetIdentity("image_resref", 3000);
        var source = AssetSource.CreateFolder("c:/test/folder", 0);
        var occurrence = new AssetOccurrence(
            identity: identity,
            sourceId: source.Id,
            locator: new FolderFileLocator(originalName),
            originalName: originalName,
            size: 0,
            validationState: ValidationState.Valid,
            extensionMetadata: null,
            sha256: new byte[32]);

        return new PreviewRequest(occurrence, source, PreviewFamily.Image);
    }

    private static byte[] BuildTga(int width, int height, int pixelDepth, byte[] pixelData)
    {
        byte[] header = new byte[18];
        header[0] = 0; // image ID length
        header[1] = 0; // color map type
        header[2] = 2; // uncompressed true-color
        // colorMapFirst/Length/Depth left at 0
        // x/y origin left at 0
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)height);
        header[16] = (byte)pixelDepth;
        header[17] = 0x20; // top-left origin, no attribute bits declared

        byte[] result = new byte[header.Length + pixelData.Length];
        header.CopyTo(result, 0);
        pixelData.CopyTo(result, header.Length);
        return result;
    }

    private static byte[] BuildUncompressedDds(int width, int height, int bitCount, bool hasAlpha, byte[] pixelData)
    {
        byte[] header = new byte[128];
        "DDS "u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 124); // dwSize
        const uint ddsdCaps = 0x1;
        const uint ddsdHeight = 0x2;
        const uint ddsdWidth = 0x4;
        const uint ddsdPitch = 0x8;
        const uint ddsdPixelFormat = 0x1000;
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(8),
            ddsdCaps | ddsdHeight | ddsdWidth | ddsdPitch | ddsdPixelFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)width);
        int bytesPerPixel = bitCount / 8;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)(width * bytesPerPixel)); // pitch

        // DDS_PIXELFORMAT at offset 76 (32 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), 32); // dwSize
        const uint ddpfAlphaPixels = 0x1;
        const uint ddpfRgb = 0x40;
        uint pfFlags = ddpfRgb | (hasAlpha ? ddpfAlphaPixels : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), pfFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84), 0); // dwFourCC
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88), (uint)bitCount); // dwRGBBitCount
        if (hasAlpha)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(92), 0x00FF0000); // R
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(96), 0x0000FF00); // G
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(100), 0x000000FF); // B
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(104), 0xFF000000); // A
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(92), 0x00FF0000); // R
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(96), 0x0000FF00); // G
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(100), 0x000000FF); // B
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(104), 0); // A
        }

        const uint ddscapsTexture = 0x1000;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(108), ddscapsTexture);

        byte[] result = new byte[header.Length + pixelData.Length];
        header.CopyTo(result, 0);
        pixelData.CopyTo(result, header.Length);
        return result;
    }

    private static byte[] BuildPlt(int width, int height, (byte intensity, byte layer)[] pixels)
    {
        const int headerSize = 24;
        byte[] result = new byte[headerSize + pixels.Length * 2];
        "PLT "u8.CopyTo(result);
        "V1  "u8.CopyTo(result.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), (uint)height);

        for (int index = 0; index < pixels.Length; index++)
        {
            result[headerSize + index * 2] = pixels[index].intensity;
            result[headerSize + index * 2 + 1] = pixels[index].layer;
        }

        return result;
    }

    #endregion

    private sealed class StubRegistry(string extension) : IResourceTypeRegistry
    {
        public bool TryGetExtension(ushort typeId, out string extension_) { extension_ = extension; return true; }
        public bool TryGetType(string extension_, out ushort typeId) { typeId = 3000; return true; }
    }

    private sealed class UnresolvedRegistry : IResourceTypeRegistry
    {
        public bool TryGetExtension(ushort typeId, out string extension) { extension = string.Empty; return false; }
        public bool TryGetType(string extension, out ushort typeId) { typeId = 0; return false; }
    }
}
