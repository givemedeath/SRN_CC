using Avalonia.Threading;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Logging;

namespace SRN.CC.App.Services;

/// <summary>
/// The <see cref="ILogSink"/> that renders the shared application log into the UI's operation-log
/// drawer, so the drawer is a projection of the one logging pipeline rather than a second,
/// parallel one.
/// </summary>
/// <remarks>
/// <para>
/// Three properties make this safe to hand to <c>AppLogger</c>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// It is attachable after construction. The sink has to exist before the logger, and the logger has
/// to exist before any service that logs — but the <see cref="OperationLogViewModel"/> it writes to
/// is created by <see cref="MainWindowViewModel"/>, which is built last. Records that arrive before
/// <see cref="Attach"/> are counted in <see cref="DroppedCount"/> and discarded; the startup story
/// they would have told is replayed deliberately from the startup report instead, so nothing is lost
/// and nothing is duplicated.
/// </description></item>
/// <item><description>
/// It marshals to the UI thread. <c>ObservableCollection</c> raises change notifications
/// synchronously on the calling thread, and every background service in the application logs.
/// </description></item>
/// <item><description>
/// It never throws. <c>AppLogger</c> already isolates a failing sink, but a sink that relies on that
/// isolation converts every UI-thread hiccup into a counted logger failure and, worse, can leave a
/// dispatcher callback throwing on a thread that has no handler at all. Both the enqueue and the
/// dequeued append are individually guarded.
/// </description></item>
/// </list>
/// <para>
/// <see cref="Flush"/> is a no-op: there is no queue of the sink's own. Once a record has been posted
/// to the dispatcher it belongs to Avalonia, and blocking a caller on the UI thread's queue from an
/// arbitrary thread is exactly the deadlock a logger must not create.
/// </para>
/// </remarks>
public sealed class ObservableLogSink : ILogSink
{
    /// <summary>
    /// Records below this level never reach the drawer. Debug and trace records still reach the
    /// file sink; the drawer is an operator-facing surface, not a developer trace.
    /// </summary>
    public const LogLevel DefaultMinimumLevel = LogLevel.Info;

    private readonly LogLevel _minimumLevel;
    private readonly Action<Action> _dispatch;

    private volatile OperationLogViewModel? _target;
    private long _droppedCount;
    private long _failureCount;

    /// <param name="minimumLevel">Lowest level rendered into the drawer.</param>
    /// <param name="dispatch">
    /// Marshals an append onto the UI thread. Overridable so the sink is assertable without an
    /// Avalonia application; defaults to <see cref="Dispatcher.UIThread"/>.
    /// </param>
    public ObservableLogSink(LogLevel minimumLevel = DefaultMinimumLevel, Action<Action>? dispatch = null)
    {
        _minimumLevel = minimumLevel;
        _dispatch = dispatch ?? PostToUiThread;
    }

    /// <summary>The view model currently receiving records, or null before <see cref="Attach"/>.</summary>
    public OperationLogViewModel? Target => _target;

    /// <summary>Records discarded because no view model was attached yet.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>Records lost to a failure inside the sink. Non-zero is a bug, never an outage.</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>Begins rendering records into <paramref name="target"/>.</summary>
    public void Attach(OperationLogViewModel target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    /// <summary>Stops rendering records. Subsequent writes are counted as dropped.</summary>
    public void Detach() => _target = null;

    /// <inheritdoc />
    public void Write(LogRecord record)
    {
        try
        {
            if (record is null || record.Level < _minimumLevel)
            {
                return;
            }

            OperationLogViewModel? target = _target;
            if (target is null)
            {
                Interlocked.Increment(ref _droppedCount);
                return;
            }

            var item = new LogEntryItem(
                record.TimestampUtc.ToLocalTime().DateTime,
                OperationLogViewModel.FormatLevel(record.Level),
                FormatMessage(record));

            _dispatch(() =>
            {
                try
                {
                    target.Append(item);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _failureCount);
                }
            });
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failureCount);
        }
    }

    /// <inheritdoc />
    /// <remarks>Deliberately a no-op; see the type-level remarks.</remarks>
    public void Flush()
    {
        // Nothing is buffered here.
    }

    /// <inheritdoc />
    public void Dispose() => Detach();

    /// <summary>
    /// Renders a record as one drawer line: the message, prefixed by its category when the record
    /// did not originate from the drawer itself, and suffixed by an exception when one is carried.
    /// </summary>
    internal static string FormatMessage(LogRecord record)
    {
        string message = record.Message ?? string.Empty;

        if (!string.IsNullOrEmpty(record.Category)
            && !string.Equals(record.Category, OperationLogViewModel.LogCategory, StringComparison.Ordinal))
        {
            message = $"[{record.Category}] {message}";
        }

        if (!string.IsNullOrEmpty(record.ExceptionType))
        {
            message = $"{message} ({record.ExceptionType}: {record.ExceptionMessage})";
        }

        return message;
    }

    /// <remarks>
    /// A failed post is deliberately allowed to propagate into <see cref="Write"/>'s guard, which
    /// counts it in <see cref="FailureCount"/>. Running the append inline instead would be the very
    /// cross-thread <c>ObservableCollection</c> mutation this sink exists to prevent: control only
    /// reaches a post failure once <see cref="Dispatcher.CheckAccess"/> has said this is <em>not</em>
    /// the UI thread, so the caller is a background worker and the collection is bound to a live UI.
    /// Losing a drawer line during a shutting-down dispatcher is the cheaper failure.
    /// </remarks>
    private static void PostToUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
        }
    }
}
