using System.Text;
using NUnit.Framework;
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

namespace SRN.CC.Tests.App.Services;

[TestFixture]
public class DependencyLocatorTests
{
    private DependencyLocator _locator = null!;
    private WorkspaceState _workspaceState = null!;

    [SetUp]
    public void Setup()
    {
        // Create a mock workspace with some assets
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();

        var identity1 = new AssetIdentity("model_01", 2002);
        var identity2 = new AssetIdentity("model_02", 2002);

        var occurrence1 = new AssetOccurrence(identity1, guid1, new HakEntryLocator(0), "model_01.mdl", 1024);
        var occurrence2 = new AssetOccurrence(identity2, guid2, new HakEntryLocator(1), "model_02.mdl", 2048);

        var curatedAsset1 = new CuratedAsset(
            identity: identity1,
            allOccurrences: new[] { occurrence1 },
            resolvedOccurrence: occurrence1,
            pin: null,
            status: ResolutionStatus.Resolved,
            isSelected: false);

        var curatedAsset2 = new CuratedAsset(
            identity: identity2,
            allOccurrences: new[] { occurrence2 },
            resolvedOccurrence: occurrence2,
            pin: null,
            status: ResolutionStatus.Resolved,
            isSelected: false);

        _workspaceState = new WorkspaceState(
            sources: Array.Empty<AssetSource>(),
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: new[] { curatedAsset1, curatedAsset2 },
            selectionState: new SelectionState(),
            pins: Array.Empty<WinnerPin>(),
            preferences: new ProjectPreferences());

        async Task<Stream> StreamOpener(
            SRN.CC.Core.Sources.AssetSource source,
            AssetOccurrence occ,
            Stream fallback,
            CancellationToken ct)
        {
            return new MemoryStream();
        }

        _locator = new DependencyLocator(_workspaceState, StreamOpener);
    }

    [Test]
    public async Task ResolveAsync_WithExistingAsset_ReturnsOccurrence()
    {
        // Arrange
        var identity = new AssetIdentity("model_01", 2002);

        // Act
        var result = await _locator.ResolveAsync(identity);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Identity, Is.EqualTo(identity));
    }

    [Test]
    public async Task ResolveAsync_WithNonexistentAsset_ReturnsNull()
    {
        // Arrange
        var identity = new AssetIdentity("missing_model", 2002);

        // Act
        var result = await _locator.ResolveAsync(identity);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ResolveAsync_CachesOccurrencesForFastLookup()
    {
        // Arrange
        var identity = new AssetIdentity("model_01", 2002);

        // Act - resolve multiple times
        var result1 = await _locator.ResolveAsync(identity);
        var result2 = await _locator.ResolveAsync(identity);

        // Assert - should return same instance
        Assert.That(result1, Is.Not.Null);
        Assert.That(result2, Is.Not.Null);
        Assert.That(result1!.SourceId, Is.EqualTo(result2!.SourceId));
    }

    [Test]
    public async Task OpenStreamAsync_WithValidOccurrence_ReturnsStream()
    {
        // Arrange
        var identity = new AssetIdentity("model_01", 2002);
        var occurrence = await _locator.ResolveAsync(identity);
        Assert.That(occurrence, Is.Not.Null);

        var fallback = new MemoryStream();

        // Act
        var stream = await _locator.OpenStreamAsync(occurrence!, fallback);

        // Assert
        Assert.That(stream, Is.Not.Null);
    }

    [Test]
    public async Task OpenStreamAsync_WithoutMatchingSource_ReturnsFallback()
    {
        // Arrange
        var identity = new AssetIdentity("model_01", 2002);
        var occurrence = await _locator.ResolveAsync(identity);
        Assert.That(occurrence, Is.Not.Null);

        var fallback = new MemoryStream();

        // Act - workspace has no sources, so should return fallback
        var stream = await _locator.OpenStreamAsync(occurrence!, fallback);

        // Assert
        Assert.That(stream, Is.SameAs(fallback));
    }

    [Test]
    public async Task ResolveAsync_WorkspaceTakesPrecedenceOverBaseGame()
    {
        var catalog = new FakeCatalog();
        catalog.Add(new AssetIdentity("model_01", 2002)); // also in the base game
        async Task<Stream> StreamOpener(AssetSource s, AssetOccurrence o, Stream f, CancellationToken ct) => new MemoryStream();
        var locator = new DependencyLocator(_workspaceState, StreamOpener, catalog);

        var result = await locator.ResolveAsync(new AssetIdentity("model_01", 2002));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SourceId, Is.Not.EqualTo(locator.BaseGameSourceId),
            "a curated occurrence wins over the base game");
        Assert.That(locator.BaseGameSatisfied, Is.Empty);
    }

    [Test]
    public async Task ResolveAsync_FallsBackToBaseGame_AndRecordsItOnRead()
    {
        var baseId = new AssetIdentity("base_tex", 2000);
        var catalog = new FakeCatalog();
        catalog.Add(baseId);
        async Task<Stream> StreamOpener(AssetSource s, AssetOccurrence o, Stream f, CancellationToken ct) => new MemoryStream();
        var locator = new DependencyLocator(_workspaceState, StreamOpener, catalog);

        var result = await locator.ResolveAsync(baseId);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SourceId, Is.EqualTo(locator.BaseGameSourceId));
        // Satisfaction is recorded on a successful read, not at resolve time.
        Assert.That(locator.BaseGameSatisfied, Is.Empty, "not yet satisfied until the payload is read");

        // The synthesized occurrence streams from the catalog so traversal can read it.
        using var stream = await locator.OpenStreamAsync(result, Stream.Null);
        using var reader = new StreamReader(stream);
        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("base:base_tex"));
        Assert.That(locator.BaseGameSatisfied, Does.Contain(baseId), "recorded once the read succeeds");
    }

    [Test]
    public async Task OpenStreamAsync_BaseGameReadFails_DoesNotRecordSatisfaction()
    {
        // The KEY lists the identity but its BIF is missing/corrupt, so OpenAsync throws. Traversal
        // will report the identity unresolved; it must NOT also be counted as base-game-satisfied.
        var baseId = new AssetIdentity("broken_base", 2000);
        var catalog = new FakeCatalog();
        catalog.Add(baseId, throwOnOpen: true);
        async Task<Stream> StreamOpener(AssetSource s, AssetOccurrence o, Stream f, CancellationToken ct) => new MemoryStream();
        var locator = new DependencyLocator(_workspaceState, StreamOpener, catalog);

        var result = await locator.ResolveAsync(baseId);
        Assert.That(result, Is.Not.Null);

        Assert.ThrowsAsync<IOException>(async () => await locator.OpenStreamAsync(result!, Stream.Null));
        Assert.That(locator.BaseGameSatisfied, Is.Empty, "a failed base-game read is never recorded as satisfied");
    }

    [Test]
    public async Task OpenStreamAsync_WorkspaceSourceWithEmptyGuid_DoesNotRouteToBaseGame()
    {
        // A project whose source pathologically carries the all-zero GUID must still open from its own
        // payload, never from the base-game catalog. The marker id is chosen to avoid this collision.
        var emptyGuid = Guid.Empty;
        var identity = new AssetIdentity("collide", 2002);
        var occurrence = new AssetOccurrence(identity, emptyGuid, new HakEntryLocator(0), "collide.mdl", 10);
        var source = new AssetSource(emptyGuid, AssetSourceKind.Hak, "c:/collide.hak", 0);
        var curated = new CuratedAsset(identity, new[] { occurrence }, occurrence, null, ResolutionStatus.Resolved, false);

        var state = new WorkspaceState(
            sources: new[] { source },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: new[] { curated },
            selectionState: new SelectionState(),
            pins: Array.Empty<WinnerPin>(),
            preferences: new ProjectPreferences());

        var catalog = new FakeCatalog();
        catalog.Add(identity); // the base game also has this identity

        async Task<Stream> StreamOpener(AssetSource s, AssetOccurrence o, Stream f, CancellationToken ct)
            => new MemoryStream(Encoding.UTF8.GetBytes("workspace-payload"));
        var locator = new DependencyLocator(state, StreamOpener, catalog);

        Assert.That(locator.BaseGameSourceId, Is.Not.EqualTo(emptyGuid),
            "the marker must avoid a workspace source's id");

        using var stream = await locator.OpenStreamAsync(occurrence, Stream.Null);
        using var reader = new StreamReader(stream);
        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("workspace-payload"),
            "the occurrence must open from its real source, not the base-game catalog");
    }

    [Test]
    public async Task ResolveAsync_NoCatalog_UnresolvedStaysNull()
    {
        var result = await _locator.ResolveAsync(new AssetIdentity("not_here", 2000));
        Assert.That(result, Is.Null);
        Assert.That(_locator.BaseGameSatisfied, Is.Empty);
    }

    [Test]
    public async Task ResolveAsync_CuratedButUnresolved_NeverSatisfiedByBaseGame()
    {
        // The identity is present in the workspace but did not resolve (e.g. an invalid pin). It also
        // happens to exist in the base game. The base game must NOT paper over the curated error: the
        // dependency stays unresolved so the operator sees and fixes it, rather than shipping a build
        // that silently uses the base asset instead of the intended override.
        var brokenIdentity = new AssetIdentity("broken_override", 2002);
        var brokenOccurrence = new AssetOccurrence(brokenIdentity, Guid.NewGuid(), new HakEntryLocator(0), "broken_override.mdl", 512);
        var brokenAsset = new CuratedAsset(
            identity: brokenIdentity,
            allOccurrences: new[] { brokenOccurrence },
            resolvedOccurrence: null,
            pin: null,
            status: ResolutionStatus.InvalidPin,
            isSelected: false);

        var state = new WorkspaceState(
            sources: Array.Empty<AssetSource>(),
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: new[] { brokenAsset },
            selectionState: new SelectionState(),
            pins: Array.Empty<WinnerPin>(),
            preferences: new ProjectPreferences());

        var catalog = new FakeCatalog();
        catalog.Add(brokenIdentity); // the base game also has it
        async Task<Stream> StreamOpener(AssetSource s, AssetOccurrence o, Stream f, CancellationToken ct) => new MemoryStream();
        var locator = new DependencyLocator(state, StreamOpener, catalog);

        var result = await locator.ResolveAsync(brokenIdentity);

        Assert.That(result, Is.Null, "a curated-but-unresolved identity must not fall back to the base game");
        Assert.That(locator.BaseGameSatisfied, Is.Empty, "the base game must not be recorded as satisfying a curated error");
    }

    private sealed class FakeCatalog : IBaseGameResourceCatalog
    {
        private readonly HashSet<AssetIdentity> _ids = new();
        private readonly HashSet<AssetIdentity> _throwOnOpen = new();
        public void Add(AssetIdentity id, bool throwOnOpen = false)
        {
            _ids.Add(id);
            if (throwOnOpen) _throwOnOpen.Add(id);
        }
        public bool Contains(AssetIdentity identity) => _ids.Contains(identity);
        public Task<Stream> OpenAsync(AssetIdentity identity, CancellationToken cancellationToken = default)
        {
            if (_throwOnOpen.Contains(identity))
            {
                throw new IOException($"BIF for {identity.Resref} is missing or corrupt.");
            }
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes($"base:{identity.Resref}")));
        }
    }
}
