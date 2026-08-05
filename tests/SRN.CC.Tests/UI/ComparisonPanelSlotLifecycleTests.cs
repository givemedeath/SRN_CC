using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.Tests.UI;

/// <summary>
/// The slot-lifecycle half of <c>PLAN.md:222</c>: "stable/overflow slots, replacement/promotion,
/// mode switching, pinning, cancellation, and linked-state rules".
/// </summary>
/// <remarks>
/// <para>
/// The comparison panel has exactly three fixed slots. Everything here is about which occurrence
/// ends up in which slot as the selection changes underneath them, driven end to end through
/// <see cref="ComparisonPanelViewModel.UpdateSelectionAsync"/> rather than by writing slot state
/// directly, because slot <i>assignment</i> is precisely what is under test.
/// </para>
/// <para>
/// <b>Vocabulary.</b> A <i>stable</i> slot keeps hosting the same occurrence across a selection
/// update that did not change it. An <i>overflow</i> occurrence is one past the third, which the
/// panel simply does not show. <i>Replacement</i> is a slot's occurrence being swapped for an
/// unrelated one; <i>promotion</i> is a previously-overflow occurrence moving into a slot because
/// something ahead of it went away.
/// </para>
/// <para>
/// <b>Relationship to <c>LinkedNavigationTests</c>.</b> Slice S14's fixture already covers the
/// <c>PLAN.md:120</c> linking rules against statically-assigned slot content (image/camera link by
/// default, audio only when all three slots are audio, text never, and the master toggle). This
/// fixture does not repeat any of that; the two linked-state tests at the end cover only what that
/// fixture cannot — that linking still holds after the panel itself has torn down and rebuilt slot
/// content through a real mode switch, and that the toggle survives the same churn.
/// </para>
/// <para>
/// The preview engine here is deliberately provider-less: every preview resolves to a failure
/// result ("No preview provider available"), which leaves <see cref="PreviewSlotViewModel.Content"/>
/// null but exercises the full assignment, cancellation, and pin-availability paths. Slot
/// <i>content</i> rendering is <c>ComparisonPanelViewTests</c>' subject, not this fixture's.
/// </para>
/// </remarks>
[TestFixture]
public class ComparisonPanelSlotLifecycleTests
{
    // =====================================================================================
    // Stable slots
    // =====================================================================================

    [Test]
    public async Task OccurrenceMode_ThreeOccurrences_FillsEverySlotInOccurrenceOrder()
    {
        Fixture fixture = new();
        CuratedAsset asset = fixture.CreateAsset("armor01", occurrenceCount: 3);

        await fixture.Panel.UpdateSelectionAsync([asset], fixture.SourceMap);

        fixture.Panel.Slots.Should().HaveCount(3);
        for (int i = 0; i < 3; i++)
        {
            fixture.Panel.Slots[i].IsActive.Should().BeTrue();
            fixture.Panel.Slots[i].AssignedOccurrence.Should().BeSameAs(asset.AllOccurrences[i]);
            fixture.Panel.Slots[i].AssignedAsset.Should().BeSameAs(asset);
            fixture.Panel.Slots[i].SlotTitle.Should().Contain("armor01");
        }
    }

    [Test]
    public async Task OccurrenceMode_ReapplyingTheSameSelection_LeavesEveryOccurrenceInItsOriginalSlot()
    {
        Fixture fixture = new();
        CuratedAsset asset = fixture.CreateAsset("armor01", occurrenceCount: 3);

        await fixture.Panel.UpdateSelectionAsync([asset], fixture.SourceMap);
        AssetOccurrence[] firstPass = fixture.Panel.Slots.Select(s => s.AssignedOccurrence!).ToArray();

        await fixture.Panel.UpdateSelectionAsync([asset], fixture.SourceMap);

        fixture.Panel.Slots.Select(s => s.AssignedOccurrence).Should().Equal(firstPass);
    }

    [Test]
    public async Task OccurrenceMode_FewerOccurrencesThanSlots_LeavesTheRemainderEmptyAndInactive()
    {
        Fixture fixture = new();
        CuratedAsset asset = fixture.CreateAsset("armor01", occurrenceCount: 1);

        await fixture.Panel.UpdateSelectionAsync([asset], fixture.SourceMap);

        fixture.Panel.Slots[0].IsActive.Should().BeTrue();
        foreach (PreviewSlotViewModel slot in fixture.Panel.Slots.Skip(1))
        {
            slot.IsActive.Should().BeFalse();
            slot.AssignedOccurrence.Should().BeNull();
            slot.SlotTitle.Should().EndWith("[Empty]");
            slot.Content.Should().BeOfType<TextContentViewModel>(
                "an empty slot always holds a placeholder, never null");
            slot.IsPinEnabled.Should().BeFalse();
        }
    }

    // =====================================================================================
    // Overflow
    // =====================================================================================

    [Test]
    public async Task OccurrenceMode_MoreOccurrencesThanSlots_ShowsTheFirstThreeAndDropsTheOverflow()
    {
        Fixture fixture = new();
        CuratedAsset asset = fixture.CreateAsset("armor01", occurrenceCount: 6);

        await fixture.Panel.UpdateSelectionAsync([asset], fixture.SourceMap);

        fixture.Panel.Slots.Select(s => s.AssignedOccurrence)
            .Should().Equal(asset.AllOccurrences.Take(3));

        AssetOccurrence[] overflow = asset.AllOccurrences.Skip(3).ToArray();
        overflow.Should().HaveCount(3);
        fixture.Panel.Slots.Select(s => s.AssignedOccurrence)
            .Should().NotIntersectWith(overflow, "the panel has exactly three slots and never grows");
    }

    [Test]
    public async Task ResolvedMode_MoreAssetsThanSlots_ShowsTheFirstThreeWinnersAndDropsTheOverflow()
    {
        Fixture fixture = new();
        CuratedAsset[] assets =
        [
            fixture.CreateAsset("asset_a", 1),
            fixture.CreateAsset("asset_b", 1),
            fixture.CreateAsset("asset_c", 1),
            fixture.CreateAsset("asset_d", 1)
        ];

        fixture.Panel.Mode = ComparisonMode.ResolvedMode;
        await fixture.Panel.UpdateSelectionAsync(assets, fixture.SourceMap);

        fixture.Panel.Slots.Select(s => s.AssignedAsset).Should().Equal(assets.Take(3));
        fixture.Panel.Slots.Select(s => s.AssignedOccurrence)
            .Should().Equal(assets.Take(3).Select(a => a.ResolvedOccurrence));
        fixture.Panel.Slots.Should().NotContain(s => ReferenceEquals(s.AssignedAsset, assets[3]));
    }

    // =====================================================================================
    // Replacement and promotion
    // =====================================================================================

    [Test]
    public async Task OccurrenceMode_SelectingADifferentAsset_ReplacesEverySlotsOccurrence()
    {
        Fixture fixture = new();
        CuratedAsset first = fixture.CreateAsset("armor01", occurrenceCount: 3);
        CuratedAsset second = fixture.CreateAsset("weapon02", occurrenceCount: 3);

        await fixture.Panel.UpdateSelectionAsync([first], fixture.SourceMap);
        await fixture.Panel.UpdateSelectionAsync([second], fixture.SourceMap);

        fixture.Panel.Slots.Select(s => s.AssignedOccurrence).Should().Equal(second.AllOccurrences);
        fixture.Panel.Slots.Should().OnlyContain(s => s.SlotTitle.Contains("weapon02"));
        fixture.Panel.Slots.Select(s => s.AssignedOccurrence)
            .Should().NotIntersectWith(first.AllOccurrences);
    }

    [Test]
    public async Task OccurrenceMode_WhenALeadingOccurrenceDisappears_ThePreviouslyOverflowOccurrencePromotesIntoASlot()
    {
        Fixture fixture = new();
        CuratedAsset withFour = fixture.CreateAsset("armor01", occurrenceCount: 4);
        AssetOccurrence overflowOccurrence = withFour.AllOccurrences[3];

        await fixture.Panel.UpdateSelectionAsync([withFour], fixture.SourceMap);
        fixture.Panel.Slots.Select(s => s.AssignedOccurrence)
            .Should().NotContain(overflowOccurrence, "it starts out beyond the third slot");

        // A rescan drops the first occurrence; the same asset now exposes occurrences 1..3.
        CuratedAsset afterRescan = new(
            withFour.Identity,
            withFour.AllOccurrences.Skip(1).ToList(),
            withFour.AllOccurrences[1],
            null,
            ResolutionStatus.Resolved,
            true);

        await fixture.Panel.UpdateSelectionAsync([afterRescan], fixture.SourceMap);

        fixture.Panel.Slots[0].AssignedOccurrence.Should().BeSameAs(withFour.AllOccurrences[1]);
        fixture.Panel.Slots[1].AssignedOccurrence.Should().BeSameAs(withFour.AllOccurrences[2]);
        fixture.Panel.Slots[2].AssignedOccurrence.Should().BeSameAs(
            overflowOccurrence,
            "the fourth occurrence is promoted into the slot the removed one vacated");
    }

    [Test]
    public async Task ResolvedMode_WhenAnAssetLosesItsWinner_TheSlotStaysBoundToTheAssetWithNoOccurrence()
    {
        Fixture fixture = new();
        CuratedAsset resolved = fixture.CreateAsset("asset_a", 1);
        CuratedAsset unresolved = new(
            new AssetIdentity("asset_b", 2000),
            Array.Empty<AssetOccurrence>(),
            null,
            null,
            ResolutionStatus.Unavailable,
            true);

        fixture.Panel.Mode = ComparisonMode.ResolvedMode;
        await fixture.Panel.UpdateSelectionAsync([resolved, unresolved], fixture.SourceMap);

        fixture.Panel.Slots[0].IsActive.Should().BeTrue();
        fixture.Panel.Slots[1].AssignedAsset.Should().BeSameAs(unresolved);
        fixture.Panel.Slots[1].AssignedOccurrence.Should().BeNull();
        fixture.Panel.Slots[1].IsActive.Should().BeFalse();
        fixture.Panel.Slots[1].IsPinEnabled.Should().BeFalse();
    }

    // =====================================================================================
    // Mode switching
    // =====================================================================================

    [Test]
    public async Task ToggleMode_FromOccurrenceToResolved_RepopulatesTheSlotsFromTheSameSelection()
    {
        Fixture fixture = new();
        CuratedAsset first = fixture.CreateAsset("asset_a", occurrenceCount: 3);
        CuratedAsset second = fixture.CreateAsset("asset_b", occurrenceCount: 2);

        await fixture.Panel.UpdateSelectionAsync([first, second], fixture.SourceMap);
        fixture.Panel.Mode.Should().Be(ComparisonMode.OccurrenceMode);
        fixture.Panel.Slots.Select(s => s.AssignedAsset)
            .Should().AllSatisfy(a => a.Should().BeSameAs(first), "occurrence mode compares one asset");

        await fixture.Panel.ToggleModeCommand.ExecuteAsync(null);

        fixture.Panel.Mode.Should().Be(ComparisonMode.ResolvedMode);
        fixture.Panel.Slots[0].AssignedOccurrence.Should().BeSameAs(first.ResolvedOccurrence);
        fixture.Panel.Slots[1].AssignedOccurrence.Should().BeSameAs(second.ResolvedOccurrence);
        fixture.Panel.Slots[2].IsActive.Should().BeFalse("only two assets are selected");
    }

    [Test]
    public async Task ToggleMode_BackToOccurrenceMode_RestoresTheOccurrenceComparisonOfTheLeadAsset()
    {
        Fixture fixture = new();
        CuratedAsset first = fixture.CreateAsset("asset_a", occurrenceCount: 3);
        CuratedAsset second = fixture.CreateAsset("asset_b", occurrenceCount: 2);

        await fixture.Panel.UpdateSelectionAsync([first, second], fixture.SourceMap);
        await fixture.Panel.ToggleModeCommand.ExecuteAsync(null);
        await fixture.Panel.ToggleModeCommand.ExecuteAsync(null);

        fixture.Panel.Mode.Should().Be(ComparisonMode.OccurrenceMode);
        fixture.Panel.Slots.Select(s => s.AssignedOccurrence).Should().Equal(first.AllOccurrences);
    }

    [Test]
    public async Task ToggleMode_WithNothingSelected_LeavesEverySlotEmpty()
    {
        Fixture fixture = new();

        await fixture.Panel.UpdateSelectionAsync(Array.Empty<CuratedAsset>(), fixture.SourceMap);
        await fixture.Panel.ToggleModeCommand.ExecuteAsync(null);

        fixture.Panel.Mode.Should().Be(ComparisonMode.ResolvedMode);
        fixture.Panel.Slots.Should().OnlyContain(s => !s.IsActive && s.AssignedOccurrence == null);
    }

    // =====================================================================================
    // Cancellation
    // =====================================================================================

    [Test]
    public async Task UpdateSelection_SupersededByALaterSelection_LeavesOnlyTheLatestSelectionInTheSlots()
    {
        Fixture fixture = new();
        CuratedAsset stale = fixture.CreateAsset("stale_asset", occurrenceCount: 3);
        CuratedAsset latest = fixture.CreateAsset("latest_asset", occurrenceCount: 3);

        Task staleUpdate = fixture.Panel.UpdateSelectionAsync([stale], fixture.SourceMap);
        Task latestUpdate = fixture.Panel.UpdateSelectionAsync([latest], fixture.SourceMap);

        await Task.WhenAll(staleUpdate, latestUpdate);

        fixture.Panel.Slots.Select(s => s.AssignedOccurrence).Should().Equal(latest.AllOccurrences);
        fixture.Panel.Slots.Should().OnlyContain(s => !s.IsLoading, "no superseded load may be left spinning");
    }

    [Test]
    public async Task ClearSelection_AfterAFullSelection_EmptiesEverySlotAndDisablesPinning()
    {
        Fixture fixture = new();
        CuratedAsset asset = fixture.CreateAsset("armor01", occurrenceCount: 3);

        await fixture.Panel.UpdateSelectionAsync([asset], fixture.SourceMap);
        await fixture.Panel.ClearSelectionAsync();

        foreach (PreviewSlotViewModel slot in fixture.Panel.Slots)
        {
            slot.IsActive.Should().BeFalse();
            slot.IsLoading.Should().BeFalse();
            slot.AssignedAsset.Should().BeNull();
            slot.AssignedOccurrence.Should().BeNull();
            slot.IsPinned.Should().BeFalse();
            slot.IsPinEnabled.Should().BeFalse();
        }
    }

    [Test]
    public async Task ClearSelection_ThenANewSelection_RepopulatesTheSlotsCleanly()
    {
        Fixture fixture = new();
        CuratedAsset first = fixture.CreateAsset("armor01", occurrenceCount: 2);
        CuratedAsset second = fixture.CreateAsset("weapon02", occurrenceCount: 3);

        await fixture.Panel.UpdateSelectionAsync([first], fixture.SourceMap);
        await fixture.Panel.ClearSelectionAsync();
        await fixture.Panel.UpdateSelectionAsync([second], fixture.SourceMap);

        fixture.Panel.Slots.Select(s => s.AssignedOccurrence).Should().Equal(second.AllOccurrences);
        fixture.Panel.Slots.Should().OnlyContain(s => s.IsActive);
    }

    // =====================================================================================
    // Pinning availability across the lifecycle (PLAN.md:222, "pinning")
    // =====================================================================================

    [Test]
    public async Task SetCanPin_False_DisablesPinningOnEveryActiveSlotAndSurvivesAReassignment()
    {
        Fixture fixture = new();
        CuratedAsset asset = fixture.CreateAsset("armor01", occurrenceCount: 3);

        await fixture.Panel.UpdateSelectionAsync([asset], fixture.SourceMap);
        fixture.Panel.Slots.Should().OnlyContain(s => s.IsPinEnabled);

        fixture.Panel.SetCanPin(false);
        fixture.Panel.Slots.Should().OnlyContain(s => !s.IsPinEnabled);

        // A read-only workspace must stay read-only when the selection changes underneath it.
        CuratedAsset other = fixture.CreateAsset("weapon02", occurrenceCount: 3);
        await fixture.Panel.UpdateSelectionAsync([other], fixture.SourceMap);
        fixture.Panel.Slots.Should().OnlyContain(s => !s.IsPinEnabled);

        fixture.Panel.SetCanPin(true);
        fixture.Panel.Slots.Should().OnlyContain(s => s.IsPinEnabled);
    }

    // =====================================================================================
    // Linked state across slot lifecycle events (PLAN.md:120 rules, PLAN.md:222 clause)
    // =====================================================================================

    [Test]
    public async Task LinkedNavigation_AfterAModeSwitchRebuildsSlotContent_StillPropagatesToTheNewInstances()
    {
        Fixture fixture = new();
        CuratedAsset first = fixture.CreateAsset("asset_a", occurrenceCount: 2);
        CuratedAsset second = fixture.CreateAsset("asset_b", occurrenceCount: 1);

        await fixture.Panel.UpdateSelectionAsync([first, second], fixture.SourceMap);
        await fixture.Panel.ToggleModeCommand.ExecuteAsync(null);

        // The mode switch tore down and reassigned every slot; attach fresh image content to the
        // rebuilt slots and confirm the panel is observing these instances, not the departed ones.
        ImageContentViewModel source = new(ImageResult());
        ImageContentViewModel sibling = new(ImageResult());
        fixture.Panel.Slots[0].Content = source;
        fixture.Panel.Slots[1].Content = sibling;
        fixture.Panel.Slots[0].IsActive = true;
        fixture.Panel.Slots[1].IsActive = true;

        source.ZoomScale = 4.25;
        source.PanX = -3.5;

        sibling.ZoomScale.Should().Be(4.25);
        sibling.PanX.Should().Be(-3.5);
    }

    [Test]
    public async Task LinkedNavigation_DisabledBeforeASelectionChange_StaysDisabledAfterTheSlotsAreRebuilt()
    {
        Fixture fixture = new();
        CuratedAsset asset = fixture.CreateAsset("armor01", occurrenceCount: 3);

        fixture.Panel.LinkNavigationEnabled = false;
        await fixture.Panel.UpdateSelectionAsync([asset], fixture.SourceMap);

        ImageContentViewModel source = new(ImageResult());
        ImageContentViewModel sibling = new(ImageResult());
        fixture.Panel.Slots[0].Content = source;
        fixture.Panel.Slots[1].Content = sibling;

        source.ZoomScale = 7.0;

        fixture.Panel.LinkNavigationEnabled.Should().BeFalse();
        sibling.ZoomScale.Should().NotBe(7.0, "the master toggle is not reset by slot reassignment");
    }

    // =====================================================================================
    // Fixture
    // =====================================================================================

    /// <summary>
    /// A panel over a provider-less <see cref="PreviewEngine"/> plus the source map its assignments
    /// need, and a factory that keeps every synthesised occurrence pointing at a registered source.
    /// </summary>
    private sealed class Fixture
    {
        private readonly Dictionary<Guid, AssetSource> _sources = new();
        private int _sourceOrdinal;

        public Fixture()
        {
            PreviewEngine engine = new(new EmptyDispatcher(), Array.Empty<IPreviewProvider>());
            Panel = new ComparisonPanelViewModel(engine, static _ => Task.CompletedTask);
        }

        public ComparisonPanelViewModel Panel { get; }

        public IReadOnlyDictionary<Guid, AssetSource> SourceMap => _sources;

        public CuratedAsset CreateAsset(string resref, int occurrenceCount)
        {
            AssetIdentity identity = new(resref, 2000);
            List<AssetOccurrence> occurrences = new(occurrenceCount);
            for (int i = 0; i < occurrenceCount; i++)
            {
                AssetSource source = AssetSource.CreateHak(
                    Path.GetFullPath($"lifecycle_{resref}_{i}.hak"),
                    _sourceOrdinal++);
                _sources[source.Id] = source;
                occurrences.Add(new AssetOccurrence(
                    identity: identity,
                    sourceId: source.Id,
                    locator: new HakEntryLocator(i),
                    originalName: $"{resref}.2da",
                    size: 128 + i));
            }

            return new CuratedAsset(
                identity,
                occurrences,
                occurrences.FirstOrDefault(),
                null,
                ResolutionStatus.Resolved,
                true);
        }

        private sealed class EmptyDispatcher : ISourceReaderDispatcher
        {
            public Task<Stream> OpenOccurrenceAsync(
                AssetSource source,
                AssetOccurrence occurrence,
                CancellationToken cancellationToken = default)
                => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        }
    }

    /// <summary>
    /// An image result with no dimensions, so <see cref="ImageContentViewModel"/> leaves its bitmap
    /// null and needs no rendering stack — zoom/pan, the only linked state here, is unaffected.
    /// </summary>
    private static PreviewResult ImageResult() => new(
        Occurrence: new AssetOccurrence(
            identity: new AssetIdentity("linked_image", 3000),
            sourceId: Guid.NewGuid(),
            locator: new HakEntryLocator(0),
            originalName: "linked_image.tga",
            size: 16),
        Family: PreviewFamily.Image,
        IsSuccess: true,
        MetadataText: null,
        RawPayload: null,
        FormattedContent: null,
        ErrorMessage: null,
        Diagnostics: Array.Empty<string>());
}
