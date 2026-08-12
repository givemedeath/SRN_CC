using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Services;

/// <summary>
/// Dispatches source-reading operations to the appropriate IAssetSourceReader based on source kind.
/// </summary>
public interface ISourceReaderDispatcher
{
    Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recomputes a source's change-detector fingerprint. Used by the build to detect source drift
    /// since the last scan. The default throws so lightweight test doubles need not implement it.
    /// </summary>
    Task<SourceFingerprint> GetFingerprintAsync(AssetSource source, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This dispatcher does not support fingerprinting.");
}
