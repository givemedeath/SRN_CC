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
/// The "Add available dependencies" flow: the confirmation dialog is presented through the host, and
/// both the confirm and the null-presenter (headless) paths reach a terminal outcome rather than
/// awaiting a task that never completes.
/// </summary>
[TestFixture]
public class AddDependenciesFlowTests
{
    private const ushort TgaType = 2000;

    [Test]
    public async Task AddDependencies_PresenterConfirms_AppliesClosureAndLogsConfirmation()
    {
        (MainWindowViewModel vm, _) = await BuildLoadedViewModelAsync();
        vm.AssetTable.SelectedRow = vm.AssetTable.FilteredRows.First();

        bool presenterInvoked = false;
        vm.ShowConfirmDependenciesDialogAsync = dlgVm =>
        {
            presenterInvoked = true;
            dlgVm.ConfirmSelectionCommand.Execute(null); // completes the dialog's task as confirmed
            return Task.CompletedTask;
        };

        await vm.AddAvailableDependenciesCommand.ExecuteAsync(null);

        presenterInvoked.Should().BeTrue("the host presenter must actually be shown");
        vm.OperationLog.Entries.Should().Contain(e => e.Message.Contains("User confirmed dependency closure"));
    }

    [Test]
    public async Task AddDependencies_NoPresenter_TreatsAsCanceled_WithoutHanging()
    {
        (MainWindowViewModel vm, _) = await BuildLoadedViewModelAsync();
        vm.AssetTable.SelectedRow = vm.AssetTable.FilteredRows.First();
        vm.ShowConfirmDependenciesDialogAsync = null;

        // Must return; a regression here would await a TaskCompletionSource that never completes.
        Task run = vm.AddAvailableDependenciesCommand.ExecuteAsync(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        run.IsCompletedSuccessfully.Should().BeTrue();
        vm.OperationLog.Entries.Should().Contain(e => e.Message.Contains("No dialog host available"));
        vm.OperationLog.Entries.Should().Contain(e => e.Message.Contains("User canceled dependency closure"));
    }

    private static async Task<(MainWindowViewModel Vm, IWorkspaceService Workspace)> BuildLoadedViewModelAsync()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = new(sourceId, AssetSourceKind.Hak, "c:/root.hak", 0);
        AssetIdentity root = new("root", TgaType); // TGA has no dependencies -> clean closure

        var index = new MockIndexService();
        index.Register(source, new AssetOccurrence(root, sourceId, new HakEntryLocator(0), "root.tga", 100));

        var cache = new AssetHashCache();
        var resolver = new WorkspaceResolver(new ConstantHashService(), cache);
        var workspace = new WorkspaceService(index, resolver, cache);
        await workspace.InitializeAsync(new[] { source });

        var registry = new ResourceTypeRegistry();
        var dispatcher = new FakeDispatcher();
        var vm = new MainWindowViewModel(
            workspace,
            new ProjectStore(index, resolver),
            new SettingsStore(),
            new UnusedBuildOrchestrator(),
            new NoopPublisher(),
            new PreviewEngine(dispatcher, Array.Empty<IPreviewProvider>()),
            registry,
            dispatcher);

        await vm.LoadWorkspaceStateAsync(workspace.CurrentState);
        return (vm, workspace);
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
            => Task.FromResult<Stream>(new MemoryStream(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0 }));
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
