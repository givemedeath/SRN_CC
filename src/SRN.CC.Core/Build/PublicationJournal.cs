namespace SRN.CC.Core.Build;

public enum PublicationState
{
    Prepared = 0,
    BackedUp = 1,
    HakReplaced = 2,
    ManifestReplaced = 3,
    Committed = 4
}

public sealed class PublicationJournal
{
    public required string DestinationHakPath { get; init; }
    public required string DestinationManifestPath { get; init; }
    public required string TempHakPath { get; init; }
    public required string TempManifestPath { get; init; }
    public required string HakBackupPath { get; init; }
    public required string ManifestBackupPath { get; init; }
    public required PublicationState State { get; set; }
    public required DateTime CreatedUtc { get; init; }
    public DateTime LastUpdatedUtc { get; set; }
}
