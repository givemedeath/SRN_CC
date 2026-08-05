using SRN.CC.Core.Identity;

namespace SRN.CC.Preview;

/// <summary>
/// Result of a dependency traversal operation.
/// </summary>
/// <remarks>
/// Contains both resolved and unresolved dependencies discovered during transitive closure,
/// along with traversal metrics and diagnostic information.
/// </remarks>
public sealed class TraversalResult
{
    /// <summary>
    /// Asset identities that were successfully resolved during traversal.
    /// </summary>
    public IReadOnlySet<AssetIdentity> Resolved { get; }

    /// <summary>
    /// Asset identities that could not be resolved and their reasons.
    /// Maps AssetIdentity to a diagnostic message describing why resolution failed.
    /// </summary>
    public IReadOnlyDictionary<AssetIdentity, string> Unresolved { get; }

    /// <summary>
    /// Total size in bytes of all resolved dependencies.
    /// </summary>
    public long TotalBytes { get; }

    /// <summary>
    /// Total count of resolved assets.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Maximum depth reached during traversal.
    /// </summary>
    public int MaxDepth { get; }

    /// <summary>
    /// Number of duplicate entries suppressed during traversal.
    /// </summary>
    public int DuplicatesSuppressed { get; }

    /// <summary>
    /// The budget that excluded at least one asset, or <see cref="TraversalLimit.None"/> if the
    /// closure completed within every budget. When several budgets are breached, this reports the
    /// first one, because that is the budget that shaped the closure.
    /// </summary>
    public TraversalLimit LimitHit { get; }

    /// <summary>
    /// True when a budget excluded at least one asset, so the closure is incomplete.
    /// </summary>
    public bool IsTruncated => LimitHit != TraversalLimit.None;

    public TraversalResult(
        IReadOnlySet<AssetIdentity> resolved,
        IReadOnlyDictionary<AssetIdentity, string> unresolved,
        long totalBytes,
        int count,
        int maxDepth,
        int duplicatesSuppressed,
        TraversalLimit limitHit = TraversalLimit.None)
    {
        Resolved = resolved ?? throw new ArgumentNullException(nameof(resolved));
        Unresolved = unresolved ?? throw new ArgumentNullException(nameof(unresolved));
        TotalBytes = totalBytes;
        Count = count;
        MaxDepth = maxDepth;
        DuplicatesSuppressed = duplicatesSuppressed;
        LimitHit = limitHit;
    }
}
