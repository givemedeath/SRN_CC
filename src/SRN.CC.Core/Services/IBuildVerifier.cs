using SRN.CC.Core.Build;

namespace SRN.CC.Core.Services;

public interface IBuildVerifier
{
    Task<BuildVerificationReport> VerifyAsync(
        BuildPlan plan,
        string tempHakPath,
        CancellationToken cancellationToken = default);
}
