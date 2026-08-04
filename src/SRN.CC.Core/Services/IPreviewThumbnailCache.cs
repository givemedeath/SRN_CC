using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Occurrences;

namespace SRN.CC.Core.Services;

/// <summary>
/// Process-external cache for PNG-encoded preview thumbnails, keyed by the identity of the source
/// data (<see cref="SourceFingerprint"/>) and the occurrence's locator within that source.
/// </summary>
/// <remarks>
/// Defined in <c>SRN.CC.Core</c> (rather than <c>SRN.CC.Preview</c> or <c>SRN.CC.Infrastructure</c>)
/// because <c>eng/dependency-policy.json</c>'s <c>approvedProjectReferences</c> allows
/// <c>SRN.CC.Preview</c> to reference only <c>SRN.CC.Core</c>/<c>SRN.CC.Formats</c>, and
/// <c>SRN.CC.Infrastructure</c> to reference only <c>SRN.CC.Core</c>/<c>SRN.CC.Formats</c> — neither
/// project may reference the other. Core is the only project both already depend on, mirroring how
/// <c>ITextureSource</c> bridges Preview and App. <c>PreviewEngine</c> (Preview) consumes this
/// interface; <c>SqlitePreviewThumbnailCache</c> (Infrastructure) implements it by delegating to
/// <c>ISqliteCacheService</c>.
///
/// This cache stores only PNG-encoded raster thumbnails (the output of PNG-producing preview
/// providers such as <c>ImagePreviewProvider</c>). It must never be used for
/// <c>IPreviewPayload</c>-bearing results (e.g. 3D model scenes) — those are process-local and are
/// never PNGs; see architecture decision A2 in the milestone-6 plan.
/// </remarks>
public interface IPreviewThumbnailCache
{
    /// <summary>Returns the cached thumbnail for this fingerprint/occurrence pair, or null on a miss.</summary>
    Task<CachedThumbnail?> TryGetAsync(
        SourceFingerprint fingerprint,
        AssetOccurrence occurrence,
        CancellationToken cancellationToken = default);

    /// <summary>Persists (or replaces) the thumbnail cached for this fingerprint/occurrence pair.</summary>
    Task SaveAsync(
        SourceFingerprint fingerprint,
        AssetOccurrence occurrence,
        int width,
        int height,
        byte[] pngBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>A cached preview thumbnail: PNG-encoded pixel data plus its logical dimensions.</summary>
public sealed record CachedThumbnail(int Width, int Height, byte[] PngBytes);
