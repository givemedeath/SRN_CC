using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;

namespace SRN.CC.Preview;

/// <summary>
/// Performs cycle-safe transitive dependency traversal with bounded recursion depth.
/// </summary>
/// <remarks>
/// Maintains visited and inFlight sets to detect and prevent infinite loops from cyclic references.
/// Returns both resolved and unresolved dependencies with diagnostic information.
/// </remarks>
public sealed class DependencyTraversalEngine
{
    private const int DefaultMaxDepth = 512;
    private readonly IDependencyAnalyzer _analyzer;
    private readonly Func<AssetIdentity, CancellationToken, Task<AssetOccurrence?>> _locator;
    private readonly Func<AssetOccurrence, Stream, CancellationToken, Task<Stream>> _streamProvider;

    private readonly HashSet<AssetIdentity> _visited = new();
    private readonly HashSet<AssetIdentity> _inFlight = new();
    private readonly HashSet<AssetIdentity> _resolved = new();
    private readonly Dictionary<AssetIdentity, string> _unresolved = new();
    private readonly Queue<(AssetIdentity Identity, int Depth)> _queue = new();

    private int _maxDepthReached = 0;
    private int _duplicatesSuppressed = 0;

    public DependencyTraversalEngine(
        IDependencyAnalyzer analyzer,
        Func<AssetIdentity, CancellationToken, Task<AssetOccurrence?>> locator,
        Func<AssetOccurrence, Stream, CancellationToken, Task<Stream>> streamProvider)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
    }

    /// <summary>
    /// Traverse the dependency graph starting from the given root assets.
    /// </summary>
    public async Task<TraversalResult> TraverseAsync(
        IEnumerable<AssetIdentity> roots,
        int maxDepth = DefaultMaxDepth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (maxDepth < 1)
        {
            throw new ArgumentException("Max depth must be at least 1.", nameof(maxDepth));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Clear state from any prior traversals
        _visited.Clear();
        _inFlight.Clear();
        _resolved.Clear();
        _unresolved.Clear();
        _queue.Clear();
        _maxDepthReached = 0;
        _duplicatesSuppressed = 0;

        // Enqueue initial roots
        foreach (var root in roots)
        {
            if (root != null)
            {
                _queue.Enqueue((root, 0));
            }
        }

        // Breadth-first traversal with cycle detection
        while (_queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (identity, depth) = _queue.Dequeue();

            // Track max depth
            if (depth > _maxDepthReached)
            {
                _maxDepthReached = depth;
            }

            // Check depth limit
            if (depth >= maxDepth)
            {
                _unresolved[identity] = $"Traversal depth limit ({maxDepth}) exceeded";
                continue;
            }

            // Check if already visited (duplicate suppression)
            if (_visited.Contains(identity))
            {
                _duplicatesSuppressed++;
                continue;
            }

            // Check if currently being processed (cycle detection)
            if (_inFlight.Contains(identity))
            {
                _unresolved[identity] = "Cyclic dependency detected";
                continue;
            }

            // Mark as in-flight
            _inFlight.Add(identity);

            try
            {
                // Locate the occurrence
                var occurrence = await _locator(identity, cancellationToken).ConfigureAwait(false);
                if (occurrence == null)
                {
                    _unresolved[identity] = "Occurrence not found in workspace";
                    _visited.Add(identity);
                    continue;
                }

                // Open stream and analyze dependencies
                try
                {
                    using var stream = await _streamProvider(occurrence, Stream.Null, cancellationToken)
                        .ConfigureAwait(false);

                    var directDeps = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream, cancellationToken)
                        .ConfigureAwait(false);

                    // Mark as resolved
                    _resolved.Add(identity);

                    // Enqueue discovered dependencies
                    foreach (var dep in directDeps)
                    {
                        if (!_visited.Contains(dep) && !_inFlight.Contains(dep))
                        {
                            _queue.Enqueue((dep, depth + 1));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _unresolved[identity] = $"Failed to analyze dependencies: {ex.Message}";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _unresolved[identity] = $"Failed to locate occurrence: {ex.Message}";
            }
            finally
            {
                // Always mark as visited and remove from in-flight
                _visited.Add(identity);
                _inFlight.Remove(identity);
            }
        }

        return new TraversalResult(
            resolved: new HashSet<AssetIdentity>(_resolved),
            unresolved: new Dictionary<AssetIdentity, string>(_unresolved),
            totalBytes: 0, // TODO: track during traversal if occurrence sizes are available
            count: _resolved.Count,
            maxDepth: _maxDepthReached,
            duplicatesSuppressed: _duplicatesSuppressed);
    }
}
