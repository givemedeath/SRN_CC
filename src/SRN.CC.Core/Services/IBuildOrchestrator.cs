using SRN.CC.Core.Build;
using SRN.CC.Core.Workspace;

namespace SRN.CC.Core.Services;

public interface IBuildOrchestrator
{
    Task<PublicationResult> ExecuteBuildAsync(
        WorkspaceState workspace,
        string destinationHakPath,
        IProgress<(string message, double progressFraction)>? progress = null,
        CancellationToken cancellationToken = default);
}
