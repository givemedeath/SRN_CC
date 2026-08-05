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
    private readonly Func<Task> _onRescanSources;
    private readonly Func<SourceItemViewModel, Task> _onRemoveSource;
    private readonly Func<bool> _canMoveSources;

    [ObservableProperty]
    private ObservableCollection<SourceItemViewModel> _sources = new();

    [ObservableProperty]
    private SourceItemViewModel? _selectedSource;

    public SourceStackViewModel(
        Func<Task> onWorkspaceChanged,
        Func<Task>? onAddHakSource = null,
        Func<Task>? onAddFolderSource = null,
        Func<Task>? onRescanSources = null,
        Func<bool>? canMoveSources = null,
        Func<SourceItemViewModel, Task>? onRemoveSource = null)
    {
        _onWorkspaceChanged = onWorkspaceChanged ?? throw new ArgumentNullException(nameof(onWorkspaceChanged));
        _onAddHakSource = onAddHakSource ?? (() => Task.CompletedTask);
        _onAddFolderSource = onAddFolderSource ?? (() => Task.CompletedTask);
        _onRescanSources = onRescanSources ?? (() => Task.CompletedTask);
        _canMoveSources = canMoveSources ?? (() => true);
        _onRemoveSource = onRemoveSource ?? (_ => Task.CompletedTask);
    }

    /// <summary>
    /// Removes the selected source from the workspace. Reordering and removal share
    /// <see cref="CanMoveSource"/>'s read-only gate, but removal additionally needs something
    /// selected, so it re-evaluates whenever the selection changes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSource))]
    private Task RemoveSourceAsync() =>
        SelectedSource is null ? Task.CompletedTask : _onRemoveSource(SelectedSource);

    private bool CanRemoveSource() => SelectedSource is not null && _canMoveSources();

    partial void OnSelectedSourceChanged(SourceItemViewModel? value) =>
        RemoveSourceCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private Task AddHakSourceAsync() => _onAddHakSource();

    [RelayCommand]
    private Task AddFolderSourceAsync() => _onAddFolderSource();

    [RelayCommand(CanExecute = nameof(CanMoveSource))]
    private Task RescanSourcesAsync() => _onRescanSources();

    private bool CanMoveSource() => _canMoveSources();

    public void UpdateSources(IEnumerable<SourceItemViewModel> sources)
    {
        Guid? selectedId = SelectedSource?.Source.Id;
        Sources = new ObservableCollection<SourceItemViewModel>(sources.OrderBy(s => s.PriorityOrdinal));

        // Every reload builds fresh item view models, so the old selection is an object that is no
        // longer in the list. Left alone it reads as a selection to CanRemoveSource while
        // Sources.IndexOf returns -1 to the move commands — an enabled remove button and two move
        // buttons that silently do nothing. Re-point it at the same source, or clear it if that
        // source has gone.
        SelectedSource = selectedId is null
            ? null
            : Sources.FirstOrDefault(s => s.Source.Id == selectedId.Value);
    }

    [RelayCommand(CanExecute = nameof(CanMoveSource))]
    private async Task MoveUpAsync()
    {
        if (SelectedSource == null) return;
        int idx = Sources.IndexOf(SelectedSource);
        if (idx <= 0) return;

        Sources.Move(idx, idx - 1);
        ReassignOrdinals();
        await _onWorkspaceChanged().ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanMoveSource))]
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
