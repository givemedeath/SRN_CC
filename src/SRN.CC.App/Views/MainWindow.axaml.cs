using System;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SRN.CC.App.ViewModels;

namespace SRN.CC.App.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// The single project-file picker filter. Both the open and the save picker used to spell the
    /// extension inline, and both spelled it <c>.srncc</c> while the store, the tests, and the
    /// startup journal recovery all used <c>.srnccproj</c>; deriving it from
    /// <see cref="MainWindowViewModel.ProjectFileExtension"/> makes that divergence impossible.
    /// </summary>
    private static FilePickerFileType ProjectFileType => new(
        $"SRN.CC Project Files (*{MainWindowViewModel.ProjectFileExtension})")
    {
        Patterns = new[] { $"*{MainWindowViewModel.ProjectFileExtension}" }
    };

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
                        ProjectFileType,
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
                    SuggestedFileName = MainWindowViewModel.DefaultProjectFileName,
                    FileTypeChoices = new[] { ProjectFileType }
                });

                return path?.Path.LocalPath;
            };

            vm.BuildOutputFilePickerAsync = async (suggestedFileName, suggestedDirectory) =>
            {
                var options = new FilePickerSaveOptions
                {
                    Title = "Save HAK Output",
                    SuggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "output.hak" : suggestedFileName,
                    DefaultExtension = "hak",
                    FileTypeChoices = new[] { new FilePickerFileType("HAK Files (*.hak)") { Patterns = new[] { "*.hak" } } }
                };

                // Opening on the previous target's folder rather than wherever the picker last was.
                // TryGetFolderFromPathAsync returns null for a directory that no longer exists, and a
                // null SuggestedStartLocation is exactly the "pick for me" default.
                if (!string.IsNullOrWhiteSpace(suggestedDirectory))
                {
                    options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(suggestedDirectory);
                }

                var path = await StorageProvider.SaveFilePickerAsync(options);

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

            vm.ShowSettingsDialogAsync = async settingsViewModel =>
            {
                var dialog = new SettingsDialog { DataContext = settingsViewModel };
                await dialog.ShowDialog(this);
            };

            vm.ShowConfirmDependenciesDialogAsync = async dialogViewModel =>
            {
                var dialog = new ConfirmDependenciesDialog { DataContext = dialogViewModel };

                // The view model completes its own task on Confirm/Cancel and flips IsDialogOpen
                // false; close the window in response so the modal await below returns.
                void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(ConfirmDependenciesDialogViewModel.IsDialogOpen)
                        && !dialogViewModel.IsDialogOpen)
                    {
                        dialog.Close();
                    }
                }

                dialogViewModel.PropertyChanged += OnPropertyChanged;
                try
                {
                    await dialog.ShowDialog(this);
                }
                finally
                {
                    dialogViewModel.PropertyChanged -= OnPropertyChanged;
                }
            };
        }
    }
}
