using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Occurrences;

namespace SRN.CC.Core.Records;

public sealed record IndexedAssetRecord
{
    public AssetOccurrence? Occurrence { get; }
    public AssetDiagnosticRecord? Diagnostic { get; }

    public bool IsValid => Occurrence != null;

    public IndexedAssetRecord(AssetOccurrence occurrence)
    {
        Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence));
        Diagnostic = null;
    }

    public IndexedAssetRecord(AssetDiagnosticRecord diagnostic)
    {
        Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        Occurrence = null;
    }
}
