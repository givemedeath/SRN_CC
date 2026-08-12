using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Sources;

namespace SRN.CC.Infrastructure.Services;

public sealed class SourceReaderDispatcher : ISourceReaderDispatcher
{
    private readonly HakAssetSourceReader _hakReader;
    private readonly FolderAssetSourceReader _folderReader;

    public SourceReaderDispatcher(HakAssetSourceReader? hakReader = null, FolderAssetSourceReader? folderReader = null, IResourceTypeRegistry? typeRegistry = null)
    {
        _hakReader = hakReader ?? new HakAssetSourceReader();
        _folderReader = folderReader ?? new FolderAssetSourceReader(typeRegistry ?? new ResourceTypeRegistry());
    }

    public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(occurrence);

        return source.Kind switch
        {
            AssetSourceKind.Hak => _hakReader.OpenOccurrenceAsync(source, occurrence, cancellationToken),
            AssetSourceKind.Folder => _folderReader.OpenOccurrenceAsync(source, occurrence, cancellationToken),
            _ => throw new NotSupportedException($"Source kind '{source.Kind}' is not supported.")
        };
    }

    public Task<SourceFingerprint> GetFingerprintAsync(AssetSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Kind switch
        {
            AssetSourceKind.Hak => _hakReader.GetFingerprintAsync(source, cancellationToken),
            AssetSourceKind.Folder => _folderReader.GetFingerprintAsync(source, cancellationToken),
            _ => throw new NotSupportedException($"Source kind '{source.Kind}' is not supported.")
        };
    }
}
