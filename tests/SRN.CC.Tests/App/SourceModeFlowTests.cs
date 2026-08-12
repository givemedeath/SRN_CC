using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Build;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Records;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Preview;

namespace SRN.CC.Tests.App;

/// <summary>
/// The source-mode picker flow: a mode change is tracked so Save/Build cannot consume a pre-change
/// snapshot, and a rapid change-and-revert applies the last edit rather than dropping it.
/// </summary>
[TestFixture]
public class SourceModeFlowTests
{
    private const ushort TgaType = 2000;
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "srncc-modeflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Save_AwaitsPendingModeChange_PersistsNewModeNotStaleSnapshot()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = new(sourceId, AssetSourceKind.Hak, Path.Combine(_tempDir, "one.hak"), 0);

        var index = new MockIndexService();
        index.Register(source, Occ(new AssetIdentity("alpha", TgaType), sourceId, 0));

        var cache = new AssetHashCache();
        var resolver = new WorkspaceResolver(new ConstantHashService(), cache);
        var real = new WorkspaceService(index, resolver, cache);
        await real.InitializeAsync(new[] { source });

        // Gate the mode operation so it is genuinely in flight when Save runs.
        var gated = new GatedWorkspaceService(real);
        string projectPath = Path.Combine(_tempDir, "p.srnccproj");
        var store = new ProjectStore(index, resolver);
        MainWindowViewModel vm = BuildViewModel(gated, store);
        await vm.LoadWorkspaceStateAsync(gated.CurrentState, projectPath);

        // Begin a mode change (blocks on the gate) then immediately request a save.
        vm.SourceStack.Sources.Single().Mode = SourceMode.Reference;
        Task saveTask = vm.SaveProjectCommand.ExecuteAsync(null);

        saveTask.IsCompleted.Should().BeFalse("Save must wait for the in-flight mode change, not snapshot the old mode");

        gated.ReleaseMode();
        await saveTask;

        WorkspaceState reloaded = await store.LoadAsync(projectPath);
        reloaded.Sources.Single().Mode.Should().Be(SourceMode.Reference, "the persisted mode is the changed one");
    }

    [Test]
    public async Task RapidModeRevert_AppliesLastEdit_NotTheFirst()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = new(sourceId, AssetSourceKind.Hak, Path.Combine(_tempDir, "one.hak"), 0);

        var index = new MockIndexService();
        index.Register(source, Occ(new AssetIdentity("alpha", TgaType), sourceId, 0));

        var cache = new AssetHashCache();
        var resolver = new WorkspaceResolver(new ConstantHashService(), cache);
        var real = new WorkspaceService(index, resolver, cache);
        await real.InitializeAsync(new[] { source });

        string projectPath = Path.Combine(_tempDir, "p2.srnccproj");
        MainWindowViewModel vm = BuildViewModel(real, new ProjectStore(index, resolver));
        await vm.LoadWorkspaceStateAsync(real.CurrentState, projectPath);

        // Full -> Reference -> Full before the first re-resolution can round-trip. The revert must win.
        SourceItemViewModel item = vm.SourceStack.Sources.Single();
        item.Mode = SourceMode.Reference;
        item.Mode = SourceMode.Full;

        // Save drains both mode tasks (it awaits the tracked pending update).
        await vm.SaveProjectCommand.ExecuteAsync(null);

        real.CurrentState.Sources.Single().Mode.Should().Be(SourceMode.Full,
            "the latest edit (revert to Full) must win, not the earlier Reference");
    }

    private static MainWindowViewModel BuildViewModel(IWorkspaceService workspace, ProjectStore store)
    {
        var registry = new ResourceTypeRegistry();
        var dispatcher = new FakeDispatcher();
        return new MainWindowViewModel(
            workspace,
            store,
            new SettingsStore(),
            new UnusedBuildOrchestrator(),
            new NoopPublisher(),
            new PreviewEngine(dispatcher, Array.Empty<IPreviewProvider>()),
            registry,
            dispatcher);
    }

    private static AssetOccurrence Occ(AssetIdentity id, Guid sourceId, int entry) =>
        new(id, sourceId, new HakEntryLocator(entry), $"{id.Resref}.tga", 100);

    /// <summary>Delegates to a real workspace but blocks SetSourceModeAsync until released.</summary>
    private sealed class GatedWorkspaceService : IWorkspaceService
    {
        private readonly IWorkspaceService _inner;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedWorkspaceService(IWorkspaceService inner) => _inner = inner;

        public void ReleaseMode() => _gate.TrySetResult();

        public WorkspaceState CurrentState => _inner.CurrentState;

        public async Task<(WorkspaceState State, ChangedInputReport Report)> SetSourceModeAsync(Guid sourceId, SourceMode mode, CancellationToken cancellationToken = default)
        {
            await _gate.Task.ConfigureAwait(false);
            return await _inner.SetSourceModeAsync(sourceId, mode, cancellationToken).ConfigureAwait(false);
        }

        public Task<(WorkspaceState State, ChangedInputReport Report)> InitializeAsync(IReadOnlyList<AssetSource> sources, IReadOnlyList<WinnerPin>? pins = null, SelectionState? selectionState = null, ProjectPreferences? preferences = null, bool isReadOnly = false, CancellationToken cancellationToken = default)
            => _inner.InitializeAsync(sources, pins, selectionState, preferences, isReadOnly, cancellationToken);
        public Task<(WorkspaceState State, ChangedInputReport Report)> ReorderSourcesAsync(IReadOnlyList<Guid> sourceIdsInOrder, CancellationToken cancellationToken = default)
            => _inner.ReorderSourcesAsync(sourceIdsInOrder, cancellationToken);
        public Task<(WorkspaceState State, ChangedInputReport Report)> RescanAsync(IEnumerable<Guid>? sourceIdsToRescan = null, CancellationToken cancellationToken = default)
            => _inner.RescanAsync(sourceIdsToRescan, cancellationToken);
        public Task<(WorkspaceState State, ChangedInputReport Report)> RelocateSourceAsync(Guid sourceId, string newPath, CancellationToken cancellationToken = default)
            => _inner.RelocateSourceAsync(sourceId, newPath, cancellationToken);
        public Task<WorkspaceState> PinAsync(WinnerPin pin, CancellationToken cancellationToken = default)
            => _inner.PinAsync(pin, cancellationToken);
        public Task<WorkspaceState> PinManyAsync(IReadOnlyList<WinnerPin> pins, CancellationToken cancellationToken = default)
            => _inner.PinManyAsync(pins, cancellationToken);
        public Task<WorkspaceState> UnpinAsync(AssetIdentity identity, CancellationToken cancellationToken = default)
            => _inner.UnpinAsync(identity, cancellationToken);
        public Task<WorkspaceState> UpdateSelectionAsync(SelectionState newSelectionState, CancellationToken cancellationToken = default)
            => _inner.UpdateSelectionAsync(newSelectionState, cancellationToken);
        public Task<WorkspaceState> LoadProjectStateAsync(WorkspaceState newState, CancellationToken cancellationToken = default)
            => _inner.LoadProjectStateAsync(newState, cancellationToken);
    }

    private sealed class MockIndexService : IAssetIndexService
    {
        private readonly Dictionary<Guid, (AssetSource Source, AssetOccurrence[] Occurrences)> _byId = new();
        public void Register(AssetSource source, params AssetOccurrence[] occurrences) => _byId[source.Id] = (source, occurrences);

        public Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            AssetOccurrence[] occ = _byId.TryGetValue(source.Id, out var e) ? e.Occurrences : Array.Empty<AssetOccurrence>();
            var snap = new SourceIndexSnapshot(
                source,
                new SourceFingerprint(source.Kind, 1, new byte[32]),
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
