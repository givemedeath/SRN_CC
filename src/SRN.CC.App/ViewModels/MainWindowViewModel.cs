using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.App.Services;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Settings;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Startup;
using SRN.CC.Core.Workspace;
using SRN.CC.Preview;

namespace SRN.CC.App.ViewModels;

public record AssetRowItem(int Id, string Resref, string ResourceType, string SourceHak, long SizeBytes);

public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// Extension of a project file, per the plan. The application previously wrote and filtered on
    /// <c>.srncc</c> in five places while every test, every fixture, and the startup journal check
    /// used <c>.srnccproj</c> — see <c>ProjectExtensionTests</c>, which pins the two together.
    /// </summary>
    public const string ProjectFileExtension = ".srnccproj";

    /// <summary>Default project file name used when no path is known and no picker is wired.</summary>
    public const string DefaultProjectFileName = "project" + ProjectFileExtension;

    /// <summary>Ceiling on the persisted recent-project list.</summary>
    public const int MaxRecentProjectPaths = 10;

    /// <summary>Log category for records this view model emits directly.</summary>
    public const string LogCategory = nameof(MainWindowViewModel);

    private readonly IWorkspaceService? _workspaceService;
    private readonly IProjectStore? _projectStore;
    private readonly ISettingsStore? _settingsStore;
    private readonly IBuildOrchestrator? _buildOrchestrator;
    private readonly IArtifactPublisher? _artifactPublisher;
    private readonly PreviewEngine? _previewEngine;
    private readonly IResourceTypeRegistry? _registry;
    private readonly ISourceReaderDispatcher? _dispatcher;
    private readonly TextureSourceHolder? _textureSourceHolder;
    private readonly IBaseGameResourceCatalog? _baseGameCatalog;
    private readonly IAppLogger _logger;
    private readonly string? _settingsPath;

    private WorkspaceState? _workspaceState;
    private string? _currentProjectPath;
    private Task _pendingSelectionUpdate = Task.CompletedTask;
    private ApplicationSettings _settings = new();

    [ObservableProperty]
    private string _title = "SRN.CC Asset Curator";

    /// <summary>
    /// The project reopened by default when no path and no picker are available. Sourced from
    /// persisted settings and rewritten on every successful open or save.
    /// </summary>
    [ObservableProperty]
    private string? _lastProjectPath;

    /// <summary>
    /// The configured game install root, or null to auto-discover. Surfaced read-only here; the
    /// value is consumed during composition to build the base-game resource catalog.
    /// </summary>
    [ObservableProperty]
    private string? _nwnInstallOverride;

    /// <summary>
    /// The settings dialog most recently opened by <see cref="OpenSettingsCommand"/>, or null before
    /// one has been. Exposed so a host — or a headless test — can drive the dialog it created.
    /// </summary>
    [ObservableProperty]
    private SettingsDialogViewModel? _settingsDialog;

    /// <summary>
    /// Most-recently-opened projects, newest first, capped at <see cref="MaxRecentProjectPaths"/>.
    /// Mirrors the persisted settings list.
    /// </summary>
    public ObservableCollection<string> RecentProjectPaths { get; } = new();

    public SourceStackViewModel SourceStack { get; }
    public AssetTableViewModel AssetTable { get; }
    public ConflictQueueViewModel ConflictQueue { get; }
    public ComparisonPanelViewModel ComparisonPanel { get; }
    public OperationLogViewModel OperationLog { get; }
    public StatusBarViewModel StatusBar { get; }

    public ObservableCollection<AssetRowViewModel> Items => AssetTable.FilteredRows;

    /// <summary>
    /// The <see cref="ITextureSource"/> for the currently loaded workspace, or null before any
    /// workspace has loaded (including the designer-preview constructor, which never receives a
    /// <see cref="TextureSourceHolder"/>). Mirrors <see cref="TextureSourceHolder.Current"/> —
    /// exposed here mainly so tests can observe the wiring without reaching into App.axaml.cs.
    /// </summary>
    public ITextureSource? CurrentTextureSource => _textureSourceHolder?.Current;

    // Parameterless constructor for XAML designer preview & synthetic performance probe
    public MainWindowViewModel()
    {
        _logger = NullAppLogger.Instance;
        OperationLog = new OperationLogViewModel();
        StatusBar = new StatusBarViewModel();
        SourceStack = new SourceStackViewModel(
            OnWorkspaceChangedAsync,
            AddHakSourceAsync,
            AddFolderSourceAsync,
            RescanSourcesAsync,
            CanMoveSources,
            RemoveSourceAsync);
        _registry = new FallbackResourceTypeRegistry();
        AssetTable = new AssetTableViewModel(
            _registry,
            OnRowSelectionChanged,
            OnBatchSelectionChanged,
            OnSelectedRowChanged,
            OnSelectedRowsChanged,
            OnSearchTextChanged,
            OnSelectedFilterModeChanged);
        ConflictQueue = new ConflictQueueViewModel(OnPinRequestedAsync, ResourceTypeNameFor, BulkPreferSourceAsync);
        ComparisonPanel = new ComparisonPanelViewModel(
            new PreviewEngine(new FallbackSourceReaderDispatcher(), new IPreviewProvider[] { }),
            OnPinRequestedAsync);

        GenerateSyntheticDemoData();
    }

    public MainWindowViewModel(
        IWorkspaceService workspaceService,
        IProjectStore projectStore,
        ISettingsStore settingsStore,
        IBuildOrchestrator buildOrchestrator,
        IArtifactPublisher artifactPublisher,
        PreviewEngine previewEngine,
        IResourceTypeRegistry registry,
        ISourceReaderDispatcher dispatcher,
        TextureSourceHolder? textureSourceHolder = null,
        IBaseGameResourceCatalog? baseGameCatalog = null,
        IAppLogger? logger = null,
        ApplicationSettings? initialSettings = null,
        string? settingsPath = null)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _buildOrchestrator = buildOrchestrator ?? throw new ArgumentNullException(nameof(buildOrchestrator));
        _artifactPublisher = artifactPublisher ?? throw new ArgumentNullException(nameof(artifactPublisher));
        _previewEngine = previewEngine ?? throw new ArgumentNullException(nameof(previewEngine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _textureSourceHolder = textureSourceHolder;
        _baseGameCatalog = baseGameCatalog;
        _logger = logger ?? NullAppLogger.Instance;
        _settingsPath = settingsPath;

        OperationLog = new OperationLogViewModel();
        StatusBar = new StatusBarViewModel();
        SourceStack = new SourceStackViewModel(
            OnWorkspaceChangedAsync,
            AddHakSourceAsync,
            AddFolderSourceAsync,
            RescanSourcesAsync,
            CanMoveSources,
            RemoveSourceAsync);
        AssetTable = new AssetTableViewModel(
            _registry,
            OnRowSelectionChanged,
            OnBatchSelectionChanged,
            OnSelectedRowChanged,
            OnSelectedRowsChanged,
            OnSearchTextChanged,
            OnSelectedFilterModeChanged);
        ConflictQueue = new ConflictQueueViewModel(OnPinRequestedAsync, ResourceTypeNameFor, BulkPreferSourceAsync);

        ComparisonPanel = new ComparisonPanelViewModel(
            _previewEngine,
            OnPinRequestedAsync);

        ApplySettings(initialSettings ?? new ApplicationSettings());
    }

    /// <summary>
    /// Adopts a loaded settings document: the last project, the recent-project list, and the
    /// configured install override all become live state. Before milestone 7 <c>ISettingsStore</c>
    /// was injected and never read.
    /// </summary>
    public void ApplySettings(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        LastProjectPath = settings.LastProjectPath;
        NwnInstallOverride = settings.NwnInstallOverride;

        RecentProjectPaths.Clear();
        foreach (string path in settings.RecentProjectPaths.Take(MaxRecentProjectPaths))
        {
            RecentProjectPaths.Add(path);
        }
    }

    /// <summary>
    /// Renders a completed startup preflight into the operation log, worst outcomes included. This
    /// is the only place the user learns that their cache was quarantined, their settings were left
    /// untouched because a newer build owns them, or an interrupted publish was rolled back.
    /// </summary>
    public void ReplayStartupReport(StartupReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (StartupCheckResult result in report.Results)
        {
            string level = result.Severity switch
            {
                StartupCheckSeverity.Blocking => "ERROR",
                StartupCheckSeverity.Degraded => "WARN",
                _ => "INFO"
            };

            OperationLog.AddEntry(level, $"Startup check '{result.CheckId}': {result.Summary}");

            if (result.Severity == StartupCheckSeverity.Ok)
            {
                continue;
            }

            foreach (string detail in result.Details)
            {
                OperationLog.AddEntry(level, $"  {detail}");
            }
        }
    }

    /// <summary>
    /// Promotes <paramref name="projectPath"/> to the front of the recent list, records it as the
    /// last project, and persists. Never throws: a settings store that refuses the write degrades
    /// to an in-memory update plus a log record.
    /// </summary>
    private async Task RecordProjectPathAsync(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(projectPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = projectPath;
        }

        LastProjectPath = full;

        int existing = IndexOfRecent(full);
        if (existing >= 0)
        {
            RecentProjectPaths.RemoveAt(existing);
        }

        RecentProjectPaths.Insert(0, full);
        while (RecentProjectPaths.Count > MaxRecentProjectPaths)
        {
            RecentProjectPaths.RemoveAt(RecentProjectPaths.Count - 1);
        }

        _settings = _settings with
        {
            LastProjectPath = full,
            RecentProjectPaths = RecentProjectPaths.ToArray()
        };

        await PersistSettingsAsync().ConfigureAwait(true);
    }

    private int IndexOfRecent(string path)
    {
        for (int i = 0; i < RecentProjectPaths.Count; i++)
        {
            if (string.Equals(RecentProjectPaths[i], path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private async Task PersistSettingsAsync()
    {
        if (_settingsStore is null)
        {
            return;
        }

        if (_settings.IsReadOnly)
        {
            // A newer build owns the file. Overwriting it would destroy that build's settings, so
            // this session keeps its changes in memory only — the startup report already told the
            // user why.
            _logger.Log(LogLevel.Debug, LogCategory, "Settings are read-only this session; not persisting.");
            return;
        }

        try
        {
            await _settingsStore.SaveAsync(_settings, _settingsPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warn, LogCategory, "Could not persist application settings.", ex);
        }
    }

    /// <summary>
    /// Rebuilds <see cref="_textureSourceHolder"/>'s <c>Current</c> value from the current
    /// <see cref="_workspaceState"/> per architecture decision A7's late-bound accessor pattern.
    /// Called after every <c>_workspaceState</c> reassignment (not only
    /// <see cref="LoadWorkspaceStateAsync"/>) so <c>MdlPreviewProvider</c>'s texture-source
    /// accessor never observes a workspace that has been superseded — e.g. a pin changing which
    /// occurrence a resref resolves to. A missing holder, dispatcher, or registry (the
    /// designer-preview constructor) leaves the holder untouched/null, which the accessor treats
    /// as "no texture source available" rather than a failure.
    /// </summary>
    private void RefreshTextureSource()
    {
        if (_textureSourceHolder is null)
        {
            return;
        }

        _textureSourceHolder.Current = _workspaceState != null && _dispatcher != null && _registry != null
            ? new WorkspaceTextureSource(_workspaceState, _dispatcher, _registry, _baseGameCatalog)
            : null;
    }

    public async Task LoadWorkspaceStateAsync(WorkspaceState state, string? projectPath = null)
    {
        await _pendingSelectionUpdate.ConfigureAwait(true);
        _workspaceState = state;
        RefreshTextureSource();
        _currentProjectPath = projectPath;

        AssetTable.SelectedRows.Clear();
        AssetTable.SelectedRow = null;
        await ComparisonPanel.ClearSelectionAsync().ConfigureAwait(true);

        Title = string.IsNullOrEmpty(projectPath)
            ? "SRN.CC Asset Curator — [Unsaved Project]"
            : $"SRN.CC Asset Curator — {Path.GetFileName(projectPath)}";

        bool canEditSources = !state.IsReadOnly;
        var sourceVMs = state.Sources.Select(s => new SourceItemViewModel(s, canEditSources, OnSourceModeChangedAsync));
        SourceStack.UpdateSources(sourceVMs);

        var sourceLabels = state.Sources.ToDictionary(s => s.Id, s => Path.GetFileName(s.FullPath));
        AssetTable.LoadAssets(state.CuratedAssets, sourceLabels);
        ConflictQueue.Load(state);
        ApplySavedFilterSettings(state.Preferences.Filters);
        ComparisonPanel.SetCanPin(!_workspaceState.IsReadOnly);

        SaveProjectCommand.NotifyCanExecuteChanged();
        SaveProjectAsCommand.NotifyCanExecuteChanged();
        RescanCommand.NotifyCanExecuteChanged();
        BuildHakCommand.NotifyCanExecuteChanged();
        SourceStack.MoveUpCommand.NotifyCanExecuteChanged();
        SourceStack.MoveDownCommand.NotifyCanExecuteChanged();
        SourceStack.RemoveSourceCommand.NotifyCanExecuteChanged();

        // The source stack's own rescan button shares CanMoveSources with the toolbar's RescanCommand
        // above. Refreshing one without the other leaves two controls for the same action disagreeing
        // about whether it is available.
        SourceStack.RescanSourcesCommand.NotifyCanExecuteChanged();

        OperationLog.AddEntry("INFO", $"Loaded workspace with {state.Sources.Count} sources and {state.CuratedAssets.Count} assets.");
    }

    public IReadOnlyList<AssetRowItem> SortByResref() =>
        AssetTable.FilteredRows.Select((r, i) => new AssetRowItem(i + 1, r.Resref, r.ResourceTypeName, r.WinnerSourceLabel, r.SizeBytes))
            .OrderBy(item => item.Resref, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<AssetRowItem> FilterByResourceType(string resourceType) =>
        AssetTable.FilteredRows.Select((r, i) => new AssetRowItem(i + 1, r.Resref, r.ResourceTypeName, r.WinnerSourceLabel, r.SizeBytes))
            .Where(item => item.ResourceType.Contains(resourceType, StringComparison.OrdinalIgnoreCase)).ToArray();

    private readonly SemaphoreSlim _selectionLock = new(1, 1);

    public Func<Task<string?>>? OpenFilePickerAsync { get; set; }
    public Func<Task<string?>>? SaveProjectFilePickerAsync { get; set; }
    /// <summary>
    /// Asks the user where to write the built HAK, given a suggested file name and directory.
    /// Returns null when the user cancels, which cancels the build.
    /// </summary>
    public Func<string, string?, Task<string?>>? BuildOutputFilePickerAsync { get; set; }
    public Func<Task<IReadOnlyList<string>>>? HakFilePickerAsync { get; set; }
    public Func<Task<string?>>? FolderPickerAsync { get; set; }

    /// <summary>
    /// Shows the settings dialog and returns when it has closed. Set by the window; left null in
    /// headless contexts, where <see cref="OpenSettingsCommand"/> simply publishes the dialog view
    /// model on <see cref="SettingsDialog"/> for the caller to drive.
    /// </summary>
    public Func<SettingsDialogViewModel, Task>? ShowSettingsDialogAsync { get; set; }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (_workspaceService == null) return;
        var (state, _) = await _workspaceService.InitializeAsync(Array.Empty<AssetSource>()).ConfigureAwait(true);
        await LoadWorkspaceStateAsync(state, null).ConfigureAwait(true);
        OperationLog.AddEntry("INFO", "Created new empty project workspace.");
    }

    [RelayCommand]
    private async Task OpenProjectAsync(string? projectPath = null)
    {
        if (_workspaceService == null || _projectStore == null) return;
        await _pendingSelectionUpdate.ConfigureAwait(true);

        string? path = projectPath;
        if (string.IsNullOrEmpty(path) && OpenFilePickerAsync != null)
        {
            path = await OpenFilePickerAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(path)) return;
        }

        if (string.IsNullOrEmpty(path))
        {
            // Settings are live since milestone 7: reopening the last project is the whole point of
            // having persisted its path.
            path = LastProjectPath;
        }

        if (string.IsNullOrEmpty(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), DefaultProjectFileName);
        }

        if (!File.Exists(path))
        {
            OperationLog.AddEntry("WARN", $"Project file not found at '{path}'.");
            return;
        }

        var project = await _projectStore.LoadAsync(path).ConfigureAwait(true);
        await RecoverPendingPublicationJournalsAsync(project, path).ConfigureAwait(true);
        var (state, _) = await _workspaceService.InitializeAsync(
            project.Sources,
            project.Pins,
            project.SelectionState,
            project.Preferences,
            isReadOnly: project.IsReadOnly).ConfigureAwait(true);
        await LoadWorkspaceStateAsync(state, path).ConfigureAwait(true);
        await RecordProjectPathAsync(path).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanSaveProject))]
    private async Task SaveProjectAsync()
    {
        await _pendingSelectionUpdate.ConfigureAwait(true);
        if (_workspaceState == null || _projectStore == null) return;
        if (_workspaceState.IsReadOnly)
        {
            OperationLog.AddEntry("WARN", "Workspace is read-only. Open a writable copy before saving.");
            return;
        }

        string? path = _currentProjectPath;
        if (string.IsNullOrWhiteSpace(path) && SaveProjectFilePickerAsync != null)
        {
            path = await SaveProjectFilePickerAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path)) return;
        }

        path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Directory.GetCurrentDirectory(), DefaultProjectFileName)
            : path;

        await _projectStore.SaveAsync(_workspaceState, path).ConfigureAwait(true);
        _currentProjectPath = path;
        Title = $"SRN.CC Asset Curator — {Path.GetFileName(path)}";
        OperationLog.AddEntry("INFO", $"Saved project state to '{path}'.");
        await RecordProjectPathAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// Writes the current workspace to a new path and continues editing there.
    /// </summary>
    /// <param name="targetProjectPath">
    /// Where to write. Null asks <see cref="SaveProjectFilePickerAsync"/>; unlike
    /// <see cref="SaveProjectCommand"/> there is no current-directory fallback, because "Save As"
    /// with no destination is a cancelled operation, not a silent write to an unnamed file.
    /// </param>
    /// <remarks>
    /// Read-only gating is identical to <see cref="SaveProjectCommand"/> — same
    /// <see cref="CanSaveProject"/> predicate and the same in-method guard, so a read-only workspace
    /// cannot be laundered into a writable copy by routing around the disabled Save button.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSaveProject))]
    private async Task SaveProjectAsAsync(string? targetProjectPath = null)
    {
        await _pendingSelectionUpdate.ConfigureAwait(true);
        if (_workspaceState == null || _projectStore == null) return;
        if (_workspaceState.IsReadOnly)
        {
            OperationLog.AddEntry("WARN", "Workspace is read-only. Open a writable copy before saving.");
            return;
        }

        string? path = targetProjectPath;
        if (string.IsNullOrWhiteSpace(path) && SaveProjectFilePickerAsync != null)
        {
            path = await SaveProjectFilePickerAsync().ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            OperationLog.AddEntry("INFO", "Save As canceled: no destination was chosen.");
            return;
        }

        await _projectStore.SaveAsAsync(_workspaceState, path).ConfigureAwait(true);
        _currentProjectPath = path;
        Title = $"SRN.CC Asset Curator — {Path.GetFileName(path)}";
        OperationLog.AddEntry("INFO", $"Saved a copy of the project state to '{path}'.");
        await RecordProjectPathAsync(path).ConfigureAwait(true);
    }

    private bool CanSaveProject() => _workspaceState != null && !_workspaceState.IsReadOnly;

    /// <summary>
    /// Re-indexes every source and reports what changed. The rescan itself is the pre-existing
    /// <see cref="RescanSourcesAsync"/> path the source-stack button already drives; this is the
    /// toolbar entry point for it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync() => RescanSourcesAsync();

    private bool CanRescan() => _workspaceState is { IsReadOnly: false };

    /// <summary>
    /// Opens the settings dialog over the single injected settings store, then adopts whatever it
    /// wrote so the running session reflects the change without a restart.
    /// </summary>
    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (_settingsStore is null)
        {
            OperationLog.AddEntry("WARN", "Settings are unavailable in this session.");
            return;
        }

        // The store is the composed singleton; its read-only-newer write guard is per-instance state
        // keyed by path, so a dialog-local store could overwrite a newer build's settings.
        var dialog = new SettingsDialogViewModel(
            _settingsStore,
            _settings,
            _settings.IsReadOnly ? SettingsLoadStatus.ReadOnlyNewer : SettingsLoadStatus.Loaded,
            _settingsPath);

        SettingsDialog = dialog;
        dialog.ShowDialog();

        if (ShowSettingsDialogAsync is not null)
        {
            await ShowSettingsDialogAsync(dialog).ConfigureAwait(true);
        }

        if (dialog.SavedSettings is { } saved)
        {
            ApplySettings(saved);
            OperationLog.AddEntry("INFO", "Application settings were updated.");
        }
    }

    private async Task OnWorkspaceChangedAsync()
    {
        if (_workspaceService == null || _workspaceState == null) return;
        if (_workspaceState.IsReadOnly)
        {
            return;
        }

        var sourceOrders = SourceStack.Sources.Select(s => s.Source.Id).ToList();
        var (newState, _) = await _workspaceService.ReorderSourcesAsync(sourceOrders).ConfigureAwait(true);
        _workspaceState = newState;
        RefreshTextureSource();

        var sourceLabels = _workspaceState.Sources.ToDictionary(s => s.Id, s => Path.GetFileName(s.FullPath));
        AssetTable.LoadAssets(_workspaceState.CuratedAssets, sourceLabels);
        ConflictQueue.Load(_workspaceState);
    }

    private void OnRowSelectionChanged(AssetRowViewModel row)
    {
        _pendingSelectionUpdate = OnRowSelectionChangedAsync(row);
    }

    private async Task OnRowSelectionChangedAsync(AssetRowViewModel row)
    {
        await _selectionLock.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_workspaceState != null && _workspaceService != null)
            {
                var currentSelection = _workspaceService.CurrentState.SelectionState;
                var newSelectionState = currentSelection.SetOverride(row.Identity, row.IsSelected);
                _workspaceState = await _workspaceService.UpdateSelectionAsync(newSelectionState).ConfigureAwait(true);
                RefreshTextureSource();
            }
        }
        finally
        {
            _selectionLock.Release();
        }
    }

    private void OnBatchSelectionChanged(IEnumerable<AssetRowViewModel> rows, bool selected)
    {
        _pendingSelectionUpdate = OnBatchSelectionChangedAsync(rows, selected);
    }

    private async Task OnBatchSelectionChangedAsync(IEnumerable<AssetRowViewModel> rows, bool selected)
    {
        await _selectionLock.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_workspaceState == null || _workspaceService == null) return;

            var currentSelection = _workspaceService.CurrentState.SelectionState;
            foreach (var r in rows)
            {
                currentSelection = currentSelection.SetOverride(r.Identity, selected);
            }
            _workspaceState = await _workspaceService.UpdateSelectionAsync(currentSelection).ConfigureAwait(true);
            RefreshTextureSource();
        }
        finally
        {
            _selectionLock.Release();
        }
    }

    private void OnSelectedRowChanged(AssetRowViewModel? selectedRow)
    {
        _ = OnSelectedRowChangedAsync(selectedRow);
    }

    private void OnSelectedRowsChanged(IReadOnlyList<AssetRowViewModel> selectedRows)
    {
        _ = UpdateComparisonSelectionAsync(selectedRows);
    }

    private async Task OnSelectedRowChangedAsync(AssetRowViewModel? selectedRow)
    {
        IReadOnlyList<CuratedAsset> curatedAssets = AssetTable.SelectedRows.Count > 0
            ? AssetTable.SelectedRows.Select(r => r.CuratedAsset).ToList()
            : (selectedRow != null ? new[] { selectedRow.CuratedAsset } : Array.Empty<CuratedAsset>());
        await UpdateComparisonSelectionAsync(curatedAssets).ConfigureAwait(true);
    }

    private async Task UpdateComparisonSelectionAsync(IReadOnlyList<AssetRowViewModel> selectedRows)
    {
        await UpdateComparisonSelectionAsync(selectedRows.Select(r => r.CuratedAsset).ToArray()).ConfigureAwait(true);
    }

    private async Task UpdateComparisonSelectionAsync(IReadOnlyList<CuratedAsset> curatedAssets)
    {
        if (_workspaceState == null) return;
        var sourceMap = _workspaceState.Sources.ToDictionary(s => s.Id);
        await ComparisonPanel.UpdateSelectionAsync(curatedAssets, sourceMap).ConfigureAwait(true);
    }

    private async Task OnPinRequestedAsync(AssetOccurrence occurrence)
    {
        if (_workspaceService == null || _workspaceState == null) return;
        if (_workspaceState.IsReadOnly)
        {
            OperationLog.AddEntry("WARN", "Pinning is disabled for read-only workspaces.");
            return;
        }

        byte[]? raw = await ComputeRawHashAsync(occurrence, CancellationToken.None).ConfigureAwait(true);
        byte[] pinHash = NormalizeHash(raw);

        var pin = new WinnerPin(occurrence.Identity, occurrence.SourceId, occurrence.Locator, pinHash);
        _workspaceState = await _workspaceService.PinAsync(pin).ConfigureAwait(true);
        RefreshTextureSource();

        var sourceLabels = _workspaceState.Sources.ToDictionary(s => s.Id, s => Path.GetFileName(s.FullPath));
        AssetTable.LoadAssets(_workspaceState.CuratedAssets, sourceLabels);
        ConflictQueue.Load(_workspaceState);
        OperationLog.AddEntry("INFO", $"Pinned occurrence {occurrence.Identity.Resref}.{occurrence.Identity.ResourceType} to source {occurrence.SourceId}.");
    }

    /// <summary>
    /// Computes an occurrence's payload SHA-256 for pinning, or null if it cannot be read. Returns a
    /// precomputed hash when present, otherwise streams the payload once through the dispatcher.
    /// </summary>
    private async Task<byte[]?> ComputeRawHashAsync(AssetOccurrence occurrence, CancellationToken cancellationToken)
    {
        if (occurrence.Sha256 is { Length: 32 })
        {
            return occurrence.Sha256;
        }
        if (_dispatcher == null || _workspaceState == null)
        {
            return null;
        }
        var source = _workspaceState.Sources.FirstOrDefault(s => s.Id == occurrence.SourceId);
        if (source == null)
        {
            return null;
        }
        try
        {
            await using var stream = await _dispatcher.OpenOccurrenceAsync(source, occurrence, cancellationToken).ConfigureAwait(true);
            using var sha = SHA256.Create();
            return await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] NormalizeHash(byte[]? hash)
    {
        if (hash is { Length: 32 })
        {
            return hash;
        }
        byte[] padded = new byte[32];
        if (hash != null) Array.Copy(hash, padded, Math.Min(hash.Length, 32));
        return padded;
    }

    /// <summary>
    /// Pins the winner for every conflicted identity that has an occurrence in <paramref name="sourceId"/>,
    /// in one batch. Identities absent from that source are skipped; same-source duplicates with
    /// diverging or unreadable payloads are skipped as ambiguous. Reports the counts to the log.
    /// </summary>
    public async Task BulkPreferSourceAsync(Guid sourceId)
    {
        if (_workspaceService == null || _workspaceState == null || _workspaceState.IsReadOnly)
        {
            return;
        }

        var conflicts = _workspaceState.CuratedAssets.Where(ConflictQueueViewModel.IsConflict).ToList();
        if (conflicts.Count == 0)
        {
            return;
        }

        string sourceLabel = _workspaceState.Sources.FirstOrDefault(s => s.Id == sourceId) is { } src
            ? Path.GetFileName(src.FullPath)
            : sourceId.ToString();

        CancellationTokenSource cts = StatusBar.BeginOperation($"Preferring source '{sourceLabel}' for conflicts...");
        CancellationToken ct = cts.Token;

        int pinned = 0, absent = 0, ambiguous = 0;
        List<WinnerPin> pins = new();

        try
        {
            for (int i = 0; i < conflicts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                CuratedAsset asset = conflicts[i];
                StatusBar.ReportProgress($"Preferring source '{sourceLabel}'... ({i + 1}/{conflicts.Count})", (double)(i + 1) / conflicts.Count);

                var candidates = asset.AllOccurrences.Where(o => o.SourceId == sourceId).ToList();
                if (candidates.Count == 0)
                {
                    absent++;
                    continue;
                }

                AssetOccurrence? chosen = await ChoosePreferredOccurrenceAsync(candidates, ct).ConfigureAwait(true);
                if (chosen is null)
                {
                    ambiguous++;
                    continue;
                }

                byte[]? raw = await ComputeRawHashAsync(chosen, ct).ConfigureAwait(true);
                if (raw is null)
                {
                    ambiguous++;
                    continue;
                }

                pins.Add(new WinnerPin(chosen.Identity, chosen.SourceId, chosen.Locator, NormalizeHash(raw)));
                pinned++;
            }

            if (pins.Count > 0)
            {
                _workspaceState = await _workspaceService.PinManyAsync(pins, ct).ConfigureAwait(true);
                RefreshTextureSource();
                var sourceLabels = _workspaceState.Sources.ToDictionary(s => s.Id, s => Path.GetFileName(s.FullPath));
                AssetTable.LoadAssets(_workspaceState.CuratedAssets, sourceLabels);
                ConflictQueue.Load(_workspaceState);
            }

            StatusBar.EndOperation("Prefer-source complete.");
            OperationLog.AddEntry("INFO",
                $"Preferred '{sourceLabel}': {pinned} pinned, {absent} skipped (no occurrence), {ambiguous} skipped (ambiguous payloads).");
        }
        catch (OperationCanceledException)
        {
            StatusBar.EndOperation("Prefer-source canceled.");
            OperationLog.AddEntry("WARN", $"Prefer-source for '{sourceLabel}' was canceled after {pinned} pins.");
        }
    }

    /// <summary>
    /// Chooses which occurrence in the preferred source to pin. A single occurrence is used directly;
    /// multiple occurrences must share an identical, readable payload (then the lowest deterministic
    /// locator wins). Diverging or unreadable duplicates return null so the caller can skip as ambiguous.
    /// </summary>
    private async Task<AssetOccurrence?> ChoosePreferredOccurrenceAsync(IReadOnlyList<AssetOccurrence> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        byte[]? reference = null;
        foreach (AssetOccurrence occ in candidates)
        {
            byte[]? hash = await ComputeRawHashAsync(occ, cancellationToken).ConfigureAwait(true);
            if (hash is null)
            {
                return null; // unreadable duplicate -> ambiguous
            }
            if (reference is null)
            {
                reference = hash;
            }
            else if (!reference.AsSpan().SequenceEqual(hash))
            {
                return null; // diverging payloads -> ambiguous
            }
        }

        return candidates.OrderBy(o => o.Locator.ToString(), StringComparer.Ordinal).First();
    }

    [RelayCommand(CanExecute = nameof(CanBuildHak))]
    private async Task BuildHakAsync()
    {
        await _pendingSelectionUpdate.ConfigureAwait(true);
        if (_workspaceState == null || _buildOrchestrator == null)
        {
            OperationLog.AddEntry("WARN", "No active workspace loaded for build.");
            return;
        }

        string defaultPath = ResolveDefaultBuildOutputPath(_workspaceState, _currentProjectPath);

        // Always ask, even when the project or its preferences already name a target. A build
        // overwrites whatever is at the destination, and the destination is usually a live override
        // or hak directory, so the one place the user gets to see and change it is here.
        string destPath = defaultPath;
        if (BuildOutputFilePickerAsync != null)
        {
            string? chosen;
            try
            {
                chosen = await BuildOutputFilePickerAsync(
                    Path.GetFileName(defaultPath),
                    Path.GetDirectoryName(defaultPath)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // This runs before the try that wraps the build itself, and an async command handler
                // that throws takes the process down rather than reporting. The picker reaches the
                // shell's storage provider with a directory taken from the project file, so a
                // targetHak naming a disconnected share or a removed drive is enough to get here.
                OperationLog.AddEntry("ERROR", $"Could not open the build output picker: {ex.Message}");
                _logger.Log(LogLevel.Error, LogCategory, "The build output picker failed to open.", ex);
                return;
            }

            if (string.IsNullOrWhiteSpace(chosen)) return;
            destPath = chosen;
        }

        var cts = StatusBar.BeginOperation("Building HAK package...");
        OperationLog.AddEntry("INFO", $"Initiating build target -> {destPath}");

        var progress = new Progress<(string message, double fraction)>(p =>
        {
            StatusBar.ReportProgress(p.message, p.fraction);
            OperationLog.AddEntry("INFO", p.message);
        });

        try
        {
            var result = await _buildOrchestrator.ExecuteBuildAsync(_workspaceState, destPath, progress, cts.Token).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                StatusBar.EndOperation("Build succeeded!");
                OperationLog.AddEntry("INFO", $"Build and publication succeeded! Output: {result.PublishedHakPath}");
            }
            else
            {
                StatusBar.EndOperation("Build failed.");
                OperationLog.AddEntry("ERROR", $"Build failed: {result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusBar.EndOperation("Build canceled.");
            OperationLog.AddEntry("WARN", "Build operation was canceled by user.");
        }
        catch (Exception ex)
        {
            StatusBar.EndOperation("Build error.");
            OperationLog.AddEntry("ERROR", $"Build exception: {ex.Message}");
        }
    }

    /// <summary>
    /// The path the build output picker opens on: the project's configured target HAK if it has
    /// one, otherwise a <c>.hak</c> named after the project beside it.
    /// </summary>
    private string ResolveDefaultBuildOutputPath(WorkspaceState state, string? projectPath)
    {
        string? configuredTargetHak = ResolveConfiguredTargetHakPath(state, projectPath);
        if (!string.IsNullOrWhiteSpace(configuredTargetHak))
        {
            return configuredTargetHak;
        }

        string projectDir = !string.IsNullOrWhiteSpace(projectPath)
            ? Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        string baseName = !string.IsNullOrWhiteSpace(projectPath)
            ? Path.GetFileNameWithoutExtension(projectPath)
            : "output";

        return Path.Combine(projectDir, $"{baseName}.hak");
    }

    private bool CanBuildHak() => _workspaceState != null && !_workspaceState.IsReadOnly;

    private bool CanMoveSources() => _workspaceState is { IsReadOnly: false };

    private async Task AddHakSourceAsync()
    {
        if (HakFilePickerAsync == null) return;
        IReadOnlyList<string> paths = await HakFilePickerAsync().ConfigureAwait(true);
        await AddSourcesAsync(paths.Select(path => AssetSource.CreateHak(path))).ConfigureAwait(true);
    }

    private async Task AddFolderSourceAsync()
    {
        if (FolderPickerAsync == null) return;
        string? path = await FolderPickerAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(path))
        {
            await AddSourcesAsync(new[] { AssetSource.CreateFolder(path) }).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Drops a source from the workspace and rebuilds resolution without it.
    /// </summary>
    /// <remarks>
    /// Pins that named the removed source go with it. Keeping them would leave the workspace
    /// carrying a pin to a source it no longer knows about, which resolution can only report as a
    /// permanently invalid pin the user has no way to clear. Selection state is kept: it is keyed by
    /// identity, so an identity still present in another source stays selected, and one that has
    /// left the workspace entirely is simply no longer referenced.
    /// </remarks>
    private async Task RemoveSourceAsync(SourceItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_workspaceService == null || _workspaceState == null || _workspaceState.IsReadOnly) return;

        Guid removedId = item.Source.Id;
        var remaining = _workspaceState.Sources
            .Where(source => source.Id != removedId)
            .Select((source, index) => source with { PriorityOrdinal = index })
            .ToList();

        if (remaining.Count == _workspaceState.Sources.Count) return;

        var pins = _workspaceState.Pins.Where(pin => pin.SourceId != removedId).ToList();
        int droppedPins = _workspaceState.Pins.Count - pins.Count;

        var (state, _) = await _workspaceService.InitializeAsync(
            remaining,
            pins,
            _workspaceState.SelectionState,
            _workspaceState.Preferences).ConfigureAwait(true);
        await LoadWorkspaceStateAsync(state, _currentProjectPath).ConfigureAwait(true);

        OperationLog.AddEntry("INFO", $"Removed source {item.Title}.");
        if (droppedPins > 0)
        {
            OperationLog.AddEntry("WARN", $"  {droppedPins} pin(s) to that source were discarded.");
        }
    }

    private async Task RescanSourcesAsync()
    {
        if (_workspaceService == null || _workspaceState == null)
        {
            return;
        }

        if (_workspaceState.IsReadOnly)
        {
            OperationLog.AddEntry("WARN", "Workspace is read-only. Rescan is disabled.");
            return;
        }

        var (state, report) = await _workspaceService.RescanAsync().ConfigureAwait(true);
        await LoadWorkspaceStateAsync(state, _currentProjectPath).ConfigureAwait(true);
        ReportChangedInputs(report);
    }

    /// <summary>
    /// Applies a new <see cref="SourceMode"/> to a source. Fired fire-and-forget from the source
    /// stack's per-item mode picker; the workspace service serializes the change and re-resolves.
    /// Mode-induced pin invalidations surface as WARN lines via <see cref="ReportChangedInputs"/>.
    /// </summary>
    private async Task OnSourceModeChangedAsync(SourceItemViewModel item, SourceMode mode)
    {
        if (_workspaceService == null || _workspaceState == null || _workspaceState.IsReadOnly)
        {
            return;
        }

        try
        {
            var (state, report) = await _workspaceService.SetSourceModeAsync(item.Source.Id, mode).ConfigureAwait(true);
            await LoadWorkspaceStateAsync(state, _currentProjectPath).ConfigureAwait(true);
            ReportChangedInputs(report, "Source mode change");
        }
        catch (Exception ex)
        {
            OperationLog.AddEntry("ERROR", $"Failed to change source mode: {ex.Message}");
        }
    }

    /// <summary>
    /// Renders a <see cref="ChangedInputReport"/> into the operation log. The rescan path already
    /// produced this report and threw it away, which is why a rescan that silently invalidated a pin
    /// looked identical to one that changed nothing.
    /// </summary>
    private void ReportChangedInputs(ChangedInputReport report, string contextLabel = "Rescan")
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!report.HasChanges)
        {
            OperationLog.AddEntry("INFO", $"{contextLabel} complete: no inputs changed.");
            return;
        }

        OperationLog.AddEntry("INFO", $"{contextLabel} complete. Changed inputs:");
        Line("INFO", "sources added", report.AddedSources.Count);
        Line("INFO", "sources removed", report.RemovedSources.Count);
        Line("WARN", "source availability transitions", report.AvailabilityTransitions.Count);
        Line("INFO", "source mode changes", report.ModeChanges.Count);
        Line("INFO", "source fingerprint changes", report.FingerprintChanges.Count);
        Line("INFO", "identities added", report.AddedIdentities.Count);
        Line("INFO", "identities removed", report.RemovedIdentities.Count);
        Line("INFO", "winner changes", report.WinnerChanges.Count);
        Line("INFO", "pins reattached", report.PinReattachments.Count);
        Line("WARN", "pins invalidated", report.PinInvalidations.Count);
        Line("INFO", "selection changes", report.SelectionChanges.Count);

        void Line(string level, string label, int count)
        {
            if (count > 0)
            {
                OperationLog.AddEntry(level, $"  {count} {label}.");
            }
        }
    }

    /// <summary>Resolves a resource type to its display extension (upper-case), or "UNKNOWN".</summary>
    private string ResourceTypeNameFor(ushort resourceType) =>
        _registry is not null && _registry.TryGetExtension(resourceType, out var name)
            ? name.ToUpperInvariant()
            : "UNKNOWN";

    private async Task AddSourcesAsync(IEnumerable<AssetSource> newSources)
    {
        if (_workspaceService == null || _workspaceState == null || _workspaceState.IsReadOnly) return;

        var additions = newSources.ToList();
        if (additions.Count == 0) return;

        var sources = _workspaceState.Sources.Concat(additions)
            .Select((source, index) => source with { PriorityOrdinal = index })
            .ToList();
        var (state, _) = await _workspaceService.InitializeAsync(
            sources,
            _workspaceState.Pins,
            _workspaceState.SelectionState,
            _workspaceState.Preferences).ConfigureAwait(true);
        await LoadWorkspaceStateAsync(state, _currentProjectPath).ConfigureAwait(true);
    }

    private void GenerateSyntheticDemoData()
    {
        Title = "SRN.CC Asset Curator — Demo Shell";
        const int count = 187943;
        var list = new List<AssetRowViewModel>(count);
        for (int i = 0; i < count; i++)
        {
            var identity = new SRN.CC.Core.Identity.AssetIdentity($"resref_{i:D6}", 2000);
            var source = AssetSource.CreateHak(Path.GetFullPath($"hak_source_{(i % 117):D3}.hak"), 0);
            var occ = new AssetOccurrence(identity, source.Id, new HakEntryLocator(i), identity.OriginalName, 1024 + (i * 37) % 500000);
            var asset = new CuratedAsset(identity, new[] { occ }, occ, null, ResolutionStatus.Resolved, true);
            list.Add(new AssetRowViewModel(asset, "GENERIC (2000)", source.FullPath));
        }
        AssetTable.LoadAssets(list.Select(r => r.CuratedAsset), new Dictionary<Guid, string>());
    }

    private void OnSearchTextChanged(string searchText)
    {
        UpdateFilterPreferences(searchText, AssetTable.SelectedFilterMode);
    }

    private void OnSelectedFilterModeChanged(ConflictFilterMode selectedFilterMode)
    {
        UpdateFilterPreferences(AssetTable.SearchText, selectedFilterMode);
    }

    private void UpdateFilterPreferences(string searchText, ConflictFilterMode selectedFilterMode)
    {
        if (_workspaceState is null) return;

        JsonObject filterSettings = _workspaceState.Preferences.Filters as JsonObject ?? new JsonObject();

        if (string.IsNullOrWhiteSpace(searchText))
        {
            filterSettings.Remove("searchText");
        }
        else
        {
            filterSettings["searchText"] = searchText;
        }

        if (selectedFilterMode == ConflictFilterMode.All)
        {
            filterSettings.Remove("filterMode");
        }
        else
        {
            filterSettings["filterMode"] = selectedFilterMode.ToString();
        }

        ProjectPreferences updatedPreferences = _workspaceState.Preferences with
        {
            Filters = filterSettings.Count > 0 ? filterSettings : null
        };

        _workspaceState = new WorkspaceState(
            _workspaceState.Sources,
            _workspaceState.Snapshots,
            _workspaceState.CuratedAssets,
            _workspaceState.SelectionState,
            _workspaceState.Pins,
            updatedPreferences,
            _workspaceState.IsReadOnly);
        RefreshTextureSource();
    }

    private void ApplySavedFilterSettings(JsonNode? savedFilters)
    {
        if (_workspaceState is null) return;
        if (savedFilters is not JsonObject filterSettings) return;

        if (filterSettings["searchText"] is JsonValue searchValue && searchValue.TryGetValue<string>(out string? searchText))
        {
            AssetTable.SearchText = searchText;
        }

        if (TryGetConflictFilterMode(filterSettings["filterMode"], out ConflictFilterMode filterMode))
        {
            AssetTable.SelectedFilterMode = filterMode;
        }
    }

    private static bool TryGetConflictFilterMode(JsonNode? node, out ConflictFilterMode filterMode)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out int mode) && Enum.IsDefined(typeof(ConflictFilterMode), mode))
            {
                filterMode = (ConflictFilterMode)mode;
                return true;
            }

            if (value.TryGetValue<string>(out string? modeName) && Enum.TryParse(modeName, ignoreCase: true, out filterMode))
            {
                return true;
            }
        }

        filterMode = ConflictFilterMode.All;
        return false;
    }

    private string? ResolveConfiguredTargetHakPath(WorkspaceState workspaceState, string? projectPath)
    {
        try
        {
            if (projectPath is null || workspaceState.Preferences.OutputSettings is not JsonObject outputSettings)
            {
                return null;
            }

            if (outputSettings["targetHak"] is not JsonValue targetHakNode ||
                !targetHakNode.TryGetValue<string>(out string? targetHak) ||
                string.IsNullOrWhiteSpace(targetHak))
            {
                return null;
            }

            if (Path.IsPathFullyQualified(targetHak))
            {
                return Path.GetFullPath(targetHak);
            }

            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return null;
            }

            string? projectDir = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(projectDir))
            {
                return null;
            }

            return Path.GetFullPath(Path.Combine(projectDir, targetHak));
        }
        catch (Exception ex)
        {
            // A configured targetHak that will not resolve to a path is a real project-file problem;
            // falling back to the default output name silently is what made it invisible.
            _logger.Log(
                LogLevel.Warn,
                LogCategory,
                $"Could not resolve the configured target HAK path for project '{projectPath}'.",
                ex);
            return null;
        }
    }

    private async Task RecoverPendingPublicationJournalsAsync(WorkspaceState workspaceState, string projectPath)
    {
        if (_artifactPublisher is null)
        {
            return;
        }

        HashSet<string> recoveryDirectories = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? projectDir = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrWhiteSpace(projectDir))
            {
                recoveryDirectories.Add(Path.GetFullPath(projectDir));
            }
        }
        catch (Exception ex)
        {
            _logger.Log(
                LogLevel.Warn,
                LogCategory,
                $"Could not derive a journal recovery directory from project path '{projectPath}'.",
                ex);
        }

        try
        {
            string? configuredTargetHak = ResolveConfiguredTargetHakPath(workspaceState, projectPath);
            if (!string.IsNullOrWhiteSpace(configuredTargetHak))
            {
                string? configuredDir = Path.GetDirectoryName(configuredTargetHak);
                if (!string.IsNullOrWhiteSpace(configuredDir))
                {
                    recoveryDirectories.Add(Path.GetFullPath(configuredDir));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log(
                LogLevel.Warn,
                LogCategory,
                "Could not derive a journal recovery directory from the configured target HAK path.",
                ex);
        }

        foreach (string journalDirectory in recoveryDirectories)
        {
            bool recovered = await _artifactPublisher.RecoverPendingJournalAsync(journalDirectory).ConfigureAwait(true);
            if (recovered)
            {
                OperationLog.AddEntry("INFO", $"Recovered pending publication journals from '{journalDirectory}'.");
            }
        }
    }

    [RelayCommand]
    private async Task AddAvailableDependenciesAsync()
    {
        await _pendingSelectionUpdate.ConfigureAwait(true);
        if (_workspaceState == null || _dispatcher == null || _registry == null)
        {
            OperationLog.AddEntry("WARN", "No active workspace or dependencies for dependency analysis.");
            return;
        }

        var selectedRows = AssetTable.SelectedRows.Count > 0
            ? (IReadOnlyList<AssetRowViewModel>)AssetTable.SelectedRows.ToList()
            : (AssetTable.SelectedRow != null ? new[] { AssetTable.SelectedRow } : Array.Empty<AssetRowViewModel>());

        if (selectedRows.Count == 0)
        {
            OperationLog.AddEntry("INFO", "No assets selected for dependency analysis.");
            return;
        }

        OperationLog.AddEntry("INFO", $"Starting dependency analysis for {selectedRows.Count} selected assets...");

        try
        {
            // Create stream opener function
            async Task<Stream> StreamOpener(AssetSource source, AssetOccurrence occ, Stream fallback, CancellationToken ct)
            {
                if (_dispatcher != null)
                {
                    return await _dispatcher.OpenOccurrenceAsync(source, occ, ct).ConfigureAwait(false);
                }
                return fallback;
            }

            // Create resolver and command
            var resolver = new DependencyLocator(_workspaceState, StreamOpener);
            var analyzer = new DependencyAnalyzer(_registry);
            var command = new Commands.AddAvailableDependenciesCommand(analyzer, _registry, resolver);

            var occurrences = selectedRows
                .Select(r => r.CuratedAsset.ResolvedOccurrence)
                .Where(o => o != null)
                .Cast<AssetOccurrence>()
                .ToList();

            var closure = await command.ExecuteAsync(occurrences, CancellationToken.None)
                .ConfigureAwait(true);

            OperationLog.AddEntry("INFO", $"Dependency analysis complete: {closure.Resolved.Count} resolved, {closure.UnresolvedGroups.Sum(g => g.Count)} unresolved.");

            // Show confirmation dialog and wait for user response
            var dialogVm = new ConfirmDependenciesDialogViewModel();
            var confirmed = await dialogVm.ShowDialogAsync(closure).ConfigureAwait(true);

            if (confirmed)
            {
                OperationLog.AddEntry("INFO", "User confirmed dependency closure.");

                if (_workspaceService != null && _workspaceState != null && !_workspaceState.IsReadOnly && closure.Resolved.Count > 0)
                {
                    await _selectionLock.WaitAsync().ConfigureAwait(true);
                    try
                    {
                        var currentSelection = _workspaceService.CurrentState.SelectionState;
                        foreach (var identity in closure.Resolved)
                        {
                            currentSelection = currentSelection.SetOverride(identity, true);
                        }
                        _workspaceState = await _workspaceService.UpdateSelectionAsync(currentSelection).ConfigureAwait(true);

                        var sourceLabels = _workspaceState.Sources.ToDictionary(s => s.Id, s => Path.GetFileName(s.FullPath));
                        AssetTable.LoadAssets(_workspaceState.CuratedAssets, sourceLabels);
                    }
                    finally
                    {
                        _selectionLock.Release();
                    }

                    OperationLog.AddEntry("INFO", $"Added {closure.Resolved.Count} resolved dependencies to the workspace selection.");
                }
            }
            else
            {
                OperationLog.AddEntry("INFO", "User canceled dependency closure.");
            }
        }
        catch (Exception ex)
        {
            OperationLog.AddEntry("ERROR", $"Dependency analysis failed: {ex.Message}");
        }
    }

    private sealed class FallbackResourceTypeRegistry : IResourceTypeRegistry
    {
        public bool TryGetExtension(ushort typeId, out string extension) { extension = "2da"; return true; }
        public bool TryGetType(string extension, out ushort typeId) { typeId = 2000; return true; }
    }

    /// <summary>
    /// Stand-in dispatcher for the designer preview and the synthetic performance probe, whose
    /// 187 943 rows describe sources that do not exist on disk.
    /// </summary>
    /// <remarks>
    /// It previously threw <see cref="NotImplementedException"/>, which turned any designer-time
    /// preview attempt into an unhandled exception in the XAML previewer. Every occurrence resolves
    /// to zero bytes instead: the preview providers this dispatcher is paired with handle an empty
    /// stream by reporting "nothing to preview", which is exactly the truth for synthetic data.
    /// </remarks>
    private sealed class FallbackSourceReaderDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(
            AssetSource source,
            AssetOccurrence occurrence,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(occurrence);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        }
    }
}
