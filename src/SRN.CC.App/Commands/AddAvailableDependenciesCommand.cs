using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Preview;

namespace SRN.CC.App.Commands;

/// <summary>
/// Command that calculates the transitive closure of dependencies for selected assets.
/// Traverses from the selected assets and groups unresolved dependencies deterministically.
/// </summary>
public sealed class AddAvailableDependenciesCommand
{
    private readonly IDependencyAnalyzer _analyzer;
    private readonly IResourceTypeRegistry _registry;
    private readonly Func<AssetIdentity, CancellationToken, Task<AssetOccurrence?>> _locator;
    private readonly Func<AssetOccurrence, Stream, CancellationToken, Task<Stream>> _streamProvider;

    public AddAvailableDependenciesCommand(
        IDependencyAnalyzer analyzer,
        IResourceTypeRegistry registry,
        Func<AssetIdentity, CancellationToken, Task<AssetOccurrence?>> locator,
        Func<AssetOccurrence, Stream, CancellationToken, Task<Stream>> streamProvider)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
    }

    /// <summary>
    /// Executes the command to calculate dependency closure for the given occurrences.
    /// </summary>
    /// <param name="occurrences">The root assets to traverse from.</param>
    /// <param name="cancellationToken">Cancellation token for graceful shutdown.</param>
    /// <returns>A closure summary with resolved and unresolved dependencies.</returns>
    public async Task<ClosureSummary> ExecuteAsync(
        IEnumerable<AssetOccurrence> occurrences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrences);

        cancellationToken.ThrowIfCancellationRequested();

        // Extract root identities from occurrences
        var rootIdentities = occurrences
            .Where(o => o != null)
            .Select(o => o.Identity)
            .ToList();

        if (rootIdentities.Count == 0)
        {
            // Return empty summary
            return new ClosureSummary(
                resolved: new HashSet<AssetIdentity>(),
                unresolvedGroups: Array.Empty<UnresolvedDependencyGroup>(),
                totalBytes: 0,
                maxDepth: 0,
                duplicatesSuppressed: 0);
        }

        // Create traversal engine
        var engine = new DependencyTraversalEngine(_analyzer, _locator, _streamProvider);

        // Perform traversal
        var traversalResult = await engine.TraverseAsync(
            rootIdentities,
            maxDepth: 512,
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Group unresolved dependencies
        var unresolvedGroups = UnresolvedDependencyGrouper.GroupUnresolved(
            traversalResult.Unresolved,
            _registry);

        // Create and return closure summary
        return new ClosureSummary(
            resolved: traversalResult.Resolved,
            unresolvedGroups: unresolvedGroups,
            totalBytes: traversalResult.TotalBytes,
            maxDepth: traversalResult.MaxDepth,
            duplicatesSuppressed: traversalResult.DuplicatesSuppressed);
    }
}
