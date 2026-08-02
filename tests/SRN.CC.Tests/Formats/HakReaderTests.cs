using System.Text;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Formats.Hak;

namespace SRN.CC.Tests.Formats;

[TestFixture]
public class HakReaderTests
{
    [Test]
    public void Read_InvalidHeader_ThrowsInvalidDataException()
    {
        byte[] bytes = new byte[160];
        Encoding.ASCII.GetBytes("BAD ").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("V1.0").CopyTo(bytes, 4);

        using MemoryStream ms = new(bytes);
        Action act = () => _ = new HakReader(ms);

        act.Should().Throw<InvalidDataException>()
           .WithMessage("*HAK*");
    }

    [Test]
    public void Read_InvalidVersion_ThrowsInvalidDataException()
    {
        byte[] bytes = new byte[160];
        Encoding.ASCII.GetBytes("HAK ").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("V2.0").CopyTo(bytes, 4);

        using MemoryStream ms = new(bytes);
        Action act = () => _ = new HakReader(ms);

        act.Should().Throw<InvalidDataException>()
           .WithMessage("*V1.0*");
    }

    [Test]
    public void Read_TableOverlapHeader_ThrowsInvalidDataException()
    {
        byte[] bytes = new byte[200];
        Encoding.ASCII.GetBytes("HAK ").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("V1.0").CopyTo(bytes, 4);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), (uint)1); // EntryCount
        BitConverter.TryWriteBytes(bytes.AsSpan(24, 4), (uint)100); // KeyList offset 100 (< 160)
        BitConverter.TryWriteBytes(bytes.AsSpan(28, 4), (uint)180); // ResourceList offset 180

        using MemoryStream ms = new(bytes);
        Action act = () => _ = new HakReader(ms);

        act.Should().Throw<InvalidDataException>()
           .WithMessage("*fixed header*");
    }

    [Test]
    public void Read_PartialPayloadOverlap_ThrowsInvalidDataException()
    {
        // 160 header + 48 key (2 entries) + 16 res (2 entries) = 224 bytes metadata
        byte[] bytes = new byte[300];
        Encoding.ASCII.GetBytes("HAK ").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("V1.0").CopyTo(bytes, 4);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), (uint)2); // EntryCount
        BitConverter.TryWriteBytes(bytes.AsSpan(24, 4), (uint)160); // KeyList offset 160
        BitConverter.TryWriteBytes(bytes.AsSpan(28, 4), (uint)208); // ResourceList offset 208

        // Key 0: resref1, id 0, type 1
        Encoding.ASCII.GetBytes("resref1").CopyTo(bytes, 160);
        BitConverter.TryWriteBytes(bytes.AsSpan(176, 4), (uint)0);
        BitConverter.TryWriteBytes(bytes.AsSpan(180, 2), (ushort)1);

        // Key 1: resref2, id 1, type 1
        Encoding.ASCII.GetBytes("resref2").CopyTo(bytes, 184);
        BitConverter.TryWriteBytes(bytes.AsSpan(200, 4), (uint)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(204, 2), (ushort)1);

        // Resource 0: offset 224, size 50 (range 224..274)
        BitConverter.TryWriteBytes(bytes.AsSpan(208, 4), (uint)224);
        BitConverter.TryWriteBytes(bytes.AsSpan(212, 4), (uint)50);

        // Resource 1: offset 240, size 50 (range 240..290 - PARTIAL OVERLAP)
        BitConverter.TryWriteBytes(bytes.AsSpan(216, 4), (uint)240);
        BitConverter.TryWriteBytes(bytes.AsSpan(220, 4), (uint)50);

        using MemoryStream ms = new(bytes);
        Action act = () => _ = new HakReader(ms);

        act.Should().Throw<InvalidDataException>()
           .WithMessage("*partially overlaps*");
    }

    [Test]
    public void Read_ExactSharedPayloadRanges_Succeeds()
    {
        // 160 header + 48 key + 16 res = 224 bytes metadata
        byte[] bytes = new byte[300];
        Encoding.ASCII.GetBytes("HAK ").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("V1.0").CopyTo(bytes, 4);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), (uint)2); // EntryCount
        BitConverter.TryWriteBytes(bytes.AsSpan(24, 4), (uint)160); // KeyList offset 160
        BitConverter.TryWriteBytes(bytes.AsSpan(28, 4), (uint)208); // ResourceList offset 208

        Encoding.ASCII.GetBytes("resref1").CopyTo(bytes, 160);
        BitConverter.TryWriteBytes(bytes.AsSpan(176, 4), (uint)0);
        BitConverter.TryWriteBytes(bytes.AsSpan(180, 2), (ushort)1);

        Encoding.ASCII.GetBytes("resref2").CopyTo(bytes, 184);
        BitConverter.TryWriteBytes(bytes.AsSpan(200, 4), (uint)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(204, 2), (ushort)1);

        // Exact shared range: both at offset 224, size 50
        BitConverter.TryWriteBytes(bytes.AsSpan(208, 4), (uint)224);
        BitConverter.TryWriteBytes(bytes.AsSpan(212, 4), (uint)50);
        BitConverter.TryWriteBytes(bytes.AsSpan(216, 4), (uint)224);
        BitConverter.TryWriteBytes(bytes.AsSpan(220, 4), (uint)50);

        using MemoryStream ms = new(bytes);
        HakReader reader = new(ms);
        reader.Entries.Should().HaveCount(2);
    }

    [Test]
    public void WriteAndRead_RoundTrip_PreservesSortingAndPayloads()
    {
        using MemoryStream outMs = new();
        byte[] payload1 = Encoding.ASCII.GetBytes("Hello HAK World 1");
        byte[] payload2 = Encoding.ASCII.GetBytes("Hello HAK World 2");

        HakFormatKey keyB = new(Encoding.ASCII.GetBytes("b_resref"), 2002);
        HakFormatKey keyA = new(Encoding.ASCII.GetBytes("a_resref"), 2002);

        HakWriter.WriteItem itemB = new(keyB, new MemoryStream(payload1), (uint)payload1.Length);
        HakWriter.WriteItem itemA = new(keyA, new MemoryStream(payload2), (uint)payload2.Length);

        HakWriter.Write(outMs, new[] { itemB, itemA });

        outMs.Position = 0;
        HakReader reader = new(outMs);

        reader.Entries.Should().HaveCount(2);
        Encoding.ASCII.GetString(reader.Entries[0].Key.ResrefBytes.Span).Should().Be("a_resref");
        Encoding.ASCII.GetString(reader.Entries[1].Key.ResrefBytes.Span).Should().Be("b_resref");

        using Stream payloadStreamA = HakReader.OpenPayloadStream(reader.Entries[0], outMs);
        using StreamReader srA = new(payloadStreamA);
        srA.ReadToEnd().Should().Be("Hello HAK World 2");
    }

    [Test]
    public void HakWriter_ExceedsSizeLimit_ThrowsInvalidOperationException()
    {
        using MemoryStream outMs = new();
        HakFormatKey key = new(Encoding.ASCII.GetBytes("bigres"), 1);
        HakWriter.WriteItem bigItem = new(key, new MemoryStream(), (uint)int.MaxValue);

        Action act = () => HakWriter.Write(outMs, new[] { bigItem });

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*exceeds single-HAK limit*");
    }
}
