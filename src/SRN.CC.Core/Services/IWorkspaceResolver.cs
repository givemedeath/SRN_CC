using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;

namespace SRN.CC.Core.Services;

public interface IWorkspaceResolver
{
    Task<WorkspaceState> ResolveAsync(
        IReadOnlyList<AssetSource> sources,
        IReadOnlyDictionary<Guid, SourceIndexSnapshot> snapshots,
        IReadOnlyList<WinnerPin> pins,
        SelectionState selectionState,
        ProjectPreferences? preferences = null,
        bool isReadOnly = false,
        CancellationToken cancellationToken = default);
}
