using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Logging;

namespace SRN.CC.App.ViewModels;

public sealed record LogEntryItem(DateTime Timestamp, string Level, string Message);

/// <summary>
/// The operation-log drawer. Since milestone 7 this is a <em>view</em> over the application log
/// rather than a second log: when a <see cref="Logger"/> is attached, <see cref="AddEntry"/> writes
/// to it and the entry arrives back here through <c>ObservableLogSink</c>, so UI-originated messages
/// land in the rolling log file alongside everything else. With no logger attached — the designer
/// preview, the synthetic performance probe, and unit tests — entries are appended directly and the
/// drawer behaves exactly as it did before.
/// </summary>
public partial class OperationLogViewModel : ObservableObject
{
    /// <summary>
    /// Hard ceiling on retained entries. <c>Entries</c> was previously unbounded, which is a leak in
    /// a long session over a 188k-row index: every progress tick is an entry. Oldest entries are
    /// evicted first; <see cref="WarningCount"/> and <see cref="ErrorCount"/> are session totals and
    /// deliberately survive eviction, so the badge does not silently decrease.
    /// </summary>
    public const int MaxEntries = 2000;

    /// <summary>
    /// Log category used for entries that originate in the drawer. <c>ObservableLogSink</c> omits
    /// this category from the rendered line, so a round-tripped entry reads the same as a direct one.
    /// </summary>
    public const string LogCategory = "OperationLog";

    [ObservableProperty]
    private ObservableCollection<LogEntryItem> _entries = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _errorCount;

    /// <summary>
    /// The application logger this drawer forwards to, or null to append directly. Set once during
    /// composition, after the sink has been attached, so no entry is written to a logger whose sink
    /// cannot yet reach this view model.
    /// </summary>
    public IAppLogger? Logger { get; set; }

    /// <summary>
    /// Records one operator-facing message. Delegates to <see cref="Logger"/> when one is attached.
    /// </summary>
    public void AddEntry(string level, string message)
    {
        if (Logger is { } logger)
        {
            logger.Log(ParseLevel(level), LogCategory, message);
            return;
        }

        Append(new LogEntryItem(DateTime.Now, NormalizeLevel(level), message));
    }

    /// <summary>
    /// Appends a fully-formed entry, evicting the oldest once <see cref="MaxEntries"/> is exceeded.
    /// Called by <c>ObservableLogSink</c> on the UI thread; must not be called from anywhere else
    /// without marshalling first.
    /// </summary>
    public void Append(LogEntryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        Entries.Add(item);
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }

        if (item.Level.Equals("WARN", StringComparison.OrdinalIgnoreCase)) WarningCount++;
        if (item.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) ErrorCount++;
    }

    /// <summary>Maps a drawer level string onto a <see cref="LogLevel"/>; unknown text is Info.</summary>
    public static LogLevel ParseLevel(string? level) => level?.ToUpperInvariant() switch
    {
        "TRACE" => LogLevel.Trace,
        "DEBUG" => LogLevel.Debug,
        "WARN" or "WARNING" => LogLevel.Warn,
        "ERROR" => LogLevel.Error,
        _ => LogLevel.Info
    };

    /// <summary>Maps a <see cref="LogLevel"/> onto the drawer level string.</summary>
    public static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO"
    };

    private static string NormalizeLevel(string? level) => FormatLevel(ParseLevel(level));

    [RelayCommand]
    private void ClearLog()
    {
        Entries.Clear();
        WarningCount = 0;
        ErrorCount = 0;
    }

    [RelayCommand]
    private void ToggleDrawer()
    {
        IsExpanded = !IsExpanded;
    }
}
