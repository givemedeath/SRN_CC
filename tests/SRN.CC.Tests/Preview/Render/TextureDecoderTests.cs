using NUnit.Framework;
using SRN.CC.Preview.Render;
using SWLOR.NWN.Formats.Plt;

namespace SRN.CC.Tests.Preview.Render;

[TestFixture]
public class TextureDecoderTests
{
    [Test]
    public void TryDecodeToBgra_ValidTga_DecodesTopOriginTruecolorToBgra()
    {
        byte[] bytes = TgaHeader(imageType: 2, width: 2, height: 1, depth: 24, descriptor: 0x20)
            .Concat(new byte[]
            {
                0, 0, 255, // pixel0 stored BGR -> red
                255, 0, 0  // pixel1 stored BGR -> blue
            })
            .ToArray();

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "tga", out var texture, out var error);

        Assert.That(decoded, Is.True);
        Assert.That(error, Is.Null);
        Assert.That(texture, Is.Not.Null);
        Assert.That(texture!.Width, Is.EqualTo(2));
        Assert.That(texture.Height, Is.EqualTo(1));
        Assert.That(texture.HasAlpha, Is.False);
        Assert.That(texture.Bgra, Is.EqualTo(new byte[]
        {
            0, 0, 255, 255,   // red pixel, BGRA
            255, 0, 0, 255    // blue pixel, BGRA
        }));
        Assert.That(texture.DecodeDiagnostic, Does.Contain("TgaReader"));
    }

    [Test]
    public void TryDecodeToBgra_TgaExtensionIsCaseInsensitive()
    {
        byte[] bytes = TgaHeader(imageType: 2, width: 1, height: 1, depth: 24, descriptor: 0x20)
            .Concat(new byte[] { 10, 20, 30 })
            .ToArray();

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "TGA", out var texture, out var error);

        Assert.That(decoded, Is.True);
        Assert.That(error, Is.Null);
        Assert.That(texture, Is.Not.Null);
    }

    [Test]
    public void TryDecodeToBgra_MalformedTga_HeaderTooSmall_ReturnsError()
    {
        byte[] bytes = new byte[10];

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "tga", out var texture, out var error);

        Assert.That(decoded, Is.False);
        Assert.That(texture, Is.Null);
        Assert.That(error, Does.Contain("too small"));
    }

    [Test]
    public void TryDecodeToBgra_MalformedTga_UnsupportedImageType_ReturnsError()
    {
        byte[] bytes = TgaHeader(imageType: 99, width: 1, height: 1, depth: 24, descriptor: 0x20)
            .Concat(new byte[3])
            .ToArray();

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "tga", out var texture, out var error);

        Assert.That(decoded, Is.False);
        Assert.That(texture, Is.Null);
        Assert.That(error, Does.Contain("TGA parse failed"));
    }

    [Test]
    public void TryDecodeToBgra_OversizedTga_ExceedsSafetyLimits_ReturnsError()
    {
        byte[] bytes = TgaHeader(imageType: 2, width: 5000, height: 5000, depth: 24, descriptor: 0x20);

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "tga", out var texture, out var error);

        Assert.That(decoded, Is.False);
        Assert.That(texture, Is.Null);
        Assert.That(error, Does.Contain("exceed preview safety limits"));
    }

    [Test]
    public void TryDecodeToBgra_ValidDds_Rgb24_DecodesViaPfim()
    {
        byte[] bytes = BuildRgb24Dds(width: 2, height: 1, pixelsBgr:
        [
            (0, 0, 255), // blue channel 0, green 0, red 255 -> red pixel
            (255, 0, 0)  // blue pixel
        ]);

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "dds", out var texture, out var error);

        Assert.That(decoded, Is.True);
        Assert.That(error, Is.Null);
        Assert.That(texture, Is.Not.Null);
        Assert.That(texture!.Width, Is.EqualTo(2));
        Assert.That(texture.Height, Is.EqualTo(1));
        Assert.That(texture.HasAlpha, Is.False);
        Assert.That(texture.Bgra.Length, Is.EqualTo(2 * 1 * 4));
        Assert.That(texture.DecodeDiagnostic, Does.Contain("DDS format"));
    }

    [Test]
    public void TryDecodeToBgra_MalformedDds_PayloadTooSmall_ReturnsError()
    {
        byte[] bytes = new byte[16];

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "dds", out var texture, out var error);

        Assert.That(decoded, Is.False);
        Assert.That(texture, Is.Null);
        Assert.That(error, Does.Contain("too small"));
    }

    [Test]
    public void TryDecodeToBgra_MalformedDds_BadSignature_ReturnsError()
    {
        byte[] bytes = BuildRgb24Dds(width: 1, height: 1, pixelsBgr: [(1, 2, 3)]);
        bytes[0] = (byte)'X';

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "dds", out var texture, out var error);

        Assert.That(decoded, Is.False);
        Assert.That(texture, Is.Null);
        Assert.That(error, Does.Contain("signature not found"));
    }

    [Test]
    public void TryDecodeToBgra_OversizedDds_ExceedsSafetyLimits_ReturnsError()
    {
        byte[] bytes = BuildRgb24DdsHeaderOnly(width: 5000, height: 5000);

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "dds", out var texture, out var error);

        Assert.That(decoded, Is.False);
        Assert.That(texture, Is.Null);
        Assert.That(error, Does.Contain("exceed preview safety limits"));
    }

    [Test]
    public void TryDecodeToBgra_ValidPlt_UsesDefaultDyeTable()
    {
        byte[] bytes = BuildPlt(width: 2, height: 1,
            (intensity: 17, layer: PltLayers.Skin),
            (intensity: 250, layer: PltLayers.Tattoo2));

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "plt", out var texture, out var error);

        Assert.That(decoded, Is.True);
        Assert.That(error, Is.Null);
        Assert.That(texture, Is.Not.Null);
        Assert.That(texture!.Width, Is.EqualTo(2));
        Assert.That(texture.Height, Is.EqualTo(1));
        Assert.That(texture.HasAlpha, Is.False);
        Assert.That(texture.Bgra.Length, Is.EqualTo(2 * 1 * 4));

        // Skin dye (224,191,170) at intensity 17 scaled by /255.
        Assert.That(texture.Bgra[0], Is.EqualTo((byte)(170 * 17 / 255))); // B
        Assert.That(texture.Bgra[1], Is.EqualTo((byte)(191 * 17 / 255))); // G
        Assert.That(texture.Bgra[2], Is.EqualTo((byte)(224 * 17 / 255))); // R
        Assert.That(texture.Bgra[3], Is.EqualTo((byte)255));             // A always opaque

        Assert.That(texture.DecodeDiagnostic, Does.Contain("default recolor table"));
    }

    [Test]
    public void TryDecodeToBgra_MalformedPlt_PayloadTooSmall_ReturnsError()
    {
        byte[] bytes = new byte[10];

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "plt", out var texture, out var error);

        Assert.That(decoded, Is.False);
        Assert.That(texture, Is.Null);
        Assert.That(error, Does.Contain("too small"));
    }

    [Test]
    public void TryDecodeToBgra_MalformedPlt_BadVersion_ReturnsError()
    {
        byte[] bytes = BuildPlt(width: 1, height: 1, (intensity: 1, layer: PltLayers.Skin));
        bytes[4] = (byte)'X';

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "plt", out var texture, out var error);

        Assert.That(decoded, Is.False);
        Assert.That(texture, Is.Null);
        Assert.That(error, Does.Contain("PLT parse failed"));
    }

    [Test]
    public void TryDecodeToBgra_OversizedPlt_ExceedsSafetyLimits_ReturnsError()
    {
        // Width exceeds MaxImageDimension (4096); PltReader itself requires the declared
        // pixel data to actually be present, so the fixture must carry it before the
        // decoder's own dimension guard can be reached.
        const int width = 5000;
        const int height = 1;
        byte[] bytes = new byte[24 + width * height * 2];
        "PLT "u8.CopyTo(bytes);
        "V1  "u8.CopyTo(bytes.AsSpan(4));
        BitConverter.GetBytes((uint)width).CopyTo(bytes, 16);
        BitConverter.GetBytes((uint)height).CopyTo(bytes, 20);

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "plt", out var texture, out var error);

        Assert.That(decoded, Is.False);
        Assert.That(texture, Is.Null);
        Assert.That(error, Does.Contain("exceed preview safety limits"));
    }

    [Test]
    public void TryDecodeToBgra_UnsupportedExtension_ReturnsError()
    {
        byte[] bytes = new byte[32];

        bool decoded = TextureDecoder.TryDecodeToBgra(bytes, "bmp", out var texture, out var error);

        Assert.That(decoded, Is.False);
        Assert.That(texture, Is.Null);
        Assert.That(error, Does.Contain("Unsupported image type"));
    }

    private static byte[] TgaHeader(byte imageType, ushort width, ushort height, byte depth, byte descriptor)
    {
        var bytes = new byte[18];
        bytes[2] = imageType;
        BitConverter.GetBytes(width).CopyTo(bytes, 12);
        BitConverter.GetBytes(height).CopyTo(bytes, 14);
        bytes[16] = depth;
        bytes[17] = descriptor;
        return bytes;
    }

    private static byte[] BuildPlt(int width, int height, params (int intensity, byte layer)[] pixels)
    {
        var bytes = new byte[24 + pixels.Length * 2];
        "PLT "u8.CopyTo(bytes);
        "V1  "u8.CopyTo(bytes.AsSpan(4));
        BitConverter.GetBytes((uint)width).CopyTo(bytes, 16);
        BitConverter.GetBytes((uint)height).CopyTo(bytes, 20);

        for (int i = 0; i < pixels.Length; i++)
        {
            bytes[24 + i * 2] = (byte)pixels[i].intensity;
            bytes[24 + i * 2 + 1] = pixels[i].layer;
        }

        return bytes;
    }

    /// <summary>
    /// Builds a minimal uncompressed 24-bit RGB DDS payload (128-byte header, DDPF_RGB pixel
    /// format) that Pfim decodes as <c>ImageFormat.Rgb24</c>.
    /// </summary>
    private static byte[] BuildRgb24Dds(int width, int height, (byte b, byte g, byte r)[] pixelsBgr)
    {
        byte[] header = BuildRgb24DdsHeaderOnly(width, height);
        byte[] bytes = new byte[header.Length + pixelsBgr.Length * 3];
        header.CopyTo(bytes, 0);

        for (int i = 0; i < pixelsBgr.Length; i++)
        {
            int offset = header.Length + i * 3;
            bytes[offset] = pixelsBgr[i].b;
            bytes[offset + 1] = pixelsBgr[i].g;
            bytes[offset + 2] = pixelsBgr[i].r;
        }

        return bytes;
    }

    private static byte[] BuildRgb24DdsHeaderOnly(int width, int height)
    {
        byte[] bytes = new byte[128];
        "DDS "u8.CopyTo(bytes);
        BitConverter.GetBytes(124u).CopyTo(bytes, 4);          // dwSize
        BitConverter.GetBytes(0x1007u).CopyTo(bytes, 8);       // dwFlags: CAPS|HEIGHT|WIDTH|PIXELFORMAT
        BitConverter.GetBytes((uint)height).CopyTo(bytes, 12); // dwHeight
        BitConverter.GetBytes((uint)width).CopyTo(bytes, 16);  // dwWidth
        BitConverter.GetBytes((uint)(width * 3)).CopyTo(bytes, 20); // dwPitchOrLinearSize
        BitConverter.GetBytes(32u).CopyTo(bytes, 76);          // pixel format dwSize
        BitConverter.GetBytes(0x40u).CopyTo(bytes, 80);        // DDPF_RGB
        BitConverter.GetBytes(24u).CopyTo(bytes, 88);          // dwRGBBitCount
        BitConverter.GetBytes(0x00FF0000u).CopyTo(bytes, 92);  // dwRBitMask
        BitConverter.GetBytes(0x0000FF00u).CopyTo(bytes, 96);  // dwGBitMask
        BitConverter.GetBytes(0x000000FFu).CopyTo(bytes, 100); // dwBBitMask
        BitConverter.GetBytes(0x1000u).CopyTo(bytes, 108);     // dwCaps: DDSCAPS_TEXTURE
        return bytes;
    }
}
