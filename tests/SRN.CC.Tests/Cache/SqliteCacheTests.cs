using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Records;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Cache;

namespace SRN.CC.Tests.Cache;

[TestFixture]
public class SqliteCacheTests
{
    private string _tempDbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), "SRNCC_CacheTests_" + Guid.NewGuid().ToString("N") + ".sqlite");
    }

    [TearDown]
    public void TearDown()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath);
        if (File.Exists(_tempDbPath + "-wal")) File.Delete(_tempDbPath + "-wal");
        if (File.Exists(_tempDbPath + "-shm")) File.Delete(_tempDbPath + "-shm");
    }

    [Test]
    public async Task SaveAndGet_ColdSaveAndWarmHit_EquivalenceAndSourceIdRematerialization()
    {
        using SqliteCacheService cacheService = new(_tempDbPath);

        Guid originalSourceId = Guid.NewGuid();
        Guid callerSourceId = Guid.NewGuid();

        AssetSource originalSource = AssetSource.CreateFolder(@"C:\Test\Folder", priorityOrdinal: 1, id: originalSourceId);
        AssetSource callerSource = AssetSource.CreateFolder(@"C:\Test\Folder", priorityOrdinal: 1, id: callerSourceId);

        byte[] digest = new byte[32];
        Array.Fill<byte>(digest, 0xAB);
        SourceFingerprint fingerprint = new(AssetSourceKind.Folder, 1, digest);

        AssetOccurrence occ = new(
            identity: new SRN.CC.Core.Identity.AssetIdentity("resref1", 2009),
            sourceId: originalSourceId,
            locator: new FolderFileLocator("resref1.nss"),
            originalName: "resref1.nss",
            size: 100);

        IndexedAssetRecord rec = new(occ);
        SourceScanStatistics stats = new(1, 100, TimeSpan.FromMilliseconds(50));

        SourceIndexSnapshot snapshotToSave = new(
            source: originalSource with { Fingerprint = fingerprint },
            fingerprint: fingerprint,
            records: new[] { rec },
            diagnostics: Array.Empty<SRN.CC.Core.Diagnostics.AssetDiagnosticRecord>(),
            isCacheHit: false,
            scanStatistics: stats);

        await cacheService.SaveSnapshotAsync(snapshotToSave);

        SourceIndexSnapshot? warmSnapshot = await cacheService.TryGetSnapshotAsync(callerSource, fingerprint);

        warmSnapshot.Should().NotBeNull();
        warmSnapshot!.IsCacheHit.Should().BeTrue();
        warmSnapshot.Records.Should().HaveCount(1);
        warmSnapshot.Records[0].Occurrence!.SourceId.Should().Be(callerSourceId, "Warm hit records must rematerialize with caller's source ID");
    }

    [Test]
    public async Task EnforceLruEviction_ExceedsMaxLogicalBytes_EvictsOldestSnapshots()
    {
        // Set small 500-byte limit
        using SqliteCacheService cacheService = new(_tempDbPath, maxLogicalBytes: 500);

        // Entry 1: 300 bytes
        byte[] digest1 = new byte[32]; Array.Fill(digest1, (byte)1);
        SourceFingerprint fp1 = new(AssetSourceKind.Folder, 1, digest1);
        AssetSource source1 = AssetSource.CreateFolder(@"C:\Test\1", id: Guid.NewGuid());
        SourceIndexSnapshot snap1 = CreateTestSnapshot(source1, fp1, logicalBytes: 300);

        await cacheService.SaveSnapshotAsync(snap1);

        // Entry 2: 300 bytes (Total 600 > 500 limit -> should evict snap1)
        byte[] digest2 = new byte[32]; Array.Fill(digest2, (byte)2);
        SourceFingerprint fp2 = new(AssetSourceKind.Folder, 1, digest2);
        AssetSource source2 = AssetSource.CreateFolder(@"C:\Test\2", id: Guid.NewGuid());
        SourceIndexSnapshot snap2 = CreateTestSnapshot(source2, fp2, logicalBytes: 300);

        await cacheService.SaveSnapshotAsync(snap2);

        SourceIndexSnapshot? hit1 = await cacheService.TryGetSnapshotAsync(source1, fp1);
        SourceIndexSnapshot? hit2 = await cacheService.TryGetSnapshotAsync(source2, fp2);

        hit1.Should().BeNull("Snap1 should have been evicted by LRU limit");
        hit2.Should().NotBeNull("Snap2 should remain in cache");
    }

    [Test]
    public async Task EnforceLruEviction_NewSnapshotExceedsLimit_RetainsNewSnapshot()
    {
        using SqliteCacheService cacheService = new(_tempDbPath, maxLogicalBytes: 500);

        byte[] oldDigest = new byte[32]; Array.Fill(oldDigest, (byte)3);
        SourceFingerprint oldFingerprint = new(AssetSourceKind.Folder, 1, oldDigest);
        AssetSource oldSource = AssetSource.CreateFolder(@"C:\Test\old", id: Guid.NewGuid());
        await cacheService.SaveSnapshotAsync(CreateTestSnapshot(oldSource, oldFingerprint, logicalBytes: 100));

        byte[] newDigest = new byte[32]; Array.Fill(newDigest, (byte)4);
        SourceFingerprint newFingerprint = new(AssetSourceKind.Folder, 1, newDigest);
        AssetSource newSource = AssetSource.CreateFolder(@"C:\Test\oversized", id: Guid.NewGuid());
        await cacheService.SaveSnapshotAsync(CreateTestSnapshot(newSource, newFingerprint, logicalBytes: 600));

        (await cacheService.TryGetSnapshotAsync(oldSource, oldFingerprint)).Should().BeNull();
        (await cacheService.TryGetSnapshotAsync(newSource, newFingerprint)).Should().NotBeNull(
            "the snapshot being saved must survive eviction even when it alone exceeds the limit");
    }

    [Test]
    public async Task TryGetSnapshot_RestoresSavedLogicalByteStatistics()
    {
        using SqliteCacheService cacheService = new(_tempDbPath);
        byte[] digest = new byte[32];
        Array.Fill(digest, (byte)5);
        SourceFingerprint fingerprint = new(AssetSourceKind.Hak, 1, digest);
        AssetSource source = AssetSource.CreateHak(@"C:\Test\stats.hak", id: Guid.NewGuid());
        SourceIndexSnapshot snapshot = CreateTestSnapshot(source, fingerprint, logicalBytes: 1_000, occurrenceBytes: 10);

        await cacheService.SaveSnapshotAsync(snapshot);

        SourceIndexSnapshot? restored = await cacheService.TryGetSnapshotAsync(source, fingerprint);
        restored!.ScanStatistics.TotalLogicalBytes.Should().Be(1_000);
    }

    private static SourceIndexSnapshot CreateTestSnapshot(
        AssetSource source,
        SourceFingerprint fingerprint,
        long logicalBytes,
        long? occurrenceBytes = null)
    {
        AssetOccurrence occ = new(
            identity: new SRN.CC.Core.Identity.AssetIdentity("file", 2009),
            sourceId: source.Id,
            locator: new FolderFileLocator("file.nss"),
            originalName: "file.nss",
            size: occurrenceBytes ?? logicalBytes);

        return new SourceIndexSnapshot(
            source: source with { Fingerprint = fingerprint },
            fingerprint: fingerprint,
            records: new[] { new IndexedAssetRecord(occ) },
            diagnostics: Array.Empty<SRN.CC.Core.Diagnostics.AssetDiagnosticRecord>(),
            isCacheHit: false,
            scanStatistics: new SourceScanStatistics(1, logicalBytes, TimeSpan.FromMilliseconds(10)));
    }
}

public static class SqliteCacheServiceExtensions
{
    public static void ClearAllPoolsForTest()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}

