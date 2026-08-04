using SRN.CC.Core.Identity;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;

namespace SRN.CC.Core.Services;

public interface IWorkspaceService
{
    WorkspaceState CurrentState { get; }

    Task<(WorkspaceState State, ChangedInputReport Report)> InitializeAsync(
        IReadOnlyList<AssetSource> sources,
        IReadOnlyList<WinnerPin>? pins = null,
        SelectionState? selectionState = null,
        ProjectPreferences? preferences = null,
        bool isReadOnly = false,
        CancellationToken cancellationToken = default);

    Task<(WorkspaceState State, ChangedInputReport Report)> ReorderSourcesAsync(
        IReadOnlyList<Guid> sourceIdsInOrder,
        CancellationToken cancellationToken = default);

    Task<(WorkspaceState State, ChangedInputReport Report)> RescanAsync(
        IEnumerable<Guid>? sourceIdsToRescan = null,
        CancellationToken cancellationToken = default);

    Task<(WorkspaceState State, ChangedInputReport Report)> RelocateSourceAsync(
        Guid sourceId,
        string newPath,
        CancellationToken cancellationToken = default);

    Task<WorkspaceState> PinAsync(
        WinnerPin pin,
        CancellationToken cancellationToken = default);

    Task<WorkspaceState> UnpinAsync(
        AssetIdentity identity,
        CancellationToken cancellationToken = default);

    Task<WorkspaceState> UpdateSelectionAsync(
        SelectionState newSelectionState,
        CancellationToken cancellationToken = default);

    Task<WorkspaceState> LoadProjectStateAsync(
        WorkspaceState newState,
        CancellationToken cancellationToken = default);
}
