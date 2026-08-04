# Milestone 5 - Phase 4: Application Shell and UI

## Scope
Wire preview providers into the UI runtime, add slot isolation, caching, linked navigation, and media playback controls.

## Deliverables
- Runtime integration of preview providers with reusable cache.
- Slot-level view model state model for zoom/pan and playback.
- Synchronized behavior between slots when linked navigation is enabled.
- Stable and user-controllable family switching and templates.

## Detailed tasks

### Architecture changes and state model
- [ ] Introduce state flow where `ComparisonPanelViewModel` owns shared link state and slot-level deltas.
- [ ] Define slot-family update contract to prevent stale family transitions.
- [ ] Ensure command/property updates stay on UI thread and remain serializable for bindings.
- [ ] Preserve strict isolation:
  - actions in one slot must not affect another unless explicitly linked.
  - cross-slot updates only occur via panel-level synchronization.

### Preview Slot View Model
- [ ] Modify `src/SRN.CC.App/ViewModels/PreviewSlotViewModel.cs`.
- [ ] Add cache integration with `ISqliteCacheService`:
  - attempt thumbnail cache lookups before decoding on supported image formats.
  - persist PNG previews after successful decode.
- [ ] Add zoom/pan state:
  - `ZoomScale`
  - `PanX`
  - `PanY`
- [ ] Propagate zoom/pan changes to parent panel model.
- [ ] Add audio playback state and commands:
  - `PlayCommand`
  - `PauseCommand`
  - `StopCommand`
  - `PositionProgress`
- [ ] Bind NAudio player and manage `IDisposable` stream/player lifecycle.
- [ ] Preserve strict slot isolation while allowing parent-driven linkage.
- [ ] Add preview throttling to avoid over-render on every mouse move:
  - debounce pan/zoom updates to UI frame boundary.
- [ ] Add defensive checks for invalid dimensions/NaN scale values.

### Audio playback lifecycle
- [ ] Implement player state transitions:
  - Idle -> Playing -> Paused -> Stopped.
- [ ] Track stream ownership and close semantics per playback start.
- [ ] Ensure progress updates are bounded and monotonic.
- [ ] Handle stream errors by exposing user-visible status.
- [ ] Explicitly free old player and stream before starting a new slot playback.
- [ ] Require a seekable playback stream for all audio slots to guarantee scrubbing and position display.
- [ ] For non-seekable providers, build a bounded seekable spool in PreviewSlotViewModel before playback begins.

### Comparison Panel View Model
- [ ] Modify `src/SRN.CC.App/ViewModels/ComparisonPanelViewModel.cs`.
- [ ] Expose shared zoom/pan state.
- [ ] Add linked navigation status state.
- [ ] Propagate zoom/pan across slots when linking is enabled.
- [ ] Propagate audio play/pause/position across slots when all active slots are audio previews.
- [ ] Propagate seek position updates from the master audio slot when audio linking is active.
- [ ] Add commands for link toggle and force sync of current slot state.
- [ ] Keep audio linking behavior behind family checks to avoid desync.
- [ ] Prevent recursive propagation loops with origin slot ID.
- [ ] Record last synced transform values for deterministic replay.

### Comparison Panel View
- [ ] Modify `src/SRN.CC.App/Views/ComparisonPanelView.axaml`.
- [ ] Render conditional templates by `Family`:
  - text scroll box
  - image canvas with render transforms
  - audio player UI (play/pause/stop, progress slider)
- [ ] Add linked navigation toggle UI control.
- [ ] Add manual slot family selector.
- [ ] Ensure bindings are virtualization-safe and do not recreate heavy controls on every property change.
- [ ] Add visual affordance for "linked" state (icon or badge).
- [ ] Add disabled-state behavior when provider not ready.

### User Requirements
- [ ] Apply default linked navigation behavior:
  - zoom/pan actions in one image slot propagate to other image slots.
- [ ] Keep a UI-visible toggle to disable/enable linked behavior.
- [ ] Persist user choice for linked navigation if settings infrastructure exists.

## Edge cases
- [ ] Mixed-family slots in comparison panel.
- [ ] Late provider load while link state is on.
- [ ] Slot replacement while a linked audio playback is active.
- [ ] Cache miss/hit race while quickly swapping image assets.

## Exit Criteria
- [ ] Linked navigation affects image slots only by default, with explicit toggle behavior.
- [ ] Playback controls are isolated per slot, and linked play/pause only affects intended synchronized slots.
- [ ] No leaked players/streams after slot removal or replacement.
