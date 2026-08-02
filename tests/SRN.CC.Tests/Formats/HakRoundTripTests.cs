using NUnit.Framework;
using FluentAssertions;
using System.Text;
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

        var items = new List<HakWriter.WriteItem>
        {
            new(key1, new MemoryStream(payload1), (uint)payload1.Length),
            new(key2, new MemoryStream(payload2), (uint)payload2.Length)
        };

        using MemoryStream outputStream = new();
        HakWriter.Write(outputStream, items);

        outputStream.Position = 0;
        HakReader reader = new(outputStream);

        reader.FileType.Should().Be("HAK ");
        reader.Version.Should().Be("V1.0");
        reader.Entries.Count.Should().Be(2);

        // Entries must be sorted by resref (resref_a comes before resref_b)
        reader.Entries[0].Key.ResrefBytes.Should().Equal("resref_a"u8.ToArray());
        reader.Entries[1].Key.ResrefBytes.Should().Equal("resref_b"u8.ToArray());

        // Verify payload streams
        using var stream0 = HakReader.OpenPayloadStream(reader.Entries[0], outputStream);
        using MemoryStream ms0 = new();
        stream0.CopyTo(ms0);
        ms0.ToArray().Should().Equal(payload2);

        using var stream1 = HakReader.OpenPayloadStream(reader.Entries[1], outputStream);
        using MemoryStream ms1 = new();
        stream1.CopyTo(ms1);
        ms1.ToArray().Should().Equal(payload1);
    }

    [Test]
    public void HAK_TwoWritesFromSameInputs_ShouldBeByteIdentical()
    {
        byte[] payload = "Constant Payload"u8.ToArray();
        HakFormatKey key = new("constant_res"u8, 2002);

        var items1 = new List<HakWriter.WriteItem> { new(key, new MemoryStream(payload), (uint)payload.Length) };
        var items2 = new List<HakWriter.WriteItem> { new(key, new MemoryStream(payload), (uint)payload.Length) };

        using MemoryStream stream1 = new();
        using MemoryStream stream2 = new();

        HakWriter.Write(stream1, items1);
        HakWriter.Write(stream2, items2);

        stream1.ToArray().Should().Equal(stream2.ToArray());
    }

    [Test]
    public void HAK_DuplicateKeys_ShouldThrow()
    {
        byte[] payload = "Data"u8.ToArray();
        HakFormatKey key = new("duplicate"u8, 2000);

        var items = new List<HakWriter.WriteItem>
        {
            new(key, new MemoryStream(payload), (uint)payload.Length),
            new(key, new MemoryStream(payload), (uint)payload.Length)
        };

        using MemoryStream stream = new();
        Action act = () => HakWriter.Write(stream, items);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate resource key*");
    }
}
