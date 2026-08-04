using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;

namespace SRN.CC.App.Services;

/// <summary>
/// Service abstraction for resolving asset dependencies with testability seams.
/// </summary>
public interface IDependencyResolver
{
    /// <summary>
    /// Resolves an asset identity to its occurrence in the workspace or catalog.
    /// Implements workspace-first fallback to catalog discovery.
    /// </summary>
    Task<AssetOccurrence?> ResolveAsync(AssetIdentity id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a readable stream for an asset occurrence.
    /// </summary>
    Task<Stream> OpenStreamAsync(AssetOccurrence occurrence, Stream fallback, CancellationToken cancellationToken = default);
}
