using CommunityToolkit.Mvvm.ComponentModel;
using SRN.CC.Core.Sources;

namespace SRN.CC.App.ViewModels;

public partial class SourceItemViewModel : ObservableObject
{
    private readonly Func<SourceItemViewModel, SourceMode, Task>? _onModeChanged;

    public AssetSource Source { get; }

    [ObservableProperty]
    private int _priorityOrdinal;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string _pathDisplay;

    [ObservableProperty]
    private string _kindDisplay;

    [ObservableProperty]
    private SourceMode _mode;

    /// <summary>Whether the mode picker is enabled (disabled for read-only projects).</summary>
    public bool CanEditMode { get; }

    /// <summary>The values offered by the per-source mode picker.</summary>
    public static IReadOnlyList<SourceMode> Modes { get; } =
        new[] { SourceMode.Full, SourceMode.Reference, SourceMode.Hidden };

    public SourceItemViewModel(
        AssetSource source,
        bool canEditMode = true,
        Func<SourceItemViewModel, SourceMode, Task>? onModeChanged = null)
    {
        Source = source;
        CanEditMode = canEditMode;
        _onModeChanged = onModeChanged;
        _priorityOrdinal = source.PriorityOrdinal;
        _isAvailable = source.IsAvailable;
        _pathDisplay = source.FullPath;
        _kindDisplay = source.Kind.ToString();
        // Set the backing field directly so the initial value does not fire OnModeChanged.
        _mode = source.Mode;
    }

    public string Title => Path.GetFileName(PathDisplay);

    /// <summary>Dims the row when its source is unavailable (missing file/folder).</summary>
    public double RowOpacity => IsAvailable ? 1.0 : 0.55;

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(RowOpacity));

    partial void OnModeChanged(SourceMode value)
    {
        // Fires only on user edits (the ctor sets the backing field directly). The handler
        // reloads the workspace, which rebuilds this item VM, so there is no feedback loop.
        if (_onModeChanged is not null && value != Source.Mode)
        {
            _ = _onModeChanged(this, value);
        }
    }
}
