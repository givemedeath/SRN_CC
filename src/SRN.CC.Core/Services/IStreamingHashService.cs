using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Services;

/// <summary>
/// Streams payload bytes to compute deterministic 32-byte SHA-256 hashes.
/// </summary>
public interface IStreamingHashService
{
    Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default);
}
