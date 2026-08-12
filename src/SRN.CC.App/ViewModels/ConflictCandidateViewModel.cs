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
    private readonly Func<AssetOccurrence, Task<bool>> _onMakeWinner;
    private readonly Func<AssetOccurrence, CancellationToken, Task<byte[]?>>? _hashLoader;
    private bool _hashRequested;

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
        Func<AssetOccurrence, Task<bool>> onMakeWinner,
        Func<AssetOccurrence, CancellationToken, Task<byte[]?>>? hashLoader = null)
    {
        Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence));
        _onMakeWinner = onMakeWinner ?? throw new ArgumentNullException(nameof(onMakeWinner));
        _hashLoader = hashLoader;
        SourceLabel = sourceLabel;
        Priority = priority;
        Mode = mode;
        _isCurrentWinner = isCurrentWinner;
        OriginLocator = occurrence.Locator.ToString() ?? "-";
        SizeFormatted = ByteFormatting.Format(occurrence.Size);
        _hashText = occurrence.Sha256 is { Length: > 0 } ? ShortHash(occurrence.Sha256) : "—";
    }

    /// <summary>
    /// Fills in <see cref="HashText"/> the first time the card is shown. Indexed occurrences usually
    /// carry no precomputed <c>Sha256</c>, so without this the card would forever read "—"; here we
    /// stream the payload once (via the injected loader) and cache the short hash. Idempotent and
    /// safe to fire-and-forget: it computes at most once, and swallows read failures back to "—".
    /// </summary>
    public async Task EnsureHashLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_hashRequested)
        {
            return;
        }
        _hashRequested = true;

        // Already shown from a precomputed hash, or no way to compute one.
        if (Occurrence.Sha256 is { Length: > 0 } || _hashLoader is null)
        {
            return;
        }

        HashText = "computing…";
        try
        {
            byte[]? hash = await _hashLoader(Occurrence, cancellationToken).ConfigureAwait(true);
            HashText = hash is { Length: > 0 } ? ShortHash(hash) : "—";
        }
        catch (OperationCanceledException)
        {
            // Let a fresh attempt run next time the card is shown.
            _hashRequested = false;
            HashText = "—";
        }
        catch
        {
            HashText = "—";
        }
    }

    private static string ShortHash(byte[] hash) =>
        Convert.ToHexString(hash).ToLowerInvariant()[..Math.Min(8, hash.Length * 2)];

    [RelayCommand]
    private async Task MakeWinner() => await _onMakeWinner(Occurrence).ConfigureAwait(true);
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
