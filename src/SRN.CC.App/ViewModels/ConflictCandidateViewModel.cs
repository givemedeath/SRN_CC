using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Sources;

namespace SRN.CC.App.ViewModels;

/// <summary>
/// One candidate occurrence in the conflict queue: the source it came from, its size and hash,
/// whether it is the current winner, and a one-click action to pin it as the winner.
/// </summary>
public partial class ConflictCandidateViewModel : ObservableObject
{
    private readonly Func<AssetOccurrence, Task> _onMakeWinner;

    public AssetOccurrence Occurrence { get; }
    public string SourceLabel { get; }
    public int Priority { get; }
    public SourceMode Mode { get; }

    /// <summary>Empty for Full sources; "Reference"/"Hidden" otherwise (Hidden never appears here).</summary>
    public string ModeBadge => Mode == SourceMode.Full ? string.Empty : Mode.ToString();

    public string OriginLocator { get; }
    public string SizeFormatted { get; }

    [ObservableProperty]
    private string _hashText;

    [ObservableProperty]
    private bool _isCurrentWinner;

    public ConflictCandidateViewModel(
        AssetOccurrence occurrence,
        string sourceLabel,
        int priority,
        SourceMode mode,
        bool isCurrentWinner,
        Func<AssetOccurrence, Task> onMakeWinner)
    {
        Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence));
        _onMakeWinner = onMakeWinner ?? throw new ArgumentNullException(nameof(onMakeWinner));
        SourceLabel = sourceLabel;
        Priority = priority;
        Mode = mode;
        _isCurrentWinner = isCurrentWinner;
        OriginLocator = occurrence.Locator.ToString() ?? "-";
        SizeFormatted = ByteFormatting.Format(occurrence.Size);
        _hashText = occurrence.Sha256 is { Length: > 0 }
            ? Convert.ToHexString(occurrence.Sha256).ToLowerInvariant()[..Math.Min(8, occurrence.Sha256.Length * 2)]
            : "—";
    }

    [RelayCommand]
    private Task MakeWinner() => _onMakeWinner(Occurrence);
}

/// <summary>Shared human-readable byte-size formatting for asset/candidate rows.</summary>
public static class ByteFormatting
{
    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1048576.0:F1} MB";
    }
}
