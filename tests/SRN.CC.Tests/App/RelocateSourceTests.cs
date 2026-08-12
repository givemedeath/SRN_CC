using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Build;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Records;
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
/// The source relocation affordance: reachable from the source stack, gated with the other
/// move/remove actions, and routed to the workspace service.
/// </summary>
[TestFixture]
public class RelocateSourceTests
{
    [Test]
    public void RelocateCommand_InvokesCallbackForSelectedSource_AndIsReadOnlyGated()
    {
        SourceItemViewModel? relocated = null;
        bool canMove = true;
        var stack = new SourceStackViewModel(
            onWorkspaceChanged: () => Task.CompletedTask,
            canMoveSources: () => canMove,
            onRelocateSource: item => { relocated = item; return Task.CompletedTask; });

        var source = AssetSource.CreateHak("c:/one.hak", 0);
        stack.UpdateSources(new[] { new SourceItemViewModel(source) });

        stack.RelocateSourceCommand.CanExecute(null).Should().BeFalse("nothing is selected yet");

        stack.SelectedSource = stack.Sources[0];
        stack.RelocateSourceCommand.CanExecute(null).Should().BeTrue();
        stack.RelocateSourceCommand.Execute(null);
        relocated.Should().NotBeNull();
        relocated!.Source.Id.Should().Be(source.Id);

        canMove = false;
        stack.RelocateSourceCommand.NotifyCanExecuteChanged();
        stack.RelocateSourceCommand.CanExecute(null).Should().BeFalse("relocation is disabled for read-only projects");
    }

    [Test]
    public async Task Relocate_ThroughTheShell_RepointsTheSourceAndLogsit()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = new(sourceId, AssetSourceKind.Hak, "c:/original.hak", 0);

        var index = new MockIndexService();
        var cache = new AssetHashCache();
        var resolver = new WorkspaceResolver(new ConstantHashService(), cache);
        var workspace = new WorkspaceService(index, resolver, cache);
        await workspace.InitializeAsync(new[] { source });

        var vm = new MainWindowViewModel(
            workspace,
            new ProjectStore(index, resolver),
            new SettingsStore(),
            new UnusedBuildOrchestrator(),
            new NoopPublisher(),
            new PreviewEngine(new FakeDispatcher(), Array.Empty<IPreviewProvider>()),
            new ResourceTypeRegistry(),
            new FakeDispatcher());
        await vm.LoadWorkspaceStateAsync(workspace.CurrentState);

        vm.SingleHakFilePickerAsync = () => Task.FromResult<string?>("c:/moved.hak");
        vm.SourceStack.SelectedSource = vm.SourceStack.Sources.Single();

        await vm.SourceStack.RelocateSourceCommand.ExecuteAsync(null);

        workspace.CurrentState.Sources.Single().FullPath.Should().Be(Path.GetFullPath("c:/moved.hak"));
        vm.OperationLog.Entries.Should().Contain(e => e.Message.Contains("Relocated source"));
    }

    private sealed class MockIndexService : IAssetIndexService
    {
        public Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var snap = new SourceIndexSnapshot(
                source,
                new SourceFingerprint(source.Kind, 1, new byte[32]),
                Array.Empty<IndexedAssetRecord>(),
                Array.Empty<AssetDiagnosticRecord>(),
                isCacheHit: false,
                scanStatistics: new SourceScanStatistics(0, 0, TimeSpan.Zero));
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
            => Task.FromResult<Stream>(new MemoryStream());
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
