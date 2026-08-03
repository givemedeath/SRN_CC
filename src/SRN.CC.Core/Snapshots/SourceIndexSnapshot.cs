using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Records;
using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Snapshots;

public sealed record SourceIndexSnapshot
{
    public AssetSource Source { get; }
    public SourceFingerprint Fingerprint { get; }
    public IReadOnlyList<IndexedAssetRecord> Records { get; }
    public IReadOnlyList<AssetDiagnosticRecord> Diagnostics { get; }
    public bool IsCacheHit { get; }
    public SourceScanStatistics ScanStatistics { get; }

    public SourceIndexSnapshot(
        AssetSource source,
        SourceFingerprint fingerprint,
        IReadOnlyList<IndexedAssetRecord> records,
        IReadOnlyList<AssetDiagnosticRecord> diagnostics,
        bool isCacheHit,
        SourceScanStatistics scanStatistics)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        Records = records ?? throw new ArgumentNullException(nameof(records));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        IsCacheHit = isCacheHit;
        ScanStatistics = scanStatistics ?? throw new ArgumentNullException(nameof(scanStatistics));
    }
}
