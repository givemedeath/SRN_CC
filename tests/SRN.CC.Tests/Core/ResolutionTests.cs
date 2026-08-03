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

[TestFixture]
public class ResolutionTests
{
    private sealed class MockHashService : IStreamingHashService
    {
        public Dictionary<(Guid SourceId, OccurrenceLocator Locator), byte[]> Hashes { get; } = new();
        public HashSet<(Guid SourceId, OccurrenceLocator Locator)> Unreadable { get; } = new();
        public int HashCalls { get; private set; }

        public Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            HashCalls++;
            if (Unreadable.Contains((source.Id, occurrence.Locator)))
            {
                throw new IOException("Simulated read error.");
            }
            if (Hashes.TryGetValue((source.Id, occurrence.Locator), out byte[]? hash))
            {
                return Task.FromResult(hash);
            }
            // Default deterministic hash from locator string
            byte[] defaultHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(occurrence.Locator.ToString()));
            return Task.FromResult(defaultHash);
        }
    }

    [Test]
    public async Task PriorityOrdering_HighestPriorityAvailableSource_ShouldWin()
    {
        Guid sourceId1 = Guid.NewGuid();
        Guid sourceId2 = Guid.NewGuid();

        AssetSource s1 = new AssetSource(sourceId1, AssetSourceKind.Hak, "c:/source1.hak", priorityOrdinal: 0);
        AssetSource s2 = new AssetSource(sourceId2, AssetSourceKind.Hak, "c:/source2.hak", priorityOrdinal: 1);

        AssetIdentity identity = new AssetIdentity("test", 2000);
        AssetOccurrence occ1 = new AssetOccurrence(identity, sourceId1, new HakEntryLocator(0), "test.tga", 100);
        AssetOccurrence occ2 = new AssetOccurrence(identity, sourceId2, new HakEntryLocator(0), "test.tga", 100);

        SourceIndexSnapshot snap1 = CreateSnapshot(s1, occ1);
        SourceIndexSnapshot snap2 = CreateSnapshot(s2, occ2);

        MockHashService hashService = new();
        WorkspaceResolver resolver = new(hashService);

        WorkspaceState state = await resolver.ResolveAsync(
            sources: new[] { s1, s2 },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot> { [sourceId1] = snap1, [sourceId2] = snap2 },
            pins: Array.Empty<WinnerPin>(),
            selectionState: SelectionState.IncludeAll());

        state.CuratedAssets.Should().HaveCount(1);
        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.ResolvedOccurrence.Should().NotBeNull();
        asset.ResolvedOccurrence!.SourceId.Should().Be(sourceId1);
        asset.HasCrossSourceCollision.Should().BeTrue();
    }

    [Test]
    public async Task UnavailableHigherPrioritySource_ShouldFallThroughToNextAvailableSource()
    {
        Guid sourceId1 = Guid.NewGuid();
        Guid sourceId2 = Guid.NewGuid();

        AssetSource s1 = new AssetSource(sourceId1, AssetSourceKind.Hak, "c:/source1.hak", priorityOrdinal: 0, isAvailable: false);
        AssetSource s2 = new AssetSource(sourceId2, AssetSourceKind.Hak, "c:/source2.hak", priorityOrdinal: 1, isAvailable: true);

        AssetIdentity identity = new AssetIdentity("test", 2000);
        AssetOccurrence occ1 = new AssetOccurrence(identity, sourceId1, new HakEntryLocator(0), "test.tga", 100);
        AssetOccurrence occ2 = new AssetOccurrence(identity, sourceId2, new HakEntryLocator(0), "test.tga", 100);

        SourceIndexSnapshot snap1 = CreateSnapshot(s1, occ1);
        SourceIndexSnapshot snap2 = CreateSnapshot(s2, occ2);

        MockHashService hashService = new();
        WorkspaceResolver resolver = new(hashService);

        WorkspaceState state = await resolver.ResolveAsync(
            sources: new[] { s1, s2 },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot> { [sourceId1] = snap1, [sourceId2] = snap2 },
            pins: Array.Empty<WinnerPin>(),
            selectionState: SelectionState.IncludeAll());

        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.ResolvedOccurrence!.SourceId.Should().Be(sourceId2);
    }

    [Test]
    public async Task SameSourceDuplicates_WithIdenticalPayloads_ShouldResolveLowestLocator()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource s = new AssetSource(sourceId, AssetSourceKind.Hak, "c:/source.hak", priorityOrdinal: 0);

        AssetIdentity identity = new AssetIdentity("dup", 2000);
        AssetOccurrence occ1 = new AssetOccurrence(identity, sourceId, new HakEntryLocator(5), "dup.tga", 100);
        AssetOccurrence occ2 = new AssetOccurrence(identity, sourceId, new HakEntryLocator(2), "dup.tga", 100);

        SourceIndexSnapshot snap = CreateSnapshot(s, occ1, occ2);

        MockHashService hashService = new();
        byte[] sameHash = new byte[32];
        sameHash[0] = 0xAA;
        hashService.Hashes[(sourceId, occ1.Locator)] = sameHash;
        hashService.Hashes[(sourceId, occ2.Locator)] = sameHash;

        WorkspaceResolver resolver = new(hashService);

        WorkspaceState state = await resolver.ResolveAsync(
            sources: new[] { s },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot> { [sourceId] = snap },
            pins: Array.Empty<WinnerPin>(),
            selectionState: SelectionState.IncludeAll());

        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.ResolvedOccurrence!.Locator.Should().Be(new HakEntryLocator(2));
        asset.HasSameSourceDuplicate.Should().BeTrue();
        asset.HasDifferingPayloads.Should().BeFalse();
    }

    [Test]
    public async Task SameSourceDuplicates_WithDifferingPayloads_ShouldUnresolve()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource s = new AssetSource(sourceId, AssetSourceKind.Hak, "c:/source.hak", priorityOrdinal: 0);

        AssetIdentity identity = new AssetIdentity("conflict", 2000);
        AssetOccurrence occ1 = new AssetOccurrence(identity, sourceId, new HakEntryLocator(0), "conflict.tga", 100);
        AssetOccurrence occ2 = new AssetOccurrence(identity, sourceId, new HakEntryLocator(1), "conflict.tga", 100);

        SourceIndexSnapshot snap = CreateSnapshot(s, occ1, occ2);

        MockHashService hashService = new();
        byte[] hash1 = new byte[32]; hash1[0] = 0x01;
        byte[] hash2 = new byte[32]; hash2[0] = 0x02;
        hashService.Hashes[(sourceId, occ1.Locator)] = hash1;
        hashService.Hashes[(sourceId, occ2.Locator)] = hash2;

        WorkspaceResolver resolver = new(hashService);

        WorkspaceState state = await resolver.ResolveAsync(
            sources: new[] { s },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot> { [sourceId] = snap },
            pins: Array.Empty<WinnerPin>(),
            selectionState: SelectionState.IncludeAll());

        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.UnresolvedDuplicate);
        asset.ResolvedOccurrence.Should().BeNull();
        asset.HasDifferingPayloads.Should().BeTrue();
    }

    [Test]
    public async Task SingleOccurrenceInWinningSource_ShouldNotComputeHashLazily()
    {
        Guid sourceId1 = Guid.NewGuid();
        Guid sourceId2 = Guid.NewGuid();

        AssetSource s1 = new AssetSource(sourceId1, AssetSourceKind.Hak, "c:/source1.hak", priorityOrdinal: 0);
        AssetSource s2 = new AssetSource(sourceId2, AssetSourceKind.Hak, "c:/source2.hak", priorityOrdinal: 1);

        AssetIdentity identity = new AssetIdentity("single", 2000);
        AssetOccurrence occ1 = new AssetOccurrence(identity, sourceId1, new HakEntryLocator(0), "single.tga", 100);
        AssetOccurrence occ2 = new AssetOccurrence(identity, sourceId2, new HakEntryLocator(0), "single.tga", 100);
        AssetOccurrence occ3 = new AssetOccurrence(identity, sourceId2, new HakEntryLocator(1), "single.tga", 100);

        SourceIndexSnapshot snap1 = CreateSnapshot(s1, occ1);
        SourceIndexSnapshot snap2 = CreateSnapshot(s2, occ2, occ3);

        MockHashService hashService = new();
        WorkspaceResolver resolver = new(hashService);

        WorkspaceState state = await resolver.ResolveAsync(
            sources: new[] { s1, s2 },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot> { [sourceId1] = snap1, [sourceId2] = snap2 },
            pins: Array.Empty<WinnerPin>(),
            selectionState: SelectionState.IncludeAll());

        hashService.HashCalls.Should().Be(0, "Single occurrence in winning source resolving without pin should not trigger lazy hashing");
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
