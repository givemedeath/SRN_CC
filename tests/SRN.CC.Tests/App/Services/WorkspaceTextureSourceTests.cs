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
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Tests.App.Services;

[TestFixture]
public class WorkspaceTextureSourceTests
{
    private static readonly IResourceTypeRegistry Registry = new ResourceTypeRegistry();
    private static readonly byte[] WorkspaceBytes = { 1, 2, 3, 4 };
    private static readonly byte[] BaseGameBytes = { 9, 9, 9, 9 };

    [Test]
    public async Task OpenTextureAsync_WorkspaceMatch_BeatsBaseGameCatalog()
    {
        var identity = new AssetIdentity("wall_diff", 2033); // dds
        Guid sourceId = Guid.NewGuid();
        var occurrence = new AssetOccurrence(identity, sourceId, new FolderFileLocator("textures/wall_diff.dds"), "wall_diff.dds", WorkspaceBytes.Length);
        var workspaceState = BuildWorkspaceState(sourceId, identity, occurrence);

        var dispatcher = new FakeReaderDispatcher { Bytes = WorkspaceBytes };
        var catalog = new FakeBaseGameCatalog();
        catalog.Add(identity, BaseGameBytes);

        var source = new WorkspaceTextureSource(workspaceState, dispatcher, Registry, catalog);

        TextureLookupResult? result = await source.OpenTextureAsync("wall_diff.dds", TextureKind.Diffuse);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Origin, Is.EqualTo(TextureOrigin.Workspace));
        Assert.That(result.Identity, Is.EqualTo(identity));
        Assert.That(ReadAll(result.Payload), Is.EqualTo(WorkspaceBytes));
        Assert.That(catalog.OpenCallCount, Is.EqualTo(0), "base-game catalog must not be consulted once workspace resolves.");
    }

    [Test]
    public async Task OpenTextureAsync_NoWorkspaceMatch_FallsBackToBaseGameCatalog()
    {
        var identity = new AssetIdentity("floor_diff", 3); // tga
        var workspaceState = EmptyWorkspaceState();

        var dispatcher = new FakeReaderDispatcher();
        var catalog = new FakeBaseGameCatalog();
        catalog.Add(identity, BaseGameBytes);

        var source = new WorkspaceTextureSource(workspaceState, dispatcher, Registry, catalog);

        TextureLookupResult? result = await source.OpenTextureAsync("floor_diff.tga", TextureKind.Diffuse);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Origin, Is.EqualTo(TextureOrigin.BaseGame));
        Assert.That(ReadAll(result.Payload), Is.EqualTo(BaseGameBytes));
    }

    [Test]
    public async Task OpenTextureAsync_UnresolvedEverywhere_ReturnsNull_NeverThrows()
    {
        var workspaceState = EmptyWorkspaceState();
        var source = new WorkspaceTextureSource(workspaceState, new FakeReaderDispatcher(), Registry, new FakeBaseGameCatalog());

        TextureLookupResult? result = await source.OpenTextureAsync("nowhere.dds", TextureKind.Diffuse);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task OpenTextureAsync_NoBaseGameCatalogLoaded_ReturnsNullRatherThanThrowing()
    {
        var workspaceState = EmptyWorkspaceState();
        var source = new WorkspaceTextureSource(workspaceState, new FakeReaderDispatcher(), Registry, baseGameCatalog: null);

        TextureLookupResult? result = await source.OpenTextureAsync("anything.dds", TextureKind.Diffuse);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task OpenTextureAsync_WorkspaceReaderThrows_FallsBackToBaseGameCatalog_NeverThrows()
    {
        var identity = new AssetIdentity("broken", 2033);
        Guid sourceId = Guid.NewGuid();
        var occurrence = new AssetOccurrence(identity, sourceId, new FolderFileLocator("textures/broken.dds"), "broken.dds", 4);
        var workspaceState = BuildWorkspaceState(sourceId, identity, occurrence);

        var dispatcher = new FakeReaderDispatcher { ThrowOnOpen = true };
        var catalog = new FakeBaseGameCatalog();
        catalog.Add(identity, BaseGameBytes);

        var source = new WorkspaceTextureSource(workspaceState, dispatcher, Registry, catalog);

        TextureLookupResult? result = await source.OpenTextureAsync("broken.dds", TextureKind.Diffuse);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Origin, Is.EqualTo(TextureOrigin.BaseGame));
    }

    [Test]
    public async Task OpenTextureAsync_BothLayersFail_ReturnsNull_NeverThrows()
    {
        var identity = new AssetIdentity("broken2", 2033);
        Guid sourceId = Guid.NewGuid();
        var occurrence = new AssetOccurrence(identity, sourceId, new FolderFileLocator("textures/broken2.dds"), "broken2.dds", 4);
        var workspaceState = BuildWorkspaceState(sourceId, identity, occurrence);

        var dispatcher = new FakeReaderDispatcher { ThrowOnOpen = true };
        var catalog = new FakeBaseGameCatalog { ThrowOnOpen = true };
        catalog.Add(identity, BaseGameBytes);

        var source = new WorkspaceTextureSource(workspaceState, dispatcher, Registry, catalog);

        TextureLookupResult? result = await source.OpenTextureAsync("broken2.dds", TextureKind.Diffuse);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task OpenTextureAsync_ResrefWithoutExtension_ReturnsNull()
    {
        var workspaceState = EmptyWorkspaceState();
        var source = new WorkspaceTextureSource(workspaceState, new FakeReaderDispatcher(), Registry, new FakeBaseGameCatalog());

        TextureLookupResult? result = await source.OpenTextureAsync("no_extension", TextureKind.Diffuse);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task OpenTextureAsync_UnknownExtension_ReturnsNull()
    {
        var workspaceState = EmptyWorkspaceState();
        var source = new WorkspaceTextureSource(workspaceState, new FakeReaderDispatcher(), Registry, new FakeBaseGameCatalog());

        TextureLookupResult? result = await source.OpenTextureAsync("thing.notarealextension", TextureKind.Diffuse);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void OpenTextureAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var workspaceState = EmptyWorkspaceState();
        var source = new WorkspaceTextureSource(workspaceState, new FakeReaderDispatcher(), Registry, new FakeBaseGameCatalog());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await source.OpenTextureAsync("anything.dds", TextureKind.Diffuse, cts.Token));
    }

    // -------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------

    private static byte[] ReadAll(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static WorkspaceState BuildWorkspaceState(Guid sourceId, AssetIdentity identity, AssetOccurrence occurrence)
    {
        var curated = new CuratedAsset(
            identity: identity,
            allOccurrences: new[] { occurrence },
            resolvedOccurrence: occurrence,
            pin: null,
            status: ResolutionStatus.Resolved,
            isSelected: false);

        var assetSource = new AssetSource(sourceId, AssetSourceKind.Folder, Path.GetTempPath());

        return new WorkspaceState(
            sources: new[] { assetSource },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: new[] { curated },
            selectionState: new SelectionState(),
            pins: Array.Empty<WinnerPin>(),
            preferences: new ProjectPreferences());
    }

    private static WorkspaceState EmptyWorkspaceState()
    {
        return new WorkspaceState(
            sources: Array.Empty<AssetSource>(),
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: Array.Empty<CuratedAsset>(),
            selectionState: new SelectionState(),
            pins: Array.Empty<WinnerPin>(),
            preferences: new ProjectPreferences());
    }

    private sealed class FakeReaderDispatcher : ISourceReaderDispatcher
    {
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public bool ThrowOnOpen { get; set; }

        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            if (ThrowOnOpen)
            {
                throw new IOException("simulated workspace read failure");
            }

            return Task.FromResult<Stream>(new MemoryStream(Bytes));
        }
    }

    private sealed class FakeBaseGameCatalog : IBaseGameResourceCatalog
    {
        private readonly Dictionary<AssetIdentity, byte[]> _entries = new();

        public bool ThrowOnOpen { get; set; }
        public int OpenCallCount { get; private set; }

        public void Add(AssetIdentity identity, byte[] bytes) => _entries[identity] = bytes;

        public bool Contains(AssetIdentity identity) => _entries.ContainsKey(identity);

        public Task<Stream> OpenAsync(AssetIdentity identity, CancellationToken cancellationToken = default)
        {
            OpenCallCount++;

            if (ThrowOnOpen)
            {
                throw new IOException("simulated base-game read failure");
            }

            if (_entries.TryGetValue(identity, out byte[]? bytes))
            {
                return Task.FromResult<Stream>(new MemoryStream(bytes));
            }

            throw new KeyNotFoundException($"'{identity}' not found in fake base-game catalog.");
        }
    }
}
