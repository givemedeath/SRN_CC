using SRN.CC.Core.Occurrences;

namespace SRN.CC.Core.Preview;

public sealed record PreviewResult(
    AssetOccurrence Occurrence,
    PreviewFamily Family,
    bool IsSuccess,
    string? MetadataText,
    byte[]? RawPayload,
    string? FormattedContent,
    string? ErrorMessage,
    IReadOnlyList<string> Diagnostics,
    int? Width = null,
    int? Height = null
);
