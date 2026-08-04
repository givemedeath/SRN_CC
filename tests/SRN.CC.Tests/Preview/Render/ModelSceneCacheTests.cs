using System.Numerics;
using NUnit.Framework;
using SRN.CC.Core.Occurrences;
using SRN.CC.Preview.Render;

namespace SRN.CC.Tests.Preview.Render;

[TestFixture]
public class ModelSceneCacheTests
{
    // ---------------------------------------------------------------------
    // Get-or-add semantics: same key, concurrent requests, single build
    // ---------------------------------------------------------------------

    [Test]
    public async Task GetOrBuildAsync_ConcurrentRequestsForSameKey_BuildExactlyOnce()
    {
        ModelSceneCache cache = new();
        ModelSceneCacheKey key = CreateKey();
        int buildCount = 0;
        TaskCompletionSource<bool> releaseGate = new();

        async Task<RenderScene> Factory()
        {
            Interlocked.Increment(ref buildCount);
            await releaseGate.Task.ConfigureAwait(false);
            return CreateScene(bytes: 100);
        }

        Task<RenderScene> t1 = cache.GetOrBuildAsync(key, Factory);
        Task<RenderScene> t2 = cache.GetOrBuildAsync(key, Factory);
        Task<RenderScene> t3 = cache.GetOrBuildAsync(key, Factory);

        // Every caller must observe the exact same in-flight task instance.
        Assert.That(t2, Is.SameAs(t1));
        Assert.That(t3, Is.SameAs(t1));

        releaseGate.SetResult(true);
        RenderScene[] results = await Task.WhenAll(t1, t2, t3);

        Assert.That(buildCount, Is.EqualTo(1), "the factory must run at most once for concurrent requests to the same key");
        Assert.That(results[0], Is.SameAs(results[1]));
        Assert.That(results[0], Is.SameAs(results[2]));
    }

    [Test]
    public async Task GetOrBuildAsync_SecondRequestAfterFirstCompletes_ReusesCachedResultWithoutRebuilding()
    {
        ModelSceneCache cache = new();
        ModelSceneCacheKey key = CreateKey();
        int buildCount = 0;
        RenderScene expected = CreateScene(bytes: 100);

        Task<RenderScene> Factory()
        {
            Interlocked.Increment(ref buildCount);
            return Task.FromResult(expected);
        }

        RenderScene first = await cache.GetOrBuildAsync(key, Factory);
        RenderScene second = await cache.GetOrBuildAsync(key, Factory);

        Assert.That(buildCount, Is.EqualTo(1));
        Assert.That(first, Is.SameAs(expected));
        Assert.That(second, Is.SameAs(expected));
    }

    // ---------------------------------------------------------------------
    // Different keys build independently
    // ---------------------------------------------------------------------

    [Test]
    public async Task GetOrBuildAsync_DifferentKeys_BuildIndependently()
    {
        ModelSceneCache cache = new();
        ModelSceneCacheKey keyA = CreateKey(sourceId: Guid.NewGuid());
        ModelSceneCacheKey keyB = CreateKey(sourceId: Guid.NewGuid());
        int buildCountA = 0;
        int buildCountB = 0;

        RenderScene sceneA = CreateScene(bytes: 10, modelName: "a");
        RenderScene sceneB = CreateScene(bytes: 20, modelName: "b");

        RenderScene resultA = await cache.GetOrBuildAsync(keyA, () =>
        {
            buildCountA++;
            return Task.FromResult(sceneA);
        });
        RenderScene resultB = await cache.GetOrBuildAsync(keyB, () =>
        {
            buildCountB++;
            return Task.FromResult(sceneB);
        });

        Assert.That(buildCountA, Is.EqualTo(1));
        Assert.That(buildCountB, Is.EqualTo(1));
        Assert.That(resultA.ModelName, Is.EqualTo("a"));
        Assert.That(resultB.ModelName, Is.EqualTo("b"));
        Assert.That(cache.Count, Is.EqualTo(2));
    }

    // ---------------------------------------------------------------------
    // Budget-based eviction
    // ---------------------------------------------------------------------

    [Test]
    public async Task GetOrBuildAsync_WhenInsertExceedsBudget_EvictsLeastRecentlyUsedEntry()
    {
        // Budget fits exactly two 40-byte scenes.
        ModelSceneCache cache = new(budgetBytes: 80);
        ModelSceneCacheKey keyA = CreateKey(sourceId: Guid.NewGuid());
        ModelSceneCacheKey keyB = CreateKey(sourceId: Guid.NewGuid());
        ModelSceneCacheKey keyC = CreateKey(sourceId: Guid.NewGuid());

        int buildCountA = 0;

        await cache.GetOrBuildAsync(keyA, () =>
        {
            buildCountA++;
            return Task.FromResult(CreateScene(bytes: 40, modelName: "a"));
        });
        await cache.GetOrBuildAsync(keyB, () => Task.FromResult(CreateScene(bytes: 40, modelName: "b")));

        Assert.That(cache.Count, Is.EqualTo(2));
        Assert.That(cache.TotalApproximateByteSize, Is.EqualTo(80));

        // Inserting a third scene pushes the total over budget; "a" is the least recently used
        // (it was built first and never re-touched) and must be evicted to make room.
        await cache.GetOrBuildAsync(keyC, () => Task.FromResult(CreateScene(bytes: 40, modelName: "c")));

        Assert.That(cache.ContainsKey(keyA), Is.False, "the least-recently-used entry should have been evicted");
        Assert.That(cache.ContainsKey(keyB), Is.True);
        Assert.That(cache.ContainsKey(keyC), Is.True);
        Assert.That(cache.TotalApproximateByteSize, Is.EqualTo(80));

        // A subsequent request for the evicted key must rebuild rather than reuse a stale result.
        await cache.GetOrBuildAsync(keyA, () =>
        {
            buildCountA++;
            return Task.FromResult(CreateScene(bytes: 40, modelName: "a"));
        });

        Assert.That(buildCountA, Is.EqualTo(2), "the evicted entry must be rebuilt on the next request");
    }

    [Test]
    public async Task GetOrBuildAsync_TouchingAnEntryProtectsItFromEviction()
    {
        ModelSceneCache cache = new(budgetBytes: 80);
        ModelSceneCacheKey keyA = CreateKey(sourceId: Guid.NewGuid());
        ModelSceneCacheKey keyB = CreateKey(sourceId: Guid.NewGuid());
        ModelSceneCacheKey keyC = CreateKey(sourceId: Guid.NewGuid());

        await cache.GetOrBuildAsync(keyA, () => Task.FromResult(CreateScene(bytes: 40, modelName: "a")));
        await cache.GetOrBuildAsync(keyB, () => Task.FromResult(CreateScene(bytes: 40, modelName: "b")));

        // Re-request "a" to mark it most-recently-used, leaving "b" as the eviction candidate.
        await cache.GetOrBuildAsync(keyA, () => throw new InvalidOperationException("must not rebuild a cached entry"));

        await cache.GetOrBuildAsync(keyC, () => Task.FromResult(CreateScene(bytes: 40, modelName: "c")));

        Assert.That(cache.ContainsKey(keyA), Is.True, "recently-touched entries must survive eviction");
        Assert.That(cache.ContainsKey(keyB), Is.False);
        Assert.That(cache.ContainsKey(keyC), Is.True);
    }

    // ---------------------------------------------------------------------
    // Build failures are not cached
    // ---------------------------------------------------------------------

    [Test]
    public void GetOrBuildAsync_WhenFactoryThrows_FaultsTheReturnedTaskAndDoesNotCacheTheFailure()
    {
        ModelSceneCache cache = new();
        ModelSceneCacheKey key = CreateKey();

        Task<RenderScene> failing = cache.GetOrBuildAsync(key, () => throw new InvalidOperationException("boom"));

        Assert.ThrowsAsync<InvalidOperationException>(async () => await failing);
        Assert.That(cache.ContainsKey(key), Is.False, "a failed build must not remain cached");
    }

    [Test]
    public async Task GetOrBuildAsync_AfterAFailedBuild_ARetryCanSucceed()
    {
        ModelSceneCache cache = new();
        ModelSceneCacheKey key = CreateKey();

        Task<RenderScene> failing = cache.GetOrBuildAsync(key, () => throw new InvalidOperationException("boom"));
        try
        {
            await failing;
        }
        catch (InvalidOperationException)
        {
            // expected
        }

        RenderScene expected = CreateScene(bytes: 10);
        RenderScene result = await cache.GetOrBuildAsync(key, () => Task.FromResult(expected));

        Assert.That(result, Is.SameAs(expected));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static ModelSceneCacheKey CreateKey(Guid? sourceId = null, byte[]? sha256 = null) =>
        new(sourceId ?? Guid.NewGuid(), new HakEntryLocator(1), sha256);

    private static RenderScene CreateScene(long bytes, string modelName = "model") => new()
    {
        ModelName = modelName,
        SuperModel = string.Empty,
        IsAsciiSource = true,
        BoundsMinimum = Vector3.Zero,
        BoundsMaximum = Vector3.One,
        Radius = 1f,
        ArtworkMeshes = Array.Empty<RenderMesh>(),
        WalkmeshMeshes = Array.Empty<RenderMesh>(),
        Materials = Array.Empty<RenderMaterial>(),
        UnsupportedFeatures = Array.Empty<string>(),
        Diagnostics = Array.Empty<string>(),
        ApproximateByteSize = bytes,
    };
}
