namespace SRN.CC.Core.Build;

public sealed record BuildVerificationReport(
    bool IsSuccess,
    int VerifiedEntryCount,
    long VerifiedTotalPayloadBytes,
    string ComputedHakSha256Hex,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings
);
