using SRN.CC.Core.Occurrences;

namespace SRN.CC.Preview.Render;

/// <summary>
/// In-process, per-occurrence cache for built <see cref="RenderScene"/> instances (slice S9).
/// Concurrent <see cref="GetOrBuildAsync"/> calls for the same <see cref="ModelSceneCacheKey"/>
/// observe and await the SAME in-flight build task rather than triggering duplicate parses/builds
/// ("three concurrent requests for the same occurrence produce one parse and one build"). Total
/// cached size is capped at a configurable byte budget
/// (<see cref="PreviewStreamHelpers.ModelSceneCacheBudgetBytes"/> by default); once a build
/// finishes and its real <see cref="RenderScene.ApproximateByteSize"/> is known, the least
/// recently used entries are evicted until the running total is back under budget.
/// </summary>
/// <remarks>
/// Process-local only, per architecture decision A2 — <see cref="RenderScene"/> instances routed
/// through this cache must never enter the SQLite <c>preview_cache</c> table; only
/// <see cref="IPreviewThumbnailCache"/>-eligible image payloads go there.
/// </remarks>
public sealed class ModelSceneCache
{
    private readonly long _budgetBytes;
    private readonly object _gate = new();
    private readonly Dictionary<ModelSceneCacheKey, CacheEntry> _entries = new();
    private readonly LinkedList<ModelSceneCacheKey> _lruOrder = new();
    private long _totalApproximateBytes;

    public ModelSceneCache(long budgetBytes = PreviewStreamHelpers.ModelSceneCacheBudgetBytes)
    {
        if (budgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budgetBytes), budgetBytes, "Budget must be positive.");
        }

        _budgetBytes = budgetBytes;
    }

    /// <summary>Number of entries (in-flight or completed) currently tracked by the cache.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Sum of <see cref="RenderScene.ApproximateByteSize"/> across all finalized (built) entries.</summary>
    public long TotalApproximateByteSize
    {
        get
        {
            lock (_gate)
            {
                return _totalApproximateBytes;
            }
        }
    }

    /// <summary>True when <paramref name="key"/> currently has an entry (in-flight or completed).</summary>
    public bool ContainsKey(ModelSceneCacheKey key)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(key);
        }
    }

    /// <summary>
    /// Returns the cached <see cref="Task{RenderScene}"/> for <paramref name="key"/>, or registers
    /// and starts <paramref name="factory"/> as the in-flight build when no entry exists yet.
    /// Every caller — including concurrent ones racing for the same key — receives a reference to
    /// the exact same task, so <paramref name="factory"/> runs at most once per key while a build
    /// is outstanding or its result remains cached.
    /// </summary>
    /// <remarks>
    /// Cancellation is caller-scoped: only the request that actually triggers the build (the first
    /// one to observe a cache miss) determines whether the underlying build can be cancelled — its
    /// <see cref="CancellationToken"/>, if any, must already be captured by <paramref name="factory"/>.
    /// Later callers that merely observe the shared task are bound by whichever token(s) they apply
    /// to their own await, not by this method.
    /// </remarks>
    public Task<RenderScene> GetOrBuildAsync(ModelSceneCacheKey key, Func<Task<RenderScene>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out CacheEntry? existing))
            {
                TouchLocked(key, existing);
                return existing.SceneTask;
            }
        }

        TaskCompletionSource<RenderScene> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CacheEntry candidate = new(tcs.Task);

        lock (_gate)
        {
            // Re-check under the lock: another thread may have won the race between the first
            // lookup above and this insert.
            if (_entries.TryGetValue(key, out CacheEntry? raced))
            {
                TouchLocked(key, raced);
                return raced.SceneTask;
            }

            _entries[key] = candidate;
            candidate.LruNode = _lruOrder.AddLast(key);
        }

        RunBuild(key, candidate, factory, tcs);
        return tcs.Task;
    }

    private void RunBuild(
        ModelSceneCacheKey key,
        CacheEntry entry,
        Func<Task<RenderScene>> factory,
        TaskCompletionSource<RenderScene> tcs)
    {
        _ = ExecuteAsync();

        async Task ExecuteAsync()
        {
            try
            {
                RenderScene scene = await factory().ConfigureAwait(false);

                lock (_gate)
                {
                    // Only finalize bookkeeping if this entry is still the one registered for
                    // `key` — it may have already been evicted by a concurrent, larger insert.
                    if (_entries.TryGetValue(key, out CacheEntry? current) && ReferenceEquals(current, entry))
                    {
                        entry.ApproximateByteSize = scene.ApproximateByteSize;
                        _totalApproximateBytes += scene.ApproximateByteSize;
                        EvictIfOverBudgetLocked(key);
                    }
                }

                tcs.TrySetResult(scene);
            }
            catch (OperationCanceledException)
            {
                RemoveFailedEntry(key, entry);
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                RemoveFailedEntry(key, entry);
                tcs.TrySetException(ex);
            }
        }
    }

    /// <summary>Removes a candidate entry that failed or was cancelled, so a later request retries instead of reusing a faulted task.</summary>
    private void RemoveFailedEntry(ModelSceneCacheKey key, CacheEntry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out CacheEntry? current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                if (entry.LruNode != null)
                {
                    _lruOrder.Remove(entry.LruNode);
                }
            }
        }
    }

    /// <summary>Moves <paramref name="key"/> to the most-recently-used end of the LRU list. Caller must hold <see cref="_gate"/>.</summary>
    private void TouchLocked(ModelSceneCacheKey key, CacheEntry entry)
    {
        if (entry.LruNode != null)
        {
            _lruOrder.Remove(entry.LruNode);
            entry.LruNode = _lruOrder.AddLast(key);
        }
    }

    /// <summary>
    /// Evicts least-recently-used entries (other than <paramref name="justInsertedKey"/>) until the
    /// running total is back under budget, or no more evictable entries remain. Caller must hold
    /// <see cref="_gate"/>.
    /// </summary>
    private void EvictIfOverBudgetLocked(ModelSceneCacheKey justInsertedKey)
    {
        LinkedListNode<ModelSceneCacheKey>? node = _lruOrder.First;
        while (_totalApproximateBytes > _budgetBytes && node != null)
        {
            LinkedListNode<ModelSceneCacheKey>? next = node.Next;
            ModelSceneCacheKey candidateKey = node.Value;

            if (!candidateKey.Equals(justInsertedKey) && _entries.TryGetValue(candidateKey, out CacheEntry? candidateEntry))
            {
                _entries.Remove(candidateKey);
                _lruOrder.Remove(node);
                _totalApproximateBytes -= candidateEntry.ApproximateByteSize;
            }

            node = next;
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(Task<RenderScene> sceneTask)
        {
            SceneTask = sceneTask;
        }

        public Task<RenderScene> SceneTask { get; }

        public long ApproximateByteSize { get; set; }

        public LinkedListNode<ModelSceneCacheKey>? LruNode { get; set; }
    }
}

/// <summary>
/// Identity of a cached <see cref="RenderScene"/>: the occurrence's source, its locator within
/// that source, and (when known) its content hash — the same triple used elsewhere to distinguish
/// otherwise-identical occurrences. <see cref="OccurrenceLocator"/>'s subtypes are records with
/// structural equality, and <see cref="Sha256Hex"/> is a plain string, so this record struct's
/// generated equality is already correct structural comparison; the constructor converts the raw
/// <c>byte[]</c> hash (which does not have structural equality) to a hex string up front so callers
/// never need a custom byte-array comparer.
/// </summary>
public readonly record struct ModelSceneCacheKey
{
    public ModelSceneCacheKey(Guid sourceId, OccurrenceLocator locator, byte[]? sha256)
    {
        ArgumentNullException.ThrowIfNull(locator);
        SourceId = sourceId;
        Locator = locator;
        Sha256Hex = sha256 is { Length: > 0 } ? Convert.ToHexString(sha256) : null;
    }

    public Guid SourceId { get; }

    public OccurrenceLocator Locator { get; }

    public string? Sha256Hex { get; }

    public static ModelSceneCacheKey FromOccurrence(AssetOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return new ModelSceneCacheKey(occurrence.SourceId, occurrence.Locator, occurrence.Sha256);
    }
}
