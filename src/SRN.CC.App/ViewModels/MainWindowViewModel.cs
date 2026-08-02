using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SRN.CC.App.ViewModels;

public record AssetRowItem(int Id, string Resref, string ResourceType, string SourceHak, long SizeBytes);

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "SRN.CC Asset Curator — Milestone 1 Validation Shell";

    [ObservableProperty]
    private ObservableCollection<AssetRowItem> _items = new();

    public MainWindowViewModel()
    {
        // Populate synthetic items to simulate the 187,943 rows corpus scale
        GenerateSyntheticRows(187943);
    }

    public void GenerateSyntheticRows(int count)
    {
        var list = new List<AssetRowItem>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new AssetRowItem(
                i + 1,
                $"resref_{i:D6}",
                (i % 10) switch
                {
                    0 => "2DA (2001)",
                    1 => "TLK (2002)",
                    2 => "TXT (2003)",
                    3 => "TGA (2004)",
                    4 => "PLT (2005)",
                    5 => "MDL (2008)",
                    6 => "NSS (2009)",
                    7 => "NCU (2010)",
                    8 => "GFF (2011)",
                    _ => "GENERIC (2000)"
                },
                $"hak_source_{(i % 117):D3}.hak",
                1024 + (i * 37) % 500000
            ));
        }
        Items = new ObservableCollection<AssetRowItem>(list);
    }

    public IReadOnlyList<AssetRowItem> SortByResref() =>
        Items.OrderBy(item => item.Resref, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<AssetRowItem> FilterByResourceType(string resourceType) =>
        Items.Where(item => string.Equals(item.ResourceType, resourceType, StringComparison.Ordinal)).ToArray();
}
