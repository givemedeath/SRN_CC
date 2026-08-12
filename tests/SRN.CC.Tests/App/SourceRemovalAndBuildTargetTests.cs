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
using SRN.CC.Core.Project;
using SRN.CC.Core.Records;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Preview;

namespace SRN.CC.Tests.App;

/// <summary>
/// Two shell affordances that had no way to be reached: taking a source back out of the workspace,
/// and choosing where a build writes.
/// </summary>
/// <remarks>
/// Adding a source was one click and removing it was impossible short of hand-editing the project
/// file. Building wrote to a path the user never saw — derived from the project name or from
/// preferences — and silently overwrote whatever was there, which for a HAK is usually a live
/// override directory.
/// </remarks>
[TestFixture]
public class SourceRemovalAndBuildTargetTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_SourceRemoval_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------ removal

    [Test]
    public async Task RemovingTheSelectedSource_LeavesTheOthersRenumberedFromZero()
    {
        MainWindowViewModel vm = CreateViewModel(out _);
        await AddThreeSourcesAsync(vm);

        vm.SourceStack.SelectedSource = vm.SourceStack.Sources.Single(s => s.Title == "b.hak");
        await vm.SourceStack.RemoveSourceCommand.ExecuteAsync(null);

        vm.SourceStack.Sources.Select(s => s.Title).Should().Equal("a.hak", "c.hak");

        // Priority is positional. A gap left where the removed source was would make the next
        // reorder move a source into an ordinal another source already holds.
        vm.SourceStack.Sources.Select(s => s.PriorityOrdinal).Should().Equal(0, 1);
    }

    [Test]
    public async Task RemovingASource_DiscardsOnlyThePinsThatNamedIt()
    {
        var workspace = new RecordingWorkspaceService();
        MainWindowViewModel vm = CreateViewModel(out _, workspace);
        workspace.SeedPins(sources =>
        [
            new WinnerPin(
                new AssetIdentity("doomed", 2000),
                sources.Single(s => Path.GetFileName(s.FullPath) == "b.hak").Id,
                new HakEntryLocator(0),
                new byte[32]),
            new WinnerPin(
                new AssetIdentity("keeper", 2000),
                sources.Single(s => Path.GetFileName(s.FullPath) == "a.hak").Id,
                new HakEntryLocator(1),
                new byte[32]),
        ]);
        await AddThreeSourcesAsync(vm);

        vm.SourceStack.SelectedSource = vm.SourceStack.Sources.Single(s => s.Title == "b.hak");
        await vm.SourceStack.RemoveSourceCommand.ExecuteAsync(null);

        // A pin to a source the workspace no longer holds can never resolve and the user has no
        // control that clears it, so removal has to take it along.
        workspace.LastPins.Select(p => p.Identity.Resref).Should().Equal("keeper");
    }

    [Test]
    public async Task RemoveIsUnavailable_UntilASourceIsSelected()
    {
        MainWindowViewModel vm = CreateViewModel(out _);
        await AddThreeSourcesAsync(vm);

        vm.SourceStack.SelectedSource = null;
        vm.SourceStack.RemoveSourceCommand.CanExecute(null).Should().BeFalse();

        vm.SourceStack.SelectedSource = vm.SourceStack.Sources[0];
        vm.SourceStack.RemoveSourceCommand.CanExecute(null).Should().BeTrue();
    }

    [Test]
    public async Task AReadOnlyWorkspace_CannotHaveSourcesRemoved()
    {
        var workspace = new RecordingWorkspaceService();
        MainWindowViewModel vm = CreateViewModel(out _, workspace);
        await AddThreeSourcesAsync(vm);
        vm.SourceStack.SelectedSource = vm.SourceStack.Sources[0];

        workspace.MakeReadOnly();
        await vm.SourceStack.RescanSourcesCommand.ExecuteAsync(null);

        vm.SourceStack.RemoveSourceCommand.CanExecute(null).Should().BeFalse();
    }

    [Test]
    public async Task RemovingTheSelectedSource_ClearsTheSelection()
    {
        MainWindowViewModel vm = CreateViewModel(out _);
        await AddThreeSourcesAsync(vm);

        vm.SourceStack.SelectedSource = vm.SourceStack.Sources.Single(s => s.Title == "b.hak");
        await vm.SourceStack.RemoveSourceCommand.ExecuteAsync(null);

        // A reload rebuilds every item view model, so a selection held across one points at an
        // object no longer in the list: the remove button stays enabled over a source that is gone
        // and the move commands see IndexOf == -1 and silently do nothing.
        vm.SourceStack.SelectedSource.Should().BeNull();
        vm.SourceStack.RemoveSourceCommand.CanExecute(null).Should().BeFalse();
    }

    [Test]
    public async Task ASurvivingSelection_FollowsTheReloadToItsNewItem()
    {
        MainWindowViewModel vm = CreateViewModel(out _);
        await AddThreeSourcesAsync(vm);

        SourceItemViewModel before = vm.SourceStack.Sources.Single(s => s.Title == "a.hak");
        vm.SourceStack.SelectedSource = before;

        await vm.SourceStack.RescanSourcesCommand.ExecuteAsync(null);

        vm.SourceStack.SelectedSource.Should().NotBeNull();
        vm.SourceStack.SelectedSource!.Title.Should().Be("a.hak");
        vm.SourceStack.Sources.Should().Contain(vm.SourceStack.SelectedSource);
    }

    // -------------------------------------------------------------- build target

    [Test]
    public async Task BuildingAlwaysAsksWhereToWrite_AndUsesTheAnswer()
    {
        var orchestrator = new RecordingBuildOrchestrator();
        MainWindowViewModel vm = CreateViewModel(out _, buildOrchestrator: orchestrator);
        await vm.NewProjectCommand.ExecuteAsync(null);

        string chosen = Path.Combine(_tempDir, "chosen.hak");
        vm.BuildOutputFilePickerAsync = (_, _) => Task.FromResult<string?>(chosen);

        await vm.BuildHakCommand.ExecuteAsync(null);

        orchestrator.DestinationPath.Should().Be(chosen);
    }

    [Test]
    public async Task CancellingTheOutputPicker_CancelsTheBuild()
    {
        var orchestrator = new RecordingBuildOrchestrator();
        MainWindowViewModel vm = CreateViewModel(out _, buildOrchestrator: orchestrator);
        await vm.NewProjectCommand.ExecuteAsync(null);

        vm.BuildOutputFilePickerAsync = (_, _) => Task.FromResult<string?>(null);

        await vm.BuildHakCommand.ExecuteAsync(null);

        // Falling through to the derived default here would write a HAK the user just declined to
        // write, over whatever already sits at that path.
        orchestrator.DestinationPath.Should().BeNull();
    }

    [Test]
    public async Task TheOutputPicker_OpensOnTheDerivedDefault()
    {
        var orchestrator = new RecordingBuildOrchestrator();
        MainWindowViewModel vm = CreateViewModel(out _, buildOrchestrator: orchestrator);
        await vm.NewProjectCommand.ExecuteAsync(null);

        string? suggestedName = null;
        vm.BuildOutputFilePickerAsync = (name, _) =>
        {
            suggestedName = name;
            return Task.FromResult<string?>(null);
        };

        await vm.BuildHakCommand.ExecuteAsync(null);

        // The prompt is a confirmation of the path the build would have used, not a blank field.
        suggestedName.Should().Be("output.hak");
    }

    [Test]
    public async Task AThrowingOutputPicker_AbortsTheBuildInsteadOfTheProcess()
    {
        var orchestrator = new RecordingBuildOrchestrator();
        MainWindowViewModel vm = CreateViewModel(out _, buildOrchestrator: orchestrator);
        await vm.NewProjectCommand.ExecuteAsync(null);

        vm.BuildOutputFilePickerAsync = (_, _) => throw new IOException("the share is gone");

        // The picker call sits ahead of the try that wraps the build, and an async command handler
        // that throws takes the process down rather than reporting — the same failure mode as the
        // comparison panel's off-thread mutation.
        Func<Task> build = () => vm.BuildHakCommand.ExecuteAsync(null);

        await build.Should().NotThrowAsync();
        orchestrator.DestinationPath.Should().BeNull();
        vm.OperationLog.Entries.Should().Contain(e => e.Level == "ERROR");
    }

    // ------------------------------------------------------------------ setup

    private async Task AddThreeSourcesAsync(MainWindowViewModel vm)
    {
        await vm.NewProjectCommand.ExecuteAsync(null);

        vm.HakFilePickerAsync = () => Task.FromResult<IReadOnlyList<string>>(
        [
            Path.Combine(_tempDir, "a.hak"),
            Path.Combine(_tempDir, "b.hak"),
            Path.Combine(_tempDir, "c.hak"),
        ]);

        await vm.SourceStack.AddHakSourceCommand.ExecuteAsync(null);
        vm.SourceStack.Sources.Should().HaveCount(3);
    }

    private MainWindowViewModel CreateViewModel(
        out ISettingsStore createdStore,
        IWorkspaceService? workspaceService = null,
        IBuildOrchestrator? buildOrchestrator = null)
    {
        createdStore = new SettingsStore();
        var registry = new ResourceTypeRegistry();
        var dispatcher = new SourceReaderDispatcher(typeRegistry: registry);
        var resolver = new WorkspaceResolver(new ConstantHashService());
        var indexService = new EmptyIndexService();

        return new MainWindowViewModel(
            workspaceService ?? new RecordingWorkspaceService(),
            new ProjectStore(indexService, resolver),
            createdStore,
            buildOrchestrator ?? new UnusedBuildOrchestrator(),
            new NoopArtifactPublisher(),
            new PreviewEngine(dispatcher, Array.Empty<IPreviewProvider>()),
            registry,
            dispatcher,
            null,
            null,
            null,
            null,
            Path.Combine(_tempDir, "settings.json"));
    }

    // ------------------------------------------------------------------ fakes

    /// <summary>
    /// A workspace service that keeps whatever sources, pins and selection it is initialized with,
    /// so a test can assert what the view model asked for rather than what resolution made of it.
    /// </summary>
    private sealed class RecordingWorkspaceService : IWorkspaceService
    {
        private WorkspaceState _state = Empty(Array.Empty<AssetSource>(), Array.Empty<WinnerPin>());
        private bool _readOnly;
        private Func<IReadOnlyList<AssetSource>, IEnumerable<WinnerPin>>? _pinFactory;

        public WorkspaceState CurrentState => _state;

        public IReadOnlyList<WinnerPin> LastPins { get; private set; } = Array.Empty<WinnerPin>();

        /// <summary>
        /// Injects pins into whatever state the next <see cref="InitializeAsync"/> produces. Pins
        /// name a source by id, and ids are minted by the view model as it adds sources, so a test
        /// can only express "pin this to that source" as a function of the sources it is handed.
        /// </summary>
        public void SeedPins(Func<IReadOnlyList<AssetSource>, IEnumerable<WinnerPin>> factory)
            => _pinFactory = factory;

        public void MakeReadOnly()
        {
            _readOnly = true;
            _state = Empty(_state.Sources, _state.Pins, isReadOnly: true);
        }

        public Task<(WorkspaceState State, ChangedInputReport Report)> InitializeAsync(
            IReadOnlyList<AssetSource> sources, IReadOnlyList<WinnerPin>? pins = null,
            SelectionState? selectionState = null, ProjectPreferences? preferences = null,
            bool isReadOnly = false, CancellationToken cancellationToken = default)
        {
            LastPins = pins ?? Array.Empty<WinnerPin>();
            // Applied once, on the first initialize that actually has sources — the new-project call
            // that precedes them has nothing for a pin to point at.
            IReadOnlyList<WinnerPin> effective = LastPins;
            if (_pinFactory is not null && sources.Count > 0)
            {
                effective = LastPins.Concat(_pinFactory(sources)).ToList();
                _pinFactory = null;
            }

            _state = Empty(sources, effective, isReadOnly || _readOnly, preferences);
            return Task.FromResult((_state, new ChangedInputReport()));
        }

        public Task<(WorkspaceState State, ChangedInputReport Report)> ReorderSourcesAsync(
            IReadOnlyList<Guid> sourceIdsInOrder, CancellationToken cancellationToken = default)
            => Task.FromResult((_state, new ChangedInputReport()));

        public Task<(WorkspaceState State, ChangedInputReport Report)> RescanAsync(
            IEnumerable<Guid>? sourceIdsToRescan = null, CancellationToken cancellationToken = default)
            => Task.FromResult((_state, new ChangedInputReport()));

        public Task<(WorkspaceState State, ChangedInputReport Report)> RelocateSourceAsync(
            Guid sourceId, string newPath, CancellationToken cancellationToken = default)
            => Task.FromResult((_state, new ChangedInputReport()));

        public Task<(WorkspaceState State, ChangedInputReport Report)> SetSourceModeAsync(
            Guid sourceId, SourceMode mode, CancellationToken cancellationToken = default)
            => Task.FromResult((_state, new ChangedInputReport()));

        public Task<WorkspaceState> PinAsync(WinnerPin pin, CancellationToken cancellationToken = default)
            => Task.FromResult(_state);

        public Task<WorkspaceState> UnpinAsync(AssetIdentity identity, CancellationToken cancellationToken = default)
            => Task.FromResult(_state);

        public Task<WorkspaceState> UpdateSelectionAsync(SelectionState newSelectionState, CancellationToken cancellationToken = default)
            => Task.FromResult(_state);

        public Task<WorkspaceState> LoadProjectStateAsync(WorkspaceState newState, CancellationToken cancellationToken = default)
        {
            _state = newState;
            return Task.FromResult(_state);
        }

        private static WorkspaceState Empty(
            IReadOnlyList<AssetSource> sources,
            IReadOnlyList<WinnerPin> pins,
            bool isReadOnly = false,
            ProjectPreferences? preferences = null) => new(
                sources,
                sources.ToDictionary(
                    s => s.Id,
                    s => new SourceIndexSnapshot(
                        s,
                        new SourceFingerprint(s.Kind, 1, new byte[32]),
                        Array.Empty<IndexedAssetRecord>(),
                        Array.Empty<AssetDiagnosticRecord>(),
                        isCacheHit: false,
                        scanStatistics: new SourceScanStatistics(0, 0, TimeSpan.Zero))),
                Array.Empty<CuratedAsset>(),
                new SelectionState(),
                pins,
                preferences ?? new ProjectPreferences(),
                isReadOnly);
    }

    private sealed class RecordingBuildOrchestrator : IBuildOrchestrator
    {
        public string? DestinationPath { get; private set; }

        public Task<PublicationResult> ExecuteBuildAsync(
            WorkspaceState workspace, string destinationHakPath,
            IProgress<(string message, double progressFraction)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DestinationPath = destinationHakPath;
            return Task.FromResult(new PublicationResult(
                true, destinationHakPath, destinationHakPath + ".manifest.json", null, Array.Empty<string>()));
        }
    }

    private sealed class EmptyIndexService : IAssetIndexService
    {
        public Task<SourceIndexSnapshot> IndexAsync(
            AssetSource source,
            IProgress<IndexProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SourceIndexSnapshot(
                source,
                new SourceFingerprint(source.Kind, 1, new byte[32]),
                Array.Empty<IndexedAssetRecord>(),
                Array.Empty<AssetDiagnosticRecord>(),
                isCacheHit: false,
                scanStatistics: new SourceScanStatistics(0, 0, TimeSpan.Zero)));
    }

    private sealed class ConstantHashService : IStreamingHashService
    {
        public Task<byte[]> ComputeSha256Async(
            AssetSource source,
            AssetOccurrence occurrence,
            CancellationToken cancellationToken = default)
            => Task.FromResult(SHA256.HashData(Encoding.UTF8.GetBytes(occurrence.Locator.ToString() ?? string.Empty)));
    }

    private sealed class UnusedBuildOrchestrator : IBuildOrchestrator
    {
        public Task<PublicationResult> ExecuteBuildAsync(
            WorkspaceState workspace, string destinationHakPath,
            IProgress<(string message, double progressFraction)>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This test does not execute a build.");
    }

    private sealed class NoopArtifactPublisher : IArtifactPublisher
    {
        public Task<PublicationResult> PublishAsync(
            BuildPlan plan, string tempHakPath, string tempManifestPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This test does not publish.");

        public Task<bool> RecoverPendingJournalAsync(string journalDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
