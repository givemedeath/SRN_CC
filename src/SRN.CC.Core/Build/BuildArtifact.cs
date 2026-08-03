namespace SRN.CC.Core.Build;

public sealed record BuildArtifact(
    string HakPath,
    string ManifestPath,
    long HakSizeBytes,
    string HakSha256Hex,
    int EntryCount
);
