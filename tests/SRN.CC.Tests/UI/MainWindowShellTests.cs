using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.App.Views;
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
using SRN.CC.Core.Settings;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Preview;

namespace SRN.CC.Tests.UI;

/// <summary>
/// Closes constraint 10: the shell exposes every command the plan names, and the read-only gate the
/// plan calls for really does disable the destructive ones.
/// </summary>
/// <remarks>
/// The toolbar assertions run headless against the real <c>MainWindow.axaml</c>, so a button that is
/// deleted, renamed, or bound to a command that does not exist fails here rather than at run time —
/// an unresolvable <c>{Binding}</c> in Avalonia is silently a null command, not an error.
/// </remarks>
[TestFixture]
public class MainWindowShellTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_MainWindowShellTests_" + Guid.NewGuid().ToString("N"));
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

    // ------------------------------------------------------------------ toolbar

    [AvaloniaTest]
    public void Toolbar_ExposesAllSevenShellCommands_BoundToTheViewModel()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };

        try
        {
            window.Show();
            window.UpdateLayout();

            AssertBound(window, "NewProjectButton", vm.NewProjectCommand);
            AssertBound(window, "OpenProjectButton", vm.OpenProjectCommand);
            AssertBound(window, "SaveProjectButton", vm.SaveProjectCommand);
            AssertBound(window, "SaveProjectAsButton", vm.SaveProjectAsCommand);
            AssertBound(window, "RescanButton", vm.RescanCommand);
            AssertBound(window, "BuildHakButton", vm.BuildHakCommand);
            AssertBound(window, "SettingsButton", vm.OpenSettingsCommand);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertBound(MainWindow window, string buttonName, ICommand expected)
    {
        Button? button = window.FindControl<Button>(buttonName);
        button.Should().NotBeNull($"the toolbar must contain a '{buttonName}' button");
        button!.Command.Should().BeSameAs(
            expected,
            $"'{buttonName}' must be bound to the view model command of the same name");
    }

    // ----------------------------------------------------- read-only command gate

    [Test]
    public async Task ASchemaVersion2Project_LeavesSaveAndBuildDisabled()
    {
        // PLAN.md:94's regression case, driven end to end: a project file from a newer build opens
        // read-only, and neither of the two commands that would write over it may run.
        string projectPath = Path.Combine(_tempDir, "v2.srnccproj");
        await File.WriteAllTextAsync(projectPath, """
        {
          "schemaVersion": 2,
          "sources": [],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": [],
          "futureOnlyField": "from a newer build"
        }
        """);

        MainWindowViewModel vm = CreateShellViewModel(out _);

        await vm.OpenProjectCommand.ExecuteAsync(projectPath);

        vm.SaveProjectCommand.CanExecute(null).Should()
            .BeFalse("a read-only project must not be savable in place");
        vm.BuildHakCommand.CanExecute(null).Should()
            .BeFalse("a read-only project must not be buildable");
        vm.SaveProjectAsCommand.CanExecute(null).Should()
            .BeFalse("Save As carries the same read-only gate as Save");
        vm.RescanCommand.CanExecute(null).Should()
            .BeFalse("rescanning would mutate a workspace that must not change");
    }

    [Test]
    public async Task AWritableProject_LeavesEveryStateChangingCommandEnabled()
    {
        // The positive half of the gate: without it, a permanently-false CanExecute would pass the
        // read-only test for the wrong reason.
        string projectPath = Path.Combine(_tempDir, "v1.srnccproj");
        await File.WriteAllTextAsync(projectPath, """
        {
          "schemaVersion": 1,
          "sources": [],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": []
        }
        """);

        MainWindowViewModel vm = CreateShellViewModel(out _);

        await vm.OpenProjectCommand.ExecuteAsync(projectPath);

        vm.SaveProjectCommand.CanExecute(null).Should().BeTrue();
        vm.SaveProjectAsCommand.CanExecute(null).Should().BeTrue();
        vm.RescanCommand.CanExecute(null).Should().BeTrue();
        vm.BuildHakCommand.CanExecute(null).Should().BeTrue();
    }

    // ---------------------------------------------------------------- save as

    [Test]
    public async Task SaveProjectAsCommand_WritesTheNewPath_AndRetargetsTheSession()
    {
        string originalPath = Path.Combine(_tempDir, "original.srnccproj");
        string copyPath = Path.Combine(_tempDir, "copy.srnccproj");

        MainWindowViewModel vm = CreateShellViewModel(out _);
        await vm.NewProjectCommand.ExecuteAsync(null);
        await vm.SaveProjectAsCommand.ExecuteAsync(originalPath);

        File.Exists(originalPath).Should().BeTrue();

        await vm.SaveProjectAsCommand.ExecuteAsync(copyPath);

        File.Exists(copyPath).Should().BeTrue("Save As writes the chosen destination");
        File.Exists(originalPath).Should().BeTrue("Save As does not move or delete the original");
        vm.Title.Should().Contain("copy.srnccproj", "the session continues in the new file");
        vm.RecentProjectPaths.Should().Contain(Path.GetFullPath(copyPath));
    }

    [Test]
    public async Task SaveProjectAsCommand_WithNoDestinationAndNoPicker_WritesNothing()
    {
        MainWindowViewModel vm = CreateShellViewModel(out _);
        await vm.NewProjectCommand.ExecuteAsync(null);

        await vm.SaveProjectAsCommand.ExecuteAsync(null);

        Directory.EnumerateFiles(_tempDir, "*.srnccproj").Should()
            .BeEmpty("Save As with no destination is a cancellation, not a write to an unnamed file");
        vm.OperationLog.Entries.Should().Contain(e => e.Message.Contains("Save As canceled"));
    }

    [Test]
    public async Task SaveProjectAsCommand_UsesThePicker_WhenNoDestinationIsSupplied()
    {
        string pickedPath = Path.Combine(_tempDir, "picked.srnccproj");

        MainWindowViewModel vm = CreateShellViewModel(out _);
        vm.SaveProjectFilePickerAsync = () => Task.FromResult<string?>(pickedPath);

        await vm.NewProjectCommand.ExecuteAsync(null);
        await vm.SaveProjectAsCommand.ExecuteAsync(null);

        File.Exists(pickedPath).Should().BeTrue();
    }

    // ----------------------------------------------------------------- rescan

    [Test]
    public async Task RescanCommand_SurfacesTheChangedInputReport()
    {
        MainWindowViewModel vm = CreateShellViewModel(out _);
        await vm.NewProjectCommand.ExecuteAsync(null);

        await vm.RescanCommand.ExecuteAsync(null);

        vm.OperationLog.Entries.Should().Contain(
            e => e.Message.Contains("Rescan complete"),
            "the rescan path produced a changed-input report and used to discard it");
    }

    [Test]
    public async Task RescanCommand_ReportsEachChangedInputCategory()
    {
        // A stub workspace service is the only way to present a report with populated categories
        // without standing up real sources on disk; the assertion is about what the shell renders.
        var identity = new AssetIdentity("c_rat", 2033);
        var report = new ChangedInputReport(
            fingerprintChanges: new[] { Guid.NewGuid() },
            winnerChanges: new[] { identity },
            pinInvalidations: new[] { identity });

        var workspaceService = new ReportingWorkspaceService(report);
        MainWindowViewModel vm = CreateShellViewModel(out _, workspaceService);

        await vm.NewProjectCommand.ExecuteAsync(null);
        await vm.RescanCommand.ExecuteAsync(null);

        vm.OperationLog.Entries.Should().Contain(e => e.Message.Contains("source fingerprint changes"));
        vm.OperationLog.Entries.Should().Contain(e => e.Message.Contains("winner changes"));
        vm.OperationLog.Entries.Should().Contain(
            e => e.Level == "WARN" && e.Message.Contains("pins invalidated"),
            "a silently invalidated pin is the change an operator most needs to see");
    }

    // --------------------------------------------------------------- settings

    [Test]
    public async Task OpenSettingsCommand_PublishesADialogOverTheInjectedStore()
    {
        MainWindowViewModel vm = CreateShellViewModel(out ISettingsStore store);

        await vm.OpenSettingsCommand.ExecuteAsync(null);

        vm.SettingsDialog.Should().NotBeNull();
        vm.SettingsDialog!.IsDialogOpen.Should().BeTrue();
        vm.SettingsDialog.IsReadOnly.Should().BeFalse();
        store.Should().NotBeNull();
    }

    [Test]
    public async Task OpenSettingsCommand_AdoptsTheSavedOverride_IntoTheRunningSession()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        string installRoot = Path.Combine(_tempDir, "game");
        var store = new SettingsStore();

        MainWindowViewModel vm = CreateShellViewModel(out _, settingsStore: store, settingsPath: settingsPath);

        vm.ShowSettingsDialogAsync = async dialog =>
        {
            dialog.NwnInstallOverride = installRoot;
            await dialog.SaveCommand.ExecuteAsync(null);
        };

        await vm.OpenSettingsCommand.ExecuteAsync(null);

        vm.NwnInstallOverride.Should().Be(installRoot, "the shell adopts what the dialog wrote");
        (await store.LoadAsync(settingsPath)).Settings.NwnInstallOverride.Should().Be(installRoot);
    }

    [Test]
    public async Task OpenSettingsCommand_WithReadOnlySettings_ProducesADisabledSave()
    {
        MainWindowViewModel vm = CreateShellViewModel(
            out _,
            initialSettings: new ApplicationSettings(isReadOnly: true));

        await vm.OpenSettingsCommand.ExecuteAsync(null);

        vm.SettingsDialog!.IsReadOnly.Should().BeTrue();
        vm.SettingsDialog.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.SettingsDialog.ReadOnlyReason.Should().NotBeNullOrWhiteSpace();
    }

    // ------------------------------------------------------------------ setup

    private MainWindowViewModel CreateShellViewModel(
        out ISettingsStore createdStore,
        IWorkspaceService? workspaceService = null,
        ISettingsStore? settingsStore = null,
        ApplicationSettings? initialSettings = null,
        string? settingsPath = null)
    {
        createdStore = settingsStore ?? new SettingsStore();
        var registry = new ResourceTypeRegistry();
        var dispatcher = new SourceReaderDispatcher(typeRegistry: registry);
        var resolver = new WorkspaceResolver(new ConstantHashService());
        var indexService = new EmptyIndexService();

        return new MainWindowViewModel(
            workspaceService ?? new WorkspaceService(indexService, resolver),
            new ProjectStore(indexService, resolver),
            createdStore,
            new UnusedBuildOrchestrator(),
            new NoopArtifactPublisher(),
            new PreviewEngine(dispatcher, Array.Empty<IPreviewProvider>()),
            registry,
            dispatcher,
            null,
            null,
            null,
            initialSettings,
            settingsPath ?? Path.Combine(_tempDir, "settings.json"));
    }

    // ------------------------------------------------------------------ fakes

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

    /// <summary>A workspace service whose rescan returns a report with every interesting category set.</summary>
    private sealed class ReportingWorkspaceService : IWorkspaceService
    {
        private readonly ChangedInputReport _rescanReport;
        private WorkspaceState _state = Empty();

        public ReportingWorkspaceService(ChangedInputReport rescanReport) => _rescanReport = rescanReport;

        public WorkspaceState CurrentState => _state;

        public Task<(WorkspaceState State, ChangedInputReport Report)> InitializeAsync(
            IReadOnlyList<AssetSource> sources, IReadOnlyList<WinnerPin>? pins = null,
            SelectionState? selectionState = null, ProjectPreferences? preferences = null,
            bool isReadOnly = false, CancellationToken cancellationToken = default)
        {
            _state = Empty(isReadOnly);
            return Task.FromResult((_state, new ChangedInputReport()));
        }

        public Task<(WorkspaceState State, ChangedInputReport Report)> ReorderSourcesAsync(
            IReadOnlyList<Guid> sourceIdsInOrder, CancellationToken cancellationToken = default)
            => Task.FromResult((_state, new ChangedInputReport()));

        public Task<(WorkspaceState State, ChangedInputReport Report)> RescanAsync(
            IEnumerable<Guid>? sourceIdsToRescan = null, CancellationToken cancellationToken = default)
            => Task.FromResult((_state, _rescanReport));

        public Task<(WorkspaceState State, ChangedInputReport Report)> RelocateSourceAsync(
            Guid sourceId, string newPath, CancellationToken cancellationToken = default)
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

        private static WorkspaceState Empty(bool isReadOnly = false) => new(
            Array.Empty<AssetSource>(),
            new Dictionary<Guid, SourceIndexSnapshot>(),
            Array.Empty<CuratedAsset>(),
            new SelectionState(),
            Array.Empty<WinnerPin>(),
            new ProjectPreferences(),
            isReadOnly);
    }

    private sealed class UnusedBuildOrchestrator : IBuildOrchestrator
    {
        public Task<PublicationResult> ExecuteBuildAsync(
            WorkspaceState workspace, string destinationHakPath,
            IProgress<(string message, double progressFraction)>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("No test in this fixture executes a build.");
    }

    private sealed class NoopArtifactPublisher : IArtifactPublisher
    {
        public Task<PublicationResult> PublishAsync(
            BuildPlan plan, string tempHakPath, string tempManifestPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("No test in this fixture publishes.");

        public Task<bool> RecoverPendingJournalAsync(string journalDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
