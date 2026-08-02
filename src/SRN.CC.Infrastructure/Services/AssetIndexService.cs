using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Sources;

namespace SRN.CC.Infrastructure.Services;

public sealed class AssetIndexService : IAssetIndexService
{
    private readonly ISqliteCacheService _cacheService;
    private readonly IResourceTypeRegistry _typeRegistry;

    public AssetIndexService(ISqliteCacheService cacheService, IResourceTypeRegistry typeRegistry)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
    }

    public async Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        IAssetSourceReader reader = source.Kind switch
        {
            AssetSourceKind.Hak => new HakAssetSourceReader(),
            AssetSourceKind.Folder => new FolderAssetSourceReader(_typeRegistry),
            _ => throw new ArgumentException($"Unsupported asset source kind '{source.Kind}'.", nameof(source))
        };

        SourceFingerprint fingerprint;
        try
        {
            fingerprint = await reader.GetFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fallback directly to index scan which produces an unavailable snapshot record
            return await reader.IndexAsync(source, progress, cancellationToken).ConfigureAwait(false);
        }

        // Check warm SQLite cache
        SourceIndexSnapshot? cachedSnapshot = await _cacheService.TryGetSnapshotAsync(source, fingerprint, cancellationToken).ConfigureAwait(false);
        if (cachedSnapshot != null)
        {
            progress?.Report(new IndexProgress(source.Id, IndexPhase.Complete, cachedSnapshot.Records.Count, cachedSnapshot.Records.Count, "Warm cache hit."));
            return cachedSnapshot;
        }

        // Cache miss: perform complete scan
        progress?.Report(new IndexProgress(source.Id, IndexPhase.Scanning, 0, null, "Cold scan starting..."));
        SourceIndexSnapshot freshSnapshot = await reader.IndexAsync(source, progress, cancellationToken).ConfigureAwait(false);

        // Commit complete snapshot to cache if source is available and fingerprint matches
        if (freshSnapshot.Source.IsAvailable && freshSnapshot.Fingerprint.Equals(fingerprint))
        {
            progress?.Report(new IndexProgress(source.Id, IndexPhase.Caching, freshSnapshot.Records.Count, freshSnapshot.Records.Count, "Saving snapshot to SQLite cache..."));
            await _cacheService.SaveSnapshotAsync(freshSnapshot, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new IndexProgress(source.Id, IndexPhase.Complete, freshSnapshot.Records.Count, freshSnapshot.Records.Count, "Scan complete."));
        return freshSnapshot;
    }
}

