using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;

namespace SRN.CC.Infrastructure.Services;

public sealed class DefaultDependencyAnalyzer : IDependencyAnalyzer
{
    public Task<IReadOnlySet<AssetIdentity>> AnalyzeDependenciesAsync(
        AssetOccurrence occurrence,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new InvalidOperationException("Dependency analyzer requires a readable stream.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // No-op baseline. Phase 1 intentionally advertises the interface and lifecycle contract only.
        return Task.FromResult<IReadOnlySet<AssetIdentity>>(new HashSet<AssetIdentity>());
    }
}
