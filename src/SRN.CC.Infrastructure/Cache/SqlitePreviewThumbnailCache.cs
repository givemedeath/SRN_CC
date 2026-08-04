using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;

namespace SRN.CC.Infrastructure.Cache;

/// <summary>
/// Thin adapter that implements the Preview-consumed <see cref="IPreviewThumbnailCache"/> abstraction
/// by delegating to the existing <see cref="ISqliteCacheService"/> preview_cache table. Carries no
/// state or logic of its own beyond DTO translation.
/// </summary>
public sealed class SqlitePreviewThumbnailCache : IPreviewThumbnailCache
{
    private readonly ISqliteCacheService _cacheService;

    public SqlitePreviewThumbnailCache(ISqliteCacheService cacheService)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
    }

    public async Task<CachedThumbnail?> TryGetAsync(
        SourceFingerprint fingerprint,
        AssetOccurrence occurrence,
        CancellationToken cancellationToken = default)
    {
        PreviewCachePayload? cached = await _cacheService
            .TryGetPreviewAsync(fingerprint, occurrence, cancellationToken)
            .ConfigureAwait(false);

        return cached is null ? null : new CachedThumbnail(cached.Width, cached.Height, cached.PngBytes);
    }

    public Task SaveAsync(
        SourceFingerprint fingerprint,
        AssetOccurrence occurrence,
        int width,
        int height,
        byte[] pngBytes,
        CancellationToken cancellationToken = default)
        => _cacheService.SavePreviewAsync(fingerprint, occurrence, width, height, pngBytes, cancellationToken);
}
