using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SRN.CC.App.ViewModels;

/// <summary>
/// One entry in the conflict queue's "prefer source" menu: pin the winner from this source for every
/// conflicted identity that has an occurrence in it.
/// </summary>
public partial class PreferSourceOptionViewModel : ObservableObject
{
    private readonly Func<Guid, Task> _onPrefer;

    public Guid SourceId { get; }
    public string Label { get; }

    public PreferSourceOptionViewModel(Guid sourceId, string label, Func<Guid, Task> onPrefer)
    {
        SourceId = sourceId;
        Label = label;
        _onPrefer = onPrefer ?? throw new ArgumentNullException(nameof(onPrefer));
    }

    [RelayCommand]
    private Task Prefer() => _onPrefer(SourceId);
}
