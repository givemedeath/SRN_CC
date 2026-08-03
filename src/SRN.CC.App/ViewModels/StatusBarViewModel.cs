using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SRN.CC.App.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    private CancellationTokenSource? _activeCts;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private bool _isBusy;

    public CancellationTokenSource BeginOperation(string message)
    {
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();

        StatusMessage = message;
        ProgressFraction = 0;
        IsBusy = true;
        return _activeCts;
    }

    public void ReportProgress(string message, double progressFraction)
    {
        StatusMessage = message;
        ProgressFraction = progressFraction;
    }

    public void EndOperation(string completionMessage)
    {
        StatusMessage = completionMessage;
        ProgressFraction = 1.0;
        IsBusy = false;
        _activeCts?.Dispose();
        _activeCts = null;
    }

    [RelayCommand]
    private void CancelActiveOperation()
    {
        if (_activeCts != null && !_activeCts.IsCancellationRequested)
        {
            _activeCts.Cancel();
            StatusMessage = "Canceling operation...";
        }
    }
}
