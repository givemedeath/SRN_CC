namespace SRN.CC.Core.Logging;

/// <summary>
/// One immutable log entry. Sinks render exactly one record per physical line; because every field
/// is either a scalar or a JSON string, embedded newlines in <paramref name="Message"/> are escaped
/// by the serializer and can never split a record across two lines.
/// </summary>
/// <param name="TimestampUtc">When the record was created, in UTC.</param>
/// <param name="Level">Severity of the record.</param>
/// <param name="Category">Logical source of the record, typically a type or subsystem name.</param>
/// <param name="Message">Human-readable text. May contain newlines; sinks escape them.</param>
/// <param name="EventCode">Optional stable identifier for machine-readable correlation.</param>
/// <param name="Data">Optional flat string/string context. Never nested, so a line stays greppable.</param>
/// <param name="ExceptionType">Optional full type name of an associated exception.</param>
/// <param name="ExceptionMessage">Optional message of an associated exception.</param>
public sealed record LogRecord(
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    string Message,
    string? EventCode,
    IReadOnlyDictionary<string, string>? Data,
    string? ExceptionType,
    string? ExceptionMessage
);
