using SRN.CC.Core.Identity;
using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Workspace;

public sealed record ChangedInputReport
{
    public IReadOnlyList<AssetSource> AddedSources { get; }
    public IReadOnlyList<AssetSource> RemovedSources { get; }
    public IReadOnlyList<Guid> AvailabilityTransitions { get; }
    public IReadOnlyList<Guid> ModeChanges { get; }
    public IReadOnlyList<Guid> FingerprintChanges { get; }
    public IReadOnlyList<AssetIdentity> AddedIdentities { get; }
    public IReadOnlyList<AssetIdentity> RemovedIdentities { get; }
    public IReadOnlyList<AssetIdentity> WinnerChanges { get; }
    public IReadOnlyList<AssetIdentity> PinReattachments { get; }
    public IReadOnlyList<AssetIdentity> PinInvalidations { get; }
    public IReadOnlyList<AssetIdentity> SelectionChanges { get; }

    public bool HasChanges =>
        AddedSources.Count > 0 ||
        RemovedSources.Count > 0 ||
        AvailabilityTransitions.Count > 0 ||
        ModeChanges.Count > 0 ||
        FingerprintChanges.Count > 0 ||
        AddedIdentities.Count > 0 ||
        RemovedIdentities.Count > 0 ||
        WinnerChanges.Count > 0 ||
        PinReattachments.Count > 0 ||
        PinInvalidations.Count > 0 ||
        SelectionChanges.Count > 0;

    public ChangedInputReport(
        IReadOnlyList<AssetSource>? addedSources = null,
        IReadOnlyList<AssetSource>? removedSources = null,
        IReadOnlyList<Guid>? availabilityTransitions = null,
        IReadOnlyList<Guid>? modeChanges = null,
        IReadOnlyList<Guid>? fingerprintChanges = null,
        IReadOnlyList<AssetIdentity>? addedIdentities = null,
        IReadOnlyList<AssetIdentity>? removedIdentities = null,
        IReadOnlyList<AssetIdentity>? winnerChanges = null,
        IReadOnlyList<AssetIdentity>? pinReattachments = null,
        IReadOnlyList<AssetIdentity>? pinInvalidations = null,
        IReadOnlyList<AssetIdentity>? selectionChanges = null)
    {
        AddedSources = (addedSources ?? Array.Empty<AssetSource>()).ToList().AsReadOnly();
        RemovedSources = (removedSources ?? Array.Empty<AssetSource>()).ToList().AsReadOnly();
        AvailabilityTransitions = (availabilityTransitions ?? Array.Empty<Guid>()).ToList().AsReadOnly();
        ModeChanges = (modeChanges ?? Array.Empty<Guid>()).ToList().AsReadOnly();
        FingerprintChanges = (fingerprintChanges ?? Array.Empty<Guid>()).ToList().AsReadOnly();
        AddedIdentities = (addedIdentities ?? Array.Empty<AssetIdentity>()).ToList().AsReadOnly();
        RemovedIdentities = (removedIdentities ?? Array.Empty<AssetIdentity>()).ToList().AsReadOnly();
        WinnerChanges = (winnerChanges ?? Array.Empty<AssetIdentity>()).ToList().AsReadOnly();
        PinReattachments = (pinReattachments ?? Array.Empty<AssetIdentity>()).ToList().AsReadOnly();
        PinInvalidations = (pinInvalidations ?? Array.Empty<AssetIdentity>()).ToList().AsReadOnly();
        SelectionChanges = (selectionChanges ?? Array.Empty<AssetIdentity>()).ToList().AsReadOnly();
    }
}
