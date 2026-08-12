using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;

namespace SRN.CC.App.Services;

/// <summary>
/// Resolves asset dependencies workspace-first, then falls back to the base-game KEY/BIF catalog.
/// Base-game hits satisfy previews and transitive traversal but are never packaged: they are tracked
/// in <see cref="BaseGameSatisfied"/> so the caller can keep them out of the build selection.
/// </summary>
public sealed class DependencyLocator : IDependencyResolver
{
    /// <summary>
    /// Source id used to mark a synthesized base-game occurrence. Chosen per instance so it cannot
    /// collide with any workspace source id: normally <see cref="Guid.Empty"/> (no real source uses
    /// it), but if a project actually carries an all-zero source id this falls back to a fresh GUID
    /// that is absent from the workspace. That keeps <see cref="OpenStreamAsync"/>'s marker test from
    /// ever misrouting a real source's occurrence to the base-game catalog.
    /// </summary>
    public Guid BaseGameSourceId { get; }

    private readonly WorkspaceState _workspaceState;
    private readonly Func<AssetSource, AssetOccurrence, Stream, CancellationToken, Task<Stream>> _streamOpener;
    private readonly IBaseGameResourceCatalog? _baseGameCatalog;
    private readonly Dictionary<AssetIdentity, AssetOccurrence> _occurrenceCache;
    private readonly HashSet<AssetIdentity> _curatedIdentities;

    /// <summary>
    /// Identities served from the base game rather than a curated source. An identity is recorded
    /// here only once its base-game payload has actually been read (see <see cref="OpenStreamAsync"/>),
    /// so a KEY entry whose BIF is missing or corrupt is never counted as satisfied.
    /// </summary>
    public HashSet<AssetIdentity> BaseGameSatisfied { get; } = new();

    public DependencyLocator(
        WorkspaceState workspaceState,
        Func<AssetSource, AssetOccurrence, Stream, CancellationToken, Task<Stream>> streamOpener,
        IBaseGameResourceCatalog? baseGameCatalog = null)
    {
        _workspaceState = workspaceState ?? throw new ArgumentNullException(nameof(workspaceState));
        _streamOpener = streamOpener ?? throw new ArgumentNullException(nameof(streamOpener));
        _baseGameCatalog = baseGameCatalog;

        // Pick a marker source id that no workspace source uses. Guid.Empty is the normal choice; the
        // loop only advances if a project pathologically carries the all-zero id, guaranteeing the
        // marker can never match a real source.
        HashSet<Guid> usedSourceIds = _workspaceState.Sources.Select(s => s.Id).ToHashSet();
        Guid marker = Guid.Empty;
        while (usedSourceIds.Contains(marker))
        {
            marker = Guid.NewGuid();
        }
        BaseGameSourceId = marker;

        // Cache the resolution winner for each curated identity. This must be ResolvedOccurrence,
        // not a first-wins scan of AllOccurrences: a valid pin overrides source priority
        // (PLAN.md:82), and only ResolvedOccurrence carries the pin's effect.
        //
        // Assets whose status is not Resolved — an invalid pin, an unresolved duplicate, an
        // unavailable or unpackageable source — are deliberately absent from the cache, so a
        // dependency on them is reported unresolved rather than silently satisfied by an arbitrary
        // occurrence (PLAN.md:86, "never fall back silently").
        //
        // _curatedIdentities holds *every* identity the workspace knows about, resolved or not. It is
        // the guard that keeps the base-game fallback from papering over a curated-source error: an
        // identity that is present but unresolved (invalid pin, unresolved duplicate, unavailable or
        // unpackageable source) must report unresolved, not be silently satisfied by the base game.
        _occurrenceCache = new Dictionary<AssetIdentity, AssetOccurrence>();
        _curatedIdentities = new HashSet<AssetIdentity>();
        foreach (var curatedAsset in _workspaceState.CuratedAssets)
        {
            _curatedIdentities.Add(curatedAsset.Identity);

            if (curatedAsset.Status != ResolutionStatus.Resolved)
            {
                continue;
            }

            var winner = curatedAsset.ResolvedOccurrence;
            if (winner != null)
            {
                _occurrenceCache[curatedAsset.Identity] = winner;
            }
        }
    }

    /// <summary>
    /// Resolves an asset identity using workspace-first strategy.
    /// Tries workspace occurrences first, then could integrate catalog fallback.
    /// </summary>
    public Task<AssetOccurrence?> ResolveAsync(AssetIdentity id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();

        // First try workspace occurrences.
        if (_occurrenceCache.TryGetValue(id, out var occurrence))
        {
            return Task.FromResult<AssetOccurrence?>(occurrence);
        }

        // An identity the workspace already knows about but did not resolve is a curated-source error
        // the operator must see and fix. Never let the base game silently satisfy it — that would
        // conceal the error and ship a build quietly using the base asset instead of the intended
        // override. Report unresolved so the failure surfaces (PLAN.md:86, "never fall back silently").
        if (_curatedIdentities.Contains(id))
        {
            return Task.FromResult<AssetOccurrence?>(null);
        }

        // Base-game fallback, only for identities genuinely absent from the workspace. A base resource
        // satisfies previews and lets traversal continue into its own dependencies, but it is never
        // packaged (PLAN.md:134). We synthesize a marker occurrence — marker source id, size 0 — that
        // OpenStreamAsync routes back to the catalog. Satisfaction is recorded there, only after the
        // payload is actually read, so a KEY entry with a missing/corrupt BIF is not counted as
        // satisfied while traversal reports it unresolved.
        if (_baseGameCatalog is not null && _baseGameCatalog.Contains(id))
        {
            AssetOccurrence baseOccurrence = new(id, BaseGameSourceId, new HakEntryLocator(0), id.Resref, 0);
            return Task.FromResult<AssetOccurrence?>(baseOccurrence);
        }

        // Not in the workspace and not in the base game: unresolved.
        return Task.FromResult<AssetOccurrence?>(null);
    }

    /// <summary>
    /// Opens a stream for the given occurrence using the workspace source.
    /// </summary>
    public async Task<Stream> OpenStreamAsync(
        AssetOccurrence occurrence,
        Stream fallback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        cancellationToken.ThrowIfCancellationRequested();

        // A synthesized base-game occurrence streams from the catalog, so traversal can read its
        // payload to discover nested dependencies. Record satisfaction only once the read succeeds:
        // if the BIF is missing or corrupt, OpenAsync throws, the identity is never marked satisfied,
        // and traversal reports it unresolved instead of double-counting it.
        if (occurrence.SourceId == BaseGameSourceId && _baseGameCatalog is not null)
        {
            Stream baseStream = await _baseGameCatalog.OpenAsync(occurrence.Identity, cancellationToken).ConfigureAwait(false);
            BaseGameSatisfied.Add(occurrence.Identity);
            return baseStream;
        }

        // Find the source for this occurrence
        var source = _workspaceState.Sources.FirstOrDefault(s => s.Id == occurrence.SourceId);
        if (source != null)
        {
            return await _streamOpener(source, occurrence, fallback, cancellationToken)
                .ConfigureAwait(false);
        }

        // Fallback if source not found
        return fallback;
    }
}
