using CommunityToolkit.Mvvm.ComponentModel;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Resolution;

namespace SRN.CC.App.ViewModels;

public partial class AssetRowViewModel : ObservableObject
{
    public CuratedAsset CuratedAsset { get; }
    public AssetIdentity Identity => CuratedAsset.Identity;

    [ObservableProperty]
    private bool _isSelected;

    public string Resref => CuratedAsset.Identity.Resref;
    public ushort ResourceType => CuratedAsset.Identity.ResourceType;
    public string ResourceTypeName { get; }
    public string StatusText => CuratedAsset.Status.ToString();

    public string WinnerSourceLabel { get; }
    public string OriginLocator { get; }
    public string HashText { get; }
    public long SizeBytes { get; }
    public string SizeFormatted { get; }
    public string DiagnosticBadges { get; }
    public bool IsPinned => CuratedAsset.Pin != null;

    public AssetRowViewModel(CuratedAsset asset, string resourceTypeName, string winnerSourceLabel)
    {
        CuratedAsset = asset ?? throw new ArgumentNullException(nameof(asset));
        _isSelected = asset.IsSelected;
        ResourceTypeName = resourceTypeName;
        WinnerSourceLabel = winnerSourceLabel;

        var winner = asset.ResolvedOccurrence;
        if (winner != null)
        {
            OriginLocator = winner.Locator.ToString();
            SizeBytes = winner.Size;
            SizeFormatted = FormatBytes(winner.Size);
            string sha = winner.Sha256 != null ? Convert.ToHexString(winner.Sha256).ToLowerInvariant() : string.Empty;
            HashText = string.IsNullOrEmpty(sha) ? "Uncomputed" : sha[..Math.Min(8, sha.Length)];
        }
        else
        {
            OriginLocator = "-";
            SizeBytes = 0;
            SizeFormatted = "-";
            HashText = "-";
        }

        List<string> badges = new();
        if (asset.HasCrossSourceCollision) badges.Add("Collision");
        if (asset.HasSameSourceDuplicate) badges.Add("Duplicate");
        if (asset.HasDifferingPayloads) badges.Add("Conflict");
        if (asset.HasCaseOnlyNamingDifference) badges.Add("CaseDiff");
        if (asset.HasInvalidPin) badges.Add("InvalidPin");
        if (asset.HasUnreadableOccurrence) badges.Add("Unreadable");

        DiagnosticBadges = badges.Count > 0 ? string.Join(" | ", badges) : "OK";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1048576.0:F1} MB";
    }
}
