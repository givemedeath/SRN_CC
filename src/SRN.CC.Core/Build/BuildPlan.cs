using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Build;

public sealed record BuildItem(
    AssetIdentity Identity,
    Guid SourceId,
    OccurrenceLocator Locator,
    long ExpectedSizeBytes,
    string? ExpectedSha256Hex,
    bool IsPinned
);

public sealed class BuildPlan
{
    public required string DestinationHakPath { get; init; }
    public required string DestinationManifestPath { get; init; }
    public required IReadOnlyList<AssetSource> FrozenSources { get; init; }
    public required IReadOnlyList<BuildItem> Items { get; init; }
    public required DateTime CreatedUtc { get; init; }

    public long TotalEstimatedPayloadSize => Items.Sum(i => i.ExpectedSizeBytes);
}
