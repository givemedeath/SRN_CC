using System.Buffers.Binary;
using Pfim;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SWLOR.NWN.Formats;
using SWLOR.NWN.Formats.Plt;
using SWLOR.NWN.Formats.Tga;

namespace SRN.CC.Preview;

public sealed class ImagePreviewProvider : IPreviewProvider
{
    private readonly IResourceTypeRegistry _registry;

    private static readonly string[] SupportedExtensions = ["tga", "dds", "plt"];

    public ImagePreviewProvider(IResourceTypeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public PreviewFamily Family => PreviewFamily.Image;

    public bool CanPreview(PreviewRequest request) =>
        PreviewStreamHelpers.HasAnyExtension(request, _registry, SupportedExtensions);

    public async Task<PreviewResult> GeneratePreviewAsync(
        PreviewRequest request,
        Stream payloadStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloadStream);
        cancellationToken.ThrowIfCancellationRequested();

        if (!PreviewStreamHelpers.TryGetOccurrenceExtension(request, _registry, out var extension))
        {
            return Failure("Unsupported image type.", request, ["Cannot resolve image extension."]);
        }

        var read = await PreviewStreamHelpers
            .ReadStreamBoundedAsync(payloadStream, PreviewStreamHelpers.ImageInputBudgetBytes, cancellationToken)
            .ConfigureAwait(false);

        var diagnostics = read.Diagnostics.ToList();
        if (read.IsTruncated)
        {
            diagnostics.Add("Image input was truncated to the preview budget.");
            return Failure("Image input exceeds the preview budget (128 MiB).", request, diagnostics);
        }

        if (read.Bytes.Length == 0)
        {
            return Failure("Image payload is empty.", request, diagnostics);
        }

        return extension.ToLowerInvariant() switch
        {
            "tga" => RenderTga(request, read.Bytes, diagnostics),
            "dds" => RenderDds(request, read.Bytes, diagnostics),
            "plt" => RenderPlt(request, read.Bytes, diagnostics),
            _ => Failure("Unsupported image type.", request, diagnostics),
        };
    }

    private static PreviewResult RenderTga(
        PreviewRequest request,
        byte[] bytes,
        List<string> diagnostics)
    {
        try
        {
            if (bytes.Length < 18)
            {
                return Failure("TGA header is too small.", request, diagnostics);
            }

            if (!PreviewStreamHelpers.TryReadUInt16LittleEndian(bytes, 12, out ushort widthRaw) ||
                !PreviewStreamHelpers.TryReadUInt16LittleEndian(bytes, 14, out ushort heightRaw))
            {
                return Failure("TGA header is truncated.", request, diagnostics);
            }

            if (!PreviewStreamHelpers.IsValidImageDimensions(widthRaw, heightRaw))
            {
                return Failure(
                    $"TGA dimensions {widthRaw}x{heightRaw} exceed preview safety limits.",
                    request,
                    diagnostics);
            }

            TgaImage image = TgaReader.Read(bytes);
            if (!PreviewStreamHelpers.IsValidImageDimensions(image.Width, image.Height))
            {
                return Failure(
                    $"TGA dimensions {image.Width}x{image.Height} exceed preview safety limits.",
                    request,
                    diagnostics);
            }

            byte[] bgra = ConvertRgbaToBgra(image.Pixels, image.Width, image.Height);
            string formatted = PreviewStreamHelpers.FormatDimensions(image.Width, image.Height);
            diagnostics.Add("TGA image decoded via SWLOR.NWN.Formats.TgaReader.");
            return Success(request, formatted, bgra, diagnostics, image.Width, image.Height);
        }
        catch (NwnFormatException ex)
        {
            return Failure($"TGA parse failed: {ex.Message}", request, diagnostics);
        }
        catch (Exception ex)
        {
            return Failure($"TGA preview failed: {ex.Message}", request, diagnostics);
        }
    }

    private static PreviewResult RenderDds(
        PreviewRequest request,
        byte[] bytes,
        List<string> diagnostics)
    {
        try
        {
            if (bytes.Length < 128)
            {
                return Failure("DDS payload is too small.", request, diagnostics);
            }

            if (!TryReadAscii(bytes, 0, 4, out var magic) || !string.Equals(magic, "DDS ", StringComparison.Ordinal))
            {
                return Failure("DDS signature not found.", request, diagnostics);
            }

            if (!PreviewStreamHelpers.TryReadUInt32LittleEndian(bytes, 12, out uint height) ||
                !PreviewStreamHelpers.TryReadUInt32LittleEndian(bytes, 16, out uint width))
            {
                return Failure("DDS header is truncated.", request, diagnostics);
            }

            if (!PreviewStreamHelpers.IsValidImageDimensions(width, height))
            {
                return Failure(
                    $"DDS dimensions {width}x{height} exceed preview safety limits.",
                    request,
                    diagnostics);
            }

            using IImage image = Pfimage.FromStream(new MemoryStream(bytes));

            byte[] bgra = image.Format switch
            {
                ImageFormat.Rgb24 => ConvertRgbToBgra(image.Data, image.Width, image.Height, image.Stride),
                ImageFormat.Rgba32 => ConvertRgbaToBgra(image.Data, image.Width, image.Height, image.Stride),
                ImageFormat.R5g5b5 => ConvertR5g5b5ToBgra(image.Data, image.Width, image.Height, image.Stride),
                ImageFormat.R5g6b5 => ConvertR5g6b5ToBgra(image.Data, image.Width, image.Height, image.Stride),
                ImageFormat.R5g5b5a1 => ConvertR5g5b5a1ToBgra(image.Data, image.Width, image.Height, image.Stride),
                _ => throw new NotSupportedException($"Unsupported DDS pixel format '{image.Format}'.")
            };

            if (!PreviewStreamHelpers.IsValidImageDimensions(image.Width, image.Height))
            {
                return Failure(
                    $"DDS dimensions {image.Width}x{image.Height} exceed preview safety limits.",
                    request,
                    diagnostics);
            }

            string formatted = PreviewStreamHelpers.FormatDimensions(image.Width, image.Height);
            diagnostics.Add(
                $"DDS format '{image.Format}', compressed={image.Compressed}, stride={image.Stride}.");
            return Success(request, formatted, bgra, diagnostics, image.Width, image.Height);
        }
        catch (NotSupportedException ex)
        {
            return Failure(ex.Message, request, diagnostics);
        }
        catch (NwnFormatException ex)
        {
            return Failure($"DDS parse failed: {ex.Message}", request, diagnostics);
        }
        catch (Exception ex)
        {
            return Failure($"DDS preview failed: {ex.Message}", request, diagnostics);
        }
    }

    private static PreviewResult RenderPlt(
        PreviewRequest request,
        byte[] bytes,
        List<string> diagnostics)
    {
        try
        {
            if (bytes.Length < 24)
            {
                return Failure("PLT payload is too small.", request, diagnostics);
            }

            PltFile plt = PltReader.Read(bytes);
            if (!PreviewStreamHelpers.IsValidImageDimensions(plt.Width, plt.Height))
            {
                return Failure(
                    $"PLT dimensions {plt.Width}x{plt.Height} exceed preview safety limits.",
                    request,
                    diagnostics);
            }

            long totalPixels = (long)plt.Width * plt.Height;
            if (totalPixels > int.MaxValue / 4)
            {
                return Failure("PLT image is too large to materialize.", request, diagnostics);
            }

            byte[] bgra = new byte[(int)(totalPixels * 4)];

            for (int index = 0; index < plt.Pixels.Count; index++)
            {
                PltPixel pixel = plt.Pixels[index];
                (byte r, byte g, byte b) dye = DefaultDyes[Math.Clamp(pixel.Layer, 0, DefaultDyes.Length - 1)];

                int target = checked(index * 4);
                byte intensity = pixel.Intensity;
                bgra[target] = ScaleChannel(dye.b, intensity);
                bgra[target + 1] = ScaleChannel(dye.g, intensity);
                bgra[target + 2] = ScaleChannel(dye.r, intensity);
                bgra[target + 3] = 255;
            }

            diagnostics.Add("PLT parsed using default recolor table (palette not embedded).");
            string formatted = PreviewStreamHelpers.FormatDimensions(plt.Width, plt.Height);
            return Success(request, formatted, bgra, diagnostics, plt.Width, plt.Height);
        }
        catch (NwnFormatException ex)
        {
            return Failure($"PLT parse failed: {ex.Message}", request, diagnostics);
        }
        catch (Exception ex)
        {
            return Failure($"PLT preview failed: {ex.Message}", request, diagnostics);
        }
    }

    private static PreviewResult Success(
        PreviewRequest request,
        string formatted,
        byte[] bgra,
        List<string> diagnostics,
        int width,
        int height)
    {
        if (bgra.Length > PreviewStreamHelpers.ImagePixelBudgetBytes)
        {
            diagnostics.Add(
                $"Decoded image exceeds preview safety limit ({PreviewStreamHelpers.ImagePixelBudgetBytes:N0} bytes).");
            return Failure(
                "Decoded image is larger than preview budget.",
                request,
                diagnostics);
        }

        return new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Image,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: bgra,
            FormattedContent: formatted,
            ErrorMessage: null,
            Diagnostics: diagnostics,
            Width: width,
            Height: height);
    }

    private static PreviewResult Failure(string reason, PreviewRequest request, IReadOnlyList<string> diagnostics) =>
        new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Image,
            IsSuccess: false,
            MetadataText: null,
            RawPayload: null,
            FormattedContent: null,
            ErrorMessage: reason,
            Diagnostics: diagnostics);

    private static byte[] ConvertRgbaToBgra(byte[] rgba, int width, int height)
    {
        long expected = (long)width * height * 4L;
        if (expected == 0 || rgba.Length == 0)
        {
            return Array.Empty<byte>();
        }

        if (rgba.Length < expected || expected > int.MaxValue)
        {
            throw new InvalidDataException("TGA payload has unexpected pixel layout.");
        }

        byte[] bgra = new byte[(int)expected];
        for (int destination = 0; destination < bgra.Length; destination += 4)
        {
            int source = destination;
            bgra[destination] = rgba[source + 2];
            bgra[destination + 1] = rgba[source + 1];
            bgra[destination + 2] = rgba[source];
            bgra[destination + 3] = rgba[source + 3];
        }

        return bgra;
    }

    private static byte[] ConvertRgbToBgra(byte[] rgb, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            return Array.Empty<byte>();
        }

        long target = (long)width * height * 4L;
        if (target > int.MaxValue)
        {
            throw new InvalidDataException("Image would exceed max byte count.");
        }

        byte[] bgra = new byte[(int)target];
        for (int y = 0; y < height; y++)
        {
            long sourceRow = (long)y * stride;
            int destination = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                long source = sourceRow + x * 3L;
                if (source + 2 >= rgb.LongLength)
                {
                    throw new InvalidDataException("DDS scanline data is truncated.");
                }

                bgra[destination++] = rgb[source + 2];
                bgra[destination++] = rgb[source + 1];
                bgra[destination++] = rgb[source];
                bgra[destination++] = 255;
            }
        }

        return bgra;
    }

    private static byte[] ConvertRgbaToBgra(byte[] rgba, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            return Array.Empty<byte>();
        }

        long target = (long)width * height * 4L;
        if (target > int.MaxValue)
        {
            throw new InvalidDataException("Image would exceed max byte count.");
        }

        byte[] bgra = new byte[(int)target];
        for (int y = 0; y < height; y++)
        {
            long sourceRow = (long)y * stride;
            int destination = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                long source = sourceRow + x * 4L;
                if (source + 3 >= rgba.LongLength)
                {
                    throw new InvalidDataException("DDS scanline data is truncated.");
                }

                bgra[destination++] = rgba[source + 2];
                bgra[destination++] = rgba[source + 1];
                bgra[destination++] = rgba[source];
                bgra[destination++] = rgba[source + 3];
            }
        }

        return bgra;
    }

    private static byte[] ConvertR5g6b5ToBgra(byte[] data, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            return Array.Empty<byte>();
        }

        long target = (long)width * height * 4L;
        if (target > int.MaxValue)
        {
            throw new InvalidDataException("Image would exceed max byte count.");
        }

        byte[] bgra = new byte[(int)target];
        for (int y = 0; y < height; y++)
        {
            long sourceRow = (long)y * stride;
            int destination = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int source = checked((int)sourceRow) + x * 2;
                if (source + 1 >= data.Length)
                {
                    throw new InvalidDataException("DDS scanline data is truncated.");
                }

                ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source, 2));
                int red = Scale5((packed >> 11) & 0x1F);
                int green = Scale6((packed >> 5) & 0x3F);
                int blue = Scale5(packed & 0x1F);

                bgra[destination++] = (byte)blue;
                bgra[destination++] = (byte)green;
                bgra[destination++] = (byte)red;
                bgra[destination++] = 255;
            }
        }

        return bgra;
    }

    private static byte[] ConvertR5g5b5ToBgra(byte[] data, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            return Array.Empty<byte>();
        }

        long target = (long)width * height * 4L;
        if (target > int.MaxValue)
        {
            throw new InvalidDataException("Image would exceed max byte count.");
        }

        byte[] bgra = new byte[(int)target];
        for (int y = 0; y < height; y++)
        {
            long sourceRow = (long)y * stride;
            int destination = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int source = (int)sourceRow + x * 2;
                if (source + 1 >= data.Length)
                {
                    throw new InvalidDataException("DDS scanline data is truncated.");
                }

                ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source, 2));
                int blue = Scale5((packed >> 0) & 0x1F);
                int green = Scale5((packed >> 5) & 0x1F);
                int red = Scale5((packed >> 11) & 0x1F);
                bgra[destination++] = (byte)blue;
                bgra[destination++] = (byte)green;
                bgra[destination++] = (byte)red;
                bgra[destination++] = 255;
            }
        }

        return bgra;
    }

    private static byte[] ConvertR5g5b5a1ToBgra(byte[] data, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            return Array.Empty<byte>();
        }

        long target = (long)width * height * 4L;
        if (target > int.MaxValue)
        {
            throw new InvalidDataException("Image would exceed max byte count.");
        }

        byte[] bgra = new byte[(int)target];
        for (int y = 0; y < height; y++)
        {
            long sourceRow = (long)y * stride;
            int destination = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int source = (int)sourceRow + x * 2;
                if (source + 1 >= data.Length)
                {
                    throw new InvalidDataException("DDS scanline data is truncated.");
                }

                ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source, 2));
                int blue = Scale5(packed & 0x1F);
                int green = Scale5((packed >> 5) & 0x1F);
                int red = Scale5((packed >> 10) & 0x1F);
                byte alpha = ((packed >> 15) & 1) == 1 ? (byte)255 : (byte)0;

                bgra[destination++] = (byte)blue;
                bgra[destination++] = (byte)green;
                bgra[destination++] = (byte)red;
                bgra[destination++] = alpha;
            }
        }

        return bgra;
    }

    private static bool TryReadAscii(byte[] bytes, int offset, int count, out string value)
    {
        if (offset < 0 || count < 0 || offset + count > bytes.Length)
        {
            value = string.Empty;
            return false;
        }

        value = System.Text.Encoding.ASCII.GetString(bytes, offset, count);
        return true;
    }

    private static int Scale5(int value) => (value * 255 + 15) / 31;
    private static int Scale6(int value) => (value * 255 + 31) / 63;
    private static byte ScaleChannel(byte baseChannel, byte intensity) => (byte)((baseChannel * intensity) / 255);

    private static readonly (byte R, byte G, byte B)[] DefaultDyes =
    [
        (224, 191, 170), // Skin
        (112, 79, 60), // Hair
        (156, 166, 184), // Metal1
        (108, 114, 127), // Metal2
        (183, 130, 84), // Cloth1
        (141, 103, 78), // Cloth2
        (130, 95, 74), // Leather1
        (92, 67, 52), // Leather2
        (74, 74, 74), // Tattoo1
        (37, 37, 37) // Tattoo2
    ];
}
