using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Preview;

namespace SRN.CC.App.ViewModels;

public record AssetRowItem(int Id, string Resref, string ResourceType, string SourceHak, long SizeBytes);

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IWorkspaceService? _workspaceService;
    private readonly IProjectStore? _projectStore;
    private readonly ISettingsStore? _settingsStore;
    private readonly IBuildOrchestrator? _buildOrchestrator;
    private readonly IArtifactPublisher? _artifactPublisher;
    private readonly PreviewEngine? _previewEngine;
    private readonly IResourceTypeRegistry? _registry;
    private readonly ISourceReaderDispatcher? _dispatcher;

    private WorkspaceState? _workspaceState;
    private string? _currentProjectPath;
    private Task _pendingSelectionUpdate = Task.CompletedTask;

    [ObservableProperty]
    private string _title = "SRN.CC Asset Curator";

    public SourceStackViewModel SourceStack { get; }
    public AssetTableViewModel AssetTable { get; }
    public ComparisonPanelViewModel ComparisonPanel { get; }
    public OperationLogViewModel OperationLog { get; }
    public StatusBarViewModel StatusBar { get; }

    public ObservableCollection<AssetRowViewModel> Items => AssetTable.FilteredRows;

    // Parameterless constructor for XAML designer preview & synthetic performance probe
    public MainWindowViewModel()
    {
        OperationLog = new OperationLogViewModel();
        StatusBar = new StatusBarViewModel();
        SourceStack = new SourceStackViewModel(
            OnWorkspaceChangedAsync,
            AddHakSourceAsync,
            AddFolderSourceAsync,
            RescanSourcesAsync,
            CanMoveSources);
        AssetTable = new AssetTableViewModel(
            new FallbackResourceTypeRegistry(),
            OnRowSelectionChanged,
            OnBatchSelectionChanged,
            OnSelectedRowChanged,
            OnSelectedRowsChanged,
            OnSearchTextChanged,
            OnSelectedFilterModeChanged);
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
        ISourceReaderDispatcher dispatcher)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _buildOrchestrator = buildOrchestrator ?? throw new ArgumentNullException(nameof(buildOrchestrator));
        _artifactPublisher = artifactPublisher ?? throw new ArgumentNullException(nameof(artifactPublisher));
        _previewEngine = previewEngine ?? throw new ArgumentNullException(nameof(previewEngine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        OperationLog = new OperationLogViewModel();
        StatusBar = new StatusBarViewModel();
        SourceStack = new SourceStackViewModel(
            OnWorkspaceChangedAsync,
            AddHakSourceAsync,
            AddFolderSourceAsync,
            RescanSourcesAsync,
            CanMoveSources);
        AssetTable = new AssetTableViewModel(
            _registry,
            OnRowSelectionChanged,
            OnBatchSelectionChanged,
            OnSelectedRowChanged,
            OnSelectedRowsChanged,
            OnSearchTextChanged,
            OnSelectedFilterModeChanged);

        ComparisonPanel = new ComparisonPanelViewModel(
            _previewEngine,
            OnPinRequestedAsync);
    }

    public async Task LoadWorkspaceStateAsync(WorkspaceState state, string? projectPath = null)
    {
        await _pendingSelectionUpdate.ConfigureAwait(true);
        _workspaceState = state;
        _currentProjectPath = projectPath;

        AssetTable.SelectedRows.Clear();
        AssetTable.SelectedRow = null;
        await ComparisonPanel.ClearSelectionAsync().ConfigureAwait(true);

        Title = string.IsNullOrEmpty(projectPath)
            ? "SRN.CC Asset Curator — [Unsaved Project]"
            : $"SRN.CC Asset Curator — {Path.GetFileName(projectPath)}";

        var sourceVMs = state.Sources.Select(s => new SourceItemViewModel(s));
        SourceStack.UpdateSources(sourceVMs);

        var sourceLabels = state.Sources.ToDictionary(s => s.Id, s => Path.GetFileName(s.FullPath));
        AssetTable.LoadAssets(state.CuratedAssets, sourceLabels);
        ApplySavedFilterSettings(state.Preferences.Filters);
        ComparisonPanel.SetCanPin(!_workspaceState.IsReadOnly);

        SaveProjectCommand.NotifyCanExecuteChanged();
        BuildHakCommand.NotifyCanExecuteChanged();
        SourceStack.MoveUpCommand.NotifyCanExecuteChanged();
        SourceStack.MoveDownCommand.NotifyCanExecuteChanged();

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
    public Func<Task<string?>>? BuildOutputFilePickerAsync { get; set; }
    public Func<Task<IReadOnlyList<string>>>? HakFilePickerAsync { get; set; }
    public Func<Task<string?>>? FolderPickerAsync { get; set; }

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
            path = Path.Combine(Directory.GetCurrentDirectory(), "project.srncc");
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
            ? Path.Combine(Directory.GetCurrentDirectory(), "project.srncc")
            : path;

        await _projectStore.SaveAsync(_workspaceState, path).ConfigureAwait(true);
        _currentProjectPath = path;
        Title = $"SRN.CC Asset Curator — {Path.GetFileName(path)}";
        OperationLog.AddEntry("INFO", $"Saved project state to '{path}'.");
    }

    private bool CanSaveProject() => _workspaceState != null && !_workspaceState.IsReadOnly;

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

        var sourceLabels = _workspaceState.Sources.ToDictionary(s => s.Id, s => Path.GetFileName(s.FullPath));
        AssetTable.LoadAssets(_workspaceState.CuratedAssets, sourceLabels);
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

        byte[]? pinHash = occurrence.Sha256;
        if (pinHash == null || pinHash.Length == 0)
        {
            var source = _workspaceState.Sources.FirstOrDefault(s => s.Id == occurrence.SourceId);
            if (source != null && _dispatcher != null)
            {
                await using var stream = await _dispatcher.OpenOccurrenceAsync(source, occurrence, CancellationToken.None).ConfigureAwait(true);
                using var sha = SHA256.Create();
                pinHash = await sha.ComputeHashAsync(stream, CancellationToken.None).ConfigureAwait(true);
            }
        }

        if (pinHash == null || pinHash.Length != 32)
        {
            byte[] padded = new byte[32];
            if (pinHash != null) Array.Copy(pinHash, padded, Math.Min(pinHash.Length, 32));
            pinHash = padded;
        }

        var pin = new WinnerPin(occurrence.Identity, occurrence.SourceId, occurrence.Locator, pinHash);
        _workspaceState = await _workspaceService.PinAsync(pin).ConfigureAwait(true);

        var sourceLabels = _workspaceState.Sources.ToDictionary(s => s.Id, s => Path.GetFileName(s.FullPath));
        AssetTable.LoadAssets(_workspaceState.CuratedAssets, sourceLabels);
        OperationLog.AddEntry("INFO", $"Pinned occurrence {occurrence.Identity.Resref}.{occurrence.Identity.ResourceType} to source {occurrence.SourceId}.");
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

        string? destPath = null;
        if (_currentProjectPath is null && BuildOutputFilePickerAsync != null)
        {
            destPath = await BuildOutputFilePickerAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(destPath)) return;
        }

        if (string.IsNullOrWhiteSpace(destPath))
        {
            string projectDir = !string.IsNullOrWhiteSpace(_currentProjectPath)
                ? Path.GetDirectoryName(_currentProjectPath) ?? Directory.GetCurrentDirectory()
                : Directory.GetCurrentDirectory();

            string baseName = !string.IsNullOrWhiteSpace(_currentProjectPath)
                ? Path.GetFileNameWithoutExtension(_currentProjectPath)
                : "output";

            string? configuredTargetHak = ResolveConfiguredTargetHakPath(_workspaceState, _currentProjectPath);
            if (!string.IsNullOrWhiteSpace(configuredTargetHak))
            {
                destPath = configuredTargetHak;
            }
            else
            {
                destPath = Path.Combine(projectDir, $"{baseName}.hak");
            }
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

        var (state, _) = await _workspaceService.RescanAsync().ConfigureAwait(true);
        await LoadWorkspaceStateAsync(state, _currentProjectPath).ConfigureAwait(true);
    }

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

    private static string? ResolveConfiguredTargetHakPath(WorkspaceState workspaceState, string? projectPath)
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
        catch
        {
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
        catch { }

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
        catch { }

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
            // Create a map of all occurrences in the workspace for fast lookup
            var occurrenceMap = new Dictionary<SRN.CC.Core.Identity.AssetIdentity, AssetOccurrence>();
            foreach (var curatedAsset in _workspaceState.CuratedAssets)
            {
                foreach (var occurrence in curatedAsset.AllOccurrences)
                {
                    if (!occurrenceMap.ContainsKey(occurrence.Identity))
                    {
                        occurrenceMap[occurrence.Identity] = occurrence;
                    }
                }
            }

            // Create locator function that resolves assets from the workspace
            async Task<AssetOccurrence?> Locator(SRN.CC.Core.Identity.AssetIdentity id, CancellationToken ct)
            {
                return occurrenceMap.TryGetValue(id, out var occ) ? occ : null;
            }

            // Create stream provider function
            async Task<Stream> StreamProvider(AssetOccurrence occ, Stream fallback, CancellationToken ct)
            {
                var source = _workspaceState.Sources.FirstOrDefault(s => s.Id == occ.SourceId);
                if (source != null && _dispatcher != null)
                {
                    return await _dispatcher.OpenOccurrenceAsync(source, occ, ct).ConfigureAwait(false);
                }
                return fallback;
            }

            // Create and execute the dependency command
            var analyzer = new DependencyAnalyzer(_registry);
            var command = new Commands.AddAvailableDependenciesCommand(
                analyzer,
                _registry,
                Locator,
                StreamProvider);

            var occurrences = selectedRows
                .Select(r => r.CuratedAsset.ResolvedOccurrence)
                .Where(o => o != null)
                .Cast<AssetOccurrence>()
                .ToList();

            var closure = await command.ExecuteAsync(occurrences, CancellationToken.None)
                .ConfigureAwait(true);

            OperationLog.AddEntry("INFO", $"Dependency analysis complete: {closure.Resolved.Count} resolved, {closure.UnresolvedGroups.Sum(g => g.Count)} unresolved.");

            // Show confirmation dialog
            var dialogVm = new ConfirmDependenciesDialogViewModel();
            dialogVm.ShowDialog(closure);

            // In a real implementation, this would show the dialog and wait for user confirmation
            // For now, we'll log the results
            if (dialogVm.IsConfirmed)
            {
                OperationLog.AddEntry("INFO", "User confirmed dependency closure.");
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

    private sealed class FallbackSourceReaderDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
