using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Preview;

public sealed record PreviewRequest(
    AssetOccurrence Occurrence,
    AssetSource Source,
    PreviewFamily PreferredFamily,
    long SafetyBudgetBytes = 1048576 // 1 MiB default for hex
);
