using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.App.ViewModels.Preview;
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

/// <summary>
/// Owns comparison-panel-wide state, including linked slot navigation (slice S14, closing the
/// milestone-5 UI debt recorded in <c>docs/MILESTONE-5.md</c>'s "Linked Navigation" note and
/// implementing <c>PLAN.md:120</c> verbatim: "Link image navigation and compatible 3D cameras by
/// default; link audio position only when every visible slot is audio; leave text/tree/table
/// scrolling unlinked.").
///
/// <para>
/// <b>Design.</b> This class never touches <see cref="PreviewSlotViewModel"/> or any content VM's
/// internals (per the S14 ownership rule) - it only observes <see cref="PreviewSlotViewModel.Content"/>
/// via <see cref="INotifyPropertyChanged.PropertyChanged"/> (the same "subscribe to a slot-level
/// PropertyChanged, watch for a specific property name" pattern <see cref="PreviewSlotViewModel"/>
/// itself already uses for <see cref="ModelViewportViewModel.RenderUnavailableReason"/>), and then
/// observes each hosted <see cref="ImageContentViewModel"/>/<see cref="ModelViewportViewModel"/>/
/// <see cref="Preview.AudioContentViewModel"/>'s own <c>PropertyChanged</c> for the specific
/// navigation property it cares about, writing the same value into every sibling slot's content of
/// the same concrete type.
/// </para>
///
/// <para>
/// <b>Re-entrancy guard.</b> A single <see cref="_isApplyingLinkedUpdate"/> flag is set for the
/// duration of a propagation pass. Every sibling write happens synchronously (CommunityToolkit's
/// generated property setters raise <c>PropertyChanged</c> inline), so the flag is set before the
/// first sibling write and cleared in a <c>finally</c> after the last one; any re-entrant
/// <c>PropertyChanged</c> the sibling writes provoke is short-circuited at the very top of the
/// handler. Each propagated value is also compared against the sibling's current value before
/// writing, so a sibling already at the target value never raises a redundant
/// <c>PropertyChanged</c> at all - belt-and-suspenders against both an infinite loop and visible
/// jitter.
/// </para>
///
/// <para>
/// <b>Audio "every visible slot" detection.</b> Per <c>PLAN.md:120</c> this is deliberately the
/// literal, strict reading: audio position links only when <i>all three</i> <see cref="Slots"/> -
/// not merely all currently-audio slots - are hosting an <see cref="Preview.AudioContentViewModel"/>
/// (<c>Slots.All(s => s.Content is AudioContentViewModel)</c>). An empty/unassigned slot's content is
/// always a placeholder <c>TextContentViewModel</c> (see <see cref="PreviewSlotViewModel.AssignOccurrenceAsync"/>),
/// never <c>null</c>, in real use, so this single check naturally covers "one slot is a different
/// family" and "one slot is empty" identically - both simply fail the <c>All</c> predicate. Recomputed
/// fresh on every <see cref="Preview.AudioContentViewModel.PositionProgress"/> change, so slot
/// reassignment (which arrives staggered per <see cref="UpdateSelectionAsync"/>) is always evaluated
/// against the live state, never a cached snapshot. Per the same clause, when the condition fails
/// (even if two of the three slots are still audio) linking is fully suspended - it does not
/// degenerate to "link whichever slots remain audio".
/// </para>
///
/// <para>
/// <b>Audio position vs. playback.</b> <see cref="Preview.AudioContentViewModel"/> is owned by S13
/// and exposes no seek method - only <c>PlayAsync</c>/<c>Pause</c>/<c>Stop</c> commands and the
/// read-oriented <c>RefreshPositionProgress</c> (which recomputes <c>PositionProgress</c> from the
/// player's own current time; it does not accept a target). The only lever this slice has, without
/// editing that file, is writing the sibling's observable <c>PositionProgress</c> directly. That is
/// exactly what happens: the sibling's displayed position fraction is updated to match the source,
/// and nothing else - <c>Play</c>/<c>Pause</c>/<c>Stop</c> are never invoked on a sibling as a result
/// of a linking action, so a sibling that is paused stays paused and a sibling that is playing keeps
/// playing (its own timer continues to advance its position on top of whatever this write left it
/// at). This satisfies "do NOT auto-start/stop playback on siblings" using the only public surface
/// available.
/// </para>
///
/// <para>
/// <b>Master toggle.</b> <see cref="LinkNavigationEnabled"/> (default <c>true</c>, matching
/// <c>PLAN.md:120</c>'s "by default") is the "links synchronization toggle" <c>docs/MILESTONE-5.md</c>
/// calls for under <c>ComparisonPanelViewModel</c>'s deliverables. It gates all three linking
/// behaviors (image, camera, audio) uniformly - flipping it off stops every propagation until it is
/// flipped back on. Wiring an actual toggle control into <c>ComparisonPanelView.axaml</c> is out of
/// scope for this slice (S10/S11 own that file); this property is deliberately a plain observable
/// bool so a future AXAML change can two-way bind a <c>ToggleButton.IsChecked</c> to it directly.
/// </para>
/// </summary>
public partial class ComparisonPanelViewModel : ObservableObject
{
    private readonly PreviewEngine _previewEngine;
    private readonly Func<AssetOccurrence, Task> _onPinRequested;
    private IReadOnlyList<CuratedAsset> _selectedAssets = Array.Empty<CuratedAsset>();
    private IReadOnlyDictionary<Guid, AssetSource> _sourceMap = new Dictionary<Guid, AssetSource>();
    private int _selectionUpdateGeneration;

    /// <summary>
    /// The items eligible to occupy a slot in the current mode: every occurrence of the target
    /// identity (occurrence mode) or every selected asset's winner (resolved mode). Slots show the
    /// first three by default; <see cref="ReplaceSlotAsync"/> cycles a slot through the rest.
    /// </summary>
    private List<SlotCandidate> _candidates = new();

    /// <summary>The candidate index shown in each of the three slots (-1 = empty).</summary>
    private readonly int[] _slotCandidateIndex = { 0, 1, 2 };

    private readonly record struct SlotCandidate(CuratedAsset? Asset, AssetOccurrence? Occurrence, AssetSource? Source);

    /// <summary>
    /// Tracks the most recently observed <see cref="PreviewSlotViewModel.Content"/> instance per slot,
    /// purely so <see cref="OnSlotPropertyChanged"/> can unsubscribe from the departing instance's
    /// <c>PropertyChanged</c> (matching the subscription added when it was assigned) without ever
    /// reacting to a stale/disposed content VM's events.
    /// </summary>
    private readonly Dictionary<PreviewSlotViewModel, PreviewContentViewModel?> _observedContent = new();

    /// <summary>Guards every propagation pass against re-entrant cascades; see the class remarks.</summary>
    private bool _isApplyingLinkedUpdate;

    [ObservableProperty]
    private ComparisonMode _mode = ComparisonMode.OccurrenceMode;

    /// <summary>
    /// Master on/off switch for linked slot navigation (image zoom/pan, 3D camera, and conditional
    /// audio position). Defaults on, per <c>PLAN.md:120</c>'s "by default" for image/3D linking; see
    /// the class remarks for why audio is still additionally gated by the all-slots-audio check even
    /// while this is on.
    /// </summary>
    [ObservableProperty]
    private bool _linkNavigationEnabled = true;

    public ObservableCollection<PreviewSlotViewModel> Slots { get; } = new();
    private bool _allowPin = true;

    public ComparisonPanelViewModel(
        PreviewEngine previewEngine,
        Func<AssetOccurrence, Task> onPinRequested)
    {
        _previewEngine = previewEngine ?? throw new ArgumentNullException(nameof(previewEngine));
        _onPinRequested = onPinRequested ?? throw new ArgumentNullException(nameof(onPinRequested));

        for (int i = 0; i < 3; i++)
        {
            var slot = new PreviewSlotViewModel(i, _previewEngine, _onPinRequested);
            Slots.Add(slot);
            _observedContent[slot] = null;
            slot.PropertyChanged += OnSlotPropertyChanged;
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
    /// <remarks>
    /// Every await in this view model keeps its context. The awaits here all lead to slot state that
    /// is bound to live UI — content, titles, pin availability — and Avalonia throws when bound
    /// state is mutated off the UI thread, which on a command handler means the process goes down
    /// rather than an exception being reported.
    /// <para>
    /// The failure was not evenly distributed, which is what made it look like a mode-switch bug
    /// specifically. A slot assignment invoked <em>from</em> the UI thread resumes there, so the
    /// first one in a loop is always safe; it is the second and later ones, resumed on a pool thread
    /// after the first completed, that cross over. Toggling the mode reassigns every slot at once
    /// and so hits that path immediately.
    /// </para>
    /// </remarks>
    private async Task ToggleModeAsync()
    {
        Mode = Mode == ComparisonMode.OccurrenceMode ? ComparisonMode.ResolvedMode : ComparisonMode.OccurrenceMode;
        await UpdateSelectionAsync(_selectedAssets, _sourceMap).ConfigureAwait(true);
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

        // Rebuild the candidate list and reset each slot to its default candidate. This is what makes
        // a selection change (and a mode switch) clear any manual Replace-Slot assignments, per
        // PLAN.md:117.
        _candidates = BuildCandidates(Mode, effectiveAssets, effectiveSourceMap);
        _slotCandidateIndex[0] = 0;
        _slotCandidateIndex[1] = 1;
        _slotCandidateIndex[2] = 2;

        for (int i = 0; i < 3; i++)
        {
            if (generation != _selectionUpdateGeneration) return;
            await AssignSlotCandidateAsync(i, generation).ConfigureAwait(true);
        }

        ReplaceSlotCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasOverflowCandidates));
    }

    private static List<SlotCandidate> BuildCandidates(
        ComparisonMode mode,
        IReadOnlyList<CuratedAsset> assets,
        IReadOnlyDictionary<Guid, AssetSource> sourceMap)
    {
        List<SlotCandidate> candidates = new();

        if (mode == ComparisonMode.OccurrenceMode)
        {
            var target = assets.FirstOrDefault();
            if (target == null)
            {
                return candidates;
            }
            foreach (var occ in target.AllOccurrences)
            {
                sourceMap.TryGetValue(occ.SourceId, out var src);
                candidates.Add(new SlotCandidate(target, occ, src));
            }
        }
        else // ResolvedMode
        {
            foreach (var asset in assets)
            {
                var winner = asset.ResolvedOccurrence;
                if (winner != null && sourceMap.TryGetValue(winner.SourceId, out var src))
                {
                    candidates.Add(new SlotCandidate(asset, winner, src));
                }
                else
                {
                    candidates.Add(new SlotCandidate(asset, null, null));
                }
            }
        }

        return candidates;
    }

    private async Task AssignSlotCandidateAsync(int slotIndex, int generation)
    {
        if (generation != _selectionUpdateGeneration) return;

        int idx = _slotCandidateIndex[slotIndex];
        if (idx >= 0 && idx < _candidates.Count)
        {
            var c = _candidates[idx];
            await Slots[slotIndex].AssignOccurrenceAsync(c.Asset, c.Occurrence, c.Source).ConfigureAwait(true);
        }
        else
        {
            await Slots[slotIndex].AssignOccurrenceAsync(null, null, null).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Explicitly cycles a slot to the next selected candidate not already shown in another slot
    /// (PLAN.md:116). Available only when there are more candidates than the three visible slots. The
    /// assignment lasts until the next selection change or mode switch, which resets every slot.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReplaceSlot))]
    private async Task ReplaceSlotAsync(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex > 2)
        {
            return;
        }

        int count = _candidates.Count;
        if (count <= 3)
        {
            return;
        }

        HashSet<int> shownElsewhere = new();
        for (int s = 0; s < 3; s++)
        {
            if (s != slotIndex && _slotCandidateIndex[s] >= 0)
            {
                shownElsewhere.Add(_slotCandidateIndex[s]);
            }
        }

        int current = _slotCandidateIndex[slotIndex] < 0 ? 0 : _slotCandidateIndex[slotIndex];
        for (int step = 1; step <= count; step++)
        {
            int cand = (current + step) % count;
            if (!shownElsewhere.Contains(cand))
            {
                _slotCandidateIndex[slotIndex] = cand;
                break;
            }
        }

        // Not a selection change: reuse the current generation so slot-guard checks still pass.
        await AssignSlotCandidateAsync(slotIndex, _selectionUpdateGeneration).ConfigureAwait(true);
    }

    private bool CanReplaceSlot(int slotIndex) => _candidates.Count > 3;

    /// <summary>Whether more candidates exist than the three visible slots (bindable for the UI).</summary>
    public bool HasOverflowCandidates => _candidates.Count > 3;

    public async Task ClearSelectionAsync()
    {
        int generation = Interlocked.Increment(ref _selectionUpdateGeneration);
        _selectedAssets = Array.Empty<CuratedAsset>();
        _sourceMap = new Dictionary<Guid, AssetSource>();
        _candidates = new();
        _slotCandidateIndex[0] = 0;
        _slotCandidateIndex[1] = 1;
        _slotCandidateIndex[2] = 2;
        await ClearAllSlotsAsync(generation).ConfigureAwait(true);
        ReplaceSlotCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasOverflowCandidates));
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
            await slot.AssignOccurrenceAsync(null, null, null).ConfigureAwait(true);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Linked navigation (slice S14) - see the class-level remarks for the full design rationale.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Fired for every one of the fixed three <see cref="Slots"/> whenever
    /// <see cref="PreviewSlotViewModel.Content"/> is reassigned (including to a value of the same
    /// type, or to <c>null</c>). Maintains the <see cref="_observedContent"/> subscription so linking
    /// always tracks the slot's live content and never a stale, possibly-disposed instance.
    /// </summary>
    private void OnSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PreviewSlotViewModel.Content) || sender is not PreviewSlotViewModel slot)
        {
            return;
        }

        _observedContent.TryGetValue(slot, out PreviewContentViewModel? oldContent);
        PreviewContentViewModel? newContent = slot.Content;

        if (ReferenceEquals(oldContent, newContent))
        {
            return;
        }

        if (IsLinkableContent(oldContent))
        {
            oldContent!.PropertyChanged -= OnLinkableContentPropertyChanged;
        }

        if (IsLinkableContent(newContent))
        {
            newContent!.PropertyChanged += OnLinkableContentPropertyChanged;
        }

        _observedContent[slot] = newContent;
    }

    /// <summary>
    /// The three content types this slice links. Deliberately excludes <see cref="TextContentViewModel"/>
    /// (and anything else) - per <c>PLAN.md:120</c>, text/tree/table/metadata/hex scrolling stays
    /// unlinked, and since that family has no scroll-position state on its VM today (it lives in the
    /// View), there is nothing to subscribe to for it anyway.
    /// </summary>
    private static bool IsLinkableContent(PreviewContentViewModel? content) =>
        content is ImageContentViewModel or ModelViewportViewModel or AudioContentViewModel;

    /// <summary>
    /// Dispatches a navigation-relevant property change on a hosted content VM to the matching
    /// propagation routine. Every propagation routine re-checks <see cref="LinkNavigationEnabled"/>
    /// and the re-entrancy guard itself, but the fast-exit checks here avoid touching <see cref="Slots"/>
    /// at all when linking is off or already mid-propagation.
    /// </summary>
    private void OnLinkableContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingLinkedUpdate || !LinkNavigationEnabled)
        {
            return;
        }

        switch (sender)
        {
            case ImageContentViewModel image
                when e.PropertyName is nameof(ImageContentViewModel.ZoomScale)
                    or nameof(ImageContentViewModel.PanX)
                    or nameof(ImageContentViewModel.PanY):
                PropagateImageNavigation(image);
                break;

            case ModelViewportViewModel viewport when e.PropertyName == nameof(ModelViewportViewModel.Camera):
                PropagateCamera(viewport);
                break;

            case AudioContentViewModel audio when e.PropertyName == nameof(AudioContentViewModel.PositionProgress):
                PropagateAudioPosition(audio);
                break;
        }
    }

    /// <summary>
    /// Image linking, unconditional whenever applicable (<c>PLAN.md:120</c>: "link image navigation
    /// ... by default"). Copies <see cref="ImageContentViewModel.ZoomScale"/>/<see cref="ImageContentViewModel.PanX"/>/
    /// <see cref="ImageContentViewModel.PanY"/> from <paramref name="source"/> to every other active
    /// slot currently hosting an <see cref="ImageContentViewModel"/>.
    /// </summary>
    private void PropagateImageNavigation(ImageContentViewModel source) =>
        PropagateLinked(source, static (target, source) =>
        {
            if (!target.ZoomScale.Equals(source.ZoomScale))
            {
                target.ZoomScale = source.ZoomScale;
            }

            if (!target.PanX.Equals(source.PanX))
            {
                target.PanX = source.PanX;
            }

            if (!target.PanY.Equals(source.PanY))
            {
                target.PanY = source.PanY;
            }
        });

    /// <summary>
    /// 3D camera linking, unconditional whenever applicable (<c>PLAN.md:120</c>: "link ... compatible
    /// 3D cameras by default" - "compatible" here just means "also a model viewport"; there is no
    /// further notion of camera incompatibility between two <see cref="ModelViewportViewModel"/>
    /// instances). Copies <see cref="ModelViewportViewModel.Camera"/> only - <see cref="ModelViewportViewModel.ShowWalkmesh"/>
    /// is deliberately left untouched, since the plan asks for camera state, not the walkmesh toggle.
    /// </summary>
    private void PropagateCamera(ModelViewportViewModel source) =>
        PropagateLinked(source, static (target, source) =>
        {
            if (target.Camera != source.Camera)
            {
                target.Camera = source.Camera;
            }
        });

    /// <summary>
    /// Audio position linking, conditional (<c>PLAN.md:120</c>: "link audio position only when every
    /// visible slot is audio"). Re-evaluates <see cref="AllSlotsAreAudio"/> fresh on every call - never
    /// a cached flag - so a slot reassignment that lands between two audio position changes is always
    /// picked up. See the class remarks for exactly what "propagate position" means given
    /// <see cref="AudioContentViewModel"/>'s public surface.
    /// </summary>
    private void PropagateAudioPosition(AudioContentViewModel source)
    {
        if (!AllSlotsAreAudio())
        {
            return;
        }

        PropagateLinked(source, static (target, source) =>
        {
            if (!target.PositionProgress.Equals(source.PositionProgress))
            {
                target.PositionProgress = source.PositionProgress;
            }
        });
    }

    /// <summary>
    /// Shared shape behind <see cref="PropagateImageNavigation"/>/<see cref="PropagateCamera"/>/
    /// <see cref="PropagateAudioPosition"/>: set the re-entrancy guard, walk every active slot whose
    /// content is the same concrete type as <paramref name="source"/> (skipping <paramref name="source"/>
    /// itself), and hand each matching target/source pair to <paramref name="copyIfDifferent"/>. Any
    /// family-specific precondition (e.g. <see cref="AllSlotsAreAudio"/>) is the caller's
    /// responsibility to check before calling this, since it must run outside the guard.
    /// </summary>
    private void PropagateLinked<TContent>(TContent source, Action<TContent, TContent> copyIfDifferent)
        where TContent : PreviewContentViewModel
    {
        _isApplyingLinkedUpdate = true;
        try
        {
            foreach (PreviewSlotViewModel slot in Slots)
            {
                if (!slot.IsActive || slot.Content is not TContent target || ReferenceEquals(target, source))
                {
                    continue;
                }

                copyIfDifferent(target, source);
            }
        }
        finally
        {
            _isApplyingLinkedUpdate = false;
        }
    }

    /// <summary>
    /// True only when all three fixed <see cref="Slots"/> are simultaneously hosting an
    /// <see cref="AudioContentViewModel"/> - the literal "every visible slot is audio" reading of
    /// <c>PLAN.md:120</c>. An empty/unassigned slot's content is a placeholder
    /// <see cref="TextContentViewModel"/> in real use (never <c>null</c>), so it fails this check the
    /// same way a Text/Image/Model slot would; a slot mid-reload (<see cref="PreviewSlotViewModel.Content"/>
    /// transiently <c>null</c>) also fails it, which simply pauses linking until the reload settles.
    /// </summary>
    private bool AllSlotsAreAudio()
    {
        foreach (PreviewSlotViewModel slot in Slots)
        {
            if (slot.Content is not AudioContentViewModel)
            {
                return false;
            }
        }

        return true;
    }
}
