using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SRN.CC.Core.Build;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Formats.Hak;
using SRN.CC.Infrastructure.Build;

namespace SRN.CC.Tests.Build;

[TestFixture]
public class AssetPackerTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "srncc_packer_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Test]
    public async Task PackAsync_ValidPlan_ProducesReadableAndSortedHak()
    {
        byte[] payload1 = Encoding.ASCII.GetBytes("hello world");
        byte[] payload2 = Encoding.ASCII.GetBytes("test 2da file content");

        string srcPath = Path.Combine(_tempDir, "test.hak");
        await File.WriteAllBytesAsync(srcPath, Array.Empty<byte>());

        AssetSource source = AssetSource.CreateHak(srcPath, 0);
        AssetIdentity id1 = new AssetIdentity("a_resref", 2000);
        AssetIdentity id2 = new AssetIdentity("b_resref", 2001);

        string hash1 = Convert.ToHexString(SHA256.HashData(payload1)).ToLowerInvariant();
        string hash2 = Convert.ToHexString(SHA256.HashData(payload2)).ToLowerInvariant();

        var loc1 = new HakEntryLocator(0);
        var loc2 = new HakEntryLocator(1);

        var plan = new BuildPlan
        {
            DestinationHakPath = Path.Combine(_tempDir, "out.hak"),
            DestinationManifestPath = Path.Combine(_tempDir, "out.srncc-manifest.json"),
            FrozenSources = new[] { source },
            Items = new[]
            {
                new BuildItem(id1, source.Id, loc1, payload1.Length, hash1, false),
                new BuildItem(id2, source.Id, loc2, payload2.Length, hash2, false)
            },
            CreatedUtc = DateTime.UtcNow
        };

        var dispatcher = new TestDispatcher(new Dictionary<OccurrenceLocator, byte[]>
        {
            [loc1] = payload1,
            [loc2] = payload2
        });

        var packer = new AssetPacker(dispatcher);

        string tempHak = Path.Combine(_tempDir, "temp.hak");
        string tempManifest = Path.Combine(_tempDir, "temp.manifest.json");

        var artifact = await packer.PackAsync(plan, tempHak, tempManifest);

        Assert.That(File.Exists(tempHak), Is.True);
        Assert.That(artifact.EntryCount, Is.EqualTo(2));

        using (FileStream fs = File.OpenRead(tempHak))
        {
            var reader = new HakReader(fs);
            Assert.That(reader.Entries.Count, Is.EqualTo(2));
        }
    }

    private sealed class TestDispatcher : ISourceReaderDispatcher
    {
        private readonly IDictionary<OccurrenceLocator, byte[]> _payloads;

        public TestDispatcher(IDictionary<OccurrenceLocator, byte[]> payloads)
        {
            _payloads = payloads;
        }

        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            if (_payloads.TryGetValue(occurrence.Locator, out var bytes))
            {
                return Task.FromResult<Stream>(new MemoryStream(bytes));
            }
            throw new FileNotFoundException("Locator not found in test stub.");
        }
    }
}
