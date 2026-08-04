using NUnit.Framework;
using SRN.CC.App.Commands;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.Tests.App.Commands;

[TestFixture]
public class AddAvailableDependenciesCommandTests
{
    private AddAvailableDependenciesCommand _command = null!;
    private StubAnalyzer _analyzer = null!;
    private StubRegistry _registry = null!;
    private StubLocator _locator = null!;

    [SetUp]
    public void Setup()
    {
        _registry = new StubRegistry();
        _analyzer = new StubAnalyzer();
        _locator = new StubLocator();

        async Task<AssetOccurrence?> Locator(AssetIdentity id, CancellationToken ct)
        {
            return await _locator.LocateAsync(id, ct);
        }

        async Task<Stream> StreamProvider(AssetOccurrence occ, Stream fallback, CancellationToken ct)
        {
            return new MemoryStream();
        }

        _command = new AddAvailableDependenciesCommand(
            _analyzer,
            _registry,
            Locator,
            StreamProvider);
    }

    [Test]
    public async Task ExecuteAsync_WithEmptyOccurrences_ReturnsEmptySummary()
    {
        // Arrange
        var occurrences = Array.Empty<AssetOccurrence>();

        // Act
        var summary = await _command.ExecuteAsync(occurrences);

        // Assert
        Assert.That(summary, Is.Not.Null);
        Assert.That(summary.Resolved, Is.Empty);
        Assert.That(summary.UnresolvedGroups, Is.Empty);
        Assert.That(summary.TotalBytes, Is.EqualTo(0));
    }

    [Test]
    public async Task ExecuteAsync_WithSingleOccurrence_ReturnsSummary()
    {
        // Arrange
        var identity = new AssetIdentity("test_model", 2002);
        var occurrence = CreateOccurrence(identity);
        var occurrences = new[] { occurrence };

        // Act
        var summary = await _command.ExecuteAsync(occurrences);

        // Assert
        Assert.That(summary, Is.Not.Null);
        Assert.That(summary.Resolved, Is.Not.Null);
        Assert.That(summary.UnresolvedGroups, Is.Not.Null);
    }

    [Test]
    public async Task ExecuteAsync_IsDeterministic()
    {
        // Arrange
        var identity = new AssetIdentity("test_model", 2002);
        var occurrence = CreateOccurrence(identity);
        var occurrences = new[] { occurrence };

        // Act - call twice with same input
        var summary1 = await _command.ExecuteAsync(occurrences);
        var summary2 = await _command.ExecuteAsync(occurrences);

        // Assert - results should be identical
        Assert.That(summary1.Resolved.Count, Is.EqualTo(summary2.Resolved.Count));
        Assert.That(summary1.UnresolvedGroups.Count, Is.EqualTo(summary2.UnresolvedGroups.Count));
        Assert.That(summary1.MaxDepth, Is.EqualTo(summary2.MaxDepth));
        Assert.That(summary1.DuplicatesSuppressed, Is.EqualTo(summary2.DuplicatesSuppressed));
    }

    [Test]
    public void ExecuteAsync_SupportsCancellation()
    {
        // Arrange
        var identity = new AssetIdentity("test_model", 2002);
        var occurrence = CreateOccurrence(identity);
        var occurrences = new[] { occurrence };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - cancellation should propagate
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _command.ExecuteAsync(occurrences, cts.Token));
    }

    private static AssetOccurrence CreateOccurrence(AssetIdentity identity)
    {
        return new AssetOccurrence(
            identity,
            Guid.NewGuid(),
            new HakEntryLocator(0),
            identity.OriginalName,
            1024);
    }

    private sealed class StubAnalyzer : IDependencyAnalyzer
    {
        public Task<IReadOnlySet<AssetIdentity>> AnalyzeDependenciesAsync(
            AssetOccurrence occurrence,
            Stream stream,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlySet<AssetIdentity>>(new HashSet<AssetIdentity>());
        }
    }

    private sealed class StubLocator
    {
        private readonly Dictionary<AssetIdentity, AssetOccurrence> _store = new();

        public void RegisterOccurrence(AssetIdentity identity, AssetOccurrence occurrence)
        {
            _store[identity] = occurrence;
        }

        public Task<AssetOccurrence?> LocateAsync(AssetIdentity identity, CancellationToken ct)
        {
            return Task.FromResult(_store.TryGetValue(identity, out var occ) ? occ : null);
        }
    }

    private sealed class StubRegistry : IResourceTypeRegistry
    {
        public bool TryGetType(string extension, out ushort typeId)
        {
            typeId = extension.ToLowerInvariant() switch
            {
                "mdl" => 2002,
                "mtr" => 2072,
                _ => 0
            };
            return typeId != 0;
        }

        public bool TryGetExtension(ushort typeId, out string extension)
        {
            extension = typeId switch
            {
                2002 => "mdl",
                2072 => "mtr",
                _ => ""
            };
            return !string.IsNullOrEmpty(extension);
        }
    }
}
