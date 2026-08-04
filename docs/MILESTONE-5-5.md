# Milestone 5 - Phase 5: Verification and Validation

> [!NOTE]
> Reconciled during the Milestone 5 completion pass. Automated Testing is done — full suite is
> 249 passed / 2 skipped (both skips are NAudio playback assertions that need a live audio output
> device and are not reliable headlessly) / 0 failed, 251 total. `TextPreviewProvider` and
> `TreePreviewProvider` test coverage predates this pass and was left as-is. Manual Verification and
> Non-functional Checks were **not** performed in this session (no interactive desktop/audio-device
> testing was done) — those checkboxes are left unchecked intentionally; do not treat this milestone
> as fully verified until someone runs that pass.

## Scope
Validate correctness, parsing behavior, and UI workflows before milestone completion.

## Automated Testing
- [x] Run `dotnet test` at each phase boundary, not only at end of milestone.
- [x] Add unit tests in `SRN.CC.Tests`:
  - `ImagePreviewProvider` with mock streams:
    - valid TGA/DDS/PLT samples.
    - oversize image rejection.
  - `TextPreviewProvider`:
    - UTF-8 and CP1252 sample matrix.
    - invalid UTF sequences and binary-like files.
  - `AudioPreviewProvider`:
    - WAV valid format.
    - BMU header-stripping case.
    - invalid header and malformed stream handling.
    - seekability and position-scrub behavior across valid WAV/BMU streams.
  - `TreePreviewProvider`:
    - GFF/ITP tree shape rendering.
    - SSF header and entry decoding.
  - `DependencyAnalyzer`:
    - cycle-safe traversal.
    - MDL/MTR dependency extraction.
    - workspace vs catalog fallback semantics.
  - `PreviewSlotViewModel`:
    - zoom/pan updates.
    - playback command transitions.
    - dispose cleanup.
  - `ComparisonPanelViewModel`:
    - link toggle propagation.
    - deterministic audio propagation behavior.
- [x] Add fixture strategy:
  - valid, malformed, and edge-boundary files per family.
  - minimal synthetic fixtures for deterministic tests.
  - clear byte budgets for limit assertions.

## Manual Verification
- [ ] Launch `SRN.CC.App` and load a mixed fixture set (HAK + folder import).
- [ ] Verify image previews:
  - load TGA and DDS.
  - validate zoom/pan controls.
  - verify linked compare behavior across image slots.
- [ ] Verify audio previews:
  - load WAV and BMU.
  - test play/pause/stop and progress scrubbing.
  - validate synchronized playback when link mode allows.
- [ ] Verify tree/text/mdl previews:
  - open each supported extension.
  - confirm no UI lockups and meaningful error display on broken files.
- [ ] Verify dependency import flow:
  - invoke "Add available dependencies".
  - validate workspace-first resolution, closure preview, unresolved list.
  - verify unresolved list includes every missing dependency with originating parent trace.

## Non-functional Checks
- [ ] Performance:
  - no noticeable frame drops during repeated zoom/pan.
  - no long-tail hangs with large but in-budget files.
- [ ] Safety:
  - enforce image/text caps with deterministic rejection.
  - reject malformed input as classified errors.
- [ ] Reliability:
  - app exit during playback and slot swaps leaves no leaked threads/handles.
  - linked scrubbing does not lose playback position across participating slots.
- [ ] User feedback quality:
  - error states are clear and actionable.
  - unresolved list communicates source and reason.

## Completion Criteria
- [x] Phases 1-4 are complete and merged behind stable interfaces.
- [x] Automated tests covering each provider and dependency path pass.
- [ ] UI behavior is correct for:
  - linked image navigation
  - linked audio behavior
  - dependency selection + prompt flow.
  (requires the manual verification pass above, not yet performed)
- [x] Safety/validation limits enforced:
  - 4096x4096 max image dimension
  - 64 MiB max decoded image
  - 8 MiB max text preview payload
- [x] No regression in existing core and existing preview flows.
- [x] Unresolved dependency list is complete for traceability across all transitive dependencies.

## Optional follow-up
- If milestone is split for another release, add migration notes for persisted cache behavior and user settings defaults.
