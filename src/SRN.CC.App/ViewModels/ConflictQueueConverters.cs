using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SRN.CC.App.ViewModels;

/// <summary>Value converters for the conflict queue view.</summary>
public static class ConflictQueueConverters
{
    /// <summary>Green border for the current winner card, transparent otherwise.</summary>
    public static readonly FuncValueConverter<bool, IBrush> WinnerBorder =
        new(isWinner => isWinner
            ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
            : Brushes.Transparent);
}
