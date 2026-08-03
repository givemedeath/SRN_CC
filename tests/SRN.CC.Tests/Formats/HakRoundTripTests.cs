using System.Buffers.Binary;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Formats.Hak;

namespace SRN.CC.Tests.Formats;

[TestFixture]
public class HakRoundTripTests
{
    [Test]
    public void HAK_WriteAndRead_ShouldRoundTripSemantically()
    {
        byte[] payload1 = "Test Payload Data 1"u8.ToArray();
        byte[] payload2 = "Test Payload Data 2 - Longer Content"u8.ToArray();
        HakFormatKey key1 = new("resref_b"u8, 2000);
        HakFormatKey key2 = new("resref_a"u8, 2000);

        byte[] output = Write(
            new(key1, new MemoryStream(payload1), (uint)payload1.Length),
            new(key2, new MemoryStream(payload2), (uint)payload2.Length));

        using MemoryStream source = new(output);
        HakReader reader = new(source);

        reader.FileType.Should().Be("HAK ");
        reader.Version.Should().Be("V1.0");
        reader.Entries[0].Key.ResrefBytes.ToArray().Should().Equal("resref_a"u8.ToArray());
        reader.Entries[1].Key.ResrefBytes.ToArray().Should().Equal("resref_b"u8.ToArray());
        ReadPayload(reader.Entries[0], source).Should().Equal(payload2);
        ReadPayload(reader.Entries[1], source).Should().Equal(payload1);
    }

    [Test]
    public void HAK_NonSequentialResourceIds_ShouldMapKeysThroughResourceTable()
    {
        byte[] output = Write(
            Item("a", 2000, "payload-a"u8),
            Item("b", 2000, "payload-b"u8));

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(160 + 16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(160 + 24 + 16, 4), 0);

        using MemoryStream source = new(output);
        HakReader reader = new(source);

        reader.Entries[0].ResourceId.Should().Be(1);
        ReadPayload(reader.Entries[0], source).Should().Equal("payload-b"u8.ToArray());
        reader.Entries[1].ResourceId.Should().Be(0);
        ReadPayload(reader.Entries[1], source).Should().Equal("payload-a"u8.ToArray());
    }

    [Test]
    public void HAK_InvalidResourceId_ShouldThrow()
    {
        byte[] output = Write(Item("a", 2000, "payload"u8));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(160 + 16, 4), 1);

        Action act = () => _ = new HakReader(new MemoryStream(output));

        act.Should().Throw<InvalidDataException>().WithMessage("*invalid resource index*");
    }

    [Test]
    public void HAK_WrongFileType_ShouldThrow()
    {
        byte[] output = Write(Item("a", 2000, "payload"u8));
        "ERF "u8.CopyTo(output);

        Action act = () => _ = new HakReader(new MemoryStream(output));

        act.Should().Throw<InvalidDataException>().WithMessage("*expected 'HAK '*");
    }

    [Test]
    public void HAK_CP1252UnknownTypeAndZeroLengthPayload_ShouldSurvive()
    {
        HakFormatKey key = new(new byte[] { (byte)'C', 0xE9 }, ushort.MaxValue);
        byte[] output = Write(new HakWriter.WriteItem(key, new MemoryStream(), 0));

        using MemoryStream source = new(output);
        HakReader reader = new(source);

        reader.Entries.Single().Key.ResrefBytes.ToArray().Should().Equal((byte)'C', 0xE9);
        reader.Entries.Single().Key.CanonicalResrefBytes.ToArray().Should().Equal((byte)'c', 0xE9);
        reader.Entries.Single().Key.ResourceType.Should().Be(ushort.MaxValue);
        ReadPayload(reader.Entries.Single(), source).Should().BeEmpty();
    }

    [Test]
    public void HAK_Writes_ShouldBeIndependentOfInputOrder()
    {
        byte[] first = Write(Item("b", 2, "b"u8), Item("a", 2, "a"u8), Item("z", 1, "z"u8));
        byte[] second = Write(Item("z", 1, "z"u8), Item("a", 2, "a"u8), Item("b", 2, "b"u8));

        first.Should().Equal(second);
    }

    [Test]
    public void HAK_MetadataRichInput_ShouldNormalizeOnRewrite()
    {
        byte[] input = Write(Item("a", 2000, "payload"u8));
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(32, 4), 2026);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(36, 4), 214);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(40, 4), 1234);
        input[44] = 0x7F;

        using MemoryStream source = new(input);
        HakReader reader = new(source);
        HakEntry entry = reader.Entries.Single();
        byte[] rewritten = Write(new HakWriter.WriteItem(entry.Key, HakReader.OpenPayloadStream(entry, source), entry.ResourceSize));

        BinaryPrimitives.ReadUInt32LittleEndian(rewritten.AsSpan(32, 4)).Should().Be(0);
        BinaryPrimitives.ReadUInt32LittleEndian(rewritten.AsSpan(36, 4)).Should().Be(0);
        BinaryPrimitives.ReadUInt32LittleEndian(rewritten.AsSpan(40, 4)).Should().Be(0);
        rewritten.AsSpan(44, 116).ToArray().Should().OnlyContain(value => value == 0);
    }

    [Test]
    public void HAK_CaseEquivalentDuplicateKeys_ShouldThrow()
    {
        Action act = () => Write(Item("Duplicate", 2000, "a"u8), Item("duplicate", 2000, "b"u8));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate resource key*");
    }

    [Test]
    public void HAK_InvalidWriterKeys_ShouldThrow()
    {
        Action tooLong = () => _ = new HakFormatKey("12345678901234567"u8, 1);
        Action separator = () => Write(Item("bad/name", 1, "payload"u8));

        tooLong.Should().Throw<ArgumentException>();
        separator.Should().Throw<ArgumentException>().WithMessage("*invalid byte*");
    }

    [Test]
    public void HAK_TruncatedAndOutOfRangePayloads_ShouldThrowSafely()
    {
        byte[] truncated = new byte[159];
        Action shortHeader = () => _ = new HakReader(new MemoryStream(truncated));

        byte[] invalidPayload = Write(Item("a", 2000, "payload"u8));
        uint resourceOffset = BinaryPrimitives.ReadUInt32LittleEndian(invalidPayload.AsSpan(28, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(invalidPayload.AsSpan(checked((int)resourceOffset), 4), uint.MaxValue);
        Action outOfRange = () => _ = new HakReader(new MemoryStream(invalidPayload));

        shortHeader.Should().Throw<InvalidDataException>();
        outOfRange.Should().Throw<InvalidDataException>().WithMessage("*payload extends past file length*");
    }

    [Test]
    public void HAK_EstimatedLegacyLimit_ShouldRejectBeforeReadingPayload()
    {
        var item = new HakWriter.WriteItem(new HakFormatKey("large"u8, 1), new MemoryStream(), int.MaxValue);

        Action act = () => Write(item);

        act.Should().Throw<InvalidOperationException>().WithMessage("*single-HAK limit*");
    }

    [Test]
    public void HAK_PayloadLongerThanDeclaredSize_ShouldThrow()
    {
        HakWriter.WriteItem item = new(new HakFormatKey("long"u8, 1), new MemoryStream("data"u8.ToArray()), 3);

        Action act = () => Write(item);

        act.Should().Throw<InvalidDataException>().WithMessage("*exceeds declared size*");
    }

    private static HakWriter.WriteItem Item(string resref, ushort type, ReadOnlySpan<byte> payload)
    {
        byte[] bytes = payload.ToArray();
        return new HakWriter.WriteItem(new HakFormatKey(System.Text.Encoding.ASCII.GetBytes(resref), type), new MemoryStream(bytes), (uint)bytes.Length);
    }

    private static byte[] Write(params HakWriter.WriteItem[] items)
    {
        using MemoryStream output = new();
        HakWriter.Write(output, items);
        return output.ToArray();
    }

    private static byte[] ReadPayload(HakEntry entry, Stream source)
    {
        using Stream payload = HakReader.OpenPayloadStream(entry, source);
        using MemoryStream copy = new();
        payload.CopyTo(copy);
        return copy.ToArray();
    }
}

