namespace SRN.CC.Core.Build;

public sealed record PublicationResult(
    bool IsSuccess,
    string? PublishedHakPath,
    string? PublishedManifestPath,
    string? ErrorMessage,
    IReadOnlyList<string> Logs
);
