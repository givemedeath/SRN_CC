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

        Sources = sources;
        Snapshots = snapshots;
        CuratedAssets = curatedAssets;
        SelectionState = selectionState;
        Pins = pins;
        Preferences = preferences ?? new ProjectPreferences();
        IsReadOnly = isReadOnly;
    }
}
