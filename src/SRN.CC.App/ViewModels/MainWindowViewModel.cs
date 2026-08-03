using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly PreviewEngine? _previewEngine;
    private readonly IResourceTypeRegistry? _registry;
    private readonly ISourceReaderDispatcher? _dispatcher;

    private WorkspaceState? _workspaceState;
    private string? _currentProjectPath;

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
        SourceStack = new SourceStackViewModel(OnWorkspaceChangedAsync);
        AssetTable = new AssetTableViewModel(
            new FallbackResourceTypeRegistry(),
            OnRowSelectionChanged,
            OnBatchSelectionChanged,
            OnSelectedRowChanged);
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
        PreviewEngine previewEngine,
        IResourceTypeRegistry registry,
        ISourceReaderDispatcher dispatcher)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _buildOrchestrator = buildOrchestrator ?? throw new ArgumentNullException(nameof(buildOrchestrator));
        _previewEngine = previewEngine ?? throw new ArgumentNullException(nameof(previewEngine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        OperationLog = new OperationLogViewModel();
        StatusBar = new StatusBarViewModel();
        SourceStack = new SourceStackViewModel(OnWorkspaceChangedAsync);
        AssetTable = new AssetTableViewModel(
            _registry,
            OnRowSelectionChanged,
            OnBatchSelectionChanged,
            OnSelectedRowChanged);

        ComparisonPanel = new ComparisonPanelViewModel(
            _previewEngine,
            OnPinRequestedAsync);
    }

    public void LoadWorkspaceState(WorkspaceState state, string? projectPath = null)
    {
        _workspaceState = state;
        _currentProjectPath = projectPath;
        Title = string.IsNullOrEmpty(projectPath)
            ? "SRN.CC Asset Curator — [Unsaved Project]"
            : $"SRN.CC Asset Curator — {Path.GetFileName(projectPath)}";

        var sourceVMs = state.Sources.Select(s => new SourceItemViewModel(s));
        SourceStack.UpdateSources(sourceVMs);

        var sourceLabels = state.Sources.ToDictionary(s => s.Id, s => Path.GetFileName(s.FullPath));
        AssetTable.LoadAssets(state.CuratedAssets, sourceLabels);

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

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (_workspaceService == null) return;
        var (state, _) = await _workspaceService.InitializeAsync(Array.Empty<AssetSource>()).ConfigureAwait(true);
        LoadWorkspaceState(state, null);
        OperationLog.AddEntry("INFO", "Created new empty project workspace.");
    }

    [RelayCommand]
    private async Task OpenProjectAsync(string? projectPath = null)
    {
        if (_workspaceService == null || _projectStore == null) return;

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
        var (state, _) = await _workspaceService.InitializeAsync(
            project.Sources,
            project.Pins,
            project.SelectionState,
            project.Preferences,
            isReadOnly: project.IsReadOnly).ConfigureAwait(true);
        LoadWorkspaceState(state, path);
    }

    [RelayCommand]
    private async Task SaveProjectAsync()
    {
        if (_workspaceState == null || _projectStore == null) return;
        string path = _currentProjectPath ?? Path.Combine(Directory.GetCurrentDirectory(), "project.srncc");
        await _projectStore.SaveAsync(_workspaceState, path).ConfigureAwait(true);
        _currentProjectPath = path;
        Title = $"SRN.CC Asset Curator — {Path.GetFileName(path)}";
        OperationLog.AddEntry("INFO", $"Saved project state to '{path}'.");
    }

    private async Task OnWorkspaceChangedAsync()
    {
        if (_workspaceService == null || _workspaceState == null) return;

        var sourceOrders = SourceStack.Sources.Select(s => s.Source.Id).ToList();
        var (newState, _) = await _workspaceService.ReorderSourcesAsync(sourceOrders).ConfigureAwait(true);
        _workspaceState = newState;

        var sourceLabels = _workspaceState.Sources.ToDictionary(s => s.Id, s => Path.GetFileName(s.FullPath));
        AssetTable.LoadAssets(_workspaceState.CuratedAssets, sourceLabels);
    }

    private void OnRowSelectionChanged(AssetRowViewModel row)
    {
        _ = OnRowSelectionChangedAsync(row);
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

        if (_workspaceState == null) return;
        var selectedRows = AssetTable.FilteredRows.Where(r => r.IsSelected).Select(r => r.CuratedAsset).ToList();
        var sourceMap = _workspaceState.Sources.ToDictionary(s => s.Id);
        await ComparisonPanel.UpdateSelectionAsync(selectedRows, sourceMap).ConfigureAwait(true);
    }

    private void OnBatchSelectionChanged(IEnumerable<AssetRowViewModel> rows, bool selected)
    {
        _ = OnBatchSelectionChangedAsync(rows, selected);
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

        if (_workspaceState == null) return;
        var selectedRows = AssetTable.FilteredRows.Where(r => r.IsSelected).Select(r => r.CuratedAsset).ToList();
        var sourceMap = _workspaceState.Sources.ToDictionary(s => s.Id);
        await ComparisonPanel.UpdateSelectionAsync(selectedRows, sourceMap).ConfigureAwait(true);
    }

    private void OnSelectedRowChanged(AssetRowViewModel? selectedRow)
    {
        _ = OnSelectedRowChangedAsync(selectedRow);
    }

    private async Task OnSelectedRowChangedAsync(AssetRowViewModel? selectedRow)
    {
        if (_workspaceState == null) return;
        var sourceMap = _workspaceState.Sources.ToDictionary(s => s.Id);
        var curatedAssets = AssetTable.SelectedRows.Count > 0
            ? AssetTable.SelectedRows.Select(r => r.CuratedAsset).ToList()
            : (selectedRow != null ? new[] { selectedRow.CuratedAsset } : Array.Empty<CuratedAsset>());
        await ComparisonPanel.UpdateSelectionAsync(curatedAssets, sourceMap).ConfigureAwait(true);
    }

    private async Task OnPinRequestedAsync(AssetOccurrence occurrence)
    {
        if (_workspaceService == null || _workspaceState == null) return;

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

    [RelayCommand]
    private async Task BuildHakAsync()
    {
        if (_workspaceState == null || _buildOrchestrator == null)
        {
            OperationLog.AddEntry("WARN", "No active workspace loaded for build.");
            return;
        }

        string destPath = Path.Combine(Directory.GetCurrentDirectory(), "output.hak");

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
