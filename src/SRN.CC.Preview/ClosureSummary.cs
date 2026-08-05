using SRN.CC.Core.Identity;

namespace SRN.CC.Preview;

/// <summary>
/// Summary of a dependency closure operation, grouping resolved and unresolved assets with diagnostic information.
/// </summary>
public sealed class ClosureSummary
{
    /// <summary>
    /// Asset identities that were successfully resolved during traversal.
    /// </summary>
    public IReadOnlySet<AssetIdentity> Resolved { get; }

    /// <summary>
    /// Unresolved dependencies grouped by family, source path, and originating parent.
    /// Sorted deterministically for stable UI presentation.
    /// </summary>
    public IReadOnlyList<UnresolvedDependencyGroup> UnresolvedGroups { get; }

    /// <summary>
    /// Total size in bytes of all resolved dependencies.
    /// </summary>
    public long TotalBytes { get; }

    /// <summary>
    /// Maximum depth reached during traversal.
    /// </summary>
    public int MaxDepth { get; }

    /// <summary>
    /// Number of duplicate entries suppressed during traversal.
    /// </summary>
    public int DuplicatesSuppressed { get; }

    /// <summary>
    /// The budget that stopped traversal, or <see cref="TraversalLimit.None"/> if the closure completed.
    /// </summary>
    public TraversalLimit LimitHit { get; }

    /// <summary>
    /// True when a budget stopped traversal, so the closure is incomplete.
    /// </summary>
    public bool IsTruncated => LimitHit != TraversalLimit.None;

    public ClosureSummary(
        IReadOnlySet<AssetIdentity> resolved,
        IReadOnlyList<UnresolvedDependencyGroup> unresolvedGroups,
        long totalBytes,
        int maxDepth,
        int duplicatesSuppressed,
        TraversalLimit limitHit = TraversalLimit.None)
    {
        Resolved = resolved ?? throw new ArgumentNullException(nameof(resolved));
        UnresolvedGroups = unresolvedGroups ?? throw new ArgumentNullException(nameof(unresolvedGroups));
        TotalBytes = totalBytes;
        MaxDepth = maxDepth;
        DuplicatesSuppressed = duplicatesSuppressed;
        LimitHit = limitHit;

        if (totalBytes < 0)
        {
            throw new ArgumentException("Total bytes must be non-negative.", nameof(totalBytes));
        }

        if (maxDepth < 0)
        {
            throw new ArgumentException("Max depth must be non-negative.", nameof(maxDepth));
        }

        if (duplicatesSuppressed < 0)
        {
            throw new ArgumentException("Duplicates suppressed must be non-negative.", nameof(duplicatesSuppressed));
        }
    }
}
