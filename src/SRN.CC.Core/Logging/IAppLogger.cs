namespace SRN.CC.Core.Logging;

/// <summary>
/// The application-wide logging entry point. Services take a trailing optional
/// <c>IAppLogger? logger = null</c> parameter defaulting to <see cref="NullAppLogger.Instance"/>,
/// so adding logging to an existing type costs no call-site churn.
/// </summary>
/// <remarks>
/// Implementations must never throw. A failing sink is swallowed and counted, not propagated.
/// </remarks>
public interface IAppLogger
{
    /// <summary>Records one entry.</summary>
    /// <param name="level">Severity of the entry.</param>
    /// <param name="category">Logical source, typically a type or subsystem name.</param>
    /// <param name="message">Human-readable text.</param>
    /// <param name="exception">Optional exception whose type and message are captured.</param>
    /// <param name="data">Optional flat string/string context.</param>
    void Log(
        LogLevel level,
        string category,
        string message,
        Exception? exception = null,
        IReadOnlyDictionary<string, string>? data = null);
}
