using System;
using Avalonia.Controls;
using SRN.CC.App.ViewModels;

namespace SRN.CC.App.Views;

/// <summary>
/// Host window for <see cref="SettingsDialogViewModel"/>. The view model decides when the dialog is
/// done; this window only closes itself when told, so the "did the operator save?" answer lives in
/// one place rather than being inferred from a dialog result.
/// </summary>
public partial class SettingsDialog : Window
{
    private SettingsDialogViewModel? _attached;

    public SettingsDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attached is not null)
        {
            _attached.CloseRequested -= OnCloseRequested;
            _attached = null;
        }

        if (DataContext is SettingsDialogViewModel viewModel)
        {
            _attached = viewModel;
            _attached.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (_attached is not null)
        {
            // The view model clears this itself on Save and Cancel, but the title-bar close, Alt+F4
            // and a parent-initiated close all bypass the view model entirely. The window is the only
            // participant that observes every close route, so it is the one that has to settle the
            // flag — otherwise a dismissed dialog goes on reporting itself as open.
            _attached.IsDialogOpen = false;

            _attached.CloseRequested -= OnCloseRequested;
            _attached = null;
        }

        base.OnClosed(e);
    }
}
