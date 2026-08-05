using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;

namespace SRN.CC.Preview;

/// <summary>
/// Performs cycle-safe transitive dependency traversal with bounded recursion depth, closure count,
/// and closure size.
/// </summary>
/// <remarks>
/// Maintains visited and inFlight sets to detect and prevent infinite loops from cyclic references.
/// Returns both resolved and unresolved dependencies with diagnostic information.
/// </remarks>
public sealed class DependencyTraversalEngine
{
    private const int DefaultMaxDepth = 512;
    private const int DefaultMaxCount = int.MaxValue;
    private const long DefaultMaxBytes = long.MaxValue;
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
    private long _totalBytes = 0;
    private TraversalLimit _limitHit = TraversalLimit.None;

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
    /// <param name="roots">The assets to start from.</param>
    /// <param name="maxDepth">Maximum edges followed from any root.</param>
    /// <param name="maxCount">Maximum number of assets admitted to the closure.</param>
    /// <param name="maxBytes">Maximum accumulated payload size of the closure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Every budget defaults to effectively unlimited, so an unbounded call behaves exactly as it did
    /// before budgets existed. When several budgets are breached, <see cref="TraversalResult.LimitHit"/>
    /// reports the first one, because that is the budget that shaped the closure. Identities rejected
    /// by a budget are reported in <see cref="TraversalResult.Unresolved"/> with a distinct reason, and
    /// contribute neither bytes nor descendants.
    /// </remarks>
    public async Task<TraversalResult> TraverseAsync(
        IEnumerable<AssetIdentity> roots,
        int maxDepth = DefaultMaxDepth,
        int maxCount = DefaultMaxCount,
        long maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (maxDepth < 1)
        {
            throw new ArgumentException("Max depth must be at least 1.", nameof(maxDepth));
        }

        if (maxCount < 1)
        {
            throw new ArgumentException("Max count must be at least 1.", nameof(maxCount));
        }

        if (maxBytes < 1)
        {
            throw new ArgumentException("Max bytes must be at least 1.", nameof(maxBytes));
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
        _totalBytes = 0;
        _limitHit = TraversalLimit.None;

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
                RecordLimit(TraversalLimit.Depth);
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

            // Check the closure count budget. Tested here because this is the point at which the
            // engine commits to admitting a new identity; an exact-budget closure is not truncated.
            if (_resolved.Count >= maxCount)
            {
                RecordLimit(TraversalLimit.Count);
                _unresolved[identity] = $"Closure count limit ({maxCount}) exceeded";
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

                    // Check the closure size budget before admitting the payload. Tested here rather
                    // than before location because the byte cost is only known once the occurrence is
                    // located. A rejected node contributes neither bytes nor descendants.
                    if (!_resolved.Contains(identity) && _totalBytes + occurrence.Size > maxBytes)
                    {
                        RecordLimit(TraversalLimit.Size);
                        _unresolved[identity] = $"Closure size limit ({maxBytes} bytes) exceeded";
                        continue;
                    }

                    // Mark as resolved and accumulate the located occurrence's payload size.
                    // Counted once per identity: duplicate references are suppressed by the
                    // visited set, so a shared dependency contributes its bytes only once.
                    if (_resolved.Add(identity))
                    {
                        _totalBytes += occurrence.Size;
                    }

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
            totalBytes: _totalBytes,
            count: _resolved.Count,
            maxDepth: _maxDepthReached,
            duplicatesSuppressed: _duplicatesSuppressed,
            limitHit: _limitHit);
    }

    /// <summary>
    /// Record the first budget breached. Later breaches do not overwrite it, because the first
    /// budget to fire is the one that shaped the closure.
    /// </summary>
    private void RecordLimit(TraversalLimit limit)
    {
        if (_limitHit == TraversalLimit.None)
        {
            _limitHit = limit;
        }
    }
}
