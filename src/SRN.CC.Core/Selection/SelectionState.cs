using SRN.CC.Core.Identity;

namespace SRN.CC.Core.Selection;

public sealed record SelectionState
{
    public bool DefaultSelected { get; }
    public IReadOnlyDictionary<AssetIdentity, bool> Overrides { get; }

    public SelectionState(bool defaultSelected = true, IReadOnlyDictionary<AssetIdentity, bool>? overrides = null)
    {
        DefaultSelected = defaultSelected;
        IReadOnlyDictionary<AssetIdentity, bool> initial = overrides is null || overrides.Count == 0
            ? new Dictionary<AssetIdentity, bool>()
            : overrides;

        // Compact redundant overrides on creation and wrap in ReadOnlyDictionary
        Overrides = CompactOverrides(DefaultSelected, initial);
    }

    public bool IsSelected(AssetIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (Overrides.TryGetValue(identity, out bool overrideVal))
        {
            return overrideVal;
        }
        return DefaultSelected;
    }

    public SelectionState SetDefault(bool defaultSelected)
    {
        if (DefaultSelected == defaultSelected) return this;
        return new SelectionState(defaultSelected, Overrides);
    }

    public SelectionState SetOverride(AssetIdentity identity, bool selected)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var dict = new Dictionary<AssetIdentity, bool>(Overrides);
        if (selected == DefaultSelected)
        {
            dict.Remove(identity);
        }
        else
        {
            dict[identity] = selected;
        }
        return new SelectionState(DefaultSelected, dict);
    }

    public SelectionState ClearOverride(AssetIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Overrides.ContainsKey(identity)) return this;

        var dict = new Dictionary<AssetIdentity, bool>(Overrides);
        dict.Remove(identity);
        return new SelectionState(DefaultSelected, dict);
    }

    public static SelectionState IncludeAll() => new SelectionState(defaultSelected: true, overrides: null);

    public static SelectionState ExcludeAll() => new SelectionState(defaultSelected: false, overrides: null);

    private static IReadOnlyDictionary<AssetIdentity, bool> CompactOverrides(bool defaultSelected, IReadOnlyDictionary<AssetIdentity, bool> overrides)
    {
        var dict = new Dictionary<AssetIdentity, bool>();
        foreach (var kvp in overrides)
        {
            if (kvp.Value != defaultSelected)
            {
                dict[kvp.Key] = kvp.Value;
            }
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<AssetIdentity, bool>(dict);
    }
}

internal static class EqualityComparerDictionary
{
    public static IReadOnlyDictionary<TKey, TValue> CreateEmpty<TKey, TValue>() where TKey : notnull
    {
        return new Dictionary<TKey, TValue>();
    }
}
