using NUnit.Framework;
using FluentAssertions;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using Avalonia.Controls;
using SRN.CC.App.Views;
using SRN.CC.App.ViewModels;

namespace SRN.CC.Tests.UI;

[TestFixture]
public class TableViewVirtualizationTests
{
    [AvaloniaTest]
    public void TableView_With187943Rows_MustVirtualizeRealizedContainers()
    {
        var vm = new MainWindowViewModel();
        vm.Items.Count.Should().Be(187943);

        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1024,
            Height = 768
        };

        window.Show();

        // Count realized visual child controls of type TableView or Row containers
        var visualDescendants = window.GetVisualDescendants().ToList();
        
        // Assert total visual elements is far smaller than 187,943 items (proving virtualization)
        visualDescendants.Count.Should().BeLessThan(2000, "virtualized view must not materialize 187,943 visual elements");
    }
}
