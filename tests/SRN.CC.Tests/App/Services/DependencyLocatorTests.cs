using NUnit.Framework;
using SRN.CC.App.Services;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
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
}
