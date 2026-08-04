using System.Buffers;
using System.Text.Json;
using SRN.CC.Core.Logging;

namespace SRN.CC.Infrastructure.Logging;

/// <summary>
/// Appends one <see cref="LogRecord"/> per physical line to a rolling JSON-line file, rotating and
/// pruning through a <see cref="LogFileSet"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Synchronous by design.</b> There is no queue, no background writer and no dedicated thread.
/// The application logs at human rates, where a lock costs microseconds; a background writer buys
/// nothing measurable and adds shutdown-ordering bugs plus a "lose the last N records on crash"
/// failure mode — precisely when the log is the only evidence left.
/// </para>
/// <para>
/// <b>One lock spans the size pre-check, the append and the flush.</b> That is the whole point of
/// the lock: a second writer must not be able to slip bytes between another writer's record and its
/// newline, because a half-line makes the file unparseable from that point on for every downstream
/// tool.
/// </para>
/// <para>
/// <b>The file handle is not held between records.</b> Each write opens with
/// <see cref="FileShare.Read"/>, appends, flushes and closes. Holding the handle would be marginally
/// faster and would also make the log directory undeletable, unrenameable and unrotatable by anything
/// outside this process — Windows refuses to remove a directory containing an open handle. Releasing
/// it means an operator can archive or clear the folder mid-run and the very next record simply
/// recreates it, and it means nothing is ever sitting in a buffer when the process dies.
/// <see cref="FileShare.Read"/> keeps <c>Get-Content -Wait</c> working throughout.
/// </para>
/// </remarks>
public sealed class JsonLineLogSink : ILogSink
{
    /// <summary>Shipped size cap for the active file: 10 MiB (PLAN.md:98).</summary>
    public const long DefaultMaxFileBytes = 10L * 1024 * 1024;

    private static readonly byte[] LineTerminator = "\n"u8.ToArray();

    private readonly object _gate = new();
    private readonly LogFileSet _files;
    private readonly TimeProvider _timeProvider;
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);
    private readonly Utf8JsonWriter _json;

    private long _failureCount;
    private bool _disposed;

    /// <summary>Creates a sink over a log directory.</summary>
    /// <param name="directoryPath">
    /// Directory for the active and rotated files, normally <c>AppPaths.LogDirectory</c>.
    /// </param>
    /// <param name="maxFileBytes">
    /// Size cap for the active file. Injectable so rotation can be proved against a few kilobytes
    /// instead of forcing a test to write 10 MiB.
    /// </param>
    /// <param name="maxFileCount">Total files retained, active file included.</param>
    /// <param name="timeProvider">Clock used for record and rotation stamps. Defaults to the system clock.</param>
    public JsonLineLogSink(
        string directoryPath,
        long maxFileBytes = DefaultMaxFileBytes,
        int maxFileCount = LogFileSet.DefaultMaxFileCount,
        TimeProvider? timeProvider = null)
        : this(new LogFileSet(directoryPath, maxFileCount), maxFileBytes, timeProvider)
    {
    }

    /// <summary>Creates a sink over an already-configured file set.</summary>
    /// <param name="files">Naming, ordering and retention policy for the directory.</param>
    /// <param name="maxFileBytes">Size cap for the active file.</param>
    /// <param name="timeProvider">Clock used for rotation stamps. Defaults to the system clock.</param>
    public JsonLineLogSink(
        LogFileSet files,
        long maxFileBytes = DefaultMaxFileBytes,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1L);

        _files = files;
        MaxFileBytes = maxFileBytes;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _json = new Utf8JsonWriter(_buffer, new JsonWriterOptions { Indented = false, SkipValidation = true });
    }

    /// <summary>The rotation policy this sink writes through.</summary>
    public LogFileSet Files => _files;

    /// <summary>Absolute path of the file currently being appended to.</summary>
    public string ActivePath => _files.ActivePath;

    /// <summary>Size beyond which the active file is rotated before the next record lands.</summary>
    public long MaxFileBytes { get; }

    /// <summary>
    /// How many records this sink failed to persist. A sink must never throw, so a full disk or a
    /// vanished directory is invisible to the caller; this counter is the only way a diagnostic or a
    /// test can tell that the file on disk is incomplete.
    /// </summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <inheritdoc />
    /// <remarks>
    /// Never throws, per the <see cref="ILogSink"/> contract. Anything that goes wrong increments
    /// <see cref="FailureCount"/> and is otherwise dropped: there is nowhere left to report a
    /// logging failure to.
    /// </remarks>
    public void Write(LogRecord record)
    {
        if (record is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                ReadOnlySpan<byte> line = Render(record);
                AppendWithRotation(line);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _failureCount);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A no-op: <see cref="Write"/> flushes and closes the file before it returns, so there is never
    /// anything buffered here. Kept because the contract promises the guarantee, not the work.
    /// </remarks>
    public void Flush()
    {
    }

    /// <summary>
    /// Releases the reusable serialization buffer and makes every later <see cref="Write"/> a no-op.
    /// Idempotent, and never throws.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _json.Dispose();
        }
    }

    /// <summary>
    /// Serializes one record into the reusable buffer as a single JSON object plus its newline.
    /// </summary>
    /// <remarks>
    /// Every field is emitted unconditionally, nulls included, so a consumer can rely on the shape of
    /// a line without probing for missing keys. <see cref="Utf8JsonWriter"/> escapes control
    /// characters, so a message containing a newline can never split a record across two lines.
    /// </remarks>
    private ReadOnlySpan<byte> Render(LogRecord record)
    {
        _buffer.Clear();
        _json.Reset(_buffer);

        _json.WriteStartObject();
        _json.WriteString("timestampUtc", record.TimestampUtc.ToUniversalTime());
        _json.WriteString("level", record.Level.ToString());
        _json.WriteString("category", record.Category);
        _json.WriteString("message", record.Message);
        WriteNullableString("eventCode", record.EventCode);

        if (record.Data is null)
        {
            _json.WriteNull("data");
        }
        else
        {
            _json.WriteStartObject("data");
            foreach (KeyValuePair<string, string> pair in record.Data)
            {
                WriteNullableString(pair.Key, pair.Value);
            }

            _json.WriteEndObject();
        }

        WriteNullableString("exceptionType", record.ExceptionType);
        WriteNullableString("exceptionMessage", record.ExceptionMessage);
        _json.WriteEndObject();
        _json.Flush();

        _buffer.Write(LineTerminator);
        return _buffer.WrittenSpan;
    }

    private void WriteNullableString(string propertyName, string? value)
    {
        if (value is null)
        {
            _json.WriteNull(propertyName);
        }
        else
        {
            _json.WriteString(propertyName, value);
        }
    }

    /// <summary>
    /// Rotates when the record would push the active file past the cap, then appends it whole.
    /// </summary>
    /// <remarks>
    /// A record larger than the whole cap is still written intact rather than dropped or truncated:
    /// the oversized record is exactly the one worth keeping, and a truncated line would corrupt the
    /// file for every JSON-line reader. It lands alone in a freshly rotated file, so the overshoot is
    /// bounded at one record.
    /// </remarks>
    private void AppendWithRotation(ReadOnlySpan<byte> line)
    {
        _files.EnsureDirectory();

        var active = new FileInfo(_files.ActivePath);
        long currentLength = active.Exists ? active.Length : 0;
        if (currentLength > 0 && currentLength + line.Length > MaxFileBytes)
        {
            Rotate();
        }

        using var stream = new FileStream(
            _files.ActivePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        stream.Write(line);
        stream.Flush();
    }

    private void Rotate()
    {
        File.Move(_files.ActivePath, _files.ReserveRotatedPath(_timeProvider.GetUtcNow()));
        _files.Prune();
    }
}
