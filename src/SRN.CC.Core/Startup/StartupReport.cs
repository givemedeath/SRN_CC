namespace SRN.CC.Core.Startup;

/// <summary>
/// Aggregate outcome of a startup preflight run.
/// </summary>
public sealed record StartupReport(IReadOnlyList<StartupCheckResult> Results)
{
    /// <summary>
    /// True if and only if at least one result is <see cref="StartupCheckSeverity.Blocking"/>.
    /// </summary>
    public bool HasBlocking => Results.Any(r => r.Severity == StartupCheckSeverity.Blocking);

    /// <summary>
    /// The most severe severity across <see cref="Results"/>, or
    /// <see cref="StartupCheckSeverity.Ok"/> when there are no results.
    /// </summary>
    public StartupCheckSeverity Worst =>
        Results.Count == 0
            ? StartupCheckSeverity.Ok
            : Results.Max(r => r.Severity);
}
