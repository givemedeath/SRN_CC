using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Preview;

namespace SRN.CC.App.ViewModels;

/// <summary>
/// ViewModel for the dependency closure confirmation dialog.
/// Displays resolved and unresolved dependencies and allows user to confirm or cancel.
/// </summary>
public partial class ConfirmDependenciesDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private ClosureSummary? summary;

    [ObservableProperty]
    private bool isConfirmed;

    [ObservableProperty]
    private bool isDialogOpen;

    private TaskCompletionSource<bool>? _dialogCompletionSource;

    public ConfirmDependenciesDialogViewModel()
    {
        // Default constructor for XAML designer
    }

    /// <summary>
    /// Sets the closure summary and opens the dialog for user confirmation.
    /// Returns a task that completes when the user confirms or cancels.
    /// </summary>
    public Task<bool> ShowDialogAsync(ClosureSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Summary = summary;
        IsConfirmed = false;
        _dialogCompletionSource = new TaskCompletionSource<bool>();
        IsDialogOpen = true;
        return _dialogCompletionSource.Task;
    }

    [RelayCommand]
    private void ConfirmSelection()
    {
        IsConfirmed = true;
        CloseDialog();
    }

    [RelayCommand]
    private void CancelSelection()
    {
        IsConfirmed = false;
        CloseDialog();
    }

    private void CloseDialog()
    {
        IsDialogOpen = false;
        _dialogCompletionSource?.TrySetResult(IsConfirmed);
    }
}
