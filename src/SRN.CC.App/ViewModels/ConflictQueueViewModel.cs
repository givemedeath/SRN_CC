using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;

namespace SRN.CC.App.ViewModels;

/// <summary>
/// A work queue over conflicted identities. It lists only cross-source collisions and unresolved
/// same-source duplicates, presents each identity's candidate occurrences side by side, and lets the
/// operator burn the queue down by pinning a winner for each. Membership and the resolved counter are
/// recomputed from workspace state on every <see cref="Load"/>.
/// </summary>
public partial class ConflictQueueViewModel : ObservableObject
{
    private readonly Func<AssetOccurrence, Task> _onMakeWinner;
    private readonly Func<ushort, string> _resourceTypeName;
    private readonly Func<Guid, Task>? _onPreferSource;
    private readonly Func<AssetOccurrence, CancellationToken, Task<byte[]?>>? _hashLoader;

    [ObservableProperty]
    private ObservableCollection<ConflictItemViewModel> _conflicts = new();

    /// <summary>Eligible sources for bulk "prefer source" (Full and Reference; never Hidden).</summary>
    [ObservableProperty]
    private ObservableCollection<PreferSourceOptionViewModel> _preferSources = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    private ConflictItemViewModel? _current;

    [ObservableProperty]
    private int _resolvedCount;

    [ObservableProperty]
    private int _totalCount;

    public ConflictQueueViewModel(
        Func<AssetOccurrence, Task> onMakeWinner,
        Func<ushort, string> resourceTypeName,
        Func<Guid, Task>? onPreferSource = null,
        Func<AssetOccurrence, CancellationToken, Task<byte[]?>>? hashLoader = null)
    {
        _onMakeWinner = onMakeWinner ?? throw new ArgumentNullException(nameof(onMakeWinner));
        _resourceTypeName = resourceTypeName ?? throw new ArgumentNullException(nameof(resourceTypeName));
        _onPreferSource = onPreferSource;
        _hashLoader = hashLoader;
    }

    public string ProgressText => $"{ResolvedCount} of {TotalCount} resolved";
    public bool HasConflicts => TotalCount > 0;

    /// <summary>Tab-header label, e.g. "Conflicts (12)".</summary>
    public string HeaderText => $"Conflicts ({TotalCount})";

    partial void OnResolvedCountChanged(int value) => OnPropertyChanged(nameof(ProgressText));

    // When the operator lands on a conflict, compute the hashes for its candidate cards so they can be
    // compared. Indexed occurrences carry no precomputed hash; each candidate loads at most once.
    partial void OnCurrentChanged(ConflictItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }
        foreach (ConflictCandidateViewModel candidate in value.Candidates)
        {
            _ = candidate.EnsureHashLoadedAsync();
        }
    }

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(HeaderText));
    }

    /// <summary>
    /// True when an identity belongs in the queue: a cross-source collision, or a same-source
    /// duplicate that could not be resolved automatically.
    /// </summary>
    public static bool IsConflict(CuratedAsset asset) =>
        asset.HasCrossSourceCollision || asset.Status == ResolutionStatus.UnresolvedDuplicate;

    /// <summary>Rebuilds the queue from workspace state, keeping the cursor on the same identity.</summary>
    public void Load(WorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Dictionary<Guid, AssetSource> sourceInfo = state.Sources.ToDictionary(s => s.Id);
        _sourceLabelLookup = id => sourceInfo.TryGetValue(id, out var s) ? Path.GetFileName(s.FullPath) : "Unknown";
        _sourcePriorityLookup = id => sourceInfo.TryGetValue(id, out var s) ? s.PriorityOrdinal : 0;
        _sourceModeLookup = id => sourceInfo.TryGetValue(id, out var s) ? s.Mode : SourceMode.Full;

        AssetIdentity? previousIdentity = Current?.Identity;

        List<ConflictItemViewModel> items = state.CuratedAssets
            .Where(IsConflict)
            .OrderBy(a => a.Identity.Resref, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Identity.ResourceType)
            .Select(a => BuildItem(a, a.ResolvedOccurrence))
            .ToList();

        Conflicts = new ObservableCollection<ConflictItemViewModel>(items);
        TotalCount = items.Count;
        ResolvedCount = items.Count(i => i.IsResolved);

        // Offer bulk "prefer source" over Full and Reference sources (Hidden contributes nothing).
        if (_onPreferSource is not null)
        {
            PreferSources = new ObservableCollection<PreferSourceOptionViewModel>(
                state.Sources
                    .Where(s => s.Mode != SourceMode.Hidden)
                    .OrderBy(s => s.PriorityOrdinal)
                    .Select(s => new PreferSourceOptionViewModel(s.Id, Path.GetFileName(s.FullPath), _onPreferSource)));
        }

        // Keep the operator where they were. If the identity they were looking at is gone or now
        // resolved, advance to the next unresolved conflict so the queue keeps flowing.
        ConflictItemViewModel? restored = previousIdentity is null
            ? null
            : items.FirstOrDefault(i => i.Identity.Equals(previousIdentity));

        if (restored is null || restored.IsResolved)
        {
            restored = items.FirstOrDefault(i => !i.IsResolved) ?? items.FirstOrDefault();
        }

        Current = restored;
    }

    private ConflictItemViewModel BuildItem(CuratedAsset asset, AssetOccurrence? winner)
    {
        List<ConflictCandidateViewModel> candidates = asset.AllOccurrences
            .Select(o => new ConflictCandidateViewModel(
                o,
                _sourceLabelLookup(o.SourceId),
                _sourcePriorityLookup(o.SourceId),
                _sourceModeLookup(o.SourceId),
                isCurrentWinner: winner is not null && winner.SourceId == o.SourceId && Equals(winner.Locator, o.Locator),
                _onMakeWinner,
                _hashLoader))
            .ToList();

        return new ConflictItemViewModel(asset, _resourceTypeName(asset.Identity.ResourceType), candidates);
    }

    // Source-metadata lookups are set for the duration of a Load() pass.
    private Func<Guid, string> _sourceLabelLookup = _ => "Unknown";
    private Func<Guid, int> _sourcePriorityLookup = _ => 0;
    private Func<Guid, SourceMode> _sourceModeLookup = _ => SourceMode.Full;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        int idx = Current is null ? -1 : Conflicts.IndexOf(Current);
        if (idx >= 0 && idx < Conflicts.Count - 1)
        {
            Current = Conflicts[idx + 1];
        }
    }

    private bool CanGoNext() => Current is not null && Conflicts.IndexOf(Current) < Conflicts.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous()
    {
        int idx = Current is null ? -1 : Conflicts.IndexOf(Current);
        if (idx > 0)
        {
            Current = Conflicts[idx - 1];
        }
    }

    private bool CanGoPrevious() => Current is not null && Conflicts.IndexOf(Current) > 0;
}
