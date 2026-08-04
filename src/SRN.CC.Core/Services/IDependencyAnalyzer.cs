using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;

namespace SRN.CC.Core.Services;

/// <summary>
/// Analyzes an occurrence's stream payload and returns dependent asset identities.
/// </summary>
/// <remarks>
/// Implementations must avoid infinite recursion for cyclic references.
/// </remarks>
public interface IDependencyAnalyzer
{
    /// <summary>
    /// Analyzes an occurrence payload and returns the set of directly or transitively required <see cref="AssetIdentity"/> values.
    /// </summary>
    /// <remarks>
    /// The caller retains ownership of <paramref name="stream"/> and remains responsible for disposing it.
    /// The analyzer must not dispose or seek the stream as part of normal execution.
    /// For preview-only families not supported in the current phase, implementations should return an empty set.
    /// </remarks>
    Task<IReadOnlySet<AssetIdentity>> AnalyzeDependenciesAsync(
        AssetOccurrence occurrence,
        Stream stream,
        CancellationToken cancellationToken = default);
}
