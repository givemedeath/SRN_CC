using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
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

    [Test]
    public async Task IndexAsync_DriftOnce_RetriesStableHak()
    {
        string hakPath = CreateHak("first", "data"u8.ToArray());
        try
        {
            bool replaced = false;
            InlineProgress<IndexProgress> progress = new(_ =>
            {
                if (replaced) return;
                replaced = true;
                WriteHak(hakPath, "second", "longer data"u8.ToArray());
            });

            SourceIndexSnapshot snapshot = await new HakAssetSourceReader()
                .IndexAsync(AssetSource.CreateHak(hakPath), progress);

            snapshot.Source.IsAvailable.Should().BeTrue();
            snapshot.Records.Single().Occurrence!.Identity.OriginalName.Should().Be("second");
        }
        finally
        {
            File.Delete(hakPath);
        }
    }

    [Test]
    public async Task IndexAsync_NonAsciiCaseVariants_AreDistinctIdentities()
    {
        string hakPath = Path.Combine(Path.GetTempPath(), "SRNCC_HakSource_" + Guid.NewGuid().ToString("N") + ".hak");
        try
        {
            using (FileStream output = new(hakPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                HakWriter.Write(output, new[]
                {
                    new HakWriter.WriteItem(new HakFormatKey(new byte[] { 0xC9 }, 2009), new MemoryStream("first"u8.ToArray()), 5),
                    new HakWriter.WriteItem(new HakFormatKey(new byte[] { 0xE9 }, 2009), new MemoryStream("second"u8.ToArray()), 6)
                });
            }

            SourceIndexSnapshot snapshot = await new HakAssetSourceReader().IndexAsync(AssetSource.CreateHak(hakPath));

            snapshot.Records.Select(record => record.Occurrence).Where(occurrence => occurrence != null)
                .Should().OnlyContain(occurrence => occurrence!.ValidationState == ValidationState.Valid);
        }
        finally
        {
            File.Delete(hakPath);
        }
    }

    [Test]
    public async Task IndexAsync_PreviouslyUnavailableSource_RecoversAvailability()
    {
        string hakPath = Path.Combine(Path.GetTempPath(), "SRNCC_HakSource_" + Guid.NewGuid().ToString("N") + ".hak");
        try
        {
            HakAssetSourceReader reader = new();
            SourceIndexSnapshot unavailable = await reader.IndexAsync(AssetSource.CreateHak(hakPath));
            unavailable.Source.IsAvailable.Should().BeFalse();
            WriteHak(hakPath, "recovered", "data"u8.ToArray());

            SourceIndexSnapshot recovered = await reader.IndexAsync(unavailable.Source);

            recovered.Source.IsAvailable.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(hakPath)) File.Delete(hakPath);
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

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}


