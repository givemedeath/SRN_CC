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

            vm.SaveProjectFilePickerAsync = async () =>
            {
                var path = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save SRN.CC Project",
                    SuggestedFileName = "project.srncc",
                    FileTypeChoices = new[] { new FilePickerFileType("SRN.CC Project Files (*.srncc)") { Patterns = new[] { "*.srncc" } } }
                });

                return path?.Path.LocalPath;
            };

            vm.BuildOutputFilePickerAsync = async () =>
            {
                var path = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save HAK Output",
                    SuggestedFileName = "output.hak",
                    FileTypeChoices = new[] { new FilePickerFileType("HAK Files (*.hak)") { Patterns = new[] { "*.hak" } } }
                });

                return path?.Path.LocalPath;
            };

            vm.HakFilePickerAsync = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Add HAK Sources",
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("HAK Files (*.hak)") { Patterns = new[] { "*.hak" } }
                    }
                });
                return files.Select(file => file.Path.LocalPath).ToArray();
            };

            vm.FolderPickerAsync = async () =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Add Folder Source",
                    AllowMultiple = false
                });
                return folders.Count > 0 ? folders[0].Path.LocalPath : null;
            };
        }
    }
}
