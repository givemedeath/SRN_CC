using SRN.CC.Core.Identity;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Cache;

namespace SRN.CC.Infrastructure.Services;

public sealed class WorkspaceService : IWorkspaceService
{
    private readonly IAssetIndexService _indexService;
    private readonly IWorkspaceResolver _resolver;
    private readonly AssetHashCache _hashCache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private WorkspaceState _currentState;

    public WorkspaceState CurrentState => _currentState;

    public WorkspaceService(
        IAssetIndexService indexService,
        IWorkspaceResolver resolver,
        AssetHashCache? hashCache = null)
    {
        _indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _hashCache = hashCache ?? (resolver as WorkspaceResolver)?.HashCache ?? new AssetHashCache();

        _currentState = new WorkspaceState(
            sources: Array.Empty<AssetSource>(),
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: Array.Empty<CuratedAsset>(),
            selectionState: SelectionState.IncludeAll(),
            pins: Array.Empty<WinnerPin>(),
            preferences: new ProjectPreferences(),
            isReadOnly: false);
    }

    public async Task<(WorkspaceState State, ChangedInputReport Report)> InitializeAsync(
        IReadOnlyList<AssetSource> sources,
        IReadOnlyList<WinnerPin>? pins = null,
        SelectionState? selectionState = null,
        ProjectPreferences? preferences = null,
        bool isReadOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkspaceState initialState = _currentState;
            pins ??= Array.Empty<WinnerPin>();
            selectionState ??= SelectionState.IncludeAll();
            preferences ??= new ProjectPreferences();

            Dictionary<Guid, SourceIndexSnapshot> snapshots = new();
            List<AssetSource> scannedSources = new();

            foreach (AssetSource s in sources)
            {
                (AssetSource updatedSource, SourceIndexSnapshot? snapshot) = await IndexOrMarkUnavailableAsync(s, cancellationToken).ConfigureAwait(false);
                scannedSources.Add(updatedSource);
                if (snapshot is not null)
                {
                    snapshots[updatedSource.Id] = snapshot;
                }
            }

            foreach (AssetSource s in scannedSources)
            {
                _hashCache.InvalidateSource(s.Id);
            }

            WorkspaceState newState = await _resolver.ResolveAsync(
                scannedSources,
                snapshots,
                pins,
                selectionState,
                preferences,
                isReadOnly: isReadOnly,
                cancellationToken).ConfigureAwait(false);

            ChangedInputReport report = CompareStates(initialState, newState);
            _currentState = newState;
            return (newState, report);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(WorkspaceState State, ChangedInputReport Report)> ReorderSourcesAsync(
        IReadOnlyList<Guid> sourceIdsInOrder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceIdsInOrder);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            WorkspaceState previous = _currentState;
            Dictionary<Guid, AssetSource> currentSources = previous.Sources.ToDictionary(s => s.Id);

            List<AssetSource> reordered = new();
            for (int i = 0; i < sourceIdsInOrder.Count; i++)
            {
                Guid id = sourceIdsInOrder[i];
                if (currentSources.TryGetValue(id, out AssetSource? existing))
                {
                    reordered.Add(existing with { PriorityOrdinal = i });
                    currentSources.Remove(id);
                }
            }

            // Append any remaining sources
            int nextOrdinal = reordered.Count;
            foreach (AssetSource remaining in currentSources.Values)
            {
                reordered.Add(remaining with { PriorityOrdinal = nextOrdinal++ });
            }

            WorkspaceState newState = await _resolver.ResolveAsync(
                reordered,
                previous.Snapshots,
                previous.Pins,
                previous.SelectionState,
                previous.Preferences,
                previous.IsReadOnly,
                cancellationToken).ConfigureAwait(false);

            ChangedInputReport report = CompareStates(previous, newState);
            _currentState = newState;
            return (newState, report);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(WorkspaceState State, ChangedInputReport Report)> RescanAsync(
        IEnumerable<Guid>? sourceIdsToRescan = null,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            WorkspaceState previous = _currentState;
            HashSet<Guid> targetSet = sourceIdsToRescan is null
                ? previous.Sources.Select(s => s.Id).ToHashSet()
                : sourceIdsToRescan.ToHashSet();

            List<AssetSource> updatedSources = new();
            Dictionary<Guid, SourceIndexSnapshot> updatedSnapshots = new(previous.Snapshots);

            foreach (AssetSource source in previous.Sources)
            {
                if (targetSet.Contains(source.Id))
                {
                    _hashCache.InvalidateSource(source.Id);

                    (AssetSource updatedSource, SourceIndexSnapshot? snapshot) = await IndexOrMarkUnavailableAsync(source, cancellationToken).ConfigureAwait(false);
                    updatedSources.Add(updatedSource);
                    if (snapshot is not null)
                    {
                        updatedSnapshots[source.Id] = snapshot;
                    }
                }
                else
                {
                    updatedSources.Add(source);
                }
            }

            WorkspaceState newState = await _resolver.ResolveAsync(
                updatedSources,
                updatedSnapshots,
                previous.Pins,
                previous.SelectionState,
                previous.Preferences,
                previous.IsReadOnly,
                cancellationToken).ConfigureAwait(false);

            ChangedInputReport report = CompareStates(previous, newState);
            _currentState = newState;
            return (newState, report);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(WorkspaceState State, ChangedInputReport Report)> RelocateSourceAsync(
        Guid sourceId,
        string newPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            WorkspaceState previous = _currentState;
            AssetSource? targetSource = previous.Sources.FirstOrDefault(s => s.Id == sourceId);
            if (targetSource is null)
            {
                throw new ArgumentException($"Source with ID {sourceId} was not found in workspace.", nameof(sourceId));
            }

            string fullNewPath = Path.GetFullPath(newPath);
            AssetSource candidate = targetSource with { FullPath = fullNewPath, IsAvailable = true, Fingerprint = null };

            // Attempt scan on new candidate path
            _hashCache.InvalidateSource(sourceId);
            (AssetSource scannedCandidate, SourceIndexSnapshot? candidateSnapshot) = await IndexOrMarkUnavailableAsync(candidate, cancellationToken).ConfigureAwait(false);

            if (!scannedCandidate.IsAvailable || candidateSnapshot is null)
            {
                throw new InvalidOperationException($"Relocation candidate at '{newPath}' could not be read or indexed.");
            }

            List<AssetSource> updatedSources = previous.Sources.Select(s => s.Id == sourceId ? scannedCandidate : s).ToList();
            Dictionary<Guid, SourceIndexSnapshot> updatedSnapshots = new(previous.Snapshots)
            {
                [sourceId] = candidateSnapshot
            };

            WorkspaceState newState = await _resolver.ResolveAsync(
                updatedSources,
                updatedSnapshots,
                previous.Pins,
                previous.SelectionState,
                previous.Preferences,
                previous.IsReadOnly,
                cancellationToken).ConfigureAwait(false);

            ChangedInputReport report = CompareStates(previous, newState);
            _currentState = newState;
            return (newState, report);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(WorkspaceState State, ChangedInputReport Report)> SetSourceModeAsync(
        Guid sourceId,
        SourceMode mode,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            WorkspaceState previous = _currentState;
            AssetSource? target = previous.Sources.FirstOrDefault(s => s.Id == sourceId);
            if (target is null)
            {
                throw new ArgumentException($"Source with ID {sourceId} was not found in workspace.", nameof(sourceId));
            }

            // Mode is a resolve-time filter over the existing snapshots, so no re-index is needed;
            // toggling a source back to Full is instant. Pins into a source going Hidden are
            // intentionally kept (not removed) so they surface as invalid pins — contrast
            // RemoveSourceAsync in the app layer, which drops pins to a removed source.
            List<AssetSource> updatedSources = previous.Sources
                .Select(s => s.Id == sourceId ? s with { Mode = mode } : s)
                .ToList();

            WorkspaceState newState = await _resolver.ResolveAsync(
                updatedSources,
                previous.Snapshots,
                previous.Pins,
                previous.SelectionState,
                previous.Preferences,
                previous.IsReadOnly,
                cancellationToken).ConfigureAwait(false);

            ChangedInputReport report = CompareStates(previous, newState);
            _currentState = newState;
            return (newState, report);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WorkspaceState> PinAsync(WinnerPin pin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pin);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            WorkspaceState previous = _currentState;
            List<WinnerPin> updatedPins = previous.Pins.Where(p => !p.Identity.Equals(pin.Identity)).ToList();
            updatedPins.Add(pin);

            WorkspaceState newState = await _resolver.ResolveAsync(
                previous.Sources,
                previous.Snapshots,
                updatedPins,
                previous.SelectionState,
                previous.Preferences,
                previous.IsReadOnly,
                cancellationToken).ConfigureAwait(false);

            _currentState = newState;
            return newState;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WorkspaceState> UnpinAsync(AssetIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            WorkspaceState previous = _currentState;
            List<WinnerPin> updatedPins = previous.Pins.Where(p => !p.Identity.Equals(identity)).ToList();

            WorkspaceState newState = await _resolver.ResolveAsync(
                previous.Sources,
                previous.Snapshots,
                updatedPins,
                previous.SelectionState,
                previous.Preferences,
                previous.IsReadOnly,
                cancellationToken).ConfigureAwait(false);

            _currentState = newState;
            return newState;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WorkspaceState> UpdateSelectionAsync(SelectionState newSelectionState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newSelectionState);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            WorkspaceState previous = _currentState;

            WorkspaceState newState = await _resolver.ResolveAsync(
                previous.Sources,
                previous.Snapshots,
                previous.Pins,
                newSelectionState,
                previous.Preferences,
                previous.IsReadOnly,
                cancellationToken).ConfigureAwait(false);

            _currentState = newState;
            return newState;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WorkspaceState> LoadProjectStateAsync(WorkspaceState newState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newState);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _currentState = newState;
            return newState;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EnsureWritable()
    {
        if (_currentState.IsReadOnly)
        {
            throw new InvalidOperationException("Workspace state is read-only (newer schema) and cannot be mutated.");
        }
    }

    private async Task<(AssetSource Source, SourceIndexSnapshot? Snapshot)> IndexOrMarkUnavailableAsync(AssetSource source, CancellationToken cancellationToken)
    {
        try
        {
            SourceIndexSnapshot snapshot = await _indexService.IndexAsync(source, progress: null, cancellationToken).ConfigureAwait(false);
            if (!snapshot.Source.IsAvailable)
            {
                AssetSource unavailable = source with { IsAvailable = false };
                return (unavailable, null);
            }
            AssetSource updatedSource = source with { IsAvailable = true, Fingerprint = snapshot.Fingerprint };
            return (updatedSource, snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            AssetSource unavailable = source with { IsAvailable = false };
            return (unavailable, null);
        }
    }

    private static ChangedInputReport CompareStates(WorkspaceState before, WorkspaceState after)
    {
        HashSet<Guid> beforeSourceIds = before.Sources.Select(s => s.Id).ToHashSet();
        HashSet<Guid> afterSourceIds = after.Sources.Select(s => s.Id).ToHashSet();

        List<AssetSource> addedSources = after.Sources.Where(s => !beforeSourceIds.Contains(s.Id)).ToList();
        List<AssetSource> removedSources = before.Sources.Where(s => !afterSourceIds.Contains(s.Id)).ToList();

        Dictionary<Guid, AssetSource> beforeSourceMap = before.Sources.ToDictionary(s => s.Id);
        List<Guid> availabilityTransitions = new();
        List<Guid> modeChanges = new();
        List<Guid> fingerprintChanges = new();

        foreach (AssetSource afterSource in after.Sources)
        {
            if (beforeSourceMap.TryGetValue(afterSource.Id, out AssetSource? beforeSource))
            {
                if (beforeSource.IsAvailable != afterSource.IsAvailable)
                {
                    availabilityTransitions.Add(afterSource.Id);
                }
                if (beforeSource.Mode != afterSource.Mode)
                {
                    modeChanges.Add(afterSource.Id);
                }
                if (!Equals(beforeSource.Fingerprint, afterSource.Fingerprint))
                {
                    fingerprintChanges.Add(afterSource.Id);
                }
            }
        }

        Dictionary<AssetIdentity, CuratedAsset> beforeAssetMap = before.CuratedAssets.ToDictionary(a => a.Identity);
        Dictionary<AssetIdentity, CuratedAsset> afterAssetMap = after.CuratedAssets.ToDictionary(a => a.Identity);

        List<AssetIdentity> addedIdentities = after.CuratedAssets.Where(a => !beforeAssetMap.ContainsKey(a.Identity)).Select(a => a.Identity).ToList();
        List<AssetIdentity> removedIdentities = before.CuratedAssets.Where(a => !afterAssetMap.ContainsKey(a.Identity)).Select(a => a.Identity).ToList();

        List<AssetIdentity> winnerChanges = new();
        List<AssetIdentity> pinReattachments = new();
        List<AssetIdentity> pinInvalidations = new();
        List<AssetIdentity> selectionChanges = new();

        foreach (CuratedAsset afterAsset in after.CuratedAssets)
        {
            if (beforeAssetMap.TryGetValue(afterAsset.Identity, out CuratedAsset? beforeAsset))
            {
                if (!Equals(beforeAsset.ResolvedOccurrence, afterAsset.ResolvedOccurrence) || beforeAsset.Status != afterAsset.Status)
                {
                    winnerChanges.Add(afterAsset.Identity);
                }

                if (beforeAsset.Pin is not null && afterAsset.Pin is not null && !Equals(beforeAsset.Pin.Locator, afterAsset.Pin.Locator))
                {
                    pinReattachments.Add(afterAsset.Identity);
                }

                if (!beforeAsset.HasInvalidPin && afterAsset.HasInvalidPin)
                {
                    pinInvalidations.Add(afterAsset.Identity);
                }

                if (beforeAsset.IsSelected != afterAsset.IsSelected)
                {
                    selectionChanges.Add(afterAsset.Identity);
                }
            }
            else
            {
                if (afterAsset.IsSelected)
                {
                    selectionChanges.Add(afterAsset.Identity);
                }
            }
        }

        return new ChangedInputReport(
            addedSources: addedSources,
            removedSources: removedSources,
            availabilityTransitions: availabilityTransitions,
            modeChanges: modeChanges,
            fingerprintChanges: fingerprintChanges,
            addedIdentities: addedIdentities,
            removedIdentities: removedIdentities,
            winnerChanges: winnerChanges,
            pinReattachments: pinReattachments,
            pinInvalidations: pinInvalidations,
            selectionChanges: selectionChanges);
    }
}
