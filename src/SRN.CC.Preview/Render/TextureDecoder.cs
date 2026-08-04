using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Pfim;
using SRN.CC.Preview;
using SWLOR.NWN.Formats;
using SWLOR.NWN.Formats.Plt;
using SWLOR.NWN.Formats.Tga;

namespace SRN.CC.Preview.Render;

/// <summary>
/// Decodes TGA, DDS (via Pfim), and PLT (via a default recolor table) image payloads into
/// straight BGRA byte buffers. Extracted from <see cref="ImagePreviewProvider"/> so the same
/// decode paths can be reused by the model-preview texture pipeline (see architecture decision A7).
/// Never throws on malformed or oversized input: failures are reported via the <c>error</c> out
/// parameter, matching the safety budgets defined in <c>PreviewStreamHelpers</c>.
/// </summary>
public static class TextureDecoder
{
    public static bool TryDecodeToBgra(
        ReadOnlySpan<byte> bytes,
        string extension,
        [NotNullWhen(true)] out DecodedTexture? texture,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(extension);

        return extension.ToLowerInvariant() switch
        {
            "tga" => TryDecodeTga(bytes, out texture, out error),
            "dds" => TryDecodeDds(bytes, out texture, out error),
            "plt" => TryDecodePlt(bytes, out texture, out error),
            _ => Fail("Unsupported image type.", out texture, out error),
        };
    }

    private static bool TryDecodeTga(
        ReadOnlySpan<byte> bytes,
        [NotNullWhen(true)] out DecodedTexture? texture,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            if (bytes.Length < 18)
            {
                return Fail("TGA header is too small.", out texture, out error);
            }

            if (!PreviewStreamHelpers.TryReadUInt16LittleEndian(bytes, 12, out ushort widthRaw) ||
                !PreviewStreamHelpers.TryReadUInt16LittleEndian(bytes, 14, out ushort heightRaw))
            {
                return Fail("TGA header is truncated.", out texture, out error);
            }

            if (!PreviewStreamHelpers.IsValidImageDimensions(widthRaw, heightRaw))
            {
                return Fail(
                    $"TGA dimensions {widthRaw}x{heightRaw} exceed preview safety limits.",
                    out texture,
                    out error);
            }

            byte pixelDepth = bytes[16];

            TgaImage image = TgaReader.Read(bytes.ToArray());
            if (!PreviewStreamHelpers.IsValidImageDimensions(image.Width, image.Height))
            {
                return Fail(
                    $"TGA dimensions {image.Width}x{image.Height} exceed preview safety limits.",
                    out texture,
                    out error);
            }

            byte[] bgra = ConvertRgbaToBgra(image.Pixels, image.Width, image.Height);
            texture = new DecodedTexture(
                image.Width,
                image.Height,
                bgra,
                HasAlpha: pixelDepth == 32,
                DecodeDiagnostic: "TGA image decoded via SWLOR.NWN.Formats.TgaReader.");
            error = null;
            return true;
        }
        catch (NwnFormatException ex)
        {
            return Fail($"TGA parse failed: {ex.Message}", out texture, out error);
        }
        catch (Exception ex)
        {
            return Fail($"TGA preview failed: {ex.Message}", out texture, out error);
        }
    }

    private static bool TryDecodeDds(
        ReadOnlySpan<byte> bytes,
        [NotNullWhen(true)] out DecodedTexture? texture,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            if (bytes.Length < 128)
            {
                return Fail("DDS payload is too small.", out texture, out error);
            }

            if (!TryReadAscii(bytes, 0, 4, out var magic) || !string.Equals(magic, "DDS ", StringComparison.Ordinal))
            {
                return Fail("DDS signature not found.", out texture, out error);
            }

            if (!PreviewStreamHelpers.TryReadUInt32LittleEndian(bytes, 12, out uint height) ||
                !PreviewStreamHelpers.TryReadUInt32LittleEndian(bytes, 16, out uint width))
            {
                return Fail("DDS header is truncated.", out texture, out error);
            }

            if (!PreviewStreamHelpers.IsValidImageDimensions(width, height))
            {
                return Fail(
                    $"DDS dimensions {width}x{height} exceed preview safety limits.",
                    out texture,
                    out error);
            }

            using IImage image = Pfimage.FromStream(new MemoryStream(bytes.ToArray()));

            bool hasAlpha = image.Format is ImageFormat.Rgba32 or ImageFormat.R5g5b5a1;
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
                return Fail(
                    $"DDS dimensions {image.Width}x{image.Height} exceed preview safety limits.",
                    out texture,
                    out error);
            }

            texture = new DecodedTexture(
                image.Width,
                image.Height,
                bgra,
                hasAlpha,
                DecodeDiagnostic: $"DDS format '{image.Format}', compressed={image.Compressed}, stride={image.Stride}.");
            error = null;
            return true;
        }
        catch (NotSupportedException ex)
        {
            return Fail(ex.Message, out texture, out error);
        }
        catch (NwnFormatException ex)
        {
            return Fail($"DDS parse failed: {ex.Message}", out texture, out error);
        }
        catch (Exception ex)
        {
            return Fail($"DDS preview failed: {ex.Message}", out texture, out error);
        }
    }

    private static bool TryDecodePlt(
        ReadOnlySpan<byte> bytes,
        [NotNullWhen(true)] out DecodedTexture? texture,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            if (bytes.Length < 24)
            {
                return Fail("PLT payload is too small.", out texture, out error);
            }

            PltFile plt = PltReader.Read(bytes.ToArray());
            if (!PreviewStreamHelpers.IsValidImageDimensions(plt.Width, plt.Height))
            {
                return Fail(
                    $"PLT dimensions {plt.Width}x{plt.Height} exceed preview safety limits.",
                    out texture,
                    out error);
            }

            long totalPixels = (long)plt.Width * plt.Height;
            if (totalPixels > int.MaxValue / 4)
            {
                return Fail("PLT image is too large to materialize.", out texture, out error);
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

            texture = new DecodedTexture(
                plt.Width,
                plt.Height,
                bgra,
                HasAlpha: false,
                DecodeDiagnostic: "PLT parsed using default recolor table (palette not embedded).");
            error = null;
            return true;
        }
        catch (NwnFormatException ex)
        {
            return Fail($"PLT parse failed: {ex.Message}", out texture, out error);
        }
        catch (Exception ex)
        {
            return Fail($"PLT preview failed: {ex.Message}", out texture, out error);
        }
    }

    private static bool Fail(
        string message,
        [NotNullWhen(true)] out DecodedTexture? texture,
        [NotNullWhen(false)] out string? error)
    {
        texture = null;
        error = message;
        return false;
    }

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

    private static bool TryReadAscii(ReadOnlySpan<byte> bytes, int offset, int count, out string value)
    {
        if (offset < 0 || count < 0 || offset + count > bytes.Length)
        {
            value = string.Empty;
            return false;
        }

        value = System.Text.Encoding.ASCII.GetString(bytes.Slice(offset, count));
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

/// <summary>
/// A decoded texture as a straight top-left-origin BGRA byte buffer.
/// </summary>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Bgra">Pixel data, 4 bytes per pixel (B, G, R, A), row-major.</param>
/// <param name="HasAlpha">Whether the source format carries a meaningful alpha channel.</param>
/// <param name="DecodeDiagnostic">An optional informational diagnostic describing the decode.</param>
public sealed record DecodedTexture(
    int Width,
    int Height,
    byte[] Bgra,
    bool HasAlpha,
    string? DecodeDiagnostic = null);
