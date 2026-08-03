using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Cache;

namespace SRN.CC.Infrastructure.Services;

public sealed class WorkspaceResolver : IWorkspaceResolver
{
    private readonly IStreamingHashService _hashService;
    private readonly AssetHashCache _hashCache;

    public WorkspaceResolver(IStreamingHashService hashService, AssetHashCache? hashCache = null)
    {
        _hashService = hashService ?? throw new ArgumentNullException(nameof(hashService));
        _hashCache = hashCache ?? new AssetHashCache();
    }

    public async Task<WorkspaceState> ResolveAsync(
        IReadOnlyList<AssetSource> sources,
        IReadOnlyDictionary<Guid, SourceIndexSnapshot> snapshots,
        IReadOnlyList<WinnerPin> pins,
        SelectionState selectionState,
        ProjectPreferences? preferences = null,
        bool isReadOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(selectionState);

        // Normalize source priority ordinals to contiguous 0..n-1
        List<AssetSource> normalizedSources = NormalizeSourcePriorities(sources);
        Dictionary<Guid, AssetSource> sourceLookup = normalizedSources.ToDictionary(s => s.Id);
        Dictionary<AssetIdentity, WinnerPin> pinLookup = pins.ToDictionary(p => p.Identity);

        // Group occurrences across all snapshots by canonical AssetIdentity
        Dictionary<AssetIdentity, List<AssetOccurrence>> identityGroupMap = new();

        foreach (AssetSource source in normalizedSources)
        {
            if (snapshots.TryGetValue(source.Id, out SourceIndexSnapshot? snapshot) && snapshot is not null)
            {
                foreach (AssetOccurrence occ in snapshot.Records.Where(r => r.Occurrence != null).Select(r => r.Occurrence!))
                {
                    if (!identityGroupMap.TryGetValue(occ.Identity, out List<AssetOccurrence>? list))
                    {
                        list = new List<AssetOccurrence>();
                        identityGroupMap[occ.Identity] = list;
                    }
                    list.Add(occ);
                }
            }
        }

        Dictionary<AssetIdentity, WinnerPin> finalPinsMap = new();
        List<CuratedAsset> curatedAssets = new();

        // Process each identity deterministically (ordered by identity.ToString())
        List<AssetIdentity> sortedIdentities = identityGroupMap.Keys
            .OrderBy(id => id.OriginalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => id.ResourceType)
            .ToList();

        foreach (AssetIdentity identity in sortedIdentities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<AssetOccurrence> allOccurrences = identityGroupMap[identity];

            // Independent diagnostic flags
            HashSet<Guid> distinctSources = allOccurrences.Select(o => o.SourceId).ToHashSet();
            bool hasCrossSourceCollision = distinctSources.Count > 1;

            bool hasSameSourceDuplicate = allOccurrences
                .GroupBy(o => o.SourceId)
                .Any(g => g.Count() > 1);

            bool hasCaseOnlyNamingDifference = allOccurrences
                .Select(o => o.OriginalName)
                .Distinct(StringComparer.Ordinal)
                .Count() > 1;

            bool hasDifferingPayloads = false;
            bool hasUnreadableOccurrence = false;
            bool hasInvalidPin = false;

            AssetOccurrence? resolvedOccurrence = null;
            WinnerPin? activePin = pinLookup.GetValueOrDefault(identity);
            WinnerPin? finalPin = activePin;
            byte[]? resolvedSha256 = null;
            ResolutionStatus status;

            if (activePin is not null)
            {
                // Pin resolution
                (resolvedOccurrence, finalPin, status, byte[]? pinHash, bool pinInvalid, bool unreadable, bool payloadConflict) =
                    await ResolvePinnedIdentityAsync(activePin, allOccurrences, sourceLookup, cancellationToken).ConfigureAwait(false);

                hasInvalidPin = pinInvalid;
                hasUnreadableOccurrence |= unreadable;
                hasDifferingPayloads |= payloadConflict;
                resolvedSha256 = pinHash;
                if (finalPin is not null)
                {
                    finalPinsMap[identity] = finalPin;
                }
            }
            else
            {
                // Automatic priority resolution
                (resolvedOccurrence, status, byte[]? autoHash, bool unreadable, bool payloadConflict) =
                    await ResolveAutomaticIdentityAsync(identity, allOccurrences, normalizedSources, cancellationToken).ConfigureAwait(false);

                hasUnreadableOccurrence |= unreadable;
                hasDifferingPayloads |= payloadConflict;
                resolvedSha256 = autoHash;
            }

            // Selection evaluation
            bool isSelected = false;
            if (status == ResolutionStatus.Resolved && resolvedOccurrence is not null)
            {
                isSelected = selectionState.IsSelected(identity);
            }

            CuratedAsset asset = new(
                identity: identity,
                allOccurrences: allOccurrences.AsReadOnly(),
                resolvedOccurrence: resolvedOccurrence,
                pin: finalPin,
                status: status,
                isSelected: isSelected,
                resolvedSha256: resolvedSha256,
                hasCrossSourceCollision: hasCrossSourceCollision,
                hasSameSourceDuplicate: hasSameSourceDuplicate,
                hasDifferingPayloads: hasDifferingPayloads,
                hasCaseOnlyNamingDifference: hasCaseOnlyNamingDifference,
                hasUnreadableOccurrence: hasUnreadableOccurrence,
                hasInvalidPin: hasInvalidPin);

            curatedAssets.Add(asset);
        }

        List<WinnerPin> updatedPins = pins.Select(p => finalPinsMap.GetValueOrDefault(p.Identity, p)).ToList();

        return new WorkspaceState(
            sources: normalizedSources.AsReadOnly(),
            snapshots: snapshots,
            curatedAssets: curatedAssets.AsReadOnly(),
            selectionState: selectionState,
            pins: updatedPins.AsReadOnly(),
            preferences: preferences,
            isReadOnly: isReadOnly);
    }

    private static List<AssetSource> NormalizeSourcePriorities(IReadOnlyList<AssetSource> sources)
    {
        List<AssetSource> ordered = sources
            .OrderBy(s => s.PriorityOrdinal)
            .ThenBy(s => s.Id)
            .ToList();

        List<AssetSource> normalized = new();
        for (int i = 0; i < ordered.Count; i++)
        {
            AssetSource s = ordered[i];
            if (s.PriorityOrdinal != i)
            {
                normalized.Add(new AssetSource(s.Id, s.Kind, s.FullPath, i, s.IsAvailable, s.Fingerprint));
            }
            else
            {
                normalized.Add(s);
            }
        }
        return normalized;
    }

    private async Task<(AssetOccurrence? Winner, WinnerPin? FinalPin, ResolutionStatus Status, byte[]? Hash, bool InvalidPin, bool Unreadable, bool PayloadConflict)>
        ResolvePinnedIdentityAsync(
            WinnerPin pin,
            List<AssetOccurrence> occurrences,
            Dictionary<Guid, AssetSource> sourceLookup,
            CancellationToken cancellationToken)
    {
        if (!sourceLookup.TryGetValue(pin.SourceId, out AssetSource? pinnedSource) || !pinnedSource.IsAvailable)
        {
            return (null, pin, ResolutionStatus.InvalidPin, null, InvalidPin: true, Unreadable: false, PayloadConflict: false);
        }

        // Try exact locator first
        AssetOccurrence? exactMatch = occurrences.FirstOrDefault(o => o.SourceId == pin.SourceId && Equals(o.Locator, pin.Locator));

        if (exactMatch is not null)
        {
            byte[]? exactHash = await GetOrComputeHashAsync(pinnedSource, exactMatch, cancellationToken).ConfigureAwait(false);
            if (exactHash is null)
            {
                return (null, pin, ResolutionStatus.InvalidPin, null, InvalidPin: true, Unreadable: true, PayloadConflict: false);
            }

            if (exactHash.AsSpan().SequenceEqual(pin.PinHash))
            {
                return (exactMatch, pin, ResolutionStatus.Resolved, exactHash, InvalidPin: false, Unreadable: false, PayloadConflict: false);
            }
        }

        // Exact locator didn't match hash. Search same source & identity for occurrences with matching hash.
        List<AssetOccurrence> sameSourceOccurrences = occurrences.Where(o => o.SourceId == pin.SourceId).ToList();
        List<(AssetOccurrence Occurrence, byte[] Hash)> matchingHashOccurrences = new();
        bool unreadableEncountered = false;

        foreach (AssetOccurrence occ in sameSourceOccurrences)
        {
            byte[]? hash = await GetOrComputeHashAsync(pinnedSource, occ, cancellationToken).ConfigureAwait(false);
            if (hash is null)
            {
                unreadableEncountered = true;
                continue;
            }

            if (hash.AsSpan().SequenceEqual(pin.PinHash))
            {
                matchingHashOccurrences.Add((occ, hash));
            }
        }

        if (matchingHashOccurrences.Count == 1)
        {
            // Reattach pin to this unique occurrence
            AssetOccurrence reattachedWinner = matchingHashOccurrences[0].Occurrence;
            byte[] hash = matchingHashOccurrences[0].Hash;
            WinnerPin reattachedPin = new WinnerPin(pin.Identity, pin.SourceId, reattachedWinner.Locator, pin.PinHash);
            return (reattachedWinner, reattachedPin, ResolutionStatus.Resolved, hash, InvalidPin: false, Unreadable: unreadableEncountered, PayloadConflict: false);
        }

        // 0 or >1 matches -> Invalid Pin
        return (null, pin, ResolutionStatus.InvalidPin, null, InvalidPin: true, Unreadable: unreadableEncountered, PayloadConflict: matchingHashOccurrences.Count > 1);
    }

    private async Task<(AssetOccurrence? Winner, ResolutionStatus Status, byte[]? Hash, bool Unreadable, bool PayloadConflict)>
        ResolveAutomaticIdentityAsync(
            AssetIdentity identity,
            List<AssetOccurrence> occurrences,
            List<AssetSource> sortedSources,
            CancellationToken cancellationToken)
    {
        // Find highest-priority available source containing occurrences for this identity
        AssetSource? winningSource = sortedSources.FirstOrDefault(s => s.IsAvailable && occurrences.Any(o => o.SourceId == s.Id));

        if (winningSource is null)
        {
            // No available source has this identity. Check if any unavailable source has it.
            bool inUnavailableSource = sortedSources.Any(s => !s.IsAvailable && occurrences.Any(o => o.SourceId == s.Id));
            ResolutionStatus status = inUnavailableSource ? ResolutionStatus.Unavailable : ResolutionStatus.Unpackageable;
            return (null, status, null, Unreadable: false, PayloadConflict: false);
        }

        List<AssetOccurrence> winningSourceOccurrences = occurrences.Where(o => o.SourceId == winningSource.Id).ToList();

        if (winningSourceOccurrences.Count == 1)
        {
            AssetOccurrence singleOcc = winningSourceOccurrences[0];
            return (singleOcc, ResolutionStatus.Resolved, null, Unreadable: false, PayloadConflict: false);
        }

        // >1 occurrences in winning source: trigger lazy SHA-256 hashing ONLY for winning source's occurrences
        List<(AssetOccurrence Occurrence, byte[] Hash)> hashedOccurrences = new();
        bool unreadableEncountered = false;

        foreach (AssetOccurrence occ in winningSourceOccurrences)
        {
            byte[]? hash = await GetOrComputeHashAsync(winningSource, occ, cancellationToken).ConfigureAwait(false);
            if (hash is null)
            {
                unreadableEncountered = true;
            }
            else
            {
                hashedOccurrences.Add((occ, hash));
            }
        }

        if (unreadableEncountered || hashedOccurrences.Count != winningSourceOccurrences.Count)
        {
            return (null, ResolutionStatus.UnresolvedDuplicate, null, Unreadable: true, PayloadConflict: false);
        }

        // Check if all hashes match
        byte[] firstHash = hashedOccurrences[0].Hash;
        bool allMatch = hashedOccurrences.All(h => h.Hash.AsSpan().SequenceEqual(firstHash));

        if (!allMatch)
        {
            return (null, ResolutionStatus.UnresolvedDuplicate, null, Unreadable: false, PayloadConflict: true);
        }

        // All hashes match! Select lowest deterministic locator
        AssetOccurrence lowestLocatorOcc = SelectLowestDeterministicLocator(winningSourceOccurrences);
        return (lowestLocatorOcc, ResolutionStatus.Resolved, firstHash, Unreadable: false, PayloadConflict: false);
    }

    private static AssetOccurrence SelectLowestDeterministicLocator(List<AssetOccurrence> occurrences)
    {
        return occurrences.OrderBy(o => o.Locator, LocatorComparer.Instance).First();
    }

    private async Task<byte[]?> GetOrComputeHashAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken)
    {
        if (occurrence.Sha256 is not null && occurrence.Sha256.Length == 32)
        {
            return occurrence.Sha256;
        }

        if (_hashCache.TryGetHash(source.Id, occurrence.Identity, occurrence.Locator, occurrence.Size, source.Fingerprint, out byte[] cachedHash))
        {
            return cachedHash;
        }

        try
        {
            byte[] computedHash = await _hashService.ComputeSha256Async(source, occurrence, cancellationToken).ConfigureAwait(false);
            if (source.Fingerprint is not null)
            {
                _hashCache.PutHash(source.Id, occurrence.Identity, occurrence.Locator, occurrence.Size, source.Fingerprint, computedHash);
            }
            return computedHash;
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

    private sealed class LocatorComparer : IComparer<OccurrenceLocator>
    {
        public static readonly LocatorComparer Instance = new();

        public int Compare(OccurrenceLocator? x, OccurrenceLocator? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            if (x is HakEntryLocator hx && y is HakEntryLocator hy)
            {
                return hx.EntryIndex.CompareTo(hy.EntryIndex);
            }

            if (x is FolderFileLocator fx && y is FolderFileLocator fy)
            {
                return string.Compare(fx.NormalizedRelativePath, fy.NormalizedRelativePath, StringComparison.Ordinal);
            }

            return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
        }
    }
}
