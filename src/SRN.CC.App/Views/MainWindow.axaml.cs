using System;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SRN.CC.App.ViewModels;

namespace SRN.CC.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.OpenFilePickerAsync = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open SRN.CC Project",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("SRN.CC Project Files (*.srncc)") { Patterns = new[] { "*.srncc" } },
                        new FilePickerFileType("All Files (*.*)") { Patterns = new[] { "*" } }
                    }
                });
                return files.Count > 0 ? files[0].Path.LocalPath : null;
            };
        }
    }
}
