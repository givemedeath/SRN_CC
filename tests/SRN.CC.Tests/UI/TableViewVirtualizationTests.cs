using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.App.Views;

namespace SRN.CC.Tests.UI;

[TestFixture]
public class TableViewVirtualizationTests
{
    private const int RowCount = 187_943;
    private const int MaximumRealizedRows = 200;

    [AvaloniaTest]
    public void TableView_With187943Rows_MustVirtualizeScrollSelectAndResize()
    {
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

            TableView table = window.FindControl<TableView>("MainTableView")
                ?? window.GetVisualDescendants().OfType<TableView>().FirstOrDefault()!;

            table.Should().NotBeNull();
            AssertBoundedRealization(window, 0);

            int middle = RowCount / 2;
            table.ScrollIntoView(middle);
            window.UpdateLayout();
            table.ContainerFromIndex(middle).Should().BeOfType<TableViewRow>();
            AssertBoundedRealization(window, middle);

            int last = RowCount - 1;
            table.ScrollIntoView(last);
            window.UpdateLayout();
            table.ContainerFromIndex(last).Should().BeOfType<TableViewRow>();
            AssertBoundedRealization(window, last);

            table.SelectedItems!.Add(vm.Items[0]);
            table.SelectedItems.Add(vm.Items[middle]);
            table.SelectedItems.Add(vm.Items[last]);
            table.SelectedItems.Count.Should().Be(3);

            window.Width = 1280;
            window.Height = 900;
            window.UpdateLayout();
            AssertBoundedRealization(window, last);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertBoundedRealization(Window window, int expectedVisibleIndex)
    {
        List<TableViewRow> rows = window.GetVisualDescendants().OfType<TableViewRow>().ToList();
        rows.Should().NotBeEmpty($"scrolling to row {expectedVisibleIndex:N0} must realize a viewport");
        rows.Count.Should().BeLessThan(
            MaximumRealizedRows,
            $"realized rows near index {expectedVisibleIndex:N0} must remain proportional to the viewport");
    }
}
