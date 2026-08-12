using Avalonia.Data.Converters;

namespace SRN.CC.App.ViewModels;

/// <summary>Value converters for the asset table view.</summary>
public static class AssetTableConverters
{
    /// <summary>Down arrow for descending, up arrow for ascending.</summary>
    public static readonly FuncValueConverter<bool, string> SortDirectionGlyph =
        new(descending => descending ? "▼" : "▲");
}
