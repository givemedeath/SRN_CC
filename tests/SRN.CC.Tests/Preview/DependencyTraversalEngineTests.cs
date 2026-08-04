using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.Tests.Preview;

[TestFixture]
public class DependencyTraversalEngineTests
{
    private DependencyTraversalEngine _engine = null!;
    private MockDependencyAnalyzer _analyzer = null!;
    private StubRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        _registry = new StubRegistry();
        _analyzer = new MockDependencyAnalyzer();
        _engine = new DependencyTraversalEngine(
            analyzer: _analyzer,
            locator: LocateAsset,
            streamProvider: ProvideStream);
    }

    #region Simple Traversal Tests

    [Test]
    public async Task Traverse_WithSingleRootNoDependencies_ReturnsOnlyRoot()
    {
        // Arrange
        _analyzer.SetDependencies(CreateId("root"), new HashSet<AssetIdentity>());
        var root = CreateId("root");

        // Act
        var result = await _engine.TraverseAsync([root]);

        // Assert
        Assert.That(result.Resolved, Contains.Item(root));
        Assert.That(result.Resolved.Count, Is.EqualTo(1));
        Assert.That(result.Unresolved, Is.Empty);
    }

    [Test]
    public async Task Traverse_WithSimpleLinearChain_ReturnsAllDependencies()
    {
        // Arrange
        var a = CreateId("a");
        var b = CreateId("b");
        var c = CreateId("c");

        _analyzer.SetDependencies(a, new HashSet<AssetIdentity> { b });
        _analyzer.SetDependencies(b, new HashSet<AssetIdentity> { c });
        _analyzer.SetDependencies(c, new HashSet<AssetIdentity>());

        // Act
        var result = await _engine.TraverseAsync([a]);

        // Assert
        Assert.That(result.Resolved.Count, Is.EqualTo(3));
        Assert.That(result.Resolved, Contains.Item(a));
        Assert.That(result.Resolved, Contains.Item(b));
        Assert.That(result.Resolved, Contains.Item(c));
        Assert.That(result.Unresolved, Is.Empty);
    }

    [Test]
    public async Task Traverse_WithMultipleBranches_ResolvesBothBranches()
    {
        // Arrange
        var root = CreateId("root");
        var left = CreateId("left");
        var right = CreateId("right");
        var shared = CreateId("shared");

        _analyzer.SetDependencies(root, new HashSet<AssetIdentity> { left, right });
        _analyzer.SetDependencies(left, new HashSet<AssetIdentity> { shared });
        _analyzer.SetDependencies(right, new HashSet<AssetIdentity> { shared });
        _analyzer.SetDependencies(shared, new HashSet<AssetIdentity>());

        // Act
        var result = await _engine.TraverseAsync([root]);

        // Assert
        Assert.That(result.Resolved.Count, Is.EqualTo(4));
        Assert.That(result.DuplicatesSuppressed, Is.GreaterThanOrEqualTo(1), "Shared dependency should be deduplicated");
    }

    #endregion

    #region Cycle Detection Tests

    [Test]
    public async Task Traverse_WithSimpleCycle_DetectsCycleAndCompletes()
    {
        // Arrange
        var a = CreateId("a");
        var b = CreateId("b");

        _analyzer.SetDependencies(a, new HashSet<AssetIdentity> { b });
        _analyzer.SetDependencies(b, new HashSet<AssetIdentity> { a }); // Cycle back

        // Act
        var result = await _engine.TraverseAsync([a]);

        // Assert
        // Both should be visited and processed without infinite loop
        Assert.That(result.Resolved.Count, Is.GreaterThanOrEqualTo(1));
        // Traversal should complete without infinite loop/stack overflow
        Assert.That(result.MaxDepth, Is.LessThanOrEqualTo(10));
    }

    [Test]
    public async Task Traverse_WithComplexCycle_CompletesWithoutInfiniteLoop()
    {
        // Arrange
        var root = CreateId("root");
        var a = CreateId("a");
        var b = CreateId("b");
        var c = CreateId("c");

        _analyzer.SetDependencies(root, new HashSet<AssetIdentity> { a, c });
        _analyzer.SetDependencies(a, new HashSet<AssetIdentity> { b });
        _analyzer.SetDependencies(b, new HashSet<AssetIdentity> { a }); // Cycle within a-b
        _analyzer.SetDependencies(c, new HashSet<AssetIdentity>());

        // Act
        var result = await _engine.TraverseAsync([root]);

        // Assert
        Assert.That(result.Resolved, Contains.Item(root));
        Assert.That(result.Resolved, Contains.Item(c));
        // Traversal should complete and not hang due to cycle
        Assert.That(result.MaxDepth, Is.LessThan(1000));
    }

    #endregion

    #region Depth Limit Tests

    [Test]
    public async Task Traverse_WithChainExceedingDepthLimit_StopsAtLimit()
    {
        // Arrange
        var maxDepth = 3;
        var ids = Enumerable.Range(0, 5)
            .Select(i => CreateId($"item_{i}"))
            .ToList();

        for (int i = 0; i < ids.Count - 1; i++)
        {
            _analyzer.SetDependencies(ids[i], new HashSet<AssetIdentity> { ids[i + 1] });
        }
        _analyzer.SetDependencies(ids.Last(), new HashSet<AssetIdentity>());

        // Act
        var result = await _engine.TraverseAsync([ids[0]], maxDepth);

        // Assert
        // Should have resolved some but not all due to depth limit
        Assert.That(result.Resolved.Count, Is.LessThan(ids.Count),
            "Should not resolve all items when depth limit is hit");
        Assert.That(result.MaxDepth, Is.LessThan(maxDepth + 1));
    }

    [Test]
    public async Task Traverse_WithDepthLimitOne_StopsAtDirectDependencies()
    {
        // Arrange
        var root = CreateId("root");
        var dep = CreateId("dep");
        var transDep = CreateId("trans_dep");

        _analyzer.SetDependencies(root, new HashSet<AssetIdentity> { dep });
        _analyzer.SetDependencies(dep, new HashSet<AssetIdentity> { transDep });

        // Act
        var result = await _engine.TraverseAsync([root], maxDepth: 1);

        // Assert
        // Should have root and direct deps, but not transitive deps beyond depth 1
        Assert.That(result.Resolved.Count, Is.LessThanOrEqualTo(2),
            "Depth limit 1 should prevent deep transitive dependencies");
    }

    #endregion

    #region Duplicate Suppression Tests

    [Test]
    public async Task Traverse_WithDuplicateReferencesFromMultipleBranches_DeduplicatesAndCounts()
    {
        // Arrange
        var root = CreateId("root");
        var branch1 = CreateId("branch1");
        var branch2 = CreateId("branch2");
        var shared = CreateId("shared");

        _analyzer.SetDependencies(root, new HashSet<AssetIdentity> { branch1, branch2 });
        _analyzer.SetDependencies(branch1, new HashSet<AssetIdentity> { shared });
        _analyzer.SetDependencies(branch2, new HashSet<AssetIdentity> { shared });
        _analyzer.SetDependencies(shared, new HashSet<AssetIdentity>());

        // Act
        var result = await _engine.TraverseAsync([root]);

        // Assert
        Assert.That(result.Resolved.Count, Is.EqualTo(4), "Should have 1 root + 2 branches + 1 shared");
        Assert.That(result.DuplicatesSuppressed, Is.GreaterThan(0), "Should have suppressed duplicate of 'shared'");
    }

    #endregion

    #region Unresolved Dependency Tests

    [Test]
    public async Task Traverse_WithMissingOccurrence_MarksDependencyUnresolved()
    {
        // Arrange
        var root = CreateId("root");
        var missing = CreateId("missing");

        _analyzer.SetDependencies(root, new HashSet<AssetIdentity> { missing });
        _analyzer.SetMissing(missing); // Mark as missing

        // Act
        var result = await _engine.TraverseAsync([root]);

        // Assert
        Assert.That(result.Resolved, Contains.Item(root));
        Assert.That(result.Unresolved, Contains.Key(missing));
        Assert.That(result.Unresolved[missing], Does.Contain("not found"));
    }

    #endregion

    #region Cancellation Tests

    [Test]
    public void Traverse_WithCancellationToken_ThrowsOperationCanceledException()
    {
        // Arrange
        var root = CreateId("root");
        _analyzer.SetDependencies(root, new HashSet<AssetIdentity>());

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await _engine.TraverseAsync([root], cancellationToken: cts.Token));
    }

    #endregion

    #region Metrics Tests

    [Test]
    public async Task Traverse_ReturnsCorrectMetrics()
    {
        // Arrange
        var a = CreateId("a");
        var b = CreateId("b");
        var c = CreateId("c");

        _analyzer.SetDependencies(a, new HashSet<AssetIdentity> { b });
        _analyzer.SetDependencies(b, new HashSet<AssetIdentity> { c });
        _analyzer.SetDependencies(c, new HashSet<AssetIdentity>());

        // Act
        var result = await _engine.TraverseAsync([a]);

        // Assert
        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result.MaxDepth, Is.EqualTo(2));
        Assert.That(result.DuplicatesSuppressed, Is.EqualTo(0));
    }

    #endregion

    #region Test Helpers

    private static AssetIdentity CreateId(string resref)
    {
        return new AssetIdentity(resref, 2000);
    }

    private async Task<AssetOccurrence?> LocateAsset(AssetIdentity identity, CancellationToken cancellationToken)
    {
        if (_analyzer.IsMissing(identity))
        {
            return null;
        }

        var source = AssetSource.CreateHak("test.hak", 0);
        return new AssetOccurrence(
            identity: identity,
            sourceId: source.Id,
            locator: new HakEntryLocator(0),
            originalName: $"{identity.Resref}.bin",
            size: 100,
            validationState: ValidationState.Valid,
            extensionMetadata: null,
            sha256: new byte[32]);
    }

    private async Task<Stream> ProvideStream(AssetOccurrence occurrence, Stream unused, CancellationToken cancellationToken)
    {
        // Return a simple memory stream with minimal data
        return await Task.FromResult<Stream>(new MemoryStream(new byte[] { 0, 1, 2 }));
    }

    private sealed class MockDependencyAnalyzer : IResourceTypeRegistry, IDependencyAnalyzer
    {
        private readonly Dictionary<AssetIdentity, IReadOnlySet<AssetIdentity>> _dependencyMap = new();
        private readonly HashSet<AssetIdentity> _missing = new();

        public void SetDependencies(AssetIdentity identity, HashSet<AssetIdentity> dependencies)
        {
            _dependencyMap[identity] = dependencies;
        }

        public void SetMissing(AssetIdentity identity)
        {
            _missing.Add(identity);
        }

        public bool IsMissing(AssetIdentity identity)
        {
            return _missing.Contains(identity);
        }

        public async Task<IReadOnlySet<AssetIdentity>> AnalyzeDependenciesAsync(
            AssetOccurrence occurrence,
            Stream stream,
            CancellationToken cancellationToken = default)
        {
            if (_dependencyMap.TryGetValue(occurrence.Identity, out var deps))
            {
                return deps;
            }

            return new HashSet<AssetIdentity>();
        }

        public bool TryGetType(string extension, out ushort typeId)
        {
            typeId = 2000;
            return true;
        }

        public bool TryGetExtension(ushort typeId, out string extension)
        {
            extension = "bin";
            return true;
        }
    }

    private sealed class StubRegistry : IResourceTypeRegistry
    {
        public bool TryGetType(string extension, out ushort typeId)
        {
            typeId = 2000;
            return true;
        }

        public bool TryGetExtension(ushort typeId, out string extension)
        {
            extension = "bin";
            return true;
        }
    }

    #endregion
}
