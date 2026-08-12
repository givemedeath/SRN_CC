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
    ReferenceOnly,
    Selected,
    Unselected
}

public enum AssetSortColumn
{
    None,
    Resref,
    Type,
    WinnerSource,
    Size
}

public partial class AssetTableViewModel : ObservableObject
{
    private readonly IResourceTypeRegistry _registry;
    private readonly Action<AssetRowViewModel>? _onRowSelectionChanged;
    private readonly Action<IEnumerable<AssetRowViewModel>, bool>? _onBatchSelectionChanged;
    private readonly Action<AssetRowViewModel?>? _onSelectedRowChanged;
    private readonly Action<IReadOnlyList<AssetRowViewModel>>? _onSelectedRowsChanged;
    private readonly Action<string>? _onSearchTextChanged;
    private readonly Action<ConflictFilterMode>? _onSelectedFilterModeChanged;
    private List<AssetRowViewModel> _allRows = new();
    private bool _isBatchUpdating;
    private bool _isRebuildingResourceTypes;

    [ObservableProperty]
    private ObservableCollection<AssetRowViewModel> _filteredRows = new();

    [ObservableProperty]
    private AssetRowViewModel? _selectedRow;

    [ObservableProperty]
    private ObservableCollection<AssetRowViewModel> _selectedRows = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ConflictFilterMode _selectedFilterMode = ConflictFilterMode.All;

    [ObservableProperty]
    private AssetSortColumn _sortColumn = AssetSortColumn.None;

    [ObservableProperty]
    private bool _sortDescending;

    public Array SortColumns => Enum.GetValues(typeof(AssetSortColumn));

    [ObservableProperty]
    private int _totalAssetCount;

    [ObservableProperty]
    private int _selectedAssetCount;

    public Array FilterModes => Enum.GetValues(typeof(ConflictFilterMode));

    /// <summary>
    /// The <see cref="SelectedResourceType"/> value meaning "do not filter by type". A sentinel
    /// string rather than null so the picker always has a selected item to display.
    /// </summary>
    public const string AllResourceTypes = "All types";

    /// <summary>
    /// <see cref="AllResourceTypes"/> followed by every type name present in the loaded workspace,
    /// ordered. Only types that actually occur are offered — a picker listing every type the
    /// registry knows would be mostly dead entries that filter to nothing.
    /// </summary>
    public ObservableCollection<string> ResourceTypes { get; } = new() { AllResourceTypes };

    [ObservableProperty]
    private string _selectedResourceType = AllResourceTypes;

    public AssetTableViewModel(
        IResourceTypeRegistry registry,
        Action<AssetRowViewModel>? onRowSelectionChanged = null,
        Action<IEnumerable<AssetRowViewModel>, bool>? onBatchSelectionChanged = null,
        Action<AssetRowViewModel?>? onSelectedRowChanged = null,
        Action<IReadOnlyList<AssetRowViewModel>>? onSelectedRowsChanged = null,
        Action<string>? onSearchTextChanged = null,
        Action<ConflictFilterMode>? onSelectedFilterModeChanged = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _onRowSelectionChanged = onRowSelectionChanged;
        _onBatchSelectionChanged = onBatchSelectionChanged;
        _onSelectedRowChanged = onSelectedRowChanged;
        _onSelectedRowsChanged = onSelectedRowsChanged;
        _onSearchTextChanged = onSearchTextChanged;
        _onSelectedFilterModeChanged = onSelectedFilterModeChanged;

        _selectedRows.CollectionChanged += (s, e) =>
        {
            _onSelectedRowsChanged?.Invoke(_selectedRows.ToList());
        };
    }

    public void LoadAssets(IEnumerable<CuratedAsset> assets, IDictionary<Guid, string> sourceLabels)
    {
        _allRows = assets.Select(a =>
        {
            string label;
            if (a.ResolvedOccurrence != null && sourceLabels.TryGetValue(a.ResolvedOccurrence.SourceId, out var l))
            {
                label = l;
            }
            else if (a.Status == ResolutionStatus.ReferenceOnly)
            {
                // No automatic winner: the identity lives only in Reference sources. The operator
                // must pin an occurrence to include it in a build.
                label = "— reference only";
            }
            else
            {
                label = "Unknown";
            }
            string typeName = _registry.TryGetExtension(a.Identity.ResourceType, out var name) ? name.ToUpperInvariant() : "UNKNOWN";
            return new AssetRowViewModel(a, typeName, label, OnRowIsSelectedChanged);
        }).ToList();

        TotalAssetCount = _allRows.Count;
        RebuildResourceTypes();
        ApplyFilters();
    }

    /// <summary>
    /// Refreshes the type picker to the types actually present. A selection that still exists is
    /// kept — reloading a workspace should not silently widen a filter the operator set — and one
    /// that no longer occurs falls back to <see cref="AllResourceTypes"/> rather than leaving the
    /// grid filtered to a type that cannot match anything.
    /// </summary>
    private void RebuildResourceTypes()
    {
        List<string> desired = new() { AllResourceTypes };
        desired.AddRange(_allRows
            .Select(r => r.ResourceTypeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase));

        // A rescan usually finds the same set of types. Leaving the bound collection untouched in
        // that case avoids disturbing the picker at all — see below for why disturbing it is costly.
        if (ResourceTypes.SequenceEqual(desired, StringComparer.Ordinal))
        {
            return;
        }

        string restored = desired.Contains(SelectedResourceType, StringComparer.OrdinalIgnoreCase)
            ? SelectedResourceType
            : AllResourceTypes;

        // Clearing an ObservableCollection that a ComboBox binds its SelectedItem to makes the
        // control reset that selection, and the two-way binding writes the reset straight back here.
        // Left unguarded, that intermediate value would run a filter pass matching nothing — an
        // empty grid, briefly, and a wasted traversal of every row. The suppression holds until the
        // real selection is back; LoadAssets runs the one filter pass that matters afterwards.
        _isRebuildingResourceTypes = true;
        try
        {
            ResourceTypes.Clear();
            foreach (string typeName in desired)
            {
                ResourceTypes.Add(typeName);
            }

            SelectedResourceType = restored;
        }
        finally
        {
            _isRebuildingResourceTypes = false;
        }
    }

    partial void OnSelectedResourceTypeChanged(string value)
    {
        if (_isRebuildingResourceTypes)
        {
            return;
        }

        ApplyFilters();
    }

    private void OnRowIsSelectedChanged(AssetRowViewModel row)
    {
        if (_isBatchUpdating) return;
        SelectedAssetCount = _allRows.Count(r => r.IsSelected);
        ApplyFilters();
        _onRowSelectionChanged?.Invoke(row);
    }

    partial void OnSelectedRowChanged(AssetRowViewModel? value)
    {
        _onSelectedRowChanged?.Invoke(value);
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
        _onSearchTextChanged?.Invoke(value);
    }

    partial void OnSelectedFilterModeChanged(ConflictFilterMode value)
    {
        ApplyFilters();
        _onSelectedFilterModeChanged?.Invoke(value);
    }

    partial void OnSortColumnChanged(AssetSortColumn value) => ApplyFilters();

    partial void OnSortDescendingChanged(bool value) => ApplyFilters();

    /// <summary>Flips the sort direction (a no-op display change when no sort column is chosen).</summary>
    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    public void ApplyFilters()
    {
        IEnumerable<AssetRowViewModel> query = _allRows;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string search = SearchText.Trim().ToLowerInvariant();
            query = query.Where(r => r.Resref.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                     r.ResourceTypeName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(SelectedResourceType, AllResourceTypes, StringComparison.Ordinal))
        {
            query = query.Where(r => string.Equals(
                r.ResourceTypeName, SelectedResourceType, StringComparison.OrdinalIgnoreCase));
        }

        query = SelectedFilterMode switch
        {
            ConflictFilterMode.Colliding => query.Where(r => r.CuratedAsset.HasCrossSourceCollision),
            ConflictFilterMode.Duplicated => query.Where(r => r.CuratedAsset.HasSameSourceDuplicate),
            ConflictFilterMode.Conflicting => query.Where(r => r.CuratedAsset.HasDifferingPayloads),
            ConflictFilterMode.Pinned => query.Where(r => r.IsPinned),
            ConflictFilterMode.InvalidPin => query.Where(r => r.CuratedAsset.HasInvalidPin),
            ConflictFilterMode.ReferenceOnly => query.Where(r => r.CuratedAsset.Status == ResolutionStatus.ReferenceOnly),
            ConflictFilterMode.Selected => query.Where(r => r.IsSelected),
            ConflictFilterMode.Unselected => query.Where(r => !r.IsSelected),
            _ => query
        };

        query = ApplySort(query);

        var list = query.ToList();
        FilteredRows = new ObservableCollection<AssetRowViewModel>(list);
        SelectedAssetCount = _allRows.Count(r => r.IsSelected);
    }

    /// <summary>
    /// Applies the chosen sort as the final step, with a resref tie-break for a stable order. Sorting
    /// only runs when a column is selected, so the default (unsorted) path pays no ordering cost.
    /// </summary>
    private IEnumerable<AssetRowViewModel> ApplySort(IEnumerable<AssetRowViewModel> query)
    {
        IOrderedEnumerable<AssetRowViewModel> ordered = SortColumn switch
        {
            AssetSortColumn.Resref => OrderText(query, r => r.Resref),
            AssetSortColumn.Type => OrderText(query, r => r.ResourceTypeName),
            AssetSortColumn.WinnerSource => OrderText(query, r => r.WinnerSourceLabel),
            AssetSortColumn.Size => SortDescending
                ? query.OrderByDescending(r => r.SizeBytes)
                : query.OrderBy(r => r.SizeBytes),
            _ => null!
        };

        if (ordered is null)
        {
            return query;
        }

        return ordered.ThenBy(r => r.Resref, StringComparer.OrdinalIgnoreCase);

        IOrderedEnumerable<AssetRowViewModel> OrderText(IEnumerable<AssetRowViewModel> source, Func<AssetRowViewModel, string> key) =>
            SortDescending
                ? source.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(key, StringComparer.OrdinalIgnoreCase);
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

        ApplyFilters();
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

        ApplyFilters();
        SelectedAssetCount = _allRows.Count(r => r.IsSelected);
        _onBatchSelectionChanged?.Invoke(FilteredRows, false);
    }
}
