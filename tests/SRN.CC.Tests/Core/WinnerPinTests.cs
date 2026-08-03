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
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Tests.Core;

[TestFixture]
public class WinnerPinTests
{
    private sealed class MockHashService : IStreamingHashService
    {
        public Dictionary<(Guid SourceId, OccurrenceLocator Locator), byte[]> Hashes { get; } = new();

        public Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            if (Hashes.TryGetValue((source.Id, occurrence.Locator), out byte[]? hash))
            {
                return Task.FromResult(hash);
            }
            byte[] defaultHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(occurrence.Locator.ToString()));
            return Task.FromResult(defaultHash);
        }
    }

    [Test]
    public async Task ExactPinMatch_ShouldOverrideLowerPrioritySource()
    {
        Guid sourceId1 = Guid.NewGuid();
        Guid sourceId2 = Guid.NewGuid();

        AssetSource s1 = new AssetSource(sourceId1, AssetSourceKind.Hak, "c:/source1.hak", priorityOrdinal: 0);
        AssetSource s2 = new AssetSource(sourceId2, AssetSourceKind.Hak, "c:/source2.hak", priorityOrdinal: 1);

        AssetIdentity identity = new AssetIdentity("pinned", 2000);
        AssetOccurrence occ1 = new AssetOccurrence(identity, sourceId1, new HakEntryLocator(0), "pinned.tga", 100);
        AssetOccurrence occ2 = new AssetOccurrence(identity, sourceId2, new HakEntryLocator(5), "pinned.tga", 100);

        SourceIndexSnapshot snap1 = CreateSnapshot(s1, occ1);
        SourceIndexSnapshot snap2 = CreateSnapshot(s2, occ2);

        byte[] pinHash = new byte[32]; pinHash[0] = 0x99;
        MockHashService hashService = new();
        hashService.Hashes[(sourceId2, occ2.Locator)] = pinHash;

        WinnerPin pin = new WinnerPin(identity, sourceId2, occ2.Locator, pinHash);
        WorkspaceResolver resolver = new(hashService);

        WorkspaceState state = await resolver.ResolveAsync(
            sources: new[] { s1, s2 },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot> { [sourceId1] = snap1, [sourceId2] = snap2 },
            pins: new[] { pin },
            selectionState: SelectionState.IncludeAll());

        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.ResolvedOccurrence!.SourceId.Should().Be(sourceId2);
        asset.ResolvedOccurrence.Locator.Should().Be(new HakEntryLocator(5));
        asset.Pin.Should().NotBeNull();
    }

    [Test]
    public async Task PinLocatorMoved_UniqueHashMatch_ShouldReattachPin()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource s = new AssetSource(sourceId, AssetSourceKind.Hak, "c:/source.hak", priorityOrdinal: 0);

        AssetIdentity identity = new AssetIdentity("moved", 2000);
        AssetOccurrence occAtNewIndex = new AssetOccurrence(identity, sourceId, new HakEntryLocator(10), "moved.tga", 100);

        SourceIndexSnapshot snap = CreateSnapshot(s, occAtNewIndex);

        byte[] pinHash = new byte[32]; pinHash[0] = 0xEE;
        MockHashService hashService = new();
        hashService.Hashes[(sourceId, occAtNewIndex.Locator)] = pinHash;

        // Pin points to old index 0, but content hash match is now at index 10
        WinnerPin oldPin = new WinnerPin(identity, sourceId, new HakEntryLocator(0), pinHash);
        WorkspaceResolver resolver = new(hashService);

        WorkspaceState state = await resolver.ResolveAsync(
            sources: new[] { s },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot> { [sourceId] = snap },
            pins: new[] { oldPin },
            selectionState: SelectionState.IncludeAll());

        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.ResolvedOccurrence!.Locator.Should().Be(new HakEntryLocator(10));
        asset.Pin!.Locator.Should().Be(new HakEntryLocator(10), "Pin should reattach to new locator");
    }

    [Test]
    public async Task InvalidPin_ShouldBlockResolution_AndNotAutoResolve()
    {
        Guid sourceId1 = Guid.NewGuid();
        Guid sourceId2 = Guid.NewGuid();

        AssetSource s1 = new AssetSource(sourceId1, AssetSourceKind.Hak, "c:/source1.hak", priorityOrdinal: 0);
        AssetSource s2 = new AssetSource(sourceId2, AssetSourceKind.Hak, "c:/source2.hak", priorityOrdinal: 1);

        AssetIdentity identity = new AssetIdentity("invalidpin", 2000);
        AssetOccurrence occ1 = new AssetOccurrence(identity, sourceId1, new HakEntryLocator(0), "invalidpin.tga", 100);

        SourceIndexSnapshot snap1 = CreateSnapshot(s1, occ1);

        // Pin references source2 which is missing
        byte[] pinHash = new byte[32];
        WinnerPin pin = new WinnerPin(identity, sourceId2, new HakEntryLocator(0), pinHash);

        MockHashService hashService = new();
        WorkspaceResolver resolver = new(hashService);

        WorkspaceState state = await resolver.ResolveAsync(
            sources: new[] { s1, s2 },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot> { [sourceId1] = snap1 },
            pins: new[] { pin },
            selectionState: SelectionState.IncludeAll());

        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.InvalidPin);
        asset.ResolvedOccurrence.Should().BeNull("Invalid pin must block auto-resolution and not fall back to priority source");
        asset.HasInvalidPin.Should().BeTrue();
    }

    private static SourceIndexSnapshot CreateSnapshot(AssetSource source, params AssetOccurrence[] occurrences)
    {
        List<IndexedAssetRecord> records = occurrences.Select(o => new IndexedAssetRecord(o)).ToList();
        byte[] fpBytes = new byte[32];
        SourceFingerprint fp = new SourceFingerprint(source.Kind, 1, fpBytes);
        SourceScanStatistics stats = new SourceScanStatistics(1, 100, TimeSpan.FromMilliseconds(5));
        return new SourceIndexSnapshot(source, fp, records, Array.Empty<AssetDiagnosticRecord>(), isCacheHit: false, scanStatistics: stats);
    }
}
