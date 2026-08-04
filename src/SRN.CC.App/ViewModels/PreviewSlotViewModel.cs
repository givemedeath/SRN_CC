using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.App.ViewModels;

public partial class PreviewSlotViewModel : ObservableObject
{
    private readonly PreviewEngine _previewEngine;
    private readonly Func<AssetOccurrence, Task> _onPinRequested;
    private CancellationTokenSource? _currentCts;
    private bool _canPin = true;

    public int SlotIndex { get; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _slotTitle = string.Empty;

    [ObservableProperty]
    private CuratedAsset? _assignedAsset;

    [ObservableProperty]
    private AssetOccurrence? _assignedOccurrence;

    [ObservableProperty]
    private AssetSource? _assignedSource;

    [ObservableProperty]
    private PreviewFamily _preferredFamily = PreviewFamily.Metadata;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _formattedContent;

    /// <summary>
    /// The polymorphic content this slot hosts (architecture decision A3). Additive alongside
    /// <see cref="FormattedContent"/> in this slice — the AXAML template now renders through this
    /// property, but <see cref="FormattedContent"/> is kept exactly as-is for compatibility until a
    /// later cleanup removes it. Every reassignment (including to <c>null</c> when the slot is
    /// cleared or a preview fails) disposes whatever was previously here; see
    /// <see cref="OnContentChanging"/>.
    /// </summary>
    [ObservableProperty]
    private PreviewContentViewModel? _content;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _diagnosticsText;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isPinEnabled;

    public PreviewSlotViewModel(
        int slotIndex,
        PreviewEngine previewEngine,
        Func<AssetOccurrence, Task> onPinRequested)
    {
        SlotIndex = slotIndex;
        _previewEngine = previewEngine ?? throw new ArgumentNullException(nameof(previewEngine));
        _onPinRequested = onPinRequested ?? throw new ArgumentNullException(nameof(onPinRequested));
        SlotTitle = $"Slot {slotIndex + 1}";
    }

    public async Task AssignOccurrenceAsync(CuratedAsset? asset, AssetOccurrence? occ, AssetSource? source)
    {
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _currentCts = new CancellationTokenSource();

        AssignedAsset = asset;
        AssignedOccurrence = occ;
        AssignedSource = source;

        if (occ == null || source == null)
        {
            IsActive = false;
            IsLoading = false;
            SlotTitle = $"Slot {SlotIndex + 1} [Empty]";
            FormattedContent = "No asset assigned to this slot.";
            Content = new TextContentViewModel(PreferredFamily, FormattedContent);
            ErrorMessage = null;
            DiagnosticsText = null;
            IsPinned = false;
            UpdatePinAvailability();
            return;
        }

        IsActive = true;
        SlotTitle = $"Slot {SlotIndex + 1}: {occ.Identity.Resref}.{occ.Identity.ResourceType} ({source.Kind})";
        IsPinned = asset?.Pin != null && asset.Pin.SourceId == source.Id && asset.Pin.Locator.Equals(occ.Locator);

        IsLoading = true;
        UpdatePinAvailability();
        FormattedContent = null;
        Content = null;
        ErrorMessage = null;
        DiagnosticsText = null;

        var token = _currentCts.Token;

        try
        {
            var req = new PreviewRequest(occ, source, PreferredFamily);
            var res = await _previewEngine.ExecutePreviewAsync(req, debounceMs: 150, cancellationToken: token).ConfigureAwait(true);

            if (token.IsCancellationRequested) return;

            IsLoading = false;
            if (res.IsSuccess)
            {
                FormattedContent = res.FormattedContent;
                Content = PreviewContentFactory.Create(res);
                ErrorMessage = null;
                DiagnosticsText = res.Diagnostics.Count > 0 ? string.Join("\n", res.Diagnostics) : null;
            }
            else
            {
                FormattedContent = null;
                Content = null;
                ErrorMessage = res.ErrorMessage;
                DiagnosticsText = string.Join("\n", res.Diagnostics);
            }

            UpdatePinAvailability();
        }
        catch (OperationCanceledException)
        {
            // Canceled
            UpdatePinAvailability();
        }
        catch (Exception ex)
        {
            IsLoading = false;
            ErrorMessage = ex.Message;
            UpdatePinAvailability();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPinThisOccurrence))]
    private async Task PinThisOccurrenceAsync()
    {
        if (AssignedOccurrence != null)
        {
            await _onPinRequested(AssignedOccurrence).ConfigureAwait(false);
            IsPinned = true;
        }
    }

    private bool CanPinThisOccurrence() => IsPinEnabled;

    public void SetPinEnabled(bool canPin)
    {
        _canPin = canPin;
        UpdatePinAvailability();
        PinThisOccurrenceCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SwitchFamilyAsync(string familyName)
    {
        if (Enum.TryParse<PreviewFamily>(familyName, out var fam))
        {
            PreferredFamily = fam;
            if (AssignedAsset != null && AssignedOccurrence != null && AssignedSource != null)
            {
                await AssignOccurrenceAsync(AssignedAsset, AssignedOccurrence, AssignedSource).ConfigureAwait(false);
            }
        }
    }

    partial void OnIsActiveChanged(bool value) => UpdatePinAvailability();
    partial void OnAssignedOccurrenceChanged(AssetOccurrence? value) => UpdatePinAvailability();

    /// <summary>
    /// Every assignment to <see cref="Content"/> — including to <c>null</c> — routes through here
    /// before the new value takes effect, so the previous content is always disposed exactly once,
    /// regardless of which code path (empty slot, success, failure) performed the assignment.
    /// </summary>
    partial void OnContentChanging(PreviewContentViewModel? oldValue, PreviewContentViewModel? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue))
        {
            oldValue?.Dispose();
        }
    }

    private void UpdatePinAvailability()
    {
        IsPinEnabled = IsActive && _canPin && AssignedOccurrence != null && AssignedSource != null && !IsLoading;
        PinThisOccurrenceCommand.NotifyCanExecuteChanged();
    }
}
