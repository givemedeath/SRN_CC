using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SRN.CC.App.ViewModels;

public sealed record LogEntryItem(DateTime Timestamp, string Level, string Message);

public partial class OperationLogViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<LogEntryItem> _entries = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _errorCount;

    public void AddEntry(string level, string message)
    {
        var item = new LogEntryItem(DateTime.Now, level, message);
        Entries.Add(item);
        if (level.Equals("WARN", StringComparison.OrdinalIgnoreCase)) WarningCount++;
        if (level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) ErrorCount++;
    }

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
