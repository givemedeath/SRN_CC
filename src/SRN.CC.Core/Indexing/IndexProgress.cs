namespace SRN.CC.Core.Indexing;

public sealed record IndexProgress
{
    public Guid SourceId { get; }
    public IndexPhase Phase { get; }
    public long CompletedCount { get; }
    public long? TotalCount { get; }
    public string? CurrentItem { get; }

    public IndexProgress(Guid sourceId, IndexPhase phase, long completedCount, long? totalCount = null, string? currentItem = null)
    {
        SourceId = sourceId;
        Phase = phase;
        CompletedCount = completedCount;
        TotalCount = totalCount;
        CurrentItem = currentItem;
    }
}
