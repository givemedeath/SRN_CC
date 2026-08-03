using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Sources;
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

    [ObservableProperty]
    private ComparisonMode _mode = ComparisonMode.OccurrenceMode;

    public ObservableCollection<PreviewSlotViewModel> Slots { get; } = new();

    public ComparisonPanelViewModel(
        PreviewEngine previewEngine,
        Func<AssetOccurrence, Task> onPinRequested)
    {
        _previewEngine = previewEngine ?? throw new ArgumentNullException(nameof(previewEngine));
        _onPinRequested = onPinRequested ?? throw new ArgumentNullException(nameof(onPinRequested));

        for (int i = 0; i < 3; i++)
        {
            Slots.Add(new PreviewSlotViewModel(i, _previewEngine, _onPinRequested));
        }
    }

    [RelayCommand]
    private async Task ToggleModeAsync()
    {
        Mode = Mode == ComparisonMode.OccurrenceMode ? ComparisonMode.ResolvedMode : ComparisonMode.OccurrenceMode;
        await ClearAllSlotsAsync().ConfigureAwait(false);
    }

    public async Task UpdateSelectionAsync(
        IReadOnlyList<CuratedAsset> selectedAssets,
        IReadOnlyDictionary<Guid, AssetSource> sourceMap)
    {
        if (Mode == ComparisonMode.OccurrenceMode)
        {
            var targetAsset = selectedAssets.FirstOrDefault();
            if (targetAsset == null)
            {
                await ClearAllSlotsAsync().ConfigureAwait(false);
                return;
            }

            var occurrences = targetAsset.AllOccurrences;
            for (int i = 0; i < 3; i++)
            {
                if (i < occurrences.Count)
                {
                    var occ = occurrences[i];
                    sourceMap.TryGetValue(occ.SourceId, out var src);
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
                if (i < selectedAssets.Count)
                {
                    var asset = selectedAssets[i];
                    var winner = asset.ResolvedOccurrence;
                    if (winner != null && sourceMap.TryGetValue(winner.SourceId, out var src))
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

    private async Task ClearAllSlotsAsync()
    {
        foreach (var slot in Slots)
        {
            await slot.AssignOccurrenceAsync(null, null, null).ConfigureAwait(false);
        }
    }
}
