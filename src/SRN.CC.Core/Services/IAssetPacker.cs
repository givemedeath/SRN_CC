using SRN.CC.Core.Build;

namespace SRN.CC.Core.Services;

public interface IAssetPacker
{
    Task<BuildArtifact> PackAsync(
        BuildPlan plan,
        string tempHakPath,
        string tempManifestPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}
