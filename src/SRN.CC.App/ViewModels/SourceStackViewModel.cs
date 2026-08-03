using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Services;

namespace SRN.CC.App.ViewModels;

public partial class SourceStackViewModel : ObservableObject
{
    private readonly Func<Task> _onWorkspaceChanged;

    [ObservableProperty]
    private ObservableCollection<SourceItemViewModel> _sources = new();

    [ObservableProperty]
    private SourceItemViewModel? _selectedSource;

    public SourceStackViewModel(Func<Task> onWorkspaceChanged)
    {
        _onWorkspaceChanged = onWorkspaceChanged ?? throw new ArgumentNullException(nameof(onWorkspaceChanged));
    }

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
