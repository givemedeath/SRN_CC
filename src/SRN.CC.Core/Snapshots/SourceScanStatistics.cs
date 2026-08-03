namespace SRN.CC.Core.Snapshots;

public sealed record SourceScanStatistics
{
    public long TotalFilesScanned { get; }
    public long TotalLogicalBytes { get; }
    public TimeSpan Duration { get; }

    public SourceScanStatistics(long totalFilesScanned, long totalLogicalBytes, TimeSpan duration)
    {
        TotalFilesScanned = totalFilesScanned;
        TotalLogicalBytes = totalLogicalBytes;
        Duration = duration;
    }
}
