using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;

namespace SRN.CC.Tests.UI;

[TestFixture]
public class ConflictQueueTests
{
    private const ushort TgaType = 2000;

    [Test]
    public void Load_IncludesCrossSourceCollisions_AndCountsResolvedByValidPin()
    {
        Guid s1 = Guid.NewGuid();
        Guid s2 = Guid.NewGuid();

        CuratedAsset conflict = Collision("alpha", s1, s2, pinned: false);
        CuratedAsset resolvedConflict = Collision("beta", s1, s2, pinned: true);
        CuratedAsset clean = Resolved("gamma", s1);

        WorkspaceState state = State(new[] { s1, s2 }, conflict, resolvedConflict, clean);

        var recorded = new List<AssetOccurrence>();
        ConflictQueueViewModel queue = new(o => { recorded.Add(o); return Task.CompletedTask; }, _ => "TGA");
        queue.Load(state);

        queue.TotalCount.Should().Be(2, "only the two colliding identities are conflicts");
        queue.ResolvedCount.Should().Be(1, "the identity with a valid pin counts as resolved");
        queue.ProgressText.Should().Be("1 of 2 resolved");
        queue.HeaderText.Should().Be("Conflicts (2)");
        queue.Conflicts.Select(c => c.Resref).Should().Contain(new[] { "alpha", "beta" });
        queue.Conflicts.Should().NotContain(c => c.Resref == "gamma");
    }

    [Test]
    public async Task MakeWinner_InvokesPinCallbackWithTheChosenOccurrence()
    {
        Guid s1 = Guid.NewGuid();
        Guid s2 = Guid.NewGuid();
        CuratedAsset conflict = Collision("alpha", s1, s2, pinned: false);
        WorkspaceState state = State(new[] { s1, s2 }, conflict);

        var recorded = new List<AssetOccurrence>();
        ConflictQueueViewModel queue = new(o => { recorded.Add(o); return Task.CompletedTask; }, _ => "TGA");
        queue.Load(state);

        ConflictCandidateViewModel candidate = queue.Current!.Candidates.First(c => c.Priority == 1);
        await candidate.MakeWinnerCommand.ExecuteAsync(null);

        recorded.Should().ContainSingle();
        recorded[0].SourceId.Should().Be(s2, "the second candidate came from the lower-priority source");
    }

    [Test]
    public void Load_KeepsCursorOnSameIdentity_AndAdvancesPastNewlyResolved()
    {
        Guid s1 = Guid.NewGuid();
        Guid s2 = Guid.NewGuid();
        CuratedAsset a = Collision("alpha", s1, s2, pinned: false);
        CuratedAsset b = Collision("beta", s1, s2, pinned: false);
        WorkspaceState state = State(new[] { s1, s2 }, a, b);

        ConflictQueueViewModel queue = new(_ => Task.CompletedTask, _ => "TGA");
        queue.Load(state);
        queue.Current!.Resref.Should().Be("alpha", "the first unresolved conflict is selected initially");

        // Now alpha becomes resolved (valid pin); reloading should advance the cursor to beta.
        CuratedAsset alphaResolved = Collision("alpha", s1, s2, pinned: true);
        WorkspaceState next = State(new[] { s1, s2 }, alphaResolved, b);
        queue.Load(next);

        queue.Current!.Resref.Should().Be("beta", "a newly resolved conflict advances the cursor");
    }

    [Test]
    public void Navigation_NextAndPrevious_MoveThroughTheQueue()
    {
        Guid s1 = Guid.NewGuid();
        Guid s2 = Guid.NewGuid();
        WorkspaceState state = State(new[] { s1, s2 },
            Collision("alpha", s1, s2, false),
            Collision("beta", s1, s2, false),
            Collision("gamma", s1, s2, false));

        ConflictQueueViewModel queue = new(_ => Task.CompletedTask, _ => "TGA");
        queue.Load(state);

        queue.Current!.Resref.Should().Be("alpha");
        queue.PreviousCommand.CanExecute(null).Should().BeFalse("already at the first conflict");

        queue.NextCommand.Execute(null);
        queue.Current!.Resref.Should().Be("beta");
        queue.NextCommand.Execute(null);
        queue.Current!.Resref.Should().Be("gamma");
        queue.NextCommand.CanExecute(null).Should().BeFalse("already at the last conflict");

        queue.PreviousCommand.Execute(null);
        queue.Current!.Resref.Should().Be("beta");
    }

    [Test]
    public async Task Candidate_EnsureHashLoaded_FillsShortHashFromLoader()
    {
        // Indexed occurrences carry no precomputed hash, so the card starts at "—" and must fill in
        // lazily from the injected loader — otherwise operators can never compare candidate hashes.
        AssetIdentity id = new("alpha", TgaType);
        AssetOccurrence occ = Occ(id, Guid.NewGuid(), 0);
        byte[] full = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        ConflictCandidateViewModel candidate = new(
            occ, "src.hak", priority: 0, SourceMode.Full, isCurrentWinner: false,
            onMakeWinner: _ => Task.CompletedTask,
            hashLoader: (_, _) => Task.FromResult<byte[]?>(full));

        candidate.HashText.Should().Be("—", "no hash is shown until the card is loaded");

        await candidate.EnsureHashLoadedAsync();

        candidate.HashText.Should().Be("00010203", "the leading bytes render as a short hex hash");
    }

    [Test]
    public async Task Candidate_EnsureHashLoaded_UsesPrecomputedHash_WithoutCallingLoader()
    {
        byte[] precomputed = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        AssetIdentity id = new("alpha", TgaType);
        AssetOccurrence occ = new(id, Guid.NewGuid(), new HakEntryLocator(0), "alpha.tga", 100, sha256: precomputed);
        bool loaderCalled = false;

        ConflictCandidateViewModel candidate = new(
            occ, "src.hak", 0, SourceMode.Full, false,
            _ => Task.CompletedTask,
            (_, _) => { loaderCalled = true; return Task.FromResult<byte[]?>(null); });

        candidate.HashText.Should().Be("fffefdfc", "a precomputed hash shows immediately");
        await candidate.EnsureHashLoadedAsync();
        loaderCalled.Should().BeFalse("a precomputed hash needs no loader");
        candidate.HashText.Should().Be("fffefdfc");
    }

    [Test]
    public async Task Candidate_EnsureHashLoaded_UnreadablePayload_StaysDash()
    {
        AssetOccurrence occ = Occ(new("alpha", TgaType), Guid.NewGuid(), 0);
        ConflictCandidateViewModel candidate = new(
            occ, "src.hak", 0, SourceMode.Full, false,
            _ => Task.CompletedTask,
            (_, _) => Task.FromResult<byte[]?>(null));

        await candidate.EnsureHashLoadedAsync();
        candidate.HashText.Should().Be("—", "an unreadable payload falls back to a dash, not a crash");
    }

    [Test]
    public void Load_ComputesHashesForTheCurrentConflictCandidates()
    {
        // Landing on a conflict (Current set during Load) must kick off hashing for its candidate
        // cards. A synchronously-completing loader lets us observe the result deterministically.
        Guid s1 = Guid.NewGuid();
        Guid s2 = Guid.NewGuid();
        WorkspaceState state = State(new[] { s1, s2 }, Collision("alpha", s1, s2, pinned: false));
        byte[] full = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        ConflictQueueViewModel queue = new(
            _ => Task.CompletedTask, _ => "TGA",
            onPreferSource: null,
            hashLoader: (_, _) => Task.FromResult<byte[]?>(full));
        queue.Load(state);

        queue.Current!.Candidates.Should().OnlyContain(c => c.HashText == "00010203",
            "the current conflict's candidate hashes are computed so they can be compared");
    }

    // Helpers ---------------------------------------------------------------

    private static AssetOccurrence Occ(AssetIdentity id, Guid sourceId, int entry) =>
        new(id, sourceId, new HakEntryLocator(entry), $"{id.Resref}.tga", 100);

    private static CuratedAsset Collision(string resref, Guid s1, Guid s2, bool pinned)
    {
        AssetIdentity id = new(resref, TgaType);
        AssetOccurrence o1 = Occ(id, s1, 0);
        AssetOccurrence o2 = Occ(id, s2, 0);
        WinnerPin? pin = pinned ? new WinnerPin(id, s1, new HakEntryLocator(0), new byte[32]) : null;
        return new CuratedAsset(
            identity: id,
            allOccurrences: new[] { o1, o2 },
            resolvedOccurrence: o1,
            pin: pin,
            status: ResolutionStatus.Resolved,
            isSelected: true,
            resolvedSha256: null,
            hasCrossSourceCollision: true);
    }

    private static CuratedAsset Resolved(string resref, Guid sourceId)
    {
        AssetIdentity id = new(resref, TgaType);
        AssetOccurrence o = Occ(id, sourceId, 0);
        return new CuratedAsset(id, new[] { o }, o, null, ResolutionStatus.Resolved, true);
    }

    private static WorkspaceState State(Guid[] sourceIds, params CuratedAsset[] assets)
    {
        var sources = sourceIds
            .Select((id, i) => new AssetSource(id, AssetSourceKind.Hak, $"c:/source{i}.hak", priorityOrdinal: i))
            .ToArray();
        return new WorkspaceState(
            sources: sources,
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: assets,
            selectionState: SelectionState.IncludeAll(),
            pins: Array.Empty<WinnerPin>());
    }
}
