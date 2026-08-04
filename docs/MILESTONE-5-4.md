# Milestone 5 - Phase 4: Application Shell and UI

> [!NOTE]
> Reconciled against delivered code during the Milestone 5 completion pass. Remaining unchecked items
> are genuine gaps, not oversights: pan/zoom debounce-to-frame-boundary, a non-seekable-stream spool
> (moot today since `AudioPreviewProvider` always returns a fully materialized, seekable WAV byte
> array), a "force sync current slot" command, last-synced-transform replay on new linked assignments,
> a disabled-state affordance for not-ready providers, persisted linked-navigation preference (no
> settings infrastructure exists yet to persist into), and the "late provider load while linked" edge
> case (untested).

## Scope
Wire preview providers into the UI runtime, add slot isolation, caching, linked navigation, and media playback controls.

## Deliverables
- Runtime integration of preview providers with reusable cache.
- Slot-level view model state model for zoom/pan and playback.
- Synchronized behavior between slots when linked navigation is enabled.
- Stable and user-controllable family switching and templates.

## Detailed tasks

### Architecture changes and state model
- [x] Introduce state flow where `ComparisonPanelViewModel` owns shared link state and slot-level deltas.
- [x] Define slot-family update contract to prevent stale family transitions.
- [x] Ensure command/property updates stay on UI thread and remain serializable for bindings.
- [x] Preserve strict isolation:
  - actions in one slot must not affect another unless explicitly linked.
  - cross-slot updates only occur via panel-level synchronization.

### Preview Slot View Model
- [x] Modify `src/SRN.CC.App/ViewModels/PreviewSlotViewModel.cs`.
- [x] Add cache integration with `ISqliteCacheService`:
  - attempt thumbnail cache lookups before decoding on supported image formats.
  - persist PNG previews after successful decode.
- [x] Add zoom/pan state:
  - `ZoomScale`
  - `PanX`
  - `PanY`
- [x] Propagate zoom/pan changes to parent panel model.
- [x] Add audio playback state and commands:
  - `PlayCommand`
  - `PauseCommand`
  - `StopCommand`
  - `PositionProgress`
- [x] Bind NAudio player and manage `IDisposable` stream/player lifecycle.
- [x] Preserve strict slot isolation while allowing parent-driven linkage.
- [ ] Add preview throttling to avoid over-render on every mouse move:
  - debounce pan/zoom updates to UI frame boundary.
- [x] Add defensive checks for invalid dimensions/NaN scale values.

### Audio playback lifecycle
- [x] Implement player state transitions:
  - Idle -> Playing -> Paused -> Stopped.
- [x] Track stream ownership and close semantics per playback start.
- [x] Ensure progress updates are bounded and monotonic.
- [x] Handle stream errors by exposing user-visible status.
- [x] Explicitly free old player and stream before starting a new slot playback.
- [x] Require a seekable playback stream for all audio slots to guarantee scrubbing and position display.
- [ ] For non-seekable providers, build a bounded seekable spool in PreviewSlotViewModel before playback begins.

### Comparison Panel View Model
- [x] Modify `src/SRN.CC.App/ViewModels/ComparisonPanelViewModel.cs`.
- [x] Expose shared zoom/pan state.
- [x] Add linked navigation status state.
- [x] Propagate zoom/pan across slots when linking is enabled.
- [x] Propagate audio play/pause/position across slots when all active slots are audio previews.
- [x] Propagate seek position updates from the master audio slot when audio linking is active.
- [ ] Add commands for link toggle and force sync of current slot state.
- [x] Keep audio linking behavior behind family checks to avoid desync.
- [x] Prevent recursive propagation loops with origin slot ID.
- [ ] Record last synced transform values for deterministic replay.

### Comparison Panel View
- [x] Modify `src/SRN.CC.App/Views/ComparisonPanelView.axaml`.
- [x] Render conditional templates by `Family`:
  - text scroll box
  - image canvas with render transforms
  - audio player UI (play/pause/stop, progress slider)
- [x] Add linked navigation toggle UI control.
- [x] Add manual slot family selector.
- [x] Ensure bindings are virtualization-safe and do not recreate heavy controls on every property change.
- [x] Add visual affordance for "linked" state (icon or badge).
- [ ] Add disabled-state behavior when provider not ready.

### User Requirements
- [x] Apply default linked navigation behavior:
  - zoom/pan actions in one image slot propagate to other image slots.
- [x] Keep a UI-visible toggle to disable/enable linked behavior.
- [ ] Persist user choice for linked navigation if settings infrastructure exists.

## Edge cases
- [x] Mixed-family slots in comparison panel.
- [ ] Late provider load while link state is on.
- [x] Slot replacement while a linked audio playback is active.
- [x] Cache miss/hit race while quickly swapping image assets.

## Exit Criteria
- [x] Linked navigation affects image slots only by default, with explicit toggle behavior.
- [x] Playback controls are isolated per slot, and linked play/pause only affects intended synchronized slots.
- [x] No leaked players/streams after slot removal or replacement.
