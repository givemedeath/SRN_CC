using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Records;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Tests.Core;

/// <summary>
/// Per-source <see cref="SourceMode"/> semantics: Reference is never an automatic winner but is a
/// valid pin target; Hidden is fully excluded from resolution, diagnostic flags, and the row set,
/// yet a pin into a Hidden source surfaces as an invalid pin rather than vanishing.
/// </summary>
[TestFixture]
public class SourceModeResolutionTests
{
    private const ushort TgaType = 2000;

    [Test]
    public async Task Reference_NeverWinsAutomatically_LowerPriorityFullSourceWins()
    {
        Guid refId = Guid.NewGuid();
        Guid fullId = Guid.NewGuid();
        AssetSource reference = Source(refId, "c:/reference.hak", priority: 0, SourceMode.Reference);
        AssetSource full = Source(fullId, "c:/full.hak", priority: 1, SourceMode.Full);
        AssetIdentity identity = new("shared", TgaType);

        WorkspaceState state = await ResolveAsync(
            [reference, full],
            [],
            (reference, [Occurrence(identity, refId, 0)]),
            (full, [Occurrence(identity, fullId, 0)]));

        CuratedAsset asset = state.CuratedAssets.Single();
        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.ResolvedOccurrence!.SourceId.Should().Be(fullId,
            "a lower-priority Full source beats a higher-priority Reference source");
    }

    [Test]
    public async Task ReferenceOnlyIdentity_YieldsReferenceOnly_AndIsNeverSelected()
    {
        Guid refId = Guid.NewGuid();
        AssetSource reference = Source(refId, "c:/reference.hak", priority: 0, SourceMode.Reference);
        AssetIdentity identity = new("refonly", TgaType);

        WorkspaceState state = await ResolveAsync(
            [reference],
            [],
            (reference, [Occurrence(identity, refId, 0)]));

        CuratedAsset asset = state.CuratedAssets.Single();
        asset.Status.Should().Be(ResolutionStatus.ReferenceOnly);
        asset.ResolvedOccurrence.Should().BeNull();
        asset.IsSelected.Should().BeFalse("a reference-only identity is never auto-selected");
        asset.AllOccurrences.Should().ContainSingle("the reference occurrence is still visible/comparable");
    }

    [Test]
    public async Task PinIntoReferenceSource_ResolvesAndIsSelectable()
    {
        Guid refId = Guid.NewGuid();
        AssetSource reference = Source(refId, "c:/reference.hak", priority: 0, SourceMode.Reference);
        AssetIdentity identity = new("pinnedref", TgaType);

        byte[] pinHash = Hash(0x42);
        WinnerPin pin = new(identity, refId, new HakEntryLocator(0), pinHash);

        WorkspaceState state = await ResolveAsync(
            [reference],
            [pin],
            (reference, [Occurrence(identity, refId, 0, sha256: pinHash)]));

        CuratedAsset asset = state.CuratedAssets.Single();
        asset.Status.Should().Be(ResolutionStatus.Resolved,
            "pinning is precisely how a reference occurrence becomes a winner");
        asset.ResolvedOccurrence!.SourceId.Should().Be(refId);
        asset.IsSelected.Should().BeTrue("a pinned reference winner participates in selection normally");
    }

    [Test]
    public async Task HiddenSource_ExcludedFromOccurrencesAndCollisionFlag()
    {
        Guid hiddenId = Guid.NewGuid();
        Guid fullId = Guid.NewGuid();
        AssetSource hidden = Source(hiddenId, "c:/hidden.hak", priority: 0, SourceMode.Hidden);
        AssetSource full = Source(fullId, "c:/full.hak", priority: 1, SourceMode.Full);
        AssetIdentity identity = new("masked", TgaType);

        WorkspaceState state = await ResolveAsync(
            [hidden, full],
            [],
            (hidden, [Occurrence(identity, hiddenId, 0)]),
            (full, [Occurrence(identity, fullId, 0)]));

        CuratedAsset asset = state.CuratedAssets.Single();
        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.ResolvedOccurrence!.SourceId.Should().Be(fullId);
        asset.AllOccurrences.Should().ContainSingle("the hidden occurrence is excluded entirely");
        asset.HasCrossSourceCollision.Should().BeFalse(
            "a hidden occurrence must not register as a cross-source collision");
    }

    [Test]
    public async Task IdentityOnlyInHiddenSource_ProducesNoCuratedAsset()
    {
        Guid hiddenId = Guid.NewGuid();
        AssetSource hidden = Source(hiddenId, "c:/hidden.hak", priority: 0, SourceMode.Hidden);
        AssetIdentity identity = new("ghost", TgaType);

        WorkspaceState state = await ResolveAsync(
            [hidden],
            [],
            (hidden, [Occurrence(identity, hiddenId, 0)]));

        state.CuratedAssets.Should().BeEmpty("an identity present only in a hidden source produces no row");
    }

    [Test]
    public async Task PinnedIdentityOnlyInHiddenSource_YieldsInvalidPinRow_WithoutDroppingThePin()
    {
        Guid hiddenId = Guid.NewGuid();
        AssetSource hidden = Source(hiddenId, "c:/hidden.hak", priority: 0, SourceMode.Hidden);
        AssetIdentity identity = new("hiddenpin", TgaType);

        byte[] pinHash = Hash(0x7A);
        WinnerPin pin = new(identity, hiddenId, new HakEntryLocator(0), pinHash);

        WorkspaceState state = await ResolveAsync(
            [hidden],
            [pin],
            (hidden, [Occurrence(identity, hiddenId, 0, sha256: pinHash)]));

        state.CuratedAssets.Should().ContainSingle(
            "a pinned identity still surfaces as a row so the operator can clear the pin");
        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.InvalidPin);
        asset.HasInvalidPin.Should().BeTrue();
        asset.ResolvedOccurrence.Should().BeNull();
        asset.AllOccurrences.Should().BeEmpty("the hidden occurrence is excluded from the row's occurrences");
        asset.IsSelected.Should().BeFalse();
        state.Pins.Should().ContainSingle().Which.Should().Be(pin,
            "a pin into a hidden source is flagged invalid, never silently removed");
    }

    // Helpers ---------------------------------------------------------------

    private static AssetSource Source(Guid id, string path, int priority, SourceMode mode) =>
        new(id, AssetSourceKind.Hak, Path.GetFullPath(path), priority, isAvailable: true, fingerprint: null, mode: mode);

    private static AssetOccurrence Occurrence(AssetIdentity identity, Guid sourceId, int entryIndex, byte[]? sha256 = null) =>
        new(identity, sourceId, new HakEntryLocator(entryIndex), $"{identity.Resref}.tga", 100,
            ValidationState.Valid, extensionMetadata: null, sha256: sha256);

    private static byte[] Hash(byte seed)
    {
        byte[] hash = new byte[32];
        Array.Fill(hash, seed);
        return hash;
    }

    private static Task<WorkspaceState> ResolveAsync(
        IReadOnlyList<AssetSource> sources,
        IReadOnlyList<WinnerPin> pins,
        params (AssetSource Source, AssetOccurrence[] Occurrences)[] content)
    {
        Dictionary<Guid, SourceIndexSnapshot> snapshots = new();
        foreach ((AssetSource source, AssetOccurrence[] occurrences) in content)
        {
            snapshots[source.Id] = Snapshot(source, occurrences);
        }

        WorkspaceResolver resolver = new(new PassthroughHashService(), new AssetHashCache());
        return resolver.ResolveAsync(sources, snapshots, pins, SelectionState.IncludeAll());
    }

    private static SourceIndexSnapshot Snapshot(AssetSource source, IReadOnlyList<AssetOccurrence> occurrences)
    {
        byte[] digest = new byte[32];
        return new SourceIndexSnapshot(
            source,
            new SourceFingerprint(source.Kind, 1, digest),
            occurrences.Select(o => new IndexedAssetRecord(o)).ToList(),
            Array.Empty<AssetDiagnosticRecord>(),
            isCacheHit: false,
            scanStatistics: new SourceScanStatistics(occurrences.Count, 0, TimeSpan.Zero));
    }

    private sealed class PassthroughHashService : IStreamingHashService
    {
        public Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default) =>
            Task.FromResult(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(occurrence.Locator.ToString()!)));
    }
}
