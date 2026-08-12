using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Records;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Tests.Core;

[TestFixture]
public class WorkspaceServiceTests
{
    private sealed class MockIndexService : IAssetIndexService
    {
        public Dictionary<Guid, SourceIndexSnapshot> IndexResults { get; } = new();
        public HashSet<Guid> FailingSources { get; } = new();
        public int IndexCallCount { get; private set; }

        public Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            IndexCallCount++;
            if (FailingSources.Contains(source.Id))
            {
                throw new IOException($"Simulated index failure for source {source.Id}");
            }
            if (IndexResults.TryGetValue(source.Id, out SourceIndexSnapshot? snap))
            {
                return Task.FromResult(snap);
            }

            byte[] fpBytes = new byte[32];
            SourceFingerprint fp = new SourceFingerprint(source.Kind, 1, fpBytes);
            SourceScanStatistics stats = new SourceScanStatistics(0, 0, TimeSpan.Zero);
            SourceIndexSnapshot emptySnap = new SourceIndexSnapshot(source, fp, Array.Empty<IndexedAssetRecord>(), Array.Empty<AssetDiagnosticRecord>(), isCacheHit: false, scanStatistics: stats);
            return Task.FromResult(emptySnap);
        }
    }

    private sealed class DummyHashService : IStreamingHashService
    {
        public Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(occurrence.Locator.ToString())));
        }
    }

    [Test]
    public async Task ReorderSources_ShouldChangeWinners_AndProduceChangedInputReport()
    {
        Guid id1 = Guid.NewGuid();
        Guid id2 = Guid.NewGuid();

        AssetSource s1 = new AssetSource(id1, AssetSourceKind.Hak, "c:/source1.hak", priorityOrdinal: 0);
        AssetSource s2 = new AssetSource(id2, AssetSourceKind.Hak, "c:/source2.hak", priorityOrdinal: 1);

        AssetIdentity identity = new AssetIdentity("shared", 2000);
        AssetOccurrence occ1 = new AssetOccurrence(identity, id1, new HakEntryLocator(0), "shared.tga", 100);
        AssetOccurrence occ2 = new AssetOccurrence(identity, id2, new HakEntryLocator(0), "shared.tga", 100);

        MockIndexService indexService = new();
        indexService.IndexResults[id1] = CreateSnapshot(s1, occ1);
        indexService.IndexResults[id2] = CreateSnapshot(s2, occ2);

        AssetHashCache cache = new AssetHashCache();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), cache);
        WorkspaceService workspaceService = new WorkspaceService(indexService, resolver, cache);

        var (initialState, _) = await workspaceService.InitializeAsync(new[] { s1, s2 });
        initialState.CuratedAssets[0].ResolvedOccurrence!.SourceId.Should().Be(id1);

        // Reorder: s2 priority 0, s1 priority 1
        var (reorderedState, report) = await workspaceService.ReorderSourcesAsync(new[] { id2, id1 });

        reorderedState.CuratedAssets[0].ResolvedOccurrence!.SourceId.Should().Be(id2);
        report.WinnerChanges.Should().Contain(identity);
    }

    [Test]
    public async Task Rescan_SourceFailure_MarksSourceUnavailable_PreservingPriorStateMetadata()
    {
        Guid id1 = Guid.NewGuid();
        AssetSource s1 = new AssetSource(id1, AssetSourceKind.Hak, "c:/source1.hak", priorityOrdinal: 0);
        AssetIdentity identity = new AssetIdentity("rescan", 2000);
        AssetOccurrence occ1 = new AssetOccurrence(identity, id1, new HakEntryLocator(0), "rescan.tga", 100);

        MockIndexService indexService = new();
        indexService.IndexResults[id1] = CreateSnapshot(s1, occ1);

        AssetHashCache cache = new AssetHashCache();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), cache);
        WorkspaceService workspaceService = new WorkspaceService(indexService, resolver, cache);

        await workspaceService.InitializeAsync(new[] { s1 });

        // Simulate failure on rescan
        indexService.FailingSources.Add(id1);

        var (rescannedState, report) = await workspaceService.RescanAsync(new[] { id1 });

        rescannedState.Sources[0].IsAvailable.Should().BeFalse();
        rescannedState.Snapshots.Should().ContainKey(id1, "Prior snapshot metadata must be preserved on rescan failure");
        report.AvailabilityTransitions.Should().Contain(id1);
    }

    [Test]
    public async Task SetSourceMode_Reference_ChangesWinner_ReportsModeChange_AndDoesNotReindex()
    {
        Guid refId = Guid.NewGuid();
        Guid fullId = Guid.NewGuid();
        AssetSource higher = new AssetSource(refId, AssetSourceKind.Hak, "c:/higher.hak", priorityOrdinal: 0);
        AssetSource lower = new AssetSource(fullId, AssetSourceKind.Hak, "c:/lower.hak", priorityOrdinal: 1);

        AssetIdentity identity = new AssetIdentity("shared", 2000);
        indexSnapshotFor(out MockIndexService indexService, (higher, identity), (lower, identity));

        AssetHashCache cache = new AssetHashCache();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), cache);
        WorkspaceService workspaceService = new WorkspaceService(indexService, resolver, cache);

        var (initialState, _) = await workspaceService.InitializeAsync(new[] { higher, lower });
        initialState.CuratedAssets[0].ResolvedOccurrence!.SourceId.Should().Be(refId,
            "the higher-priority source wins while both are Full");
        int callsAfterInit = indexService.IndexCallCount;

        // Switch the higher-priority source to Reference: the lower Full source must now win.
        var (state, report) = await workspaceService.SetSourceModeAsync(refId, SourceMode.Reference);

        state.CuratedAssets[0].ResolvedOccurrence!.SourceId.Should().Be(fullId);
        report.ModeChanges.Should().Contain(refId);
        report.WinnerChanges.Should().Contain(identity);
        indexService.IndexCallCount.Should().Be(callsAfterInit,
            "changing mode re-resolves from existing snapshots and never re-indexes");
    }

    [Test]
    public async Task PinMany_ReplacesExistingPinsAndAddsNew_InOneResolve()
    {
        Guid id1 = Guid.NewGuid();
        Guid id2 = Guid.NewGuid();
        AssetSource s1 = new AssetSource(id1, AssetSourceKind.Hak, "c:/a.hak", 0);
        AssetSource s2 = new AssetSource(id2, AssetSourceKind.Hak, "c:/b.hak", 1);

        AssetIdentity alpha = new("alpha", 2000);
        AssetIdentity beta = new("beta", 2000);
        // Both identities live in both sources so a pin to either is a legitimate winner.
        AssetOccurrence a1 = new(alpha, id1, new HakEntryLocator(0), "alpha.tga", 100);
        AssetOccurrence a2 = new(alpha, id2, new HakEntryLocator(0), "alpha.tga", 100);
        AssetOccurrence b1 = new(beta, id1, new HakEntryLocator(1), "beta.tga", 100);
        AssetOccurrence b2 = new(beta, id2, new HakEntryLocator(1), "beta.tga", 100);

        MockIndexService indexService = new();
        indexService.IndexResults[id1] = CreateSnapshot(s1, a1, b1);
        indexService.IndexResults[id2] = CreateSnapshot(s2, a2, b2);

        AssetHashCache cache = new AssetHashCache();
        DummyHashService hasher = new();
        WorkspaceResolver resolver = new WorkspaceResolver(hasher, cache);
        WorkspaceService svc = new WorkspaceService(indexService, resolver, cache);
        await svc.InitializeAsync(new[] { s1, s2 });

        // Pre-pin alpha to s1, then batch: move alpha to s2 and add a beta pin to s2.
        byte[] alphaHashS1 = await hasher.ComputeSha256Async(s1, a1);
        await svc.PinAsync(new WinnerPin(alpha, id1, a1.Locator, alphaHashS1));

        byte[] alphaHashS2 = await hasher.ComputeSha256Async(s2, a2);
        byte[] betaHashS2 = await hasher.ComputeSha256Async(s2, b2);
        WorkspaceState state = await svc.PinManyAsync(new[]
        {
            new WinnerPin(alpha, id2, a2.Locator, alphaHashS2),
            new WinnerPin(beta, id2, b2.Locator, betaHashS2),
        });

        state.Pins.Should().HaveCount(2, "the alpha pin is replaced in place, the beta pin is added");
        state.CuratedAssets.Single(a => a.Identity.Equals(alpha)).ResolvedOccurrence!.SourceId.Should().Be(id2);
        state.CuratedAssets.Single(a => a.Identity.Equals(beta)).ResolvedOccurrence!.SourceId.Should().Be(id2);
    }

    [Test]
    public async Task SetSourceMode_OnReadOnlyWorkspace_Throws()
    {
        MockIndexService indexService = new();
        AssetHashCache cache = new AssetHashCache();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), cache);
        WorkspaceService workspaceService = new WorkspaceService(indexService, resolver, cache);

        Guid id = Guid.NewGuid();
        WorkspaceState readOnlyState = new WorkspaceState(
            sources: new[] { new AssetSource(id, AssetSourceKind.Hak, "c:/ro.hak", priorityOrdinal: 0) },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: Array.Empty<CuratedAsset>(),
            selectionState: SelectionState.IncludeAll(),
            pins: Array.Empty<WinnerPin>(),
            isReadOnly: true);
        await workspaceService.LoadProjectStateAsync(readOnlyState);

        Func<Task> act = async () => await workspaceService.SetSourceModeAsync(id, SourceMode.Hidden);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static void indexSnapshotFor(out MockIndexService indexService, params (AssetSource Source, AssetIdentity Identity)[] content)
    {
        indexService = new MockIndexService();
        foreach ((AssetSource source, AssetIdentity identity) in content)
        {
            AssetOccurrence occ = new AssetOccurrence(identity, source.Id, new HakEntryLocator(0), $"{identity.Resref}.tga", 100);
            indexService.IndexResults[source.Id] = CreateSnapshot(source, occ);
        }
    }

    [Test]
    public async Task Mutations_OnReadOnlyWorkspace_ShouldThrowInvalidOperationException()
    {
        MockIndexService indexService = new();
        AssetHashCache cache = new AssetHashCache();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), cache);
        WorkspaceService workspaceService = new WorkspaceService(indexService, resolver, cache);

        WorkspaceState readOnlyState = new WorkspaceState(
            sources: Array.Empty<AssetSource>(),
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: Array.Empty<CuratedAsset>(),
            selectionState: SelectionState.IncludeAll(),
            pins: Array.Empty<WinnerPin>(),
            isReadOnly: true);

        await workspaceService.LoadProjectStateAsync(readOnlyState);

        Func<Task> pin = async () => await workspaceService.PinAsync(new WinnerPin(new AssetIdentity("a", 1000), Guid.NewGuid(), new HakEntryLocator(0), new byte[32]));
        Func<Task> reorder = async () => await workspaceService.ReorderSourcesAsync(Array.Empty<Guid>());
        Func<Task> rescan = async () => await workspaceService.RescanAsync();
        Func<Task> select = async () => await workspaceService.UpdateSelectionAsync(SelectionState.IncludeAll());

        await pin.Should().ThrowAsync<InvalidOperationException>();
        await reorder.Should().ThrowAsync<InvalidOperationException>();
        await rescan.Should().ThrowAsync<InvalidOperationException>();
        await select.Should().ThrowAsync<InvalidOperationException>();
    }

    private static SourceIndexSnapshot CreateSnapshot(AssetSource source, params AssetOccurrence[] occurrences)
    {
        List<IndexedAssetRecord> records = occurrences.Select(o => new IndexedAssetRecord(o)).ToList();
        byte[] fpBytes = new byte[32];
        SourceFingerprint fp = new SourceFingerprint(source.Kind, 1, fpBytes);
        SourceScanStatistics stats = new SourceScanStatistics(1, 100, TimeSpan.FromMilliseconds(5));
        return new SourceIndexSnapshot(source, fp, records, Array.Empty<AssetDiagnosticRecord>(), isCacheHit: false, scanStatistics: stats);
    }
}
