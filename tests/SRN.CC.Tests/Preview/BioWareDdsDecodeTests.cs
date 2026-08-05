using System.Buffers.Binary;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Preview.Render;

namespace SRN.CC.Tests.Preview;

/// <summary>
/// Neverwinter Nights ships BioWare's own DDS variant, not Microsoft's. It carries no
/// <c>"DDS "</c> signature at all, so a decoder that gates on one rejects every texture the game
/// actually contains.
/// </summary>
/// <remarks>
/// <para>
/// The layout asserted here was established by measurement, not by documentation: across the 2,315
/// type-2033 resources of a stock tileset HAK, every file's length equalled twenty header bytes
/// plus a complete DXT mipmap chain computed from the header's own width, height and
/// bytes-per-pixel, with zero mismatches. That exact-fit result is what fixes the header at twenty
/// bytes and identifies bytes-per-pixel 3 as DXT1 and 4 as DXT5.
/// </para>
/// <para>
/// The fixtures below are built here rather than committed as binaries: the repository ships no
/// game content, and a hand-built single-block texture pins the contract more precisely than a real
/// file would anyway.
/// </para>
/// </remarks>
[TestFixture]
public class BioWareDdsDecodeTests
{
    private const int HeaderBytes = 20;

    /// <summary>Opaque red as RGB565, the colour both fixture blocks encode.</summary>
    private const ushort Red565 = 0xF800;

    [Test]
    public void ADxt1Payload_DecodesToItsDeclaredDimensions()
    {
        byte[] payload = BioWareDds(width: 4, height: 4, bytesPerPixel: 3, Dxt1Block(Red565));

        bool decoded = TextureDecoder.TryDecodeToBgra(payload, "dds", out DecodedTexture? texture, out string? error);

        decoded.Should().BeTrue(because: error);
        texture!.Width.Should().Be(4);
        texture.Height.Should().Be(4);
        texture.Bgra.Should().HaveCount(4 * 4 * 4);
    }

    [Test]
    public void ADxt1Payload_DecodesToTheColourItEncodes()
    {
        byte[] payload = BioWareDds(width: 4, height: 4, bytesPerPixel: 3, Dxt1Block(Red565));

        TextureDecoder.TryDecodeToBgra(payload, "dds", out DecodedTexture? texture, out _);

        // Every pixel of the single block is colour0: red, fully opaque. Channel order is BGRA.
        for (int i = 0; i < texture!.Bgra.Length; i += 4)
        {
            texture.Bgra[i + 2].Should().BeGreaterThan(200, "the red channel carries the encoded colour");
            texture.Bgra[i + 1].Should().BeLessThan(60, "green was not encoded");
            texture.Bgra[i].Should().BeLessThan(60, "blue was not encoded — swapping it with red is the bug this catches");
            texture.Bgra[i + 3].Should().Be(255, "a decoded texture must not be invisible");
        }
    }

    [Test]
    public void ADxt1Payload_IsReportedAsHavingNoAlpha()
    {
        byte[] payload = BioWareDds(width: 4, height: 4, bytesPerPixel: 3, Dxt1Block(Red565));

        TextureDecoder.TryDecodeToBgra(payload, "dds", out DecodedTexture? texture, out _);

        texture!.HasAlpha.Should().BeFalse("bytes-per-pixel 3 is DXT1, which carries no alpha channel");
        texture.DecodeDiagnostic.Should().Contain("DXT1");
    }

    [Test]
    public void ADxt5Payload_DecodesAndIsReportedAsHavingAlpha()
    {
        byte[] payload = BioWareDds(width: 4, height: 4, bytesPerPixel: 4, Dxt5Block(Red565));

        bool decoded = TextureDecoder.TryDecodeToBgra(payload, "dds", out DecodedTexture? texture, out string? error);

        decoded.Should().BeTrue(because: error);
        texture!.HasAlpha.Should().BeTrue("bytes-per-pixel 4 is DXT5");
        texture.DecodeDiagnostic.Should().Contain("DXT5");
        texture.Bgra.Should().HaveCount(4 * 4 * 4);
    }

    [Test]
    public void ANonSquarePayload_KeepsWidthAndHeightTheRightWayRound()
    {
        // Two blocks wide by one tall. A width/height transposition survives every square fixture,
        // so the asymmetric case is the one that catches it.
        byte[] blocks = [.. Dxt1Block(Red565), .. Dxt1Block(Red565)];
        byte[] payload = BioWareDds(width: 8, height: 4, bytesPerPixel: 3, blocks);

        TextureDecoder.TryDecodeToBgra(payload, "dds", out DecodedTexture? texture, out string? error)
            .Should().BeTrue(because: error);

        texture!.Width.Should().Be(8);
        texture.Height.Should().Be(4);
    }

    [Test]
    public void APayloadWhoseBlockDataIsShortOfItsDeclaredSize_FailsInsteadOfDecodingGarbage()
    {
        byte[] payload = BioWareDds(width: 8, height: 4, bytesPerPixel: 3, Dxt1Block(Red565)); // Needs two blocks.

        bool decoded = TextureDecoder.TryDecodeToBgra(payload, "dds", out DecodedTexture? texture, out string? error);

        decoded.Should().BeFalse();
        texture.Should().BeNull();
        error.Should().Contain("top mip level");
    }

    [Test]
    public void APayloadWithNeitherASignatureNorAKnownBytesPerPixel_StillReportsTheMissingSignature()
    {
        byte[] payload = BioWareDds(width: 4, height: 4, bytesPerPixel: 7, Dxt1Block(Red565));

        TextureDecoder.TryDecodeToBgra(payload, "dds", out _, out string? error).Should().BeFalse();
        error.Should().Contain("DDS signature not found");
    }

    [Test]
    public void AMicrosoftSignedPayload_StillTakesTheMicrosoftPath()
    {
        // Truncated on purpose: the point is which branch claims it, and the Microsoft branch is
        // the only one that reports a payload as too small.
        byte[] payload = new byte[64];
        "DDS "u8.CopyTo(payload);

        TextureDecoder.TryDecodeToBgra(payload, "dds", out _, out string? error).Should().BeFalse();
        error.Should().Contain("too small", "a signed payload must not be re-read as a BioWare header");
    }

    /// <summary>Builds the twenty-byte BioWare header in front of <paramref name="blockData"/>.</summary>
    private static byte[] BioWareDds(uint width, uint height, uint bytesPerPixel, ReadOnlySpan<byte> blockData)
    {
        byte[] payload = new byte[HeaderBytes + blockData.Length];
        Span<byte> header = payload.AsSpan(0, HeaderBytes);

        BinaryPrimitives.WriteUInt32LittleEndian(header, width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], height);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], bytesPerPixel);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)blockData.Length);
        BinaryPrimitives.WriteSingleLittleEndian(header[16..], 1.0f);

        blockData.CopyTo(payload.AsSpan(HeaderBytes));
        return payload;
    }

    /// <summary>One 4×4 DXT1 block: both endpoints <paramref name="colour"/>, every index 0.</summary>
    private static byte[] Dxt1Block(ushort colour)
    {
        byte[] block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block, colour);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), colour);
        return block;
    }

    /// <summary>One 4×4 DXT5 block: a fully opaque alpha block followed by a DXT1 colour block.</summary>
    private static byte[] Dxt5Block(ushort colour)
    {
        byte[] block = new byte[16];
        block[0] = 255; // alpha0
        block[1] = 255; // alpha1 — equal endpoints make every interpolated index opaque.
        Dxt1Block(colour).CopyTo(block.AsSpan(8));
        return block;
    }
}
