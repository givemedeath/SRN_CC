using SRN.CC.Core.Workspace;

namespace SRN.CC.Core.Services;

public interface IProjectStore
{
    Task<WorkspaceState> LoadAsync(string projectPath, CancellationToken cancellationToken = default);
    Task SaveAsync(WorkspaceState state, string projectPath, CancellationToken cancellationToken = default);
    Task SaveAsAsync(WorkspaceState state, string targetProjectPath, CancellationToken cancellationToken = default);
}
