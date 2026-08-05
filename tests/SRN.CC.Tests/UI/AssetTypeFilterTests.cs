using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Tests.UI;

/// <summary>
/// The asset grid's resource-type filter: narrowing the grid to one type, and composing with the
/// search box and the conflict-state filter rather than replacing either.
/// </summary>
[TestFixture]
public class AssetTypeFilterTests
{
    private const ushort Tga = 3;
    private const ushort TwoDa = 2017;
    private const ushort Mdl = 2002;

    [Test]
    public void ANewTable_OffersOnlyTheAllOption()
    {
        AssetTableViewModel table = NewTable();

        table.ResourceTypes.Should().Equal(AssetTableViewModel.AllResourceTypes);
        table.SelectedResourceType.Should().Be(AssetTableViewModel.AllResourceTypes);
    }

    [Test]
    public void LoadingAssets_OffersAllPlusEveryTypePresentOrdered()
    {
        AssetTableViewModel table = NewTable();

        table.LoadAssets(
            new[] { Asset("tower", Mdl), Asset("crate", Tga), Asset("appearance", TwoDa), Asset("barrel", Tga) },
            new Dictionary<Guid, string>());

        table.ResourceTypes.Should().Equal(
            AssetTableViewModel.AllResourceTypes, "2DA", "MDL", "TGA");
    }

    [Test]
    public void TheAllOption_FiltersNothingOut()
    {
        AssetTableViewModel table = LoadedTable();

        table.SelectedResourceType = AssetTableViewModel.AllResourceTypes;

        table.FilteredRows.Should().HaveCount(4);
    }

    [Test]
    public void SelectingAType_ShowsOnlyThatType()
    {
        AssetTableViewModel table = LoadedTable();

        table.SelectedResourceType = "TGA";

        table.FilteredRows.Should().HaveCount(2);
        table.FilteredRows.Select(r => r.ResourceTypeName).Should().AllBe("TGA");
    }

    [Test]
    public void SelectingAType_ComposesWithTheSearchBox()
    {
        AssetTableViewModel table = LoadedTable();

        table.SelectedResourceType = "TGA";
        table.SearchText = "crate";

        table.FilteredRows.Should().ContainSingle()
            .Which.Resref.Should().Be("crate");
    }

    [Test]
    public void ReloadingAWorkspaceThatStillHasTheType_KeepsTheSelection()
    {
        AssetTableViewModel table = LoadedTable();
        table.SelectedResourceType = "TGA";

        table.LoadAssets(new[] { Asset("crate", Tga), Asset("tower", Mdl) }, new Dictionary<Guid, string>());

        table.SelectedResourceType.Should().Be(
            "TGA", "a reload should not silently widen a filter the operator set");
        table.FilteredRows.Should().ContainSingle();
    }

    [Test]
    public void ReloadingAWorkspaceWithoutTheType_FallsBackToAll()
    {
        AssetTableViewModel table = LoadedTable();
        table.SelectedResourceType = "TGA";

        table.LoadAssets(new[] { Asset("tower", Mdl) }, new Dictionary<Guid, string>());

        table.SelectedResourceType.Should().Be(
            AssetTableViewModel.AllResourceTypes,
            "keeping a type that no longer occurs would leave the grid empty with no visible reason");
        table.FilteredRows.Should().ContainSingle();
    }

    [Test]
    public void ReloadingAWorkspaceWithTheSameTypes_LeavesTheBoundCollectionUntouched()
    {
        AssetTableViewModel table = LoadedTable();
        table.SelectedResourceType = "TGA";

        int changes = 0;
        table.ResourceTypes.CollectionChanged += (_, _) => changes++;

        // A rescan that found the same types. Clearing and repopulating here would make a bound
        // ComboBox reset its selection, and that reset writes straight back through the binding.
        table.LoadAssets(
            new[] { Asset("crate", Tga), Asset("barrel", Tga), Asset("tower", Mdl), Asset("appearance", TwoDa) },
            new Dictionary<Guid, string>());

        changes.Should().Be(0, "the set of types did not change, so the picker must not be disturbed");
        table.SelectedResourceType.Should().Be("TGA");
        table.FilteredRows.Should().HaveCount(2);
    }

    [Test]
    public void RebuildingTheTypeList_NeverLeavesTheGridFilteredToNothing()
    {
        AssetTableViewModel table = LoadedTable();
        table.SelectedResourceType = "TGA";

        // Every intermediate state the rebuild passes through, as a bound view would observe it.
        List<int> observedCounts = new();
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssetTableViewModel.FilteredRows))
            {
                observedCounts.Add(table.FilteredRows.Count);
            }
        };

        table.LoadAssets(new[] { Asset("crate", Tga), Asset("tower", Mdl) }, new Dictionary<Guid, string>());

        observedCounts.Should().NotBeEmpty();
        observedCounts.Should().NotContain(
            0, "a rebuild must not publish an empty grid on its way to the correct one");
    }

    private static AssetTableViewModel NewTable() => new(new ResourceTypeRegistry());

    private static AssetTableViewModel LoadedTable()
    {
        AssetTableViewModel table = NewTable();
        table.LoadAssets(
            new[] { Asset("crate", Tga), Asset("barrel", Tga), Asset("tower", Mdl), Asset("appearance", TwoDa) },
            new Dictionary<Guid, string>());
        return table;
    }

    private static CuratedAsset Asset(string resref, ushort resourceType)
    {
        AssetIdentity identity = new(resref, resourceType);
        AssetOccurrence occurrence = new(
            identity: identity,
            sourceId: Guid.NewGuid(),
            locator: new HakEntryLocator(0),
            originalName: resref,
            size: 32);

        return new CuratedAsset(
            identity,
            new[] { occurrence },
            occurrence,
            null,
            ResolutionStatus.Resolved,
            true);
    }
}
