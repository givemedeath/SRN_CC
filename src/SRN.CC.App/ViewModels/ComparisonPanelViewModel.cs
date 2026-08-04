using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Preview;

namespace SRN.CC.App.ViewModels;

public enum ComparisonMode
{
    OccurrenceMode, // Compares up to 3 occurrences of 1 asset identity
    ResolvedMode    // Compares winning occurrences of up to 3 selected assets
}

public partial class ComparisonPanelViewModel : ObservableObject
{
    private readonly PreviewEngine _previewEngine;
    private readonly Func<AssetOccurrence, Task> _onPinRequested;
    private IReadOnlyList<CuratedAsset> _selectedAssets = Array.Empty<CuratedAsset>();
    private IReadOnlyDictionary<Guid, AssetSource> _sourceMap = new Dictionary<Guid, AssetSource>();
    private int _selectionUpdateGeneration;

    [ObservableProperty]
    private ComparisonMode _mode = ComparisonMode.OccurrenceMode;

    [ObservableProperty]
    private bool _isLinkedNavigationEnabled = true;

    public ObservableCollection<PreviewSlotViewModel> Slots { get; } = new();
    private bool _allowPin = true;

    public ComparisonPanelViewModel(
        PreviewEngine previewEngine,
        Func<AssetOccurrence, Task> onPinRequested,
        ISqliteCacheService? cacheService = null)
    {
        _previewEngine = previewEngine ?? throw new ArgumentNullException(nameof(previewEngine));
        _onPinRequested = onPinRequested ?? throw new ArgumentNullException(nameof(onPinRequested));

        for (int i = 0; i < 3; i++)
        {
            var slot = new PreviewSlotViewModel(i, _previewEngine, _onPinRequested, cacheService);
            slot.ZoomPanChanged += OnSlotZoomPanChanged;
            slot.AudioStateChanged += OnSlotAudioStateChanged;
            Slots.Add(slot);
        }
    }

    [RelayCommand]
    private void ToggleLink() => IsLinkedNavigationEnabled = !IsLinkedNavigationEnabled;

    private void OnSlotZoomPanChanged(object? sender, EventArgs e)
    {
        if (!IsLinkedNavigationEnabled) return;
        if (sender is not PreviewSlotViewModel origin) return;
        if (origin.PreferredFamily != PreviewFamily.Image) return;

        foreach (var slot in Slots)
        {
            if (slot == origin) continue;
            if (!slot.IsActive) continue;
            if (slot.PreferredFamily != PreviewFamily.Image) continue;

            slot.ApplyLinkedZoomPan(origin.ZoomScale, origin.PanX, origin.PanY);
        }
    }

    private void OnSlotAudioStateChanged(object? sender, EventArgs e)
    {
        if (!IsLinkedNavigationEnabled) return;
        if (sender is not PreviewSlotViewModel origin) return;

        var activeSlots = Slots.Where(s => s.IsActive).ToArray();
        if (activeSlots.Length == 0 || activeSlots.Any(s => s.PreferredFamily != PreviewFamily.Audio))
        {
            return;
        }

        foreach (var slot in activeSlots)
        {
            if (slot == origin) continue;
            slot.ApplyLinkedAudioState(origin.PlaybackState, origin.PositionProgress);
        }
    }

    public void SetCanPin(bool canPin)
    {
        _allowPin = canPin;
        foreach (var slot in Slots)
        {
            slot.SetPinEnabled(_allowPin);
        }
    }

    [RelayCommand]
    private async Task ToggleModeAsync()
    {
        Mode = Mode == ComparisonMode.OccurrenceMode ? ComparisonMode.ResolvedMode : ComparisonMode.OccurrenceMode;
        await UpdateSelectionAsync(_selectedAssets, _sourceMap).ConfigureAwait(false);
    }

    public async Task UpdateSelectionAsync(
        IReadOnlyList<CuratedAsset> selectedAssets,
        IReadOnlyDictionary<Guid, AssetSource> sourceMap)
    {
        int generation = Interlocked.Increment(ref _selectionUpdateGeneration);
        var effectiveAssets = selectedAssets.ToArray();
        var effectiveSourceMap = sourceMap.ToDictionary(kv => kv.Key, kv => kv.Value);

        _selectedAssets = effectiveAssets;
        _sourceMap = effectiveSourceMap;

        if (generation != _selectionUpdateGeneration)
        {
            return;
        }

        if (Mode == ComparisonMode.OccurrenceMode)
        {
            var targetAsset = effectiveAssets.FirstOrDefault();
            if (targetAsset == null)
            {
                await ClearAllSlotsAsync(generation).ConfigureAwait(false);
                return;
            }

            var occurrences = targetAsset.AllOccurrences;
            for (int i = 0; i < 3; i++)
            {
                if (generation != _selectionUpdateGeneration) return;
                if (i < occurrences.Count)
                {
                    var occ = occurrences[i];
                    effectiveSourceMap.TryGetValue(occ.SourceId, out var src);
                    await Slots[i].AssignOccurrenceAsync(targetAsset, occ, src).ConfigureAwait(false);
                }
                else
                {
                    await Slots[i].AssignOccurrenceAsync(null, null, null).ConfigureAwait(false);
                }
            }
        }
        else // ResolvedMode
        {
            for (int i = 0; i < 3; i++)
            {
                if (generation != _selectionUpdateGeneration) return;
                if (i < effectiveAssets.Length)
                {
                    var asset = effectiveAssets[i];
                    var winner = asset.ResolvedOccurrence;
                    if (winner != null && effectiveSourceMap.TryGetValue(winner.SourceId, out var src))
                    {
                        await Slots[i].AssignOccurrenceAsync(asset, winner, src).ConfigureAwait(false);
                    }
                    else
                    {
                        await Slots[i].AssignOccurrenceAsync(asset, null, null).ConfigureAwait(false);
                    }
                }
                else
                {
                    await Slots[i].AssignOccurrenceAsync(null, null, null).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task ClearSelectionAsync()
    {
        int generation = Interlocked.Increment(ref _selectionUpdateGeneration);
        _selectedAssets = Array.Empty<CuratedAsset>();
        _sourceMap = new Dictionary<Guid, AssetSource>();
        await ClearAllSlotsAsync(generation).ConfigureAwait(false);
    }

    private async Task ClearAllSlotsAsync(int generation)
    {
        if (generation != _selectionUpdateGeneration)
        {
            return;
        }

        foreach (var slot in Slots)
        {
            if (generation != _selectionUpdateGeneration) return;
            await slot.AssignOccurrenceAsync(null, null, null).ConfigureAwait(false);
        }
    }
}
