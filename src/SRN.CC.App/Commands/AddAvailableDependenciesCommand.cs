using SRN.CC.App.Services;
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
    private readonly IDependencyResolver _resolver;

    public AddAvailableDependenciesCommand(
        IDependencyAnalyzer analyzer,
        IResourceTypeRegistry registry,
        IDependencyResolver resolver)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Creates a command with inline locator and stream provider delegates for testing.
    /// </summary>
    public AddAvailableDependenciesCommand(
        IDependencyAnalyzer analyzer,
        IResourceTypeRegistry registry,
        Func<AssetIdentity, CancellationToken, Task<AssetOccurrence?>> locator,
        Func<AssetOccurrence, Stream, CancellationToken, Task<Stream>> streamProvider)
        : this(analyzer, registry, new InlineResolver(locator, streamProvider))
    {
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

        // Create locator and stream provider functions from resolver
        async Task<AssetOccurrence?> Locator(AssetIdentity id, CancellationToken ct)
        {
            return await _resolver.ResolveAsync(id, ct).ConfigureAwait(false);
        }

        async Task<Stream> StreamProvider(AssetOccurrence occ, Stream fallback, CancellationToken ct)
        {
            return await _resolver.OpenStreamAsync(occ, fallback, ct).ConfigureAwait(false);
        }

        // Create traversal engine
        var engine = new DependencyTraversalEngine(_analyzer, Locator, StreamProvider);

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

    /// <summary>
    /// Inline resolver for testing and backward compatibility.
    /// </summary>
    private sealed class InlineResolver : IDependencyResolver
    {
        private readonly Func<AssetIdentity, CancellationToken, Task<AssetOccurrence?>> _locator;
        private readonly Func<AssetOccurrence, Stream, CancellationToken, Task<Stream>> _streamProvider;

        public InlineResolver(
            Func<AssetIdentity, CancellationToken, Task<AssetOccurrence?>> locator,
            Func<AssetOccurrence, Stream, CancellationToken, Task<Stream>> streamProvider)
        {
            _locator = locator ?? throw new ArgumentNullException(nameof(locator));
            _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        }

        public Task<AssetOccurrence?> ResolveAsync(AssetIdentity id, CancellationToken cancellationToken = default)
        {
            return _locator(id, cancellationToken);
        }

        public Task<Stream> OpenStreamAsync(AssetOccurrence occurrence, Stream fallback, CancellationToken cancellationToken = default)
        {
            return _streamProvider(occurrence, fallback, cancellationToken);
        }
    }
}
