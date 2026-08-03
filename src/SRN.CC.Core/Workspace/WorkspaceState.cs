using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Workspace;

public sealed record WorkspaceState
{
    public IReadOnlyList<AssetSource> Sources { get; }
    public IReadOnlyDictionary<Guid, SourceIndexSnapshot> Snapshots { get; }
    public IReadOnlyList<CuratedAsset> CuratedAssets { get; }
    public SelectionState SelectionState { get; }
    public IReadOnlyList<WinnerPin> Pins { get; }
    public ProjectPreferences Preferences { get; }
    public bool IsReadOnly { get; }

    public WorkspaceState(
        IReadOnlyList<AssetSource> sources,
        IReadOnlyDictionary<Guid, SourceIndexSnapshot> snapshots,
        IReadOnlyList<CuratedAsset> curatedAssets,
        SelectionState selectionState,
        IReadOnlyList<WinnerPin> pins,
        ProjectPreferences? preferences = null,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(curatedAssets);
        ArgumentNullException.ThrowIfNull(selectionState);
        ArgumentNullException.ThrowIfNull(pins);

        Sources = sources.ToList().AsReadOnly();
        Snapshots = new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, SourceIndexSnapshot>(new Dictionary<Guid, SourceIndexSnapshot>(snapshots));
        CuratedAssets = curatedAssets.ToList().AsReadOnly();
        SelectionState = selectionState;
        Pins = pins.ToList().AsReadOnly();
        Preferences = preferences ?? new ProjectPreferences();
        IsReadOnly = isReadOnly;
    }
}
