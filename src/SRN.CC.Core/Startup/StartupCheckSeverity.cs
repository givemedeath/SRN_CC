namespace SRN.CC.Core.Startup;

/// <summary>
/// Severity of a single startup check outcome. Ordered least to most severe so the
/// numeric maximum across a set of results is the worst outcome.
/// </summary>
public enum StartupCheckSeverity
{
    Ok,
    Degraded,
    Blocking
}
