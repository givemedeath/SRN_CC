using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Indexing;
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
/// Backfill for the rescan half of the resolution bullet at <c>PLAN.md:221</c>: rescan with a
/// source changed, a source unavailable, and a source relocated, plus the contents of the
/// <see cref="ChangedInputReport"/> those rescans produce. Also covers the priority,
/// unavailable-source, identical/conflicting duplicate, hash-error, and case-only-flag cases as
/// they are re-evaluated across a rescan.
/// </summary>
[TestFixture]
public class ResolutionRescanTests
{
    private const ushort TgaType = 2000;

    #region Rescan: source changed

    [Test]
    public async Task Rescan_WithSourceContentChanged_ReindexesAndReportsFingerprintChange()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/changing.hak", 0);
        AssetIdentity identity = new("stable", TgaType);

        MockIndexService index = new();
        index.Register(source.FullPath, fingerprintSeed: 0x11, Occurrence(identity, sourceId, 0, size: 100));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([source]);
        SourceFingerprint before = workspace.CurrentState.Sources[0].Fingerprint!;

        // The file on disk changed: same identity, new payload, new fingerprint.
        index.Register(source.FullPath, fingerprintSeed: 0x22, Occurrence(identity, sourceId, 0, size: 4096));

        (WorkspaceState state, ChangedInputReport report) = await workspace.RescanAsync();

        report.FingerprintChanges.Should().Contain(sourceId);
        state.Sources[0].Fingerprint.Should().NotBe(before);
        state.CuratedAssets.Should().ContainSingle();
        state.CuratedAssets[0].ResolvedOccurrence!.Size.Should().Be(4096, "the rescan must adopt the new payload");
    }

    [Test]
    public async Task Rescan_WithSourceContentChanged_ReportsAddedAndRemovedIdentitiesAndWinnerChanges()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/churn.hak", 0);

        AssetIdentity kept = new("kept", TgaType);
        AssetIdentity dropped = new("dropped", TgaType);
        AssetIdentity introduced = new("introduced", TgaType);

        MockIndexService index = new();
        index.Register(
            source.FullPath,
            fingerprintSeed: 0x01,
            Occurrence(kept, sourceId, 0, size: 10),
            Occurrence(dropped, sourceId, 1, size: 20));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([source]);

        index.Register(
            source.FullPath,
            fingerprintSeed: 0x02,
            Occurrence(kept, sourceId, 5, size: 10),
            Occurrence(introduced, sourceId, 6, size: 30));

        (_, ChangedInputReport report) = await workspace.RescanAsync();

        report.HasChanges.Should().BeTrue();
        report.AddedIdentities.Should().Contain(introduced);
        report.RemovedIdentities.Should().Contain(dropped);
        report.WinnerChanges.Should().Contain(kept, "the kept identity moved to a different entry index");
        report.AddedSources.Should().BeEmpty();
        report.RemovedSources.Should().BeEmpty();
        report.AvailabilityTransitions.Should().BeEmpty();
        report.PinReattachments.Should().BeEmpty();
        report.PinInvalidations.Should().BeEmpty();
    }

    [Test]
    public async Task Rescan_WithNothingChanged_ProducesAnEmptyChangedInputReport()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/static.hak", 0);
        AssetIdentity identity = new("unchanged", TgaType);

        MockIndexService index = new();
        index.Register(source.FullPath, fingerprintSeed: 0x33, Occurrence(identity, sourceId, 0, size: 42));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([source]);

        (_, ChangedInputReport report) = await workspace.RescanAsync();

        report.HasChanges.Should().BeFalse("an idempotent rescan must report nothing changed");
    }

    [Test]
    public async Task Rescan_TargetedAtOneSource_LeavesUntargetedSourcesUntouched()
    {
        Guid targetId = Guid.NewGuid();
        Guid untouchedId = Guid.NewGuid();
        AssetSource target = Hak(targetId, "c:/target.hak", 0);
        AssetSource untouched = Hak(untouchedId, "c:/untouched.hak", 1);

        AssetIdentity a = new("alpha", TgaType);
        AssetIdentity b = new("bravo", TgaType);

        MockIndexService index = new();
        index.Register(target.FullPath, fingerprintSeed: 0x41, Occurrence(a, targetId, 0, size: 10));
        index.Register(untouched.FullPath, fingerprintSeed: 0x42, Occurrence(b, untouchedId, 0, size: 10));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([target, untouched]);
        index.ResetIndexCounts();

        // Both sources change on disk, but only one is rescanned.
        index.Register(target.FullPath, fingerprintSeed: 0x51, Occurrence(a, targetId, 9, size: 10));
        index.Register(untouched.FullPath, fingerprintSeed: 0x52, Occurrence(b, untouchedId, 9, size: 10));

        (WorkspaceState state, ChangedInputReport report) = await workspace.RescanAsync([targetId]);

        index.IndexCount(target.FullPath).Should().Be(1);
        index.IndexCount(untouched.FullPath).Should().Be(0, "an untargeted source must not be reindexed");
        report.FingerprintChanges.Should().Contain(targetId);
        report.FingerprintChanges.Should().NotContain(untouchedId);
        state.CuratedAssets.Single(x => x.Identity.Equals(b)).ResolvedOccurrence!.Locator
            .Should().Be(new HakEntryLocator(0));
    }

    #endregion

    #region Rescan: source unavailable

    [Test]
    public async Task Rescan_WithSourceNowUnavailable_MarksItUnavailableAndReportsTheTransition()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/vanishing.hak", 0);
        AssetIdentity identity = new("onlyhere", TgaType);

        MockIndexService index = new();
        index.Register(source.FullPath, fingerprintSeed: 0x61, Occurrence(identity, sourceId, 0, size: 10));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([source]);
        workspace.CurrentState.CuratedAssets[0].Status.Should().Be(ResolutionStatus.Resolved);

        index.FailingPaths.Add(Normalize(source.FullPath));

        (WorkspaceState state, ChangedInputReport report) = await workspace.RescanAsync();

        state.Sources[0].IsAvailable.Should().BeFalse();
        report.AvailabilityTransitions.Should().Contain(sourceId);
        state.Snapshots.Should().ContainKey(sourceId, "the prior snapshot is retained so the identity remains visible");
        state.CuratedAssets[0].Status.Should().Be(ResolutionStatus.Unavailable);
        state.CuratedAssets[0].ResolvedOccurrence.Should().BeNull();
        report.WinnerChanges.Should().Contain(identity);
    }

    [Test]
    public async Task Rescan_WithHigherPrioritySourceUnavailable_FallsThroughToTheNextAvailableSource()
    {
        Guid highId = Guid.NewGuid();
        Guid lowId = Guid.NewGuid();
        AssetSource high = Hak(highId, "c:/high.hak", 0);
        AssetSource low = Hak(lowId, "c:/low.hak", 1);
        AssetIdentity identity = new("shared", TgaType);

        MockIndexService index = new();
        index.Register(high.FullPath, fingerprintSeed: 0x71, Occurrence(identity, highId, 0, size: 10));
        index.Register(low.FullPath, fingerprintSeed: 0x72, Occurrence(identity, lowId, 0, size: 20));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([high, low]);
        workspace.CurrentState.CuratedAssets[0].ResolvedOccurrence!.SourceId.Should().Be(highId);

        index.FailingPaths.Add(Normalize(high.FullPath));

        (WorkspaceState state, ChangedInputReport report) = await workspace.RescanAsync();

        state.CuratedAssets[0].ResolvedOccurrence!.SourceId.Should().Be(lowId);
        report.AvailabilityTransitions.Should().Contain(highId);
        report.WinnerChanges.Should().Contain(identity);
    }

    [Test]
    public async Task Rescan_WithPriorityOrder_ResolvesToTheHighestPriorityAvailableSource()
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        Guid thirdId = Guid.NewGuid();

        AssetSource first = Hak(firstId, "c:/p0.hak", 0);
        AssetSource second = Hak(secondId, "c:/p1.hak", 1);
        AssetSource third = Hak(thirdId, "c:/p2.hak", 2);
        AssetIdentity identity = new("prio", TgaType);

        MockIndexService index = new();
        index.Register(first.FullPath, fingerprintSeed: 0x81);
        index.Register(second.FullPath, fingerprintSeed: 0x82, Occurrence(identity, secondId, 0, size: 10));
        index.Register(third.FullPath, fingerprintSeed: 0x83, Occurrence(identity, thirdId, 0, size: 20));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([first, second, third]);

        workspace.CurrentState.CuratedAssets[0].ResolvedOccurrence!.SourceId
            .Should().Be(secondId, "priority 0 does not contain the identity, so priority 1 wins");

        // After a rescan that adds the identity to the top-priority source, priority wins again.
        index.Register(first.FullPath, fingerprintSeed: 0x84, Occurrence(identity, firstId, 0, size: 30));

        (WorkspaceState state, ChangedInputReport report) = await workspace.RescanAsync();

        state.CuratedAssets[0].ResolvedOccurrence!.SourceId.Should().Be(firstId);
        report.WinnerChanges.Should().Contain(identity);
    }

    #endregion

    #region Rescan: duplicates, hash errors, case-only flags

    [Test]
    public async Task Rescan_WhenIdenticalDuplicateAppears_ResolvesToTheLowestDeterministicLocator()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/dupes.hak", 0);
        AssetIdentity identity = new("dupe", TgaType);

        byte[] sharedHash = Hash(0xAA);

        MockIndexService index = new();
        index.Register(source.FullPath, fingerprintSeed: 0x91, Occurrence(identity, sourceId, 4, size: 10, sha256: sharedHash));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([source]);

        index.Register(
            source.FullPath,
            fingerprintSeed: 0x92,
            Occurrence(identity, sourceId, 4, size: 10, sha256: sharedHash),
            Occurrence(identity, sourceId, 2, size: 10, sha256: sharedHash));

        (WorkspaceState state, _) = await workspace.RescanAsync();

        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.HasSameSourceDuplicate.Should().BeTrue();
        asset.HasDifferingPayloads.Should().BeFalse();
        asset.ResolvedOccurrence!.Locator.Should().Be(new HakEntryLocator(2));
    }

    [Test]
    public async Task Rescan_WhenConflictingDuplicateAppears_LeavesTheIdentityUnresolved()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/conflict.hak", 0);
        AssetIdentity identity = new("conflict", TgaType);

        MockIndexService index = new();
        index.Register(source.FullPath, fingerprintSeed: 0xA1, Occurrence(identity, sourceId, 0, size: 10, sha256: Hash(0x01)));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([source]);

        index.Register(
            source.FullPath,
            fingerprintSeed: 0xA2,
            Occurrence(identity, sourceId, 0, size: 10, sha256: Hash(0x01)),
            Occurrence(identity, sourceId, 1, size: 11, sha256: Hash(0x02)));

        (WorkspaceState state, ChangedInputReport report) = await workspace.RescanAsync();

        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.UnresolvedDuplicate);
        asset.HasDifferingPayloads.Should().BeTrue();
        asset.ResolvedOccurrence.Should().BeNull();
        report.WinnerChanges.Should().Contain(identity);
    }

    [Test]
    public async Task Rescan_WhenPayloadHashingFails_FlagsTheUnreadableOccurrenceAndBlocksResolution()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/unreadable.hak", 0);
        AssetIdentity identity = new("unreadable", TgaType);

        // Sha256 is deliberately absent so the resolver must call the hash service, which fails.
        AssetOccurrence first = Occurrence(identity, sourceId, 0, size: 10);
        AssetOccurrence second = Occurrence(identity, sourceId, 1, size: 10);

        MockIndexService index = new();
        index.Register(source.FullPath, fingerprintSeed: 0xB1, first);

        WorkspaceService workspace = CreateWorkspace(index, out FaultingHashService hashService);
        await workspace.InitializeAsync([source]);

        index.Register(source.FullPath, fingerprintSeed: 0xB2, first, second);
        hashService.FailingLocators.Add((sourceId, second.Locator));

        (WorkspaceState state, _) = await workspace.RescanAsync();

        CuratedAsset asset = state.CuratedAssets[0];
        asset.Status.Should().Be(ResolutionStatus.UnresolvedDuplicate);
        asset.HasUnreadableOccurrence.Should().BeTrue("a hash error must surface as an unreadable occurrence");
        asset.ResolvedOccurrence.Should().BeNull();
    }

    [Test]
    public async Task Rescan_WhenCaseOnlyNamingDifferenceAppears_SetsTheFlagWithoutBlockingResolution()
    {
        Guid highId = Guid.NewGuid();
        Guid lowId = Guid.NewGuid();
        AssetSource high = Hak(highId, "c:/case_high.hak", 0);
        AssetSource low = Hak(lowId, "c:/case_low.hak", 1);
        AssetIdentity identity = new("casefold", TgaType);

        MockIndexService index = new();
        index.Register(high.FullPath, fingerprintSeed: 0xC1, Occurrence(identity, highId, 0, size: 10, originalName: "casefold.tga"));
        index.Register(low.FullPath, fingerprintSeed: 0xC2, Occurrence(identity, lowId, 0, size: 10, originalName: "casefold.tga"));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([high, low]);
        workspace.CurrentState.CuratedAssets[0].HasCaseOnlyNamingDifference.Should().BeFalse();

        index.Register(low.FullPath, fingerprintSeed: 0xC3, Occurrence(identity, lowId, 0, size: 10, originalName: "CaseFold.TGA"));

        (WorkspaceState state, _) = await workspace.RescanAsync([lowId]);

        CuratedAsset asset = state.CuratedAssets[0];
        asset.HasCaseOnlyNamingDifference.Should().BeTrue();
        asset.HasCrossSourceCollision.Should().BeTrue();
        asset.Status.Should().Be(ResolutionStatus.Resolved, "a case-only naming difference is a flag, not a blocker");
        asset.ResolvedOccurrence!.SourceId.Should().Be(highId);
    }

    #endregion

    #region Rescan: source relocated

    [Test]
    public async Task Relocate_ToANewPathWithTheSameContent_RepointsTheSourceAndKeepsIdentitiesResolved()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/original/moved.hak", 0);
        AssetIdentity identity = new("relocated", TgaType);

        string newPath = Normalize("c:/relocated/moved.hak");

        MockIndexService index = new();
        index.Register(source.FullPath, fingerprintSeed: 0xD1, Occurrence(identity, sourceId, 0, size: 10));
        index.Register(newPath, fingerprintSeed: 0xD1, Occurrence(identity, sourceId, 0, size: 10));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([source]);

        (WorkspaceState state, ChangedInputReport report) = await workspace.RelocateSourceAsync(sourceId, newPath);

        state.Sources[0].FullPath.Should().Be(newPath);
        state.Sources[0].IsAvailable.Should().BeTrue();
        state.Sources[0].Id.Should().Be(sourceId, "relocation keeps the source identity so pins and order survive");
        state.CuratedAssets[0].Status.Should().Be(ResolutionStatus.Resolved);
        report.AddedSources.Should().BeEmpty();
        report.RemovedSources.Should().BeEmpty();
        report.HasChanges.Should().BeFalse("relocating to byte-identical content changes nothing the operator must review");
    }

    [Test]
    public async Task Relocate_ToANewPathWithDifferentContent_ReportsTheChangedIdentities()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/original/drifted.hak", 0);
        AssetIdentity original = new("original", TgaType);
        AssetIdentity replacement = new("replacement", TgaType);

        string newPath = Normalize("c:/relocated/drifted.hak");

        MockIndexService index = new();
        index.Register(source.FullPath, fingerprintSeed: 0xE1, Occurrence(original, sourceId, 0, size: 10));
        index.Register(newPath, fingerprintSeed: 0xE2, Occurrence(replacement, sourceId, 0, size: 10));

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([source]);

        (WorkspaceState state, ChangedInputReport report) = await workspace.RelocateSourceAsync(sourceId, newPath);

        report.AddedIdentities.Should().Contain(replacement);
        report.RemovedIdentities.Should().Contain(original);
        report.FingerprintChanges.Should().Contain(sourceId);
        state.Sources[0].FullPath.Should().Be(newPath);
    }

    [Test]
    public async Task Relocate_ToAnUnreadablePath_ThrowsAndLeavesTheWorkspaceUnchanged()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/original/kept.hak", 0);
        AssetIdentity identity = new("kept", TgaType);

        string badPath = Normalize("c:/nowhere/kept.hak");

        MockIndexService index = new();
        index.Register(source.FullPath, fingerprintSeed: 0xF1, Occurrence(identity, sourceId, 0, size: 10));
        index.FailingPaths.Add(badPath);

        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync([source]);
        WorkspaceState before = workspace.CurrentState;

        Func<Task> act = async () => await workspace.RelocateSourceAsync(sourceId, badPath);

        await act.Should().ThrowAsync<InvalidOperationException>();
        workspace.CurrentState.Should().BeSameAs(before, "a failed relocation must not mutate the workspace");
        workspace.CurrentState.Sources[0].FullPath.Should().Be(Normalize("c:/original/kept.hak"));
    }

    [Test]
    public async Task Relocate_OfAnUnknownSource_Throws()
    {
        MockIndexService index = new();
        WorkspaceService workspace = CreateWorkspace(index, out _);
        await workspace.InitializeAsync(Array.Empty<AssetSource>());

        Func<Task> act = async () => await workspace.RelocateSourceAsync(Guid.NewGuid(), "c:/anywhere.hak");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region Helpers

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static AssetSource Hak(Guid id, string path, int priority) =>
        new(id, AssetSourceKind.Hak, Normalize(path), priority);

    private static AssetOccurrence Occurrence(
        AssetIdentity identity,
        Guid sourceId,
        int entryIndex,
        long size,
        byte[]? sha256 = null,
        string? originalName = null)
    {
        return new AssetOccurrence(
            identity,
            sourceId,
            new HakEntryLocator(entryIndex),
            originalName ?? $"{identity.Resref}.tga",
            size,
            ValidationState.Valid,
            extensionMetadata: null,
            sha256: sha256);
    }

    private static byte[] Hash(byte seed)
    {
        byte[] hash = new byte[32];
        Array.Fill(hash, seed);
        return hash;
    }

    private static WorkspaceService CreateWorkspace(MockIndexService index, out FaultingHashService hashService)
    {
        hashService = new FaultingHashService();
        AssetHashCache cache = new();
        WorkspaceResolver resolver = new(hashService, cache);
        return new WorkspaceService(index, resolver, cache);
    }

    private sealed class FaultingHashService : IStreamingHashService
    {
        public HashSet<(Guid SourceId, OccurrenceLocator Locator)> FailingLocators { get; } = new();

        public Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            if (FailingLocators.Contains((source.Id, occurrence.Locator)))
            {
                throw new IOException("Simulated hash failure.");
            }

            return Task.FromResult(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(occurrence.Locator.ToString()!)));
        }
    }

    /// <summary>
    /// Index service keyed by <em>path</em> rather than source id, so that relocation - which keeps
    /// the source id but changes the path - genuinely reads different content.
    /// </summary>
    private sealed class MockIndexService : IAssetIndexService
    {
        private readonly Dictionary<string, (byte Seed, AssetOccurrence[] Occurrences)> _byPath =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, int> _indexCounts = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> FailingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string path, byte fingerprintSeed, params AssetOccurrence[] occurrences)
        {
            _byPath[Normalize(path)] = (fingerprintSeed, occurrences);
        }

        public void ResetIndexCounts() => _indexCounts.Clear();

        public int IndexCount(string path) =>
            _indexCounts.TryGetValue(Normalize(path), out int count) ? count : 0;

        public Task<SourceIndexSnapshot> IndexAsync(
            AssetSource source,
            IProgress<IndexProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string key = Normalize(source.FullPath);
            _indexCounts[key] = IndexCount(key) + 1;

            if (FailingPaths.Contains(key))
            {
                throw new IOException($"Simulated index failure for '{key}'.");
            }

            (byte seed, AssetOccurrence[] occurrences) = _byPath.TryGetValue(key, out var entry)
                ? entry
                : ((byte)0, Array.Empty<AssetOccurrence>());

            byte[] digest = new byte[32];
            Array.Fill(digest, seed);

            SourceIndexSnapshot snapshot = new(
                source,
                new SourceFingerprint(source.Kind, 1, digest),
                occurrences.Select(o => new IndexedAssetRecord(o)).ToList(),
                Array.Empty<AssetDiagnosticRecord>(),
                isCacheHit: false,
                scanStatistics: new SourceScanStatistics(occurrences.Length, 0, TimeSpan.Zero));

            return Task.FromResult(snapshot);
        }
    }

    #endregion
}
