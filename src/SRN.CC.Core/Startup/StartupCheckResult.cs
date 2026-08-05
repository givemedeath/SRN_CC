using SRN.CC.Core.Diagnostics;

namespace SRN.CC.Core.Startup;

public sealed record StartupCheckResult(
    string CheckId,
    StartupCheckSeverity Severity,
    string Summary,
    IReadOnlyList<string> Details,
    DiagnosticCode? Code = null
);
