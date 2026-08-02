using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Formats.Hak;
using SRN.CC.Infrastructure.Sources;

namespace SRN.CC.Tests.Sources;

[TestFixture]
public class HakSourceReaderTests
{
    [Test]
    public async Task IndexAsync_EmptyResref_ReturnsUnavailableSnapshot()
    {
        string hakPath = Path.Combine(Path.GetTempPath(), "SRNCC_InvalidResref_" + Guid.NewGuid().ToString("N") + ".hak");
        try
        {
            File.WriteAllBytes(hakPath, BuildHakWithEmptyResref());
            AssetSource source = AssetSource.CreateHak(hakPath);
            HakAssetSourceReader reader = new();

            var snapshot = await reader.IndexAsync(source);

            snapshot.Source.IsAvailable.Should().BeFalse();
            snapshot.Diagnostics.Should().ContainSingle(d => d.Code == DiagnosticCode.InvalidResref);
        }
        finally
        {
            if (File.Exists(hakPath)) File.Delete(hakPath);
        }
    }

    [Test]
    public async Task IndexAsync_PreservesDecodedResrefAsOriginalName()
    {
        string hakPath = CreateHak("MixedCase", "data"u8.ToArray());
        try
        {
            AssetSource source = AssetSource.CreateHak(hakPath);
            SourceIndexSnapshot snapshot = await new HakAssetSourceReader().IndexAsync(source);

            snapshot.Records.Single().Occurrence!.OriginalName.Should().Be("MixedCase");
        }
        finally
        {
            File.Delete(hakPath);
        }
    }

    [Test]
    public async Task OpenOccurrenceAsync_ReplacedEntryWithSameSize_RejectsStaleLocator()
    {
        string hakPath = CreateHak("first", "data"u8.ToArray());
        try
        {
            AssetSource source = AssetSource.CreateHak(hakPath);
            HakAssetSourceReader reader = new();
            SourceIndexSnapshot snapshot = await reader.IndexAsync(source);
            var occurrence = snapshot.Records.Single().Occurrence!;
            WriteHak(hakPath, "other", "data"u8.ToArray());

            Func<Task> act = async () => { using Stream _ = await reader.OpenOccurrenceAsync(source, occurrence); };

            await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*identity no longer matches*");
        }
        finally
        {
            File.Delete(hakPath);
        }
    }

    private static string CreateHak(string resref, byte[] payload)
    {
        string path = Path.Combine(Path.GetTempPath(), "SRNCC_HakSource_" + Guid.NewGuid().ToString("N") + ".hak");
        WriteHak(path, resref, payload);
        return path;
    }

    private static void WriteHak(string path, string resref, byte[] payload)
    {
        using FileStream output = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        HakWriter.Write(output, new[]
        {
            new HakWriter.WriteItem(new HakFormatKey(System.Text.Encoding.ASCII.GetBytes(resref), 2009), new MemoryStream(payload), (uint)payload.Length)
        });
    }

    private static byte[] BuildHakWithEmptyResref()
    {
        byte[] bytes = new byte[192];
        "HAK "u8.CopyTo(bytes);
        "V1.0"u8.CopyTo(bytes.AsSpan(4));
        BitConverter.GetBytes(1u).CopyTo(bytes, 16);
        BitConverter.GetBytes(160u).CopyTo(bytes, 24);
        BitConverter.GetBytes(184u).CopyTo(bytes, 28);
        BitConverter.GetBytes((ushort)2009).CopyTo(bytes, 180);
        BitConverter.GetBytes(192u).CopyTo(bytes, 184);
        return bytes;
    }
}

