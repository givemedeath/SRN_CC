using SRN.CC.Core.Identity;

namespace SRN.CC.Preview;

/// <summary>
/// Represents a group of unresolved dependencies grouped by family, source path, and originating parent.
/// </summary>
public sealed class UnresolvedDependencyGroup
{
    /// <summary>
    /// Resource family name (e.g., "mdl", "mtr", "set").
    /// </summary>
    public string Family { get; }

    /// <summary>
    /// Source dependency path - the chain of parent assets that led to discovery of this unresolved dependency.
    /// </summary>
    public IReadOnlyList<AssetIdentity> SourceDependencyPath { get; }

    /// <summary>
    /// The immediate parent asset that directly references these unresolved dependencies.
    /// </summary>
    public AssetIdentity OriginatingParent { get; }

    /// <summary>
    /// Number of unresolved dependencies in this group.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// The reasons why these dependencies could not be resolved, grouped by reason string.
    /// Maps reason string to the set of asset identities with that reason.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<AssetIdentity>> ReasonGroups { get; }

    public UnresolvedDependencyGroup(
        string family,
        IReadOnlyList<AssetIdentity> sourceDependencyPath,
        AssetIdentity originatingParent,
        int count,
        IReadOnlyDictionary<string, IReadOnlySet<AssetIdentity>> reasonGroups)
    {
        Family = family ?? throw new ArgumentNullException(nameof(family));
        SourceDependencyPath = sourceDependencyPath ?? throw new ArgumentNullException(nameof(sourceDependencyPath));
        OriginatingParent = originatingParent ?? throw new ArgumentNullException(nameof(originatingParent));
        ReasonGroups = reasonGroups ?? throw new ArgumentNullException(nameof(reasonGroups));

        if (count < 0)
        {
            throw new ArgumentException("Count must be non-negative.", nameof(count));
        }

        Count = count;
    }
}
