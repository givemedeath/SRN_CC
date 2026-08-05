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
/// Backfill for the selection-defaults and override-compaction cases at <c>PLAN.md:221</c>: a
/// filtered include/exclude writes only the overrides it must, "Exclude All" flips the default and
/// clears the override set, and an identity first seen after the fact follows whatever the current
/// default is.
/// </summary>
[TestFixture]
public class SelectionOverrideCompactionTests
{
    private const ushort TgaType = 2000;

    #region Filtered include/exclude writes only the necessary overrides

    [Test]
    public void FilteredExclude_UnderIncludeAllDefault_WritesOneOverridePerExcludedIdentityOnly()
    {
        AssetIdentity[] all = Identities("a", "b", "c", "d", "e");
        SelectionState state = SelectionState.IncludeAll();

        // "Exclude all filtered" folds SetOverride over the filtered rows, as the shell does.
        state = ApplyToFiltered(state, [all[1], all[3]], selected: false);

        state.DefaultSelected.Should().BeTrue();
        state.Overrides.Should().HaveCount(2, "only the identities that differ from the default are stored");
        state.Overrides.Keys.Should().BeEquivalentTo(new[] { all[1], all[3] });
        state.Overrides.Values.Should().AllSatisfy(v => v.Should().BeFalse());
        all.Where(state.IsSelected).Should().BeEquivalentTo(new[] { all[0], all[2], all[4] });
    }

    [Test]
    public void FilteredInclude_UnderIncludeAllDefault_WritesNoOverridesAtAll()
    {
        AssetIdentity[] all = Identities("a", "b", "c");
        SelectionState state = SelectionState.IncludeAll();

        state = ApplyToFiltered(state, all, selected: true);

        state.Overrides.Should().BeEmpty("including under an include-all default is already the default");
        all.Should().OnlyContain(id => state.IsSelected(id));
    }

    [Test]
    public void FilteredInclude_UnderExcludeAllDefault_WritesOneOverridePerIncludedIdentityOnly()
    {
        AssetIdentity[] all = Identities("a", "b", "c", "d");
        SelectionState state = SelectionState.ExcludeAll();

        state = ApplyToFiltered(state, [all[0], all[2]], selected: true);

        state.DefaultSelected.Should().BeFalse();
        state.Overrides.Should().HaveCount(2);
        state.Overrides.Keys.Should().BeEquivalentTo(new[] { all[0], all[2] });
        state.Overrides.Values.Should().AllSatisfy(v => v.Should().BeTrue());
        all.Where(state.IsSelected).Should().BeEquivalentTo(new[] { all[0], all[2] });
    }

    [Test]
    public void FilteredInclude_ReversingAnEarlierFilteredExclude_CompactsBackToNoOverrides()
    {
        AssetIdentity[] all = Identities("a", "b", "c");
        SelectionState state = SelectionState.IncludeAll();

        state = ApplyToFiltered(state, [all[0], all[1]], selected: false);
        state.Overrides.Should().HaveCount(2);

        state = ApplyToFiltered(state, [all[0], all[1]], selected: true);

        state.Overrides.Should().BeEmpty("redundant overrides are removed, not left to accumulate");
        all.Should().OnlyContain(id => state.IsSelected(id));
    }

    [Test]
    public void FilteredExclude_AppliedTwice_IsIdempotentAndDoesNotDuplicateOverrides()
    {
        AssetIdentity[] all = Identities("a", "b");
        SelectionState state = SelectionState.IncludeAll();

        state = ApplyToFiltered(state, [all[0]], selected: false);
        state = ApplyToFiltered(state, [all[0]], selected: false);

        state.Overrides.Should().HaveCount(1);
        state.IsSelected(all[0]).Should().BeFalse();
    }

    [Test]
    public void OverrideCompaction_HappensOnConstructionForCallerSuppliedOverrides()
    {
        AssetIdentity redundant = new("redundant", TgaType);
        AssetIdentity meaningful = new("meaningful", TgaType);

        SelectionState state = new(
            defaultSelected: true,
            overrides: new Dictionary<AssetIdentity, bool>
            {
                [redundant] = true,
                [meaningful] = false,
            });

        state.Overrides.Should().ContainSingle("an override equal to the default carries no information");
        state.Overrides.Should().ContainKey(meaningful);
        state.Overrides.Should().NotContainKey(redundant);
    }

    [Test]
    public void OverrideCompaction_SetDefaultKeepsOnlyOverridesThatStillDifferFromTheNewDefault()
    {
        AssetIdentity excluded = new("excluded", TgaType);
        AssetIdentity alsoExcluded = new("also", TgaType);

        SelectionState state = SelectionState.IncludeAll()
            .SetOverride(excluded, false)
            .SetOverride(alsoExcluded, false);
        state.Overrides.Should().HaveCount(2);

        SelectionState flipped = state.SetDefault(false);

        flipped.DefaultSelected.Should().BeFalse();
        flipped.Overrides.Should().BeEmpty(
            "both overrides said 'not selected', which is now exactly what the default says");
        flipped.IsSelected(excluded).Should().BeFalse();
    }

    [Test]
    public void OverrideCompaction_SetDefaultPreservesOverridesThatStillContradictTheNewDefault()
    {
        AssetIdentity keptIn = new("keptin", TgaType);
        AssetIdentity droppedOut = new("droppedout", TgaType);

        SelectionState state = SelectionState.ExcludeAll().SetOverride(keptIn, true);
        state = state.SetOverride(droppedOut, false);
        state.Overrides.Should().ContainSingle().And.ContainKey(keptIn);

        SelectionState flipped = state.SetDefault(true);

        flipped.DefaultSelected.Should().BeTrue();
        flipped.Overrides.Should().BeEmpty();
        flipped.IsSelected(keptIn).Should().BeTrue();
        flipped.IsSelected(droppedOut).Should().BeTrue("it never carried an override to preserve");
    }

    [Test]
    public void ClearOverride_ReturnsTheSameInstanceWhenThereIsNothingToClear()
    {
        AssetIdentity identity = new("untouched", TgaType);
        SelectionState state = SelectionState.IncludeAll();

        state.ClearOverride(identity).Should().BeSameAs(state);
    }

    #endregion

    #region "Exclude All" flips the default and clears the overrides

    [Test]
    public void ExcludeAll_SetsTheDefaultFalseAndClearsEveryExistingOverride()
    {
        AssetIdentity[] all = Identities("a", "b", "c");
        SelectionState state = SelectionState.IncludeAll();
        state = ApplyToFiltered(state, [all[0], all[1]], selected: false);
        state.Overrides.Should().HaveCount(2);

        SelectionState excluded = SelectionState.ExcludeAll();

        excluded.DefaultSelected.Should().BeFalse();
        excluded.Overrides.Should().BeEmpty("Exclude All is a reset, not an accumulation of per-row overrides");
        all.Should().OnlyContain(id => !excluded.IsSelected(id));
    }

    [Test]
    public void IncludeAll_SetsTheDefaultTrueAndClearsEveryExistingOverride()
    {
        AssetIdentity[] all = Identities("a", "b");
        SelectionState state = SelectionState.ExcludeAll();
        state = ApplyToFiltered(state, all, selected: true);
        state.Overrides.Should().HaveCount(2);

        SelectionState included = SelectionState.IncludeAll();

        included.DefaultSelected.Should().BeTrue();
        included.Overrides.Should().BeEmpty();
        all.Should().OnlyContain(id => included.IsSelected(id));
    }

    #endregion

    #region A new identity follows the current default

    [Test]
    public void NewIdentity_UnderIncludeAllDefault_IsSelectedWithoutAnOverrideBeingWritten()
    {
        SelectionState state = SelectionState.IncludeAll().SetOverride(new AssetIdentity("known", TgaType), false);
        AssetIdentity newcomer = new("newcomer", TgaType);

        state.IsSelected(newcomer).Should().BeTrue();
        state.Overrides.Should().NotContainKey(newcomer);
    }

    [Test]
    public void NewIdentity_UnderExcludeAllDefault_IsNotSelectedWithoutAnOverrideBeingWritten()
    {
        SelectionState state = SelectionState.ExcludeAll().SetOverride(new AssetIdentity("known", TgaType), true);
        AssetIdentity newcomer = new("newcomer", TgaType);

        state.IsSelected(newcomer).Should().BeFalse();
        state.Overrides.Should().NotContainKey(newcomer);
    }

    [Test]
    public async Task NewIdentity_AppearingOnARescan_FollowsTheCurrentDefaultRatherThanAnOverride()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = new(sourceId, AssetSourceKind.Hak, Path.GetFullPath("c:/selection.hak"), 0);

        AssetIdentity existing = new("existing", TgaType);
        AssetIdentity newcomer = new("newcomer", TgaType);

        MockIndexService index = new();
        index.Register(source.FullPath, 0x01, Occurrence(existing, sourceId, 0));

        AssetHashCache cache = new();
        WorkspaceResolver resolver = new(new ConstantHashService(), cache);
        WorkspaceService workspace = new(index, resolver, cache);

        await workspace.InitializeAsync([source], selectionState: SelectionState.ExcludeAll());
        workspace.CurrentState.CuratedAssets[0].IsSelected.Should().BeFalse();

        index.Register(source.FullPath, 0x02, Occurrence(existing, sourceId, 0), Occurrence(newcomer, sourceId, 1));

        (WorkspaceState state, ChangedInputReport report) = await workspace.RescanAsync();

        state.CuratedAssets.Should().HaveCount(2);
        state.CuratedAssets.Should().OnlyContain(a => !a.IsSelected,
            "the exclude-all default applies to identities the operator has never seen");
        state.SelectionState.Overrides.Should().BeEmpty();
        report.AddedIdentities.Should().Contain(newcomer);
        report.SelectionChanges.Should().NotContain(newcomer,
            "an unselected newcomer is not a selection change the operator must review");
    }

    [Test]
    public async Task NewIdentity_AppearingOnARescanUnderIncludeAll_IsSelectedAndReportedAsASelectionChange()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = new(sourceId, AssetSourceKind.Hak, Path.GetFullPath("c:/selection_inc.hak"), 0);

        AssetIdentity existing = new("existing", TgaType);
        AssetIdentity newcomer = new("newcomer", TgaType);

        MockIndexService index = new();
        index.Register(source.FullPath, 0x01, Occurrence(existing, sourceId, 0));

        AssetHashCache cache = new();
        WorkspaceResolver resolver = new(new ConstantHashService(), cache);
        WorkspaceService workspace = new(index, resolver, cache);

        await workspace.InitializeAsync([source], selectionState: SelectionState.IncludeAll());

        index.Register(source.FullPath, 0x02, Occurrence(existing, sourceId, 0), Occurrence(newcomer, sourceId, 1));

        (WorkspaceState state, ChangedInputReport report) = await workspace.RescanAsync();

        state.CuratedAssets.Single(a => a.Identity.Equals(newcomer)).IsSelected.Should().BeTrue();
        state.SelectionState.Overrides.Should().BeEmpty("following the default requires no stored override");
        report.SelectionChanges.Should().Contain(newcomer);
    }

    [Test]
    public async Task UpdateSelection_WithACompactedState_RoundTripsThroughTheWorkspaceUnchanged()
    {
        Guid sourceId = Guid.NewGuid();
        AssetSource source = new(sourceId, AssetSourceKind.Hak, Path.GetFullPath("c:/selection_rt.hak"), 0);

        AssetIdentity kept = new("kept", TgaType);
        AssetIdentity dropped = new("dropped", TgaType);

        MockIndexService index = new();
        index.Register(source.FullPath, 0x01, Occurrence(kept, sourceId, 0), Occurrence(dropped, sourceId, 1));

        AssetHashCache cache = new();
        WorkspaceResolver resolver = new(new ConstantHashService(), cache);
        WorkspaceService workspace = new(index, resolver, cache);
        await workspace.InitializeAsync([source]);

        SelectionState filtered = ApplyToFiltered(SelectionState.IncludeAll(), [dropped], selected: false);
        WorkspaceState state = await workspace.UpdateSelectionAsync(filtered);

        state.SelectionState.Overrides.Should().ContainSingle().And.ContainKey(dropped);
        state.CuratedAssets.Single(a => a.Identity.Equals(kept)).IsSelected.Should().BeTrue();
        state.CuratedAssets.Single(a => a.Identity.Equals(dropped)).IsSelected.Should().BeFalse();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Mirrors the shell's bulk include/exclude path (folding
    /// <see cref="SelectionState.SetOverride"/> over the filtered rows).
    /// </summary>
    private static SelectionState ApplyToFiltered(SelectionState state, IEnumerable<AssetIdentity> filtered, bool selected)
    {
        foreach (AssetIdentity identity in filtered)
        {
            state = state.SetOverride(identity, selected);
        }
        return state;
    }

    private static AssetIdentity[] Identities(params string[] resrefs) =>
        resrefs.Select(r => new AssetIdentity(r, TgaType)).ToArray();

    private static AssetOccurrence Occurrence(AssetIdentity identity, Guid sourceId, int entryIndex) =>
        new(identity, sourceId, new HakEntryLocator(entryIndex), $"{identity.Resref}.tga", 100);

    private sealed class ConstantHashService : IStreamingHashService
    {
        public Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            byte[] hash = new byte[32];
            return Task.FromResult(hash);
        }
    }

    private sealed class MockIndexService : IAssetIndexService
    {
        private readonly Dictionary<string, (byte Seed, AssetOccurrence[] Occurrences)> _byPath =
            new(StringComparer.OrdinalIgnoreCase);

        public void Register(string path, byte fingerprintSeed, params AssetOccurrence[] occurrences)
        {
            _byPath[Path.GetFullPath(path)] = (fingerprintSeed, occurrences);
        }

        public Task<SourceIndexSnapshot> IndexAsync(
            AssetSource source,
            IProgress<IndexProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            (byte seed, AssetOccurrence[] occurrences) = _byPath.TryGetValue(Path.GetFullPath(source.FullPath), out var entry)
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
