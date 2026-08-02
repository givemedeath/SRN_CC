using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Services;

public interface IAssetSourceReader
{
    Task<SourceFingerprint> GetFingerprintAsync(AssetSource source, CancellationToken cancellationToken = default);
    Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default);
}
