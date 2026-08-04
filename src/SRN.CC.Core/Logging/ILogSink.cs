namespace SRN.CC.Core.Logging;

/// <summary>
/// A destination for <see cref="LogRecord"/> values — a file, an in-memory buffer, or a UI list.
/// </summary>
/// <remarks>
/// Implementations must be safe for concurrent callers and must never throw out of
/// <see cref="Write"/> or <see cref="Flush"/>: a logger that throws turns a diagnostic into an
/// outage. Fan-out and per-sink failure isolation belong to the logger, not to the sink.
/// </remarks>
public interface ILogSink : IDisposable
{
    /// <summary>Appends one record.</summary>
    void Write(LogRecord record);

    /// <summary>Ensures every previously written record has reached the underlying medium.</summary>
    void Flush();
}
