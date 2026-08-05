using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.Commands;
using SRN.CC.App.Services;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Preview;

namespace SRN.CC.Tests.Preview;

/// <summary>
/// Backfill for the dependency-test bullet at <c>PLAN.md:225</c>: curated precedence, base
/// fallback, missing references, cycles, closure size/count, unresolved dependencies, and no
/// automatic base-game packaging. Also the only direct coverage of the <c>totalBytes</c>
/// accumulation in <see cref="DependencyTraversalEngine"/>.
/// </summary>
[TestFixture]
public class DependencyClosureTests
{
    private const ushort MdlType = 2002;
    private const ushort MtrType = 2072;

    private DependencyGraphFixture _graph = null!;
    private DependencyTraversalEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _graph = new DependencyGraphFixture();
        _engine = new DependencyTraversalEngine(_graph, _graph.LocateAsync, _graph.OpenAsync);
    }

    #region Closure size (totalBytes)

    [Test]
    public async Task Closure_WithKnownOccurrenceSizes_ReportsNonZeroTotalBytes()
    {
        AssetIdentity root = _graph.AddNode("root", size: 1000);
        AssetIdentity child = _graph.AddNode("child", size: 234);
        _graph.SetDependencies(root, child);

        TraversalResult result = await _engine.TraverseAsync([root]);

        result.TotalBytes.Should().Be(1234, "totalBytes is the sum of every resolved occurrence's payload size");
        result.TotalBytes.Should().BePositive();
    }

    [Test]
    public async Task Closure_WithSharedDependencyReachedByTwoPaths_CountsItsBytesExactlyOnce()
    {
        AssetIdentity root = _graph.AddNode("root", size: 10);
        AssetIdentity left = _graph.AddNode("left", size: 20);
        AssetIdentity right = _graph.AddNode("right", size: 40);
        AssetIdentity shared = _graph.AddNode("shared", size: 8000);

        _graph.SetDependencies(root, left, right);
        _graph.SetDependencies(left, shared);
        _graph.SetDependencies(right, shared);

        TraversalResult result = await _engine.TraverseAsync([root]);

        result.Resolved.Should().HaveCount(4);
        result.DuplicatesSuppressed.Should().BeGreaterThan(0);
        result.TotalBytes.Should().Be(8070, "the shared 8000-byte payload must contribute once, not twice");
    }

    [Test]
    public async Task Closure_WithDuplicateRoots_CountsRootBytesExactlyOnce()
    {
        AssetIdentity root = _graph.AddNode("root", size: 512);

        TraversalResult result = await _engine.TraverseAsync([root, root, root]);

        result.Resolved.Should().HaveCount(1);
        result.TotalBytes.Should().Be(512);
    }

    [Test]
    public async Task Closure_WithUnresolvedDependency_ExcludesItsBytesFromTotalBytes()
    {
        AssetIdentity root = _graph.AddNode("root", size: 100);
        AssetIdentity missing = new AssetIdentity("missing", MdlType);
        _graph.SetDependencies(root, missing);

        TraversalResult result = await _engine.TraverseAsync([root]);

        result.Unresolved.Should().ContainKey(missing);
        result.TotalBytes.Should().Be(100, "an occurrence that could not be located contributes no bytes");
    }

    [Test]
    public async Task Closure_WithUnreadableOccurrence_ExcludesItsBytesFromTotalBytes()
    {
        AssetIdentity root = _graph.AddNode("root", size: 100);
        AssetIdentity broken = _graph.AddNode("broken", size: 9999);
        _graph.SetDependencies(root, broken);
        _graph.MarkStreamFailure(broken);

        TraversalResult result = await _engine.TraverseAsync([root]);

        result.Unresolved.Should().ContainKey(broken);
        result.Resolved.Should().NotContain(broken);
        result.TotalBytes.Should().Be(100, "an occurrence whose payload could not be analyzed is never resolved");
    }

    [Test]
    public async Task Closure_TotalBytes_ResetsBetweenTraversalsOnTheSameEngine()
    {
        AssetIdentity first = _graph.AddNode("first", size: 700);
        AssetIdentity second = _graph.AddNode("second", size: 300);

        TraversalResult firstResult = await _engine.TraverseAsync([first]);
        TraversalResult secondResult = await _engine.TraverseAsync([second]);

        firstResult.TotalBytes.Should().Be(700);
        secondResult.TotalBytes.Should().Be(300, "engine state, including totalBytes, must reset per traversal");
    }

    [Test]
    public async Task Closure_TotalBytes_IsZeroWhenNothingResolves()
    {
        AssetIdentity absent = new AssetIdentity("absent", MdlType);

        TraversalResult result = await _engine.TraverseAsync([absent]);

        result.Resolved.Should().BeEmpty();
        result.TotalBytes.Should().Be(0);
    }

    [Test]
    public async Task ClosureSummary_SurfacesTraversalTotalBytes()
    {
        AssetIdentity root = _graph.AddNode("root", size: 4096);
        AssetIdentity child = _graph.AddNode("child", size: 1024);
        _graph.SetDependencies(root, child);

        AddAvailableDependenciesCommand command = new(
            _graph,
            _graph,
            _graph.LocateAsync,
            _graph.OpenAsync);

        ClosureSummary summary = await command.ExecuteAsync([_graph.OccurrenceFor(root)]);

        summary.TotalBytes.Should().Be(5120, "ClosureSummary must carry the traversal's byte total, not a hard-coded zero");
        summary.Resolved.Should().HaveCount(2);
    }

    #endregion

    #region Closure size and count limits

    [Test]
    public async Task Closure_SizeLimit_DepthBoundedTraversalReportsSmallerTotalBytesThanFullClosure()
    {
        AssetIdentity[] chain = _graph.AddChain("size", length: 5, sizePerNode: 1000);

        TraversalResult full = await _engine.TraverseAsync([chain[0]]);
        TraversalResult bounded = await _engine.TraverseAsync([chain[0]], maxDepth: 2);

        full.TotalBytes.Should().Be(5000);
        bounded.TotalBytes.Should().Be(2000, "a bounded closure must report only the bytes it actually resolved");
        bounded.TotalBytes.Should().BeLessThan(full.TotalBytes);
    }

    [Test]
    public async Task Closure_CountLimit_DepthBoundCapsResolvedCountAndReportsRemainderUnresolved()
    {
        AssetIdentity[] chain = _graph.AddChain("count", length: 6, sizePerNode: 10);

        TraversalResult result = await _engine.TraverseAsync([chain[0]], maxDepth: 3);

        result.Count.Should().Be(3, "depth 0, 1, and 2 resolve; depth 3 is rejected by the limit");
        result.Resolved.Should().BeEquivalentTo(new[] { chain[0], chain[1], chain[2] });
        result.Unresolved.Should().ContainKey(chain[3]);
        result.Unresolved[chain[3]].Should().Contain("depth limit");
        result.MaxDepth.Should().Be(3);
    }

    [Test]
    public async Task Closure_CountLimit_RejectsNonPositiveMaxDepth()
    {
        AssetIdentity root = _graph.AddNode("root", size: 1);

        Func<Task> act = async () => await _engine.TraverseAsync([root], maxDepth: 0);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region Cycles

    [Test]
    public async Task Closure_WithTwoNodeCycle_TerminatesAndResolvesEachIdentityExactlyOnce()
    {
        AssetIdentity a = _graph.AddNode("cyc_a", size: 100);
        AssetIdentity b = _graph.AddNode("cyc_b", size: 200);
        _graph.SetDependencies(a, b);
        _graph.SetDependencies(b, a);

        TraversalResult result = await _engine.TraverseAsync([a]);

        result.Resolved.Should().BeEquivalentTo(new[] { a, b });
        result.TotalBytes.Should().Be(300, "a cycle must not double-count either member");
        _graph.AnalyzeCallCount(a).Should().Be(1);
        _graph.AnalyzeCallCount(b).Should().Be(1);
    }

    [Test]
    public async Task Closure_WithSelfReferencingNode_TerminatesAndCountsItOnce()
    {
        AssetIdentity self = _graph.AddNode("selfref", size: 64);
        _graph.SetDependencies(self, self);

        TraversalResult result = await _engine.TraverseAsync([self]);

        result.Resolved.Should().BeEquivalentTo(new[] { self });
        result.TotalBytes.Should().Be(64);
    }

    [Test]
    public async Task Closure_WithLongCycleReachableFromRoot_TerminatesAndResolvesWholeRing()
    {
        AssetIdentity root = _graph.AddNode("ring_root", size: 1);
        AssetIdentity a = _graph.AddNode("ring_a", size: 2);
        AssetIdentity b = _graph.AddNode("ring_b", size: 4);
        AssetIdentity c = _graph.AddNode("ring_c", size: 8);

        _graph.SetDependencies(root, a);
        _graph.SetDependencies(a, b);
        _graph.SetDependencies(b, c);
        _graph.SetDependencies(c, a);

        TraversalResult result = await _engine.TraverseAsync([root]);

        result.Resolved.Should().BeEquivalentTo(new[] { root, a, b, c });
        result.TotalBytes.Should().Be(15);
        result.MaxDepth.Should().BeLessThan(10, "the ring must not be walked repeatedly");
    }

    #endregion

    #region Missing references and unresolved dependencies

    [Test]
    public async Task Closure_WithMissingReference_ReportsItUnresolvedWithoutFailingTheClosure()
    {
        AssetIdentity root = _graph.AddNode("root", size: 10);
        AssetIdentity present = _graph.AddNode("present", size: 20);
        AssetIdentity missing = new AssetIdentity("gone", MdlType);

        _graph.SetDependencies(root, present, missing);

        TraversalResult result = await _engine.TraverseAsync([root]);

        result.Resolved.Should().BeEquivalentTo(new[] { root, present });
        result.Unresolved.Should().ContainKey(missing);
        result.Unresolved[missing].Should().Contain("not found");
    }

    [Test]
    public async Task Closure_WithMissingReferencesInSeveralFamilies_GroupsThemByFamilyDeterministically()
    {
        AssetIdentity root = _graph.AddNode("root", size: 10);
        AssetIdentity missingModel = new AssetIdentity("gone_mdl", MdlType);
        AssetIdentity missingMaterial = new AssetIdentity("gone_mtr", MtrType);
        _graph.SetDependencies(root, missingModel, missingMaterial);

        TraversalResult result = await _engine.TraverseAsync([root]);
        IReadOnlyList<UnresolvedDependencyGroup> groups =
            UnresolvedDependencyGrouper.GroupUnresolved(result.Unresolved, _graph);

        groups.Should().HaveCount(2);
        groups.Select(g => g.Family).Should().BeInAscendingOrder();
        groups.Select(g => g.Family).Should().Contain(new[] { "mdl", "mtr" });
        groups.Sum(g => g.Count).Should().Be(2);
    }

    [Test]
    public async Task Closure_WithUnresolvedDependency_StillResolvesUnaffectedSiblings()
    {
        AssetIdentity root = _graph.AddNode("root", size: 1);
        AssetIdentity healthy = _graph.AddNode("healthy", size: 2);
        AssetIdentity healthyChild = _graph.AddNode("healthy_child", size: 4);
        AssetIdentity broken = _graph.AddNode("broken", size: 8);

        _graph.SetDependencies(root, healthy, broken);
        _graph.SetDependencies(healthy, healthyChild);
        _graph.MarkStreamFailure(broken);

        TraversalResult result = await _engine.TraverseAsync([root]);

        result.Resolved.Should().BeEquivalentTo(new[] { root, healthy, healthyChild });
        result.Unresolved.Should().ContainKey(broken);
        result.TotalBytes.Should().Be(7);
    }

    #endregion

    #region Curated precedence, base fallback, no automatic base-game packaging

    [Test]
    public async Task Closure_WithIdentityInTwoSources_UsesCuratedPrecedenceWinnerAsTheDependency()
    {
        Guid highPriority = Guid.NewGuid();
        Guid lowPriority = Guid.NewGuid();

        AssetSource high = new(highPriority, AssetSourceKind.Hak, "c:/high.hak", priorityOrdinal: 0);
        AssetSource low = new(lowPriority, AssetSourceKind.Hak, "c:/low.hak", priorityOrdinal: 1);

        AssetIdentity identity = new("shared_dep", MdlType);
        AssetOccurrence winner = new(identity, highPriority, new HakEntryLocator(0), "shared_dep.mdl", 100);
        AssetOccurrence loser = new(identity, lowPriority, new HakEntryLocator(7), "shared_dep.mdl", 999);

        WorkspaceState state = CreateWorkspaceState(
            sources: [high, low],
            new CuratedAsset(
                identity: identity,
                allOccurrences: [winner, loser],
                resolvedOccurrence: winner,
                pin: null,
                status: ResolutionStatus.Resolved,
                isSelected: true,
                hasCrossSourceCollision: true));

        DependencyLocator locator = new(state, (_, _, fallback, _) => Task.FromResult(fallback));

        AssetOccurrence? resolved = await locator.ResolveAsync(identity);

        resolved.Should().NotBeNull();
        resolved!.SourceId.Should().Be(highPriority, "the curated (highest-priority) occurrence must win the dependency lookup");
        resolved.Locator.Should().Be(new HakEntryLocator(0));
    }

    [Test]
    public async Task Closure_WithDependencyOnlyInBaseGame_ReportsItUnresolvedAndPackagesNothingAutomatically()
    {
        AssetIdentity root = new("root_mdl", MdlType);
        AssetIdentity baseOnly = new("base_only", MdlType);

        Guid sourceId = Guid.NewGuid();
        AssetSource source = new(sourceId, AssetSourceKind.Hak, "c:/curated.hak", priorityOrdinal: 0);
        AssetOccurrence rootOccurrence = new(root, sourceId, new HakEntryLocator(0), "root_mdl.mdl", 256);

        WorkspaceState state = CreateWorkspaceState(
            sources: [source],
            new CuratedAsset(
                identity: root,
                allOccurrences: [rootOccurrence],
                resolvedOccurrence: rootOccurrence,
                pin: null,
                status: ResolutionStatus.Resolved,
                isSelected: true));

        // The base-game catalog holds `baseOnly`, but the dependency locator is workspace-only:
        // nothing from the base game may silently enter the closure and therefore the build.
        StubBaseGameCatalog catalog = new();
        catalog.Add(baseOnly);

        _graph.SetDependencies(root, baseOnly);

        DependencyLocator locator = new(state, (_, _, fallback, _) => Task.FromResult(fallback));
        DependencyTraversalEngine engine = new(
            _graph,
            (id, ct) => locator.ResolveAsync(id, ct),
            (occ, fallback, ct) => Task.FromResult<Stream>(new MemoryStream()));

        TraversalResult result = await engine.TraverseAsync([root]);

        result.Resolved.Should().BeEquivalentTo(new[] { root });
        result.Resolved.Should().NotContain(baseOnly, "base-game resources are never packaged automatically");
        result.Unresolved.Should().ContainKey(baseOnly);
        catalog.Contains(baseOnly).Should().BeTrue("the catalog does hold it - the closure still refuses to adopt it");
    }

    [Test]
    public async Task Closure_BaseGameFallback_SuppliesPayloadForPreviewWithBaseGameOrigin()
    {
        AssetIdentity baseOnly = new("base_tex", MdlType);

        WorkspaceState empty = CreateWorkspaceState(sources: []);
        StubBaseGameCatalog catalog = new();
        catalog.Add(baseOnly);

        WorkspaceTextureSource textureSource = new(
            empty,
            new ThrowingDispatcher(),
            _graph,
            catalog);

        TextureLookupResult? lookup = await textureSource.OpenTextureAsync("base_tex.mdl", TextureKind.Diffuse);

        lookup.Should().NotBeNull();
        lookup!.Origin.Should().Be(TextureOrigin.BaseGame, "a resource absent from the workspace falls back to the base game for preview only");
        lookup.Identity.Should().Be(baseOnly);
        await lookup.Payload.DisposeAsync();
    }

    [Test]
    public async Task Closure_CuratedPrecedence_WorkspaceOccurrenceBeatsBaseGameCatalogEntry()
    {
        AssetIdentity identity = new("both_places", MdlType);
        Guid sourceId = Guid.NewGuid();
        AssetSource source = new(sourceId, AssetSourceKind.Hak, "c:/curated.hak", priorityOrdinal: 0);
        AssetOccurrence occurrence = new(identity, sourceId, new HakEntryLocator(3), "both_places.mdl", 64);

        WorkspaceState state = CreateWorkspaceState(
            sources: [source],
            new CuratedAsset(
                identity: identity,
                allOccurrences: [occurrence],
                resolvedOccurrence: occurrence,
                pin: null,
                status: ResolutionStatus.Resolved,
                isSelected: true));

        StubBaseGameCatalog catalog = new();
        catalog.Add(identity);

        WorkspaceTextureSource textureSource = new(
            state,
            new MemoryDispatcher(),
            _graph,
            catalog);

        TextureLookupResult? lookup = await textureSource.OpenTextureAsync("both_places.mdl", TextureKind.Diffuse);

        lookup.Should().NotBeNull();
        lookup!.Origin.Should().Be(TextureOrigin.Workspace, "curated content takes precedence over the base game");
        catalog.OpenCount.Should().Be(0, "the base game must not even be consulted when the workspace has the resource");
        await lookup.Payload.DisposeAsync();
    }

    #endregion

    #region Helpers

    private static WorkspaceState CreateWorkspaceState(IReadOnlyList<AssetSource> sources, params CuratedAsset[] assets)
    {
        return new WorkspaceState(
            sources: sources,
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: assets,
            selectionState: SelectionState.IncludeAll(),
            pins: Array.Empty<WinnerPin>(),
            preferences: new ProjectPreferences());
    }

    private sealed class StubBaseGameCatalog : IBaseGameResourceCatalog
    {
        private readonly HashSet<AssetIdentity> _entries = new();

        public int OpenCount { get; private set; }

        public void Add(AssetIdentity identity) => _entries.Add(identity);

        public bool Contains(AssetIdentity identity) => _entries.Contains(identity);

        public Task<Stream> OpenAsync(AssetIdentity identity, CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3, 4 }));
        }
    }

    private sealed class ThrowingDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("No workspace source should be opened in this test.");
        }
    }

    private sealed class MemoryDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Stream>(new MemoryStream(new byte[] { 9, 9 }));
        }
    }

    /// <summary>
    /// An in-memory dependency graph that doubles as the analyzer, the locator, the stream
    /// provider, and the resource-type registry. Every node carries an explicit payload size so
    /// <c>totalBytes</c> assertions are exact.
    /// </summary>
    private sealed class DependencyGraphFixture : IDependencyAnalyzer, IResourceTypeRegistry
    {
        private readonly Guid _sourceId = Guid.NewGuid();
        private readonly Dictionary<AssetIdentity, AssetOccurrence> _occurrences = new();
        private readonly Dictionary<AssetIdentity, HashSet<AssetIdentity>> _dependencies = new();
        private readonly HashSet<AssetIdentity> _streamFailures = new();
        private readonly Dictionary<AssetIdentity, int> _analyzeCalls = new();
        private int _nextEntryIndex;

        public AssetIdentity AddNode(string resref, long size, ushort resourceType = MdlType)
        {
            AssetIdentity identity = new(resref, resourceType);
            _occurrences[identity] = new AssetOccurrence(
                identity,
                _sourceId,
                new HakEntryLocator(_nextEntryIndex++),
                $"{resref}.bin",
                size);
            _dependencies[identity] = new HashSet<AssetIdentity>();
            return identity;
        }

        public AssetIdentity[] AddChain(string prefix, int length, long sizePerNode)
        {
            AssetIdentity[] chain = Enumerable.Range(0, length)
                .Select(i => AddNode($"{prefix}_{i}", sizePerNode))
                .ToArray();

            for (int i = 0; i < chain.Length - 1; i++)
            {
                SetDependencies(chain[i], chain[i + 1]);
            }

            return chain;
        }

        public void SetDependencies(AssetIdentity identity, params AssetIdentity[] dependencies)
        {
            _dependencies[identity] = new HashSet<AssetIdentity>(dependencies);
        }

        public void MarkStreamFailure(AssetIdentity identity) => _streamFailures.Add(identity);

        public AssetOccurrence OccurrenceFor(AssetIdentity identity) => _occurrences[identity];

        public int AnalyzeCallCount(AssetIdentity identity) =>
            _analyzeCalls.TryGetValue(identity, out int count) ? count : 0;

        public Task<AssetOccurrence?> LocateAsync(AssetIdentity identity, CancellationToken cancellationToken)
        {
            return Task.FromResult(_occurrences.TryGetValue(identity, out AssetOccurrence? occ) ? occ : null);
        }

        public Task<Stream> OpenAsync(AssetOccurrence occurrence, Stream fallback, CancellationToken cancellationToken)
        {
            if (_streamFailures.Contains(occurrence.Identity))
            {
                throw new IOException($"Simulated read failure for {occurrence.Identity.Resref}.");
            }

            return Task.FromResult<Stream>(new MemoryStream(new byte[] { 0, 1, 2 }));
        }

        public Task<IReadOnlySet<AssetIdentity>> AnalyzeDependenciesAsync(
            AssetOccurrence occurrence,
            Stream stream,
            CancellationToken cancellationToken = default)
        {
            _analyzeCalls[occurrence.Identity] = AnalyzeCallCount(occurrence.Identity) + 1;

            IReadOnlySet<AssetIdentity> deps = _dependencies.TryGetValue(occurrence.Identity, out HashSet<AssetIdentity>? set)
                ? set
                : new HashSet<AssetIdentity>();
            return Task.FromResult(deps);
        }

        public bool TryGetType(string extension, out ushort typeId)
        {
            typeId = extension.ToLowerInvariant() switch
            {
                "mdl" => MdlType,
                "mtr" => MtrType,
                _ => (ushort)0
            };
            return typeId != 0;
        }

        public bool TryGetExtension(ushort typeId, out string extension)
        {
            extension = typeId switch
            {
                MdlType => "mdl",
                MtrType => "mtr",
                _ => string.Empty
            };
            return extension.Length > 0;
        }
    }

    #endregion
}
