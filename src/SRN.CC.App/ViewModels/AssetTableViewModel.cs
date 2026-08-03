using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Services;

namespace SRN.CC.App.ViewModels;

public enum ConflictFilterMode
{
    All,
    Colliding,
    Duplicated,
    Conflicting,
    Pinned,
    InvalidPin,
    Selected,
    Unselected
}

public partial class AssetTableViewModel : ObservableObject
{
    private readonly IResourceTypeRegistry _registry;
    private readonly Action<AssetRowViewModel>? _onRowSelectionChanged;
    private readonly Action<IEnumerable<AssetRowViewModel>, bool>? _onBatchSelectionChanged;
    private readonly Action<AssetRowViewModel?>? _onSelectedRowChanged;
    private List<AssetRowViewModel> _allRows = new();
    private bool _isBatchUpdating;

    [ObservableProperty]
    private ObservableCollection<AssetRowViewModel> _filteredRows = new();

    [ObservableProperty]
    private AssetRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ConflictFilterMode _selectedFilterMode = ConflictFilterMode.All;

    [ObservableProperty]
    private int _totalAssetCount;

    [ObservableProperty]
    private int _selectedAssetCount;

    public Array FilterModes => Enum.GetValues(typeof(ConflictFilterMode));

    public AssetTableViewModel(
        IResourceTypeRegistry registry,
        Action<AssetRowViewModel>? onRowSelectionChanged = null,
        Action<IEnumerable<AssetRowViewModel>, bool>? onBatchSelectionChanged = null,
        Action<AssetRowViewModel?>? onSelectedRowChanged = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _onRowSelectionChanged = onRowSelectionChanged;
        _onBatchSelectionChanged = onBatchSelectionChanged;
        _onSelectedRowChanged = onSelectedRowChanged;
    }

    public void LoadAssets(IEnumerable<CuratedAsset> assets, IDictionary<Guid, string> sourceLabels)
    {
        _allRows = assets.Select(a =>
        {
            string label = a.ResolvedOccurrence != null && sourceLabels.TryGetValue(a.ResolvedOccurrence.SourceId, out var l)
                ? l : "Unknown";
            string typeName = _registry.TryGetExtension(a.Identity.ResourceType, out var name) ? name.ToUpperInvariant() : "UNKNOWN";
            return new AssetRowViewModel(a, typeName, label, OnRowIsSelectedChanged);
        }).ToList();

        TotalAssetCount = _allRows.Count;
        ApplyFilters();
    }

    private void OnRowIsSelectedChanged(AssetRowViewModel row)
    {
        if (_isBatchUpdating) return;
        SelectedAssetCount = _allRows.Count(r => r.IsSelected);
        _onRowSelectionChanged?.Invoke(row);
    }

    partial void OnSelectedRowChanged(AssetRowViewModel? value)
    {
        _onSelectedRowChanged?.Invoke(value);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedFilterModeChanged(ConflictFilterMode value) => ApplyFilters();

    public void ApplyFilters()
    {
        IEnumerable<AssetRowViewModel> query = _allRows;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string search = SearchText.Trim().ToLowerInvariant();
            query = query.Where(r => r.Resref.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                     r.ResourceTypeName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        query = SelectedFilterMode switch
        {
            ConflictFilterMode.Colliding => query.Where(r => r.CuratedAsset.HasCrossSourceCollision),
            ConflictFilterMode.Duplicated => query.Where(r => r.CuratedAsset.HasSameSourceDuplicate),
            ConflictFilterMode.Conflicting => query.Where(r => r.CuratedAsset.HasDifferingPayloads),
            ConflictFilterMode.Pinned => query.Where(r => r.IsPinned),
            ConflictFilterMode.InvalidPin => query.Where(r => r.CuratedAsset.HasInvalidPin),
            ConflictFilterMode.Selected => query.Where(r => r.IsSelected),
            ConflictFilterMode.Unselected => query.Where(r => !r.IsSelected),
            _ => query
        };

        var list = query.ToList();
        FilteredRows = new ObservableCollection<AssetRowViewModel>(list);
        SelectedAssetCount = _allRows.Count(r => r.IsSelected);
    }

    [RelayCommand]
    private void IncludeAllFiltered()
    {
        _isBatchUpdating = true;
        try
        {
            foreach (var row in FilteredRows)
            {
                row.IsSelected = true;
            }
        }
        finally
        {
            _isBatchUpdating = false;
        }

        SelectedAssetCount = _allRows.Count(r => r.IsSelected);
        _onBatchSelectionChanged?.Invoke(FilteredRows, true);
    }

    [RelayCommand]
    private void ExcludeAllFiltered()
    {
        _isBatchUpdating = true;
        try
        {
            foreach (var row in FilteredRows)
            {
                row.IsSelected = false;
            }
        }
        finally
        {
            _isBatchUpdating = false;
        }

        SelectedAssetCount = _allRows.Count(r => r.IsSelected);
        _onBatchSelectionChanged?.Invoke(FilteredRows, false);
    }
}
