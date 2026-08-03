using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;

namespace SRN.CC.Core.Resolution;

public sealed record CuratedAsset
{
    private readonly byte[]? _resolvedSha256;

    public AssetIdentity Identity { get; }
    public IReadOnlyList<AssetOccurrence> AllOccurrences { get; }
    public AssetOccurrence? ResolvedOccurrence { get; }
    public WinnerPin? Pin { get; }
    public ResolutionStatus Status { get; }
    public bool IsSelected { get; }
    public byte[]? ResolvedSha256 => _resolvedSha256?.ToArray();
    public ReadOnlyMemory<byte>? ResolvedSha256Memory => _resolvedSha256;

    public bool HasCrossSourceCollision { get; }
    public bool HasSameSourceDuplicate { get; }
    public bool HasDifferingPayloads { get; }
    public bool HasCaseOnlyNamingDifference { get; }
    public bool HasUnreadableOccurrence { get; }
    public bool HasInvalidPin { get; }

    public CuratedAsset(
        AssetIdentity identity,
        IReadOnlyList<AssetOccurrence> allOccurrences,
        AssetOccurrence? resolvedOccurrence,
        WinnerPin? pin,
        ResolutionStatus status,
        bool isSelected,
        byte[]? resolvedSha256 = null,
        bool hasCrossSourceCollision = false,
        bool hasSameSourceDuplicate = false,
        bool hasDifferingPayloads = false,
        bool hasCaseOnlyNamingDifference = false,
        bool hasUnreadableOccurrence = false,
        bool hasInvalidPin = false)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(allOccurrences);

        Identity = identity;
        AllOccurrences = allOccurrences.ToList().AsReadOnly();
        ResolvedOccurrence = resolvedOccurrence;
        Pin = pin;
        Status = status;
        IsSelected = isSelected;
        _resolvedSha256 = resolvedSha256?.ToArray();
        HasCrossSourceCollision = hasCrossSourceCollision;
        HasSameSourceDuplicate = hasSameSourceDuplicate;
        HasDifferingPayloads = hasDifferingPayloads;
        HasCaseOnlyNamingDifference = hasCaseOnlyNamingDifference;
        HasUnreadableOccurrence = hasUnreadableOccurrence;
        HasInvalidPin = hasInvalidPin;
    }
}
