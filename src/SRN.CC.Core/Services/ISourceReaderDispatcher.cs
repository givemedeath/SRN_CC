using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Services;

/// <summary>
/// Dispatches source-reading operations to the appropriate IAssetSourceReader based on source kind.
/// </summary>
public interface ISourceReaderDispatcher
{
    Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default);
}
