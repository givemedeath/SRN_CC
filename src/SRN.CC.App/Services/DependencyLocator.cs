using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;

namespace SRN.CC.App.Services;

/// <summary>
/// Resolves asset dependencies using workspace-first then catalog-fallback strategy.
/// </summary>
public sealed class DependencyLocator : IDependencyResolver
{
    private readonly WorkspaceState _workspaceState;
    private readonly Func<AssetSource, AssetOccurrence, Stream, CancellationToken, Task<Stream>> _streamOpener;
    private readonly Dictionary<AssetIdentity, AssetOccurrence> _occurrenceCache;

    public DependencyLocator(
        WorkspaceState workspaceState,
        Func<AssetSource, AssetOccurrence, Stream, CancellationToken, Task<Stream>> streamOpener)
    {
        _workspaceState = workspaceState ?? throw new ArgumentNullException(nameof(workspaceState));
        _streamOpener = streamOpener ?? throw new ArgumentNullException(nameof(streamOpener));

        // Build cache of all workspace occurrences for quick lookup
        _occurrenceCache = new Dictionary<AssetIdentity, AssetOccurrence>();
        foreach (var curatedAsset in _workspaceState.CuratedAssets)
        {
            foreach (var occurrence in curatedAsset.AllOccurrences)
            {
                if (!_occurrenceCache.ContainsKey(occurrence.Identity))
                {
                    _occurrenceCache[occurrence.Identity] = occurrence;
                }
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

        // First try workspace occurrences
        if (_occurrenceCache.TryGetValue(id, out var occurrence))
        {
            return Task.FromResult<AssetOccurrence?>(occurrence);
        }

        // Workspace-first strategy: if not in workspace, it's unresolved
        // In a full implementation, this would check the catalog here
        // For now, just return null (unresolved)
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
