using System.Collections.ObjectModel;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Services;

namespace SRN.CC.Preview;

/// <summary>
/// Groups unresolved dependencies deterministically by family, source dependency path, and originating parent.
/// </summary>
public static class UnresolvedDependencyGrouper
{
    /// <summary>
    /// Groups unresolved dependencies from a traversal result into deterministic groups.
    /// </summary>
    public static IReadOnlyList<UnresolvedDependencyGroup> GroupUnresolved(
        IReadOnlyDictionary<AssetIdentity, string> unresolved,
        IResourceTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(unresolved);
        ArgumentNullException.ThrowIfNull(registry);

        if (unresolved.Count == 0)
        {
            return Array.Empty<UnresolvedDependencyGroup>();
        }

        // Group by (family, sourcePath, originatingParent)
        // For now, we'll group by family and create placeholder paths/parents
        // In a full implementation, this would track the dependency chain
        var groupDict = new Dictionary<string, List<AssetIdentity>>();

        foreach (var (identity, _reason) in unresolved)
        {
            var family = GetFamilyName(identity.ResourceType, registry);
            if (!groupDict.ContainsKey(family))
            {
                groupDict[family] = new List<AssetIdentity>();
            }
            groupDict[family].Add(identity);
        }

        // Convert to UnresolvedDependencyGroup with deterministic ordering
        var groups = new List<UnresolvedDependencyGroup>();
        foreach (var (family, identities) in groupDict.OrderBy(x => x.Key))
        {
            // Group by reason for this family
            var reasonMap = new Dictionary<string, HashSet<AssetIdentity>>();
            foreach (var identity in identities)
            {
                if (unresolved.TryGetValue(identity, out var reason))
                {
                    if (!reasonMap.ContainsKey(reason))
                    {
                        reasonMap[reason] = new HashSet<AssetIdentity>();
                    }
                    reasonMap[reason].Add(identity);
                }
            }

            // For now, use the first unresolved identity as the originating parent
            // In a full implementation, this would track the actual parent chain
            var originatingParent = identities.First();
            var reasonGroups = new Dictionary<string, IReadOnlySet<AssetIdentity>>(
                reasonMap.Select(x => new KeyValuePair<string, IReadOnlySet<AssetIdentity>>(
                    x.Key,
                    new ReadOnlySet(x.Value))));

            var group = new UnresolvedDependencyGroup(
                family,
                new[] { originatingParent },
                originatingParent,
                identities.Count,
                new ReadOnlyDictionary<string, IReadOnlySet<AssetIdentity>>(reasonGroups));

            groups.Add(group);
        }

        return groups.AsReadOnly();
    }

    private static string GetFamilyName(int resourceType, IResourceTypeRegistry registry)
    {
        // Try to get the family name from the registry
        // Fallback to a hex representation if not found
        if (registry.TryGetExtension((ushort)resourceType, out var extension))
        {
            return extension;
        }
        return $"type_{resourceType:X4}";
    }

    private sealed class ReadOnlySet : IReadOnlySet<AssetIdentity>
    {
        private readonly HashSet<AssetIdentity> _set;

        public ReadOnlySet(HashSet<AssetIdentity> set)
        {
            _set = set;
        }

        public int Count => _set.Count;

        public bool Contains(AssetIdentity item) => _set.Contains(item);
        public IEnumerator<AssetIdentity> GetEnumerator() => _set.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _set.GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<AssetIdentity> other) => _set.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<AssetIdentity> other) => _set.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<AssetIdentity> other) => _set.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<AssetIdentity> other) => _set.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<AssetIdentity> other) => _set.Overlaps(other);
        public bool SetEquals(IEnumerable<AssetIdentity> other) => _set.SetEquals(other);
    }
}
