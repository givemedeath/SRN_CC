using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Services;

namespace SRN.CC.App.ViewModels;

public partial class SourceStackViewModel : ObservableObject
{
    private readonly Func<Task> _onWorkspaceChanged;
    private readonly Func<Task> _onAddHakSource;
    private readonly Func<Task> _onAddFolderSource;

    [ObservableProperty]
    private ObservableCollection<SourceItemViewModel> _sources = new();

    [ObservableProperty]
    private SourceItemViewModel? _selectedSource;

    public SourceStackViewModel(Func<Task> onWorkspaceChanged, Func<Task>? onAddHakSource = null, Func<Task>? onAddFolderSource = null)
    {
        _onWorkspaceChanged = onWorkspaceChanged ?? throw new ArgumentNullException(nameof(onWorkspaceChanged));
        _onAddHakSource = onAddHakSource ?? (() => Task.CompletedTask);
        _onAddFolderSource = onAddFolderSource ?? (() => Task.CompletedTask);
    }

    [RelayCommand]
    private Task AddHakSourceAsync() => _onAddHakSource();

    [RelayCommand]
    private Task AddFolderSourceAsync() => _onAddFolderSource();

    public void UpdateSources(IEnumerable<SourceItemViewModel> sources)
    {
        Sources = new ObservableCollection<SourceItemViewModel>(sources.OrderBy(s => s.PriorityOrdinal));
    }

    [RelayCommand]
    private async Task MoveUpAsync()
    {
        if (SelectedSource == null) return;
        int idx = Sources.IndexOf(SelectedSource);
        if (idx <= 0) return;

        Sources.Move(idx, idx - 1);
        ReassignOrdinals();
        await _onWorkspaceChanged().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task MoveDownAsync()
    {
        if (SelectedSource == null) return;
        int idx = Sources.IndexOf(SelectedSource);
        if (idx < 0 || idx >= Sources.Count - 1) return;

        Sources.Move(idx, idx + 1);
        ReassignOrdinals();
        await _onWorkspaceChanged().ConfigureAwait(false);
    }

    private void ReassignOrdinals()
    {
        for (int i = 0; i < Sources.Count; i++)
        {
            Sources[i].PriorityOrdinal = i;
        }
    }
}
