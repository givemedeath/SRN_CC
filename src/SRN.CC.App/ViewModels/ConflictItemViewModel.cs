using System.Collections.ObjectModel;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Resolution;

namespace SRN.CC.App.ViewModels;

/// <summary>
/// One conflicted identity in the queue: its candidate occurrences side by side, its diagnostic
/// badges, and whether the operator has resolved it with a valid pin.
/// </summary>
public sealed class ConflictItemViewModel
{
    public AssetIdentity Identity { get; }
    public string Resref => Identity.Resref;
    public ushort ResourceType => Identity.ResourceType;
    public string ResourceTypeName { get; }
    public string DiagnosticBadges { get; }

    /// <summary>
    /// True when the operator has made an explicit, valid decision: a pin that is not flagged
    /// invalid. This is what the queue's "N of M resolved" counter tallies.
    /// </summary>
    public bool IsResolved { get; }

    public ObservableCollection<ConflictCandidateViewModel> Candidates { get; }

    public ConflictItemViewModel(
        CuratedAsset asset,
        string resourceTypeName,
        IEnumerable<ConflictCandidateViewModel> candidates)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Identity = asset.Identity;
        ResourceTypeName = resourceTypeName;
        IsResolved = asset.Pin is not null && !asset.HasInvalidPin;
        Candidates = new ObservableCollection<ConflictCandidateViewModel>(candidates);

        List<string> badges = new();
        if (asset.HasCrossSourceCollision) badges.Add("Collision");
        if (asset.HasSameSourceDuplicate) badges.Add("Duplicate");
        if (asset.HasDifferingPayloads) badges.Add("Conflict");
        if (asset.HasInvalidPin) badges.Add("InvalidPin");
        if (asset.HasUnreadableOccurrence) badges.Add("Unreadable");
        DiagnosticBadges = badges.Count > 0 ? string.Join(" | ", badges) : "Unresolved";
    }
}
