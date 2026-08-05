using SRN.CC.Core.Logging;

namespace SRN.CC.Infrastructure.Logging;

/// <summary>
/// The shipped <see cref="IAppLogger"/>: builds one <see cref="LogRecord"/> per call and fans it out
/// to every configured sink, isolating each sink's failures from the caller and from its peers.
/// </summary>
/// <remarks>
/// <para>
/// <b>A logger must never throw.</b> Every call site in this application logs from inside a
/// <c>catch</c> or a hot loop it does not want to abort; an exception escaping here would convert a
/// diagnostic into the outage it was meant to describe. A sink that throws is counted and skipped,
/// and the remaining sinks still receive the record — one broken destination must not silence the
/// others.
/// </para>
/// <para>
/// Fan-out is synchronous and in-order, matching <see cref="JsonLineLogSink"/>'s design: no queue, no
/// background thread, nothing buffered that a crash could take with it.
/// </para>
/// </remarks>
public sealed class AppLogger : IAppLogger, IDisposable
{
    /// <summary>
    /// Key that promotes a <c>data</c> entry into <see cref="LogRecord.EventCode"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IAppLogger.Log"/> has no event-code parameter and cannot grow one without touching
    /// every call site in the solution, so the code travels in the data dictionary under this
    /// well-known key. It is copied into the record's dedicated field and also left in <c>data</c>,
    /// so a caller that never heard of this convention still sees exactly what it passed.
    /// </remarks>
    public const string EventCodeKey = "eventCode";

    private readonly ILogSink[] _sinks;

    /// <summary>Per-sink "has failed at least once" flags, indexed in step with <see cref="_sinks"/>.</summary>
    private readonly int[] _sinkFailed;

    private readonly TimeProvider _timeProvider;

    private int _failedSinkCount;
    private bool _disposed;

    /// <summary>Creates a logger over a fixed set of sinks.</summary>
    /// <param name="sinks">
    /// Destinations for every record. Snapshotted at construction: the set is fixed for the lifetime
    /// of the logger so that fan-out needs no lock on the hot path.
    /// </param>
    /// <param name="timeProvider">
    /// Clock stamped onto each record. Injectable so a test can assert on an exact timestamp instead
    /// of a tolerance window.
    /// </param>
    public AppLogger(IEnumerable<ILogSink> sinks, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        _sinks = sinks.Where(static sink => sink is not null).ToArray();
        _sinkFailed = new int[_sinks.Length];
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Number of configured sinks.</summary>
    public int SinkCount => _sinks.Length;

    /// <summary>
    /// How many distinct sinks have failed at least once, whether in <see cref="ILogSink.Write"/>,
    /// <see cref="ILogSink.Flush"/> or <see cref="IDisposable.Dispose"/>. Counted per sink rather
    /// than per record so the number answers "how many destinations are broken", which is what a
    /// startup diagnostic and an operator both want; a single dead sink logged a million times still
    /// reports one.
    /// </summary>
    public int FailedSinkCount => Volatile.Read(ref _failedSinkCount);

    /// <inheritdoc />
    public void Log(
        LogLevel level,
        string category,
        string message,
        Exception? exception = null,
        IReadOnlyDictionary<string, string>? data = null)
    {
        if (_disposed || _sinks.Length == 0)
        {
            return;
        }

        string? eventCode = null;
        if (data is not null && data.TryGetValue(EventCodeKey, out string? code))
        {
            eventCode = code;
        }

        var record = new LogRecord(
            _timeProvider.GetUtcNow(),
            level,
            category ?? string.Empty,
            message ?? string.Empty,
            eventCode,
            data,
            exception?.GetType().FullName,
            exception?.Message);

        for (int i = 0; i < _sinks.Length; i++)
        {
            try
            {
                _sinks[i].Write(record);
            }
            catch (Exception)
            {
                MarkFailed(i);
            }
        }
    }

    /// <summary>Flushes every sink, isolating failures exactly as <see cref="Log"/> does.</summary>
    public void Flush()
    {
        for (int i = 0; i < _sinks.Length; i++)
        {
            try
            {
                _sinks[i].Flush();
            }
            catch (Exception)
            {
                MarkFailed(i);
            }
        }
    }

    /// <summary>
    /// Flushes and disposes every sink. Idempotent and, like everything else here, never throws: a
    /// sink that fails on the way out must not take application shutdown with it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Flush();

        for (int i = 0; i < _sinks.Length; i++)
        {
            try
            {
                _sinks[i].Dispose();
            }
            catch (Exception)
            {
                MarkFailed(i);
            }
        }
    }

    private void MarkFailed(int index)
    {
        if (Interlocked.Exchange(ref _sinkFailed[index], 1) == 0)
        {
            Interlocked.Increment(ref _failedSinkCount);
        }
    }
}
