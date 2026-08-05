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
/// The pin reattachment ladder from <c>PLAN.md</c> rule 7 - "exact source/identity/locator with
/// matching hash, then a unique source/identity/hash match" - together with every case in which
/// reattachment must fail and leave the pin invalid instead of silently falling back to automatic
/// priority resolution.
/// </summary>
[TestFixture]
public class PinReattachmentTests
{
    private const ushort TgaType = 2000;

    #region Ladder rung 1: exact source/identity/locator with a matching hash

    [Test]
    public async Task Reattach_Rung1_ExactSourceLocatorWithMatchingHash_ResolvesWithoutMovingThePin()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/pinned.hak", 0);
        AssetIdentity identity = new("pinned", TgaType);

        byte[] pinHash = Hash(0x42);
        AssetOccurrence exact = Occurrence(identity, sourceId, entryIndex: 3, sha256: pinHash);
        AssetOccurrence other = Occurrence(identity, sourceId, entryIndex: 9, sha256: Hash(0x11));

        WinnerPin pin = new(identity, sourceId, new HakEntryLocator(3), pinHash);

        CuratedAsset asset = await ResolveSingleAsync([source], [pin], (source, [exact, other]));

        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.HasInvalidPin.Should().BeFalse();
        asset.ResolvedOccurrence!.Locator.Should().Be(new HakEntryLocator(3));
        asset.Pin!.Locator.Should().Be(new HakEntryLocator(3), "rung 1 matched, so the pin does not move");
        asset.Pin.Should().Be(pin);
    }

    [Test]
    public async Task Reattach_Rung1_IsPreferredOverAnotherOccurrenceCarryingTheSameHash()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/pinned.hak", 0);
        AssetIdentity identity = new("preferexact", TgaType);

        byte[] pinHash = Hash(0x7F);
        AssetOccurrence exact = Occurrence(identity, sourceId, entryIndex: 5, sha256: pinHash);
        AssetOccurrence twin = Occurrence(identity, sourceId, entryIndex: 1, sha256: pinHash);

        WinnerPin pin = new(identity, sourceId, new HakEntryLocator(5), pinHash);

        CuratedAsset asset = await ResolveSingleAsync([source], [pin], (source, [exact, twin]));

        asset.Status.Should().Be(ResolutionStatus.Resolved,
            "an exact locator hit short-circuits before the ambiguity check on rung 2");
        asset.ResolvedOccurrence!.Locator.Should().Be(new HakEntryLocator(5));
    }

    [Test]
    public async Task Reattach_Rung1_PinnedSourceWinsEvenWhenAHigherPrioritySourceHasTheIdentity()
    {
        Guid highId = Guid.NewGuid();
        Guid pinnedId = Guid.NewGuid();
        AssetSource high = Hak(highId, "c:/high.hak", 0);
        AssetSource pinnedSource = Hak(pinnedId, "c:/pinned.hak", 1);
        AssetIdentity identity = new("override", TgaType);

        byte[] pinHash = Hash(0x55);
        AssetOccurrence highOccurrence = Occurrence(identity, highId, entryIndex: 0, sha256: Hash(0x01));
        AssetOccurrence pinnedOccurrence = Occurrence(identity, pinnedId, entryIndex: 0, sha256: pinHash);

        WinnerPin pin = new(identity, pinnedId, new HakEntryLocator(0), pinHash);

        CuratedAsset asset = await ResolveSingleAsync(
            [high, pinnedSource],
            [pin],
            (high, [highOccurrence]),
            (pinnedSource, [pinnedOccurrence]));

        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.ResolvedOccurrence!.SourceId.Should().Be(pinnedId);
    }

    #endregion

    #region Ladder rung 2: unique source/identity/hash match

    [Test]
    public async Task Reattach_Rung2_ExactLocatorHashMismatch_ButUniqueSameSourceHashMatch_MovesThePin()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/moved.hak", 0);
        AssetIdentity identity = new("moved", TgaType);

        byte[] pinHash = Hash(0xEE);
        AssetOccurrence atOldIndex = Occurrence(identity, sourceId, entryIndex: 0, sha256: Hash(0x01));
        AssetOccurrence atNewIndex = Occurrence(identity, sourceId, entryIndex: 10, sha256: pinHash);

        WinnerPin pin = new(identity, sourceId, new HakEntryLocator(0), pinHash);

        CuratedAsset asset = await ResolveSingleAsync([source], [pin], (source, [atOldIndex, atNewIndex]));

        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.HasInvalidPin.Should().BeFalse();
        asset.ResolvedOccurrence!.Locator.Should().Be(new HakEntryLocator(10));
        asset.Pin!.Locator.Should().Be(new HakEntryLocator(10), "the pin reattaches to the unique hash match");
        asset.Pin.PinHash.Should().Equal(pinHash, "reattachment preserves the pinned payload hash");
        asset.Pin.SourceId.Should().Be(sourceId);
    }

    [Test]
    public async Task Reattach_Rung2_ExactLocatorGoneEntirely_ButUniqueSameSourceHashMatch_MovesThePin()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/shrunk.hak", 0);
        AssetIdentity identity = new("shrunk", TgaType);

        byte[] pinHash = Hash(0xAB);
        AssetOccurrence onlyRemaining = Occurrence(identity, sourceId, entryIndex: 2, sha256: pinHash);

        WinnerPin pin = new(identity, sourceId, new HakEntryLocator(17), pinHash);

        CuratedAsset asset = await ResolveSingleAsync([source], [pin], (source, [onlyRemaining]));

        asset.Status.Should().Be(ResolutionStatus.Resolved);
        asset.ResolvedOccurrence!.Locator.Should().Be(new HakEntryLocator(2));
        asset.Pin!.Locator.Should().Be(new HakEntryLocator(2));
    }

    [Test]
    public async Task Reattach_Rung2_UpdatedPinIsPersistedIntoWorkspaceStatePins()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/persisted.hak", 0);
        AssetIdentity identity = new("persisted", TgaType);

        byte[] pinHash = Hash(0x3C);
        AssetOccurrence moved = Occurrence(identity, sourceId, entryIndex: 6, sha256: pinHash);
        WinnerPin pin = new(identity, sourceId, new HakEntryLocator(0), pinHash);

        WorkspaceState state = await ResolveAsync([source], [pin], (source, [moved]));

        state.Pins.Should().ContainSingle();
        state.Pins[0].Locator.Should().Be(new HakEntryLocator(6),
            "the reattached pin must be written back so a later save records the new locator");
    }

    #endregion

    #region Reattachment must FAIL: the pin stays invalid, resolution does not fall back

    [Test]
    public async Task Reattach_Fails_WhenNoOccurrenceInThePinnedSourceCarriesThePinHash()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/rewritten.hak", 0);
        AssetIdentity identity = new("rewritten", TgaType);

        AssetOccurrence replaced = Occurrence(identity, sourceId, entryIndex: 0, sha256: Hash(0x01));
        WinnerPin pin = new(identity, sourceId, new HakEntryLocator(0), Hash(0xFF));

        CuratedAsset asset = await ResolveSingleAsync([source], [pin], (source, [replaced]));

        AssertPinLeftInvalid(asset, pin);
    }

    [Test]
    public async Task Reattach_Fails_WhenTwoOccurrencesInThePinnedSourceShareThePinHash()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/ambiguous.hak", 0);
        AssetIdentity identity = new("ambiguous", TgaType);

        byte[] pinHash = Hash(0x5A);
        AssetOccurrence firstTwin = Occurrence(identity, sourceId, entryIndex: 1, sha256: pinHash);
        AssetOccurrence secondTwin = Occurrence(identity, sourceId, entryIndex: 2, sha256: pinHash);

        // The pin's own locator matches neither twin, so rung 1 cannot break the tie.
        WinnerPin pin = new(identity, sourceId, new HakEntryLocator(99), pinHash);

        CuratedAsset asset = await ResolveSingleAsync([source], [pin], (source, [firstTwin, secondTwin]));

        AssertPinLeftInvalid(asset, pin);
        asset.HasDifferingPayloads.Should().BeTrue("an ambiguous reattachment is reported, not guessed");
    }

    [Test]
    public async Task Reattach_Fails_WhenTheMatchingHashLivesInADifferentSource()
    {
        Guid pinnedId = Guid.NewGuid();
        Guid otherId = Guid.NewGuid();
        AssetSource pinnedSource = Hak(pinnedId, "c:/pinned.hak", 0);
        AssetSource otherSource = Hak(otherId, "c:/other.hak", 1);
        AssetIdentity identity = new("crosssource", TgaType);

        byte[] pinHash = Hash(0x6B);
        AssetOccurrence inPinnedSource = Occurrence(identity, pinnedId, entryIndex: 0, sha256: Hash(0x02));
        AssetOccurrence inOtherSource = Occurrence(identity, otherId, entryIndex: 0, sha256: pinHash);

        WinnerPin pin = new(identity, pinnedId, new HakEntryLocator(0), pinHash);

        CuratedAsset asset = await ResolveSingleAsync(
            [pinnedSource, otherSource],
            [pin],
            (pinnedSource, [inPinnedSource]),
            (otherSource, [inOtherSource]));

        AssertPinLeftInvalid(asset, pin);
        asset.ResolvedOccurrence.Should().BeNull(
            "reattachment never crosses source boundaries, even when the exact payload exists elsewhere");
    }

    [Test]
    public async Task Reattach_Fails_WhenTheIdentityIsGoneFromThePinnedSourceEntirely()
    {
        Guid pinnedId = Guid.NewGuid();
        AssetSource pinnedSource = Hak(pinnedId, "c:/emptied.hak", 0);
        AssetIdentity identity = new("emptied", TgaType);

        WinnerPin pin = new(identity, pinnedId, new HakEntryLocator(0), Hash(0x77));

        CuratedAsset asset = await ResolveSingleAsync([pinnedSource], [pin], (pinnedSource, []));

        AssertPinLeftInvalid(asset, pin);
        asset.AllOccurrences.Should().BeEmpty();
    }

    [Test]
    public async Task Reattach_Fails_WhenThePinnedSourceIsUnavailable()
    {
        Guid pinnedId = Guid.NewGuid();
        Guid otherId = Guid.NewGuid();
        AssetSource pinnedSource = new(pinnedId, AssetSourceKind.Hak, Normalize("c:/offline.hak"), 0, isAvailable: false);
        AssetSource otherSource = Hak(otherId, "c:/online.hak", 1);
        AssetIdentity identity = new("offline", TgaType);

        byte[] pinHash = Hash(0x08);
        AssetOccurrence pinnedOccurrence = Occurrence(identity, pinnedId, entryIndex: 0, sha256: pinHash);
        AssetOccurrence otherOccurrence = Occurrence(identity, otherId, entryIndex: 0, sha256: Hash(0x09));

        WinnerPin pin = new(identity, pinnedId, new HakEntryLocator(0), pinHash);

        CuratedAsset asset = await ResolveSingleAsync(
            [pinnedSource, otherSource],
            [pin],
            (pinnedSource, [pinnedOccurrence]),
            (otherSource, [otherOccurrence]));

        AssertPinLeftInvalid(asset, pin);
        asset.ResolvedOccurrence.Should().BeNull(
            "an unavailable pinned source must not silently degrade to the next available source");
    }

    [Test]
    public async Task Reattach_Fails_WhenThePinnedSourceIsNotInTheWorkspaceAtAll()
    {
        Guid presentId = Guid.NewGuid();
        AssetSource present = Hak(presentId, "c:/present.hak", 0);
        AssetIdentity identity = new("orphan", TgaType);

        AssetOccurrence occurrence = Occurrence(identity, presentId, entryIndex: 0, sha256: Hash(0x03));
        WinnerPin pin = new(identity, Guid.NewGuid(), new HakEntryLocator(0), Hash(0x03));

        CuratedAsset asset = await ResolveSingleAsync([present], [pin], (present, [occurrence]));

        AssertPinLeftInvalid(asset, pin);
    }

    [Test]
    public async Task Reattach_Fails_WhenTheExactLocatorMatchesButItsPayloadIsUnreadable()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/locked.hak", 0);
        AssetIdentity identity = new("locked", TgaType);

        // No precomputed Sha256, so the resolver must hash - and hashing fails.
        AssetOccurrence unreadable = Occurrence(identity, sourceId, entryIndex: 0, sha256: null);
        WinnerPin pin = new(identity, sourceId, new HakEntryLocator(0), Hash(0x04));

        FaultingHashService hashService = new();
        hashService.FailingLocators.Add((sourceId, unreadable.Locator));

        CuratedAsset asset = await ResolveSingleAsync([source], [pin], hashService, (source, [unreadable]));

        AssertPinLeftInvalid(asset, pin);
        asset.HasUnreadableOccurrence.Should().BeTrue();
    }

    [Test]
    public async Task Reattach_Fails_WhenTheOnlyRung2CandidateIsUnreadable()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/halfLocked.hak", 0);
        AssetIdentity identity = new("halflocked", TgaType);

        byte[] pinHash = Hash(0x0A);
        AssetOccurrence readableMismatch = Occurrence(identity, sourceId, entryIndex: 0, sha256: Hash(0x0B));
        AssetOccurrence unreadableCandidate = Occurrence(identity, sourceId, entryIndex: 4, sha256: null);

        WinnerPin pin = new(identity, sourceId, new HakEntryLocator(0), pinHash);

        FaultingHashService hashService = new();
        hashService.FailingLocators.Add((sourceId, unreadableCandidate.Locator));

        CuratedAsset asset = await ResolveSingleAsync(
            [source],
            [pin],
            hashService,
            (source, [readableMismatch, unreadableCandidate]));

        AssertPinLeftInvalid(asset, pin);
        asset.HasUnreadableOccurrence.Should().BeTrue(
            "a candidate that could not be hashed is skipped, never assumed to match");
    }

    [Test]
    public async Task Reattach_Fails_AndTheOriginalPinSurvivesInWorkspaceStateForOperatorRepair()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/broken.hak", 0);
        AssetIdentity identity = new("survivor", TgaType);

        AssetOccurrence replaced = Occurrence(identity, sourceId, entryIndex: 0, sha256: Hash(0x0C));
        WinnerPin pin = new(identity, sourceId, new HakEntryLocator(0), Hash(0x0D));

        WorkspaceState state = await ResolveAsync([source], [pin], (source, [replaced]));

        state.Pins.Should().ContainSingle();
        state.Pins[0].Should().Be(pin, "a failed reattachment must not rewrite or drop the pin");
        state.CuratedAssets[0].IsSelected.Should().BeFalse("an unresolved identity is never packaged");
    }

    #endregion

    #region Pins across a rescan: changed pins, reattachment, invalidation reporting

    [Test]
    public async Task Rescan_WhenThePinnedPayloadMovesEntryIndex_ReportsAPinReattachment()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/rescan_pin.hak", 0);
        AssetIdentity identity = new("rescanpin", TgaType);

        byte[] pinHash = Hash(0x21);
        MockIndexService index = new();
        index.Register(source.FullPath, 0x01, Occurrence(identity, sourceId, 0, sha256: pinHash));

        (WorkspaceService workspace, _) = CreateWorkspace(index);
        await workspace.InitializeAsync([source], pins: [new WinnerPin(identity, sourceId, new HakEntryLocator(0), pinHash)]);
        workspace.CurrentState.CuratedAssets[0].Status.Should().Be(ResolutionStatus.Resolved);

        // Repack: same payload, new entry index.
        index.Register(source.FullPath, 0x02, Occurrence(identity, sourceId, 12, sha256: pinHash));

        (WorkspaceState state, ChangedInputReport report) = await workspace.RescanAsync();

        report.PinReattachments.Should().Contain(identity);
        report.PinInvalidations.Should().BeEmpty();
        state.CuratedAssets[0].Status.Should().Be(ResolutionStatus.Resolved);
        state.Pins[0].Locator.Should().Be(new HakEntryLocator(12));
    }

    [Test]
    public async Task Rescan_WhenThePinnedPayloadIsReplaced_ReportsAPinInvalidation()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/rescan_break.hak", 0);
        AssetIdentity identity = new("rescanbreak", TgaType);

        byte[] pinHash = Hash(0x31);
        MockIndexService index = new();
        index.Register(source.FullPath, 0x01, Occurrence(identity, sourceId, 0, sha256: pinHash));

        (WorkspaceService workspace, _) = CreateWorkspace(index);
        await workspace.InitializeAsync([source], pins: [new WinnerPin(identity, sourceId, new HakEntryLocator(0), pinHash)]);

        // The pinned payload is gone; a different payload occupies the identity.
        index.Register(source.FullPath, 0x02, Occurrence(identity, sourceId, 0, sha256: Hash(0x32)));

        (WorkspaceState state, ChangedInputReport report) = await workspace.RescanAsync();

        report.PinInvalidations.Should().Contain(identity);
        report.PinReattachments.Should().BeEmpty();
        state.CuratedAssets[0].Status.Should().Be(ResolutionStatus.InvalidPin);
        state.CuratedAssets[0].ResolvedOccurrence.Should().BeNull();
        state.Pins[0].PinHash.Should().Equal(pinHash);
    }

    [Test]
    public async Task Unpin_AfterAFailedReattachment_RestoresAutomaticPriorityResolution()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = Hak(sourceId, "c:/unpin.hak", 0);
        AssetIdentity identity = new("unpinme", TgaType);

        MockIndexService index = new();
        index.Register(source.FullPath, 0x01, Occurrence(identity, sourceId, 0, sha256: Hash(0x40)));

        (WorkspaceService workspace, _) = CreateWorkspace(index);
        await workspace.InitializeAsync([source], pins: [new WinnerPin(identity, sourceId, new HakEntryLocator(0), Hash(0x41))]);
        workspace.CurrentState.CuratedAssets[0].Status.Should().Be(ResolutionStatus.InvalidPin);

        WorkspaceState state = await workspace.UnpinAsync(identity);

        state.Pins.Should().BeEmpty();
        state.CuratedAssets[0].Status.Should().Be(ResolutionStatus.Resolved,
            "removing the pin is the operator's explicit consent to fall back to priority order");
    }

    #endregion

    #region Helpers

    private static void AssertPinLeftInvalid(CuratedAsset asset, WinnerPin pin)
    {
        asset.Status.Should().Be(ResolutionStatus.InvalidPin);
        asset.HasInvalidPin.Should().BeTrue();
        asset.ResolvedOccurrence.Should().BeNull("a pin that cannot reattach must not silently fall back");
        asset.Pin.Should().Be(pin, "the original pin is preserved so the operator can repair or clear it");
        asset.IsSelected.Should().BeFalse();
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static AssetSource Hak(Guid id, string path, int priority) =>
        new(id, AssetSourceKind.Hak, Normalize(path), priority);

    private static AssetOccurrence Occurrence(AssetIdentity identity, Guid sourceId, int entryIndex, byte[]? sha256) =>
        new(identity, sourceId, new HakEntryLocator(entryIndex), $"{identity.Resref}.tga", 100,
            ValidationState.Valid, extensionMetadata: null, sha256: sha256);

    private static byte[] Hash(byte seed)
    {
        byte[] hash = new byte[32];
        Array.Fill(hash, seed);
        return hash;
    }

    private static Task<CuratedAsset> ResolveSingleAsync(
        IReadOnlyList<AssetSource> sources,
        IReadOnlyList<WinnerPin> pins,
        params (AssetSource Source, AssetOccurrence[] Occurrences)[] content) =>
        ResolveSingleAsync(sources, pins, new FaultingHashService(), content);

    private static async Task<CuratedAsset> ResolveSingleAsync(
        IReadOnlyList<AssetSource> sources,
        IReadOnlyList<WinnerPin> pins,
        FaultingHashService hashService,
        params (AssetSource Source, AssetOccurrence[] Occurrences)[] content)
    {
        WorkspaceState state = await ResolveAsync(sources, pins, hashService, content);
        state.CuratedAssets.Should().ContainSingle();
        return state.CuratedAssets[0];
    }

    private static Task<WorkspaceState> ResolveAsync(
        IReadOnlyList<AssetSource> sources,
        IReadOnlyList<WinnerPin> pins,
        params (AssetSource Source, AssetOccurrence[] Occurrences)[] content) =>
        ResolveAsync(sources, pins, new FaultingHashService(), content);

    private static Task<WorkspaceState> ResolveAsync(
        IReadOnlyList<AssetSource> sources,
        IReadOnlyList<WinnerPin> pins,
        FaultingHashService hashService,
        params (AssetSource Source, AssetOccurrence[] Occurrences)[] content)
    {
        Dictionary<Guid, SourceIndexSnapshot> snapshots = new();
        foreach ((AssetSource source, AssetOccurrence[] occurrences) in content)
        {
            snapshots[source.Id] = Snapshot(source, occurrences);
        }

        WorkspaceResolver resolver = new(hashService, new AssetHashCache());
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

    private static (WorkspaceService Workspace, FaultingHashService HashService) CreateWorkspace(MockIndexService index)
    {
        FaultingHashService hashService = new();
        AssetHashCache cache = new();
        WorkspaceResolver resolver = new(hashService, cache);
        return (new WorkspaceService(index, resolver, cache), hashService);
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

    private sealed class MockIndexService : IAssetIndexService
    {
        private readonly Dictionary<string, (byte Seed, AssetOccurrence[] Occurrences)> _byPath =
            new(StringComparer.OrdinalIgnoreCase);

        public void Register(string path, byte fingerprintSeed, params AssetOccurrence[] occurrences)
        {
            _byPath[Normalize(path)] = (fingerprintSeed, occurrences);
        }

        public Task<SourceIndexSnapshot> IndexAsync(
            AssetSource source,
            IProgress<IndexProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string key = Normalize(source.FullPath);
            (byte seed, AssetOccurrence[] occurrences) = _byPath.TryGetValue(key, out var entry)
                ? entry
                : ((byte)0, Array.Empty<AssetOccurrence>());

            byte[] digest = new byte[32];
            Array.Fill(digest, seed);

            return Task.FromResult(new SourceIndexSnapshot(
                source,
                new SourceFingerprint(source.Kind, 1, digest),
                occurrences.Select(o => new IndexedAssetRecord(o)).ToList(),
                Array.Empty<AssetDiagnosticRecord>(),
                isCacheHit: false,
                scanStatistics: new SourceScanStatistics(occurrences.Length, 0, TimeSpan.Zero)));
        }
    }

    #endregion
}
