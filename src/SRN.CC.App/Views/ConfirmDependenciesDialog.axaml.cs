using Avalonia.Controls;
using SRN.CC.App.ViewModels;

namespace SRN.CC.App.Views;

public partial class ConfirmDependenciesDialog : Window
{
    public ConfirmDependenciesDialog()
    {
        InitializeComponent();
        DataContext = new ConfirmDependenciesDialogViewModel();
    }
}
