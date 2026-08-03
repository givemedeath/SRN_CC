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

        public Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
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
