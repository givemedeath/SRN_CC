using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.App.Views;

namespace SRN.CC.Tests.UI;

/// <summary>
/// The data-operation half of <c>PLAN.md:222</c> at full scale: with 187 943 rows loaded, sorting,
/// filtering and bulk selection must all stay virtualized, and inclusion state must survive a
/// filter change.
/// </summary>
/// <remarks>
/// <para>
/// <c>TableViewVirtualizationTests</c> already proves that <i>scrolling</i> and <i>resizing</i> keep
/// the realized-container count proportional to the viewport. That is the navigation axis. This
/// fixture covers the axis it does not touch: operations that rewrite or re-order the entire
/// <c>ItemsSource</c>. Those are the operations most likely to silently defeat virtualization,
/// because each one hands the control a brand-new collection of ~188 000 items.
/// </para>
/// <para>
/// <b>How realization is measured.</b> Every <see cref="TableViewRow"/> in the window's visual tree
/// is a realized container. <see cref="RealizationProbe"/> samples that count after each operation
/// and keeps the maximum, so the assertion is against a high-water mark across the whole sequence
/// rather than against whatever the count happens to be at the end. The bound,
/// <see cref="MaximumRealizedRows"/>, matches the sibling fixture: a 768–900 px viewport of
/// ~24 px rows realizes a few dozen containers, so 200 leaves generous headroom for recycling
/// buffers while still being three orders of magnitude below the row count.
/// </para>
/// <para>
/// <b>Runtime.</b> This test stays inside the ordinary headless budget (a few seconds, dominated by
/// constructing the 187 943 view models) and is therefore deliberately <i>not</i> marked
/// <c>[Category("Performance")]</c> — virtualization is a correctness property, and excluding it
/// from the default run would defeat the point.
/// </para>
/// </remarks>
[TestFixture]
public class TableViewLargeDatasetTests
{
    private const int RowCount = 187_943;

    /// <summary>Same ceiling as <c>TableViewVirtualizationTests</c>, for the same reason.</summary>
    private const int MaximumRealizedRows = 200;

    [AvaloniaTest]
    public void TableView_With187943Rows_SortFilterAndBulkSelectionAllStayVirtualized()
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        var vm = new MainWindowViewModel();
        vm.Items.Count.Should().Be(RowCount);

        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1024,
            Height = 768
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            TableView table = FindTable(window);
            RealizationProbe probe = new(window);

            probe.Sample("initial layout").Should().BeGreaterThan(
                0, "the viewport must realize something to be virtualizing at all");

            // ---- Sort: hand the control a fully re-ordered collection of every row. ------------
            List<AssetRowItem> sorted = vm.SortByResref().ToList();
            sorted.Should().HaveCount(RowCount, "sorting is over the whole set, not the realized window");
            sorted[0].Resref.Should().Be("resref_000000");
            sorted[^1].Resref.Should().Be("resref_187942");

            var descending = new ObservableCollection<AssetRowViewModel>(
                vm.AssetTable.FilteredRows.OrderByDescending(r => r.Resref, StringComparer.Ordinal));
            vm.AssetTable.FilteredRows = descending;
            window.UpdateLayout();

            table.ContainerFromIndex(0).Should().BeOfType<TableViewRow>();
            probe.Sample("after descending sort");

            // ---- Filter: narrow to a subset, then widen back to the whole set. -----------------
            vm.AssetTable.SearchText = "resref_1879";
            window.UpdateLayout();
            int narrowed = vm.AssetTable.FilteredRows.Count;
            narrowed.Should().BeInRange(1, RowCount - 1, "the filter must actually narrow the set");
            probe.Sample("after narrowing filter");

            vm.AssetTable.SearchText = string.Empty;
            window.UpdateLayout();
            vm.AssetTable.FilteredRows.Count.Should().Be(RowCount);
            probe.Sample("after clearing the filter");

            // ---- Bulk selection over all 187 943 filtered rows. --------------------------------
            vm.AssetTable.ExcludeAllFilteredCommand.Execute(null);
            window.UpdateLayout();
            vm.AssetTable.SelectedAssetCount.Should().Be(0);
            probe.Sample("after excluding every row");

            vm.AssetTable.IncludeAllFilteredCommand.Execute(null);
            window.UpdateLayout();
            vm.AssetTable.SelectedAssetCount.Should().Be(RowCount);
            probe.Sample("after including every row");

            // ---- The realized set never approached the data set. -------------------------------
            probe.HighWaterMark.Should().BeLessThan(
                MaximumRealizedRows,
                $"the realized-row high-water mark across {probe.SampleCount} operations "
                + $"(peak at '{probe.HighWaterOperation}') must stay proportional to the viewport, "
                + $"not to the {RowCount:N0} rows loaded");

            elapsed.Stop();
            TestContext.Out.WriteLine(
                $"realized-row high-water mark: {probe.HighWaterMark} (at '{probe.HighWaterOperation}'), "
                + $"asserted bound < {MaximumRealizedRows}, rows {RowCount:N0}, "
                + $"wall clock {elapsed.Elapsed.TotalSeconds:0.00}s");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void TableView_With187943Rows_InclusionStateSurvivesAFilterChange()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1024,
            Height = 768
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            RealizationProbe probe = new(window);

            // Narrow to a small, exactly-known subset and exclude precisely that subset.
            vm.AssetTable.SearchText = "resref_18794";
            window.UpdateLayout();

            string[] excludedResrefs = vm.AssetTable.FilteredRows.Select(r => r.Resref).ToArray();
            excludedResrefs.Should().NotBeEmpty().And.HaveCountLessThan(RowCount);

            vm.AssetTable.ExcludeAllFilteredCommand.Execute(null);
            window.UpdateLayout();
            probe.Sample("after excluding the filtered subset");

            int expectedSelected = RowCount - excludedResrefs.Length;
            vm.AssetTable.SelectedAssetCount.Should().Be(expectedSelected);

            // Widen the filter all the way back out: the exclusions must still be there.
            vm.AssetTable.SearchText = string.Empty;
            window.UpdateLayout();
            probe.Sample("after clearing the filter");

            vm.AssetTable.FilteredRows.Count.Should().Be(RowCount);
            vm.AssetTable.SelectedAssetCount.Should().Be(
                expectedSelected,
                "a filter change re-projects rows; it must never re-select or clear them");

            var excluded = excludedResrefs.ToHashSet(StringComparer.Ordinal);
            vm.AssetTable.FilteredRows
                .Where(r => excluded.Contains(r.Resref))
                .Should().OnlyContain(r => !r.IsSelected, "every row excluded while filtered stays excluded");
            vm.AssetTable.FilteredRows
                .Where(r => !excluded.Contains(r.Resref))
                .Should().OnlyContain(r => r.IsSelected, "no untouched row may change state");

            // Narrowing to the complementary view must still show the same inclusion state.
            vm.AssetTable.SelectedFilterMode = ConflictFilterMode.Unselected;
            window.UpdateLayout();
            probe.Sample("after switching to the Unselected filter mode");

            vm.AssetTable.FilteredRows.Count.Should().Be(excludedResrefs.Length);
            vm.AssetTable.FilteredRows.Select(r => r.Resref).Should().BeEquivalentTo(excludedResrefs);

            probe.HighWaterMark.Should().BeLessThan(
                MaximumRealizedRows,
                "selection bookkeeping must not force the full row set into the visual tree");
        }
        finally
        {
            window.Close();
        }
    }

    private static TableView FindTable(Window window) =>
        window.FindControl<TableView>("MainTableView")
        ?? window.GetVisualDescendants().OfType<TableView>().FirstOrDefault()
        ?? throw new InvalidOperationException("MainWindow does not host a TableView.");

    /// <summary>
    /// Samples the number of realized <see cref="TableViewRow"/> containers after each operation and
    /// remembers the maximum, so a transient realization spike in the middle of the sequence is
    /// caught rather than being hidden by whatever the final steady state happens to be.
    /// </summary>
    private sealed class RealizationProbe(Window window)
    {
        public int HighWaterMark { get; private set; }

        public string HighWaterOperation { get; private set; } = "none";

        public int SampleCount { get; private set; }

        public int Sample(string operation)
        {
            int realized = window.GetVisualDescendants().OfType<TableViewRow>().Count();
            SampleCount++;

            if (realized > HighWaterMark)
            {
                HighWaterMark = realized;
                HighWaterOperation = operation;
            }

            return realized;
        }
    }
}
