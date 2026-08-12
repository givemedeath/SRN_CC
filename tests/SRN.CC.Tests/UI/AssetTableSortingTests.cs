using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Resolution;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Tests.UI;

/// <summary>
/// The asset grid's column sorting: ordering by a chosen column, flipping direction, and composing
/// with the existing filters rather than replacing them.
/// </summary>
[TestFixture]
public class AssetTableSortingTests
{
    private const ushort Tga = 3;
    private const ushort Mdl = 2002;

    [Test]
    public void DefaultIsUnsorted_PreservingLoadOrder()
    {
        AssetTableViewModel table = Loaded();

        table.SortColumn.Should().Be(AssetSortColumn.None);
        table.FilteredRows.Select(r => r.Resref).Should().Equal("delta", "alpha", "charlie", "bravo");
    }

    [Test]
    public void SortByResref_Ascending_ThenDescending()
    {
        AssetTableViewModel table = Loaded();

        table.SortColumn = AssetSortColumn.Resref;
        table.FilteredRows.Select(r => r.Resref).Should().Equal("alpha", "bravo", "charlie", "delta");

        table.ToggleSortDirectionCommand.Execute(null);
        table.FilteredRows.Select(r => r.Resref).Should().Equal("delta", "charlie", "bravo", "alpha");
    }

    [Test]
    public void SortBySize_IsNumeric()
    {
        AssetTableViewModel table = Loaded();

        table.SortColumn = AssetSortColumn.Size;
        table.FilteredRows.Select(r => r.SizeBytes).Should().BeInAscendingOrder();
    }

    [Test]
    public void Sort_ComposesWithTypeFilter()
    {
        AssetTableViewModel table = Loaded();

        table.SelectedResourceType = "TGA";
        table.SortColumn = AssetSortColumn.Resref;

        table.FilteredRows.Select(r => r.Resref).Should().Equal("alpha", "delta");
    }

    private static AssetTableViewModel Loaded()
    {
        AssetTableViewModel table = new(new ResourceTypeRegistry());
        table.LoadAssets(
            new[]
            {
                Asset("delta", Tga, 40),
                Asset("alpha", Tga, 10),
                Asset("charlie", Mdl, 30),
                Asset("bravo", Mdl, 20),
            },
            new Dictionary<Guid, string>());
        return table;
    }

    private static CuratedAsset Asset(string resref, ushort resourceType, long size)
    {
        AssetIdentity identity = new(resref, resourceType);
        AssetOccurrence occurrence = new(identity, Guid.NewGuid(), new HakEntryLocator(0), resref, size);
        return new CuratedAsset(identity, new[] { occurrence }, occurrence, null, ResolutionStatus.Resolved, true);
    }
}
