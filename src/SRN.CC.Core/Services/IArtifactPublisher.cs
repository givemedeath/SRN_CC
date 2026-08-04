using SRN.CC.Core.Build;

namespace SRN.CC.Core.Services;

public interface IArtifactPublisher
{
    Task<PublicationResult> PublishAsync(
        BuildPlan plan,
        string tempHakPath,
        string tempManifestPath,
        CancellationToken cancellationToken = default);

    Task<bool> RecoverPendingJournalAsync(
        string journalDirectory,
        CancellationToken cancellationToken = default);
}
