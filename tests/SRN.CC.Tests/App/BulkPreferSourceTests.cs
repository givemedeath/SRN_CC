using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Build;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Records;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Preview;

namespace SRN.CC.Tests.App;

/// <summary>
/// Bulk "prefer source": pinning the winner from a chosen source across every conflicted identity,
/// with honest counts for identities absent from that source.
/// </summary>
[TestFixture]
public class BulkPreferSourceTests
{
    private const ushort TgaType = 2000;

    [Test]
    public async Task BulkPreferSource_PinsConflictsInSource_AndCountsAbsentOnes()
    {
        Guid s1 = Guid.NewGuid();
        Guid s2 = Guid.NewGuid();
        Guid s3 = Guid.NewGuid();
        AssetSource src1 = new(s1, AssetSourceKind.Hak, "c:/one.hak", 0);
        AssetSource src2 = new(s2, AssetSourceKind.Hak, "c:/two.hak", 1);
        AssetSource src3 = new(s3, AssetSourceKind.Hak, "c:/three.hak", 2);

        AssetIdentity alpha = new("alpha", TgaType);
        AssetIdentity beta = new("beta", TgaType);

        // alpha collides across s1/s2; beta collides across s1/s3 (absent from the preferred s2).
        var index = new MockIndexService();
        index.Register(src1, Occ(alpha, s1, 0), Occ(beta, s1, 0));
        index.Register(src2, Occ(alpha, s2, 0));
        index.Register(src3, Occ(beta, s3, 0));

        var cache = new AssetHashCache();
        var resolver = new WorkspaceResolver(new ConstantHashService(), cache);
        var workspace = new WorkspaceService(index, resolver, cache);
        await workspace.InitializeAsync(new[] { src1, src2, src3 });

        MainWindowViewModel vm = BuildViewModel(workspace, index, resolver);
        await vm.LoadWorkspaceStateAsync(workspace.CurrentState);

        await vm.BulkPreferSourceAsync(s2);

        vm.OperationLog.Entries.Should().Contain(e =>
            e.Message.Contains("1 pinned") &&
            e.Message.Contains("1 skipped (no occurrence)") &&
            e.Message.Contains("0 skipped (ambiguous"));

        CuratedAsset alphaAsset = workspace.CurrentState.CuratedAssets.Single(a => a.Identity.Equals(alpha));
        alphaAsset.ResolvedOccurrence!.SourceId.Should().Be(s2, "alpha was preferred to source two");
        alphaAsset.Pin.Should().NotBeNull();
    }

    [Test]
    public async Task MakeWinner_WhenPayloadUnreadable_DoesNotPin_AndLogsWarning()
    {
        Guid s1 = Guid.NewGuid();
        Guid s2 = Guid.NewGuid();
        AssetSource src1 = new(s1, AssetSourceKind.Hak, "c:/one.hak", 0);
        AssetSource src2 = new(s2, AssetSourceKind.Hak, "c:/two.hak", 1);

        AssetIdentity alpha = new("alpha", TgaType);

        // alpha collides across s1/s2 so it enters the conflict queue.
        var index = new MockIndexService();
        index.Register(src1, Occ(alpha, s1, 0));
        index.Register(src2, Occ(alpha, s2, 0));

        var cache = new AssetHashCache();
        var resolver = new WorkspaceResolver(new ConstantHashService(), cache);
        var workspace = new WorkspaceService(index, resolver, cache);
        await workspace.InitializeAsync(new[] { src1, src2 });

        // The dispatcher fails to open any payload, so hashing the chosen occurrence returns null.
        MainWindowViewModel vm = BuildViewModel(workspace, index, resolver, new ThrowingDispatcher());
        await vm.LoadWorkspaceStateAsync(workspace.CurrentState);

        ConflictCandidateViewModel candidate = vm.ConflictQueue.Current!.Candidates.First(c => c.Occurrence.SourceId == s2);
        await candidate.MakeWinnerCommand.ExecuteAsync(null);

        CuratedAsset alphaAsset = workspace.CurrentState.CuratedAssets.Single(a => a.Identity.Equals(alpha));
        alphaAsset.Pin.Should().BeNull("an unreadable payload must not be persisted as an all-zero pin");
        vm.OperationLog.Entries.Should().Contain(e => e.Message.Contains("Could not pin"),
            "the read failure is reported instead of a false success");
    }

    private static MainWindowViewModel BuildViewModel(IWorkspaceService workspace, IAssetIndexService index, WorkspaceResolver resolver, ISourceReaderDispatcher? dispatcher = null)
    {
        var registry = new ResourceTypeRegistry();
        dispatcher ??= new FakeDispatcher();
        return new MainWindowViewModel(
            workspace,
            new ProjectStore(index, resolver),
            new SettingsStore(),
            new UnusedBuildOrchestrator(),
            new NoopPublisher(),
            new PreviewEngine(dispatcher, Array.Empty<IPreviewProvider>()),
            registry,
            dispatcher);
    }

    private static AssetOccurrence Occ(AssetIdentity id, Guid sourceId, int entry) =>
        new(id, sourceId, new HakEntryLocator(entry), $"{id.Resref}.tga", 100);

    private sealed class MockIndexService : IAssetIndexService
    {
        private readonly Dictionary<Guid, (AssetSource Source, AssetOccurrence[] Occurrences)> _byId = new();

        public void Register(AssetSource source, params AssetOccurrence[] occurrences) =>
            _byId[source.Id] = (source, occurrences);

        public Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            AssetOccurrence[] occ = _byId.TryGetValue(source.Id, out var e) ? e.Occurrences : Array.Empty<AssetOccurrence>();
            byte[] digest = new byte[32];
            var snap = new SourceIndexSnapshot(
                source,
                new SourceFingerprint(source.Kind, 1, digest),
                occ.Select(o => new IndexedAssetRecord(o)).ToList(),
                Array.Empty<AssetDiagnosticRecord>(),
                isCacheHit: false,
                scanStatistics: new SourceScanStatistics(occ.Length, 0, TimeSpan.Zero));
            return Task.FromResult(snap);
        }
    }

    private sealed class ConstantHashService : IStreamingHashService
    {
        public Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default) =>
            Task.FromResult(SHA256.HashData(Encoding.UTF8.GetBytes($"{source.Id}:{occurrence.Locator}")));
    }

    private sealed class FakeDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes($"{source.Id}:{occurrence.Locator}")));
    }

    private sealed class ThrowingDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
            => throw new IOException("payload unreadable");
    }

    private sealed class UnusedBuildOrchestrator : IBuildOrchestrator
    {
        public Task<PublicationResult> ExecuteBuildAsync(WorkspaceState workspace, string destinationHakPath, IProgress<(string message, double progressFraction)>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NoopPublisher : IArtifactPublisher
    {
        public Task<PublicationResult> PublishAsync(BuildPlan plan, string tempHakPath, string tempManifestPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> RecoverPendingJournalAsync(string journalDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
