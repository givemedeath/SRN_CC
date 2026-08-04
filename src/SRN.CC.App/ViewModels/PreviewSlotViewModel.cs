using System.ComponentModel;
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
                // Build the content VM before touching any observable state: if PreviewContentFactory.Create
                // throws (e.g. a malformed audio payload a provider's own validation missed), the catch
                // block below must not be reached with FormattedContent already holding this attempt's
                // value — it was already reset to null before the request started, so leaving it untouched
                // here keeps ErrorMessage and FormattedContent from disagreeing about whether this attempt
                // succeeded.
                PreviewContentViewModel content = PreviewContentFactory.Create(res);
                FormattedContent = res.FormattedContent;
                Content = content;
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
    /// Also unsubscribes from a departing <see cref="ModelViewportViewModel"/>'s
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> before it is disposed, matching the
    /// subscription added in <see cref="OnContentChanged"/> below.
    /// </summary>
    partial void OnContentChanging(PreviewContentViewModel? oldValue, PreviewContentViewModel? newValue)
    {
        if (ReferenceEquals(oldValue, newValue))
        {
            return;
        }

        if (oldValue is ModelViewportViewModel oldViewport)
        {
            oldViewport.PropertyChanged -= OnModelViewportContentPropertyChanged;
        }

        oldValue?.Dispose();
    }

    /// <summary>
    /// Observes a newly-assigned <see cref="ModelViewportViewModel"/> for
    /// <see cref="ModelViewportViewModel.RenderUnavailableReason"/> becoming non-null (a GPU
    /// capability shortfall, shader link failure, or the viewport-concurrency backstop refusing a
    /// fourth viewport — see <c>ModelViewportControl</c>/<c>ModelViewportRegistry</c>, slice S11) and
    /// degrades the slot to its text preview when that happens, per architecture decision A4.
    /// </summary>
    partial void OnContentChanged(PreviewContentViewModel? value)
    {
        if (value is ModelViewportViewModel viewport)
        {
            viewport.PropertyChanged += OnModelViewportContentPropertyChanged;

            if (viewport.RenderUnavailableReason is { } reasonAlreadySet)
            {
                DegradeModelViewportToText(reasonAlreadySet);
            }
        }
    }

    private void OnModelViewportContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModelViewportViewModel.RenderUnavailableReason))
        {
            return;
        }

        if (sender is ModelViewportViewModel { RenderUnavailableReason: { } reason })
        {
            DegradeModelViewportToText(reason);
        }
    }

    /// <summary>
    /// Swaps <see cref="Content"/> from an unavailable <see cref="ModelViewportViewModel"/> back to
    /// the reliable <see cref="TextContentViewModel"/> degradation target (the same
    /// <see cref="FormattedContent"/> the provider already produced), folding the unavailability
    /// reason into <see cref="DiagnosticsText"/> so it stays visible to the user.
    /// </summary>
    private void DegradeModelViewportToText(string reason)
    {
        Content = new TextContentViewModel(PreviewFamily.Model, FormattedContent);
        DiagnosticsText = string.IsNullOrEmpty(DiagnosticsText)
            ? reason
            : $"{DiagnosticsText}\n{reason}";
    }

    private void UpdatePinAvailability()
    {
        IsPinEnabled = IsActive && _canPin && AssignedOccurrence != null && AssignedSource != null && !IsLoading;
        PinThisOccurrenceCommand.NotifyCanExecuteChanged();
    }
}
