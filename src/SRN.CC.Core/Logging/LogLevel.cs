namespace SRN.CC.Core.Logging;

/// <summary>
/// Severity of a single <see cref="LogRecord"/>, ordered from least to most severe so that
/// sinks and viewers can filter with a simple <c>&gt;=</c> comparison.
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error
}
