using CommunityToolkit.Mvvm.ComponentModel;
using SRN.CC.Core.Sources;

namespace SRN.CC.App.ViewModels;

public partial class SourceItemViewModel : ObservableObject
{
    public AssetSource Source { get; }

    [ObservableProperty]
    private int _priorityOrdinal;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string _pathDisplay;

    [ObservableProperty]
    private string _kindDisplay;

    public SourceItemViewModel(AssetSource source)
    {
        Source = source;
        _priorityOrdinal = source.PriorityOrdinal;
        _isAvailable = source.IsAvailable;
        _pathDisplay = source.FullPath;
        _kindDisplay = source.Kind.ToString();
    }

    public string Title => Path.GetFileName(PathDisplay);
}
