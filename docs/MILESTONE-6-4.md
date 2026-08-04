# Milestone 6 - Phase 4: Model Viewport and Remaining M5 UI Debt (Wave 3)

## Scope

With S10's polymorphic content surface in place, five slices land in parallel: the model viewport
itself (the headline feature, and the slice that carries the "only for visible 3D slots" gate
evidence), plus the three remaining Milestone-5 UI-debt items (image zoom/pan, audio transport,
linked navigation) and the App-level texture-source wiring. None of these five slices' file sets
overlap, by construction of A3 and the ownership rule.

## Deliverables

- `ModelViewportControl` (`OpenGlControlBase`), `ModelViewportViewModel` (camera state,
  orbit/pan/dolly/reset commands, walkmesh toggle, disposal), and `ModelViewportRegistry`
  (`MaxConcurrent = 3`) implementing A4's GPU lifetime rules end to end.
- `ImageContentViewModel` with `ZoomScale`/`PanX`/`PanY`.
- `AudioContentViewModel` with `PlayCommand`/`PauseCommand`/`StopCommand`/`PositionProgress` and
  NAudio player lifetime management.
- Linked navigation in `ComparisonPanelViewModel`: images and compatible 3D cameras linked by
  default; audio position linked only when every visible slot is audio; text/tree/table unlinked.
- `App.axaml.cs`/`MainWindowViewModel.cs` wiring of the late-bound `textureSourceAccessor` into
  `MdlPreviewProvider`.

## Detailed tasks

### S11 — Model viewport (needs S8, S9, S10)
Owns NEW `src/SRN.CC.App/ViewModels/Preview/ModelViewportViewModel.cs` (camera state,
orbit/pan/dolly/reset commands, `ShowWalkmesh` toggle, disposal),
`src/SRN.CC.App/Views/ModelViewportControl.cs`, `ModelViewportRegistry.cs`,
`Views/PreviewTemplates/ModelPreviewTemplate.axaml`; NEW
`tests/SRN.CC.Tests/UI/ModelViewportControlTests.cs`,
`tests/SRN.CC.Tests/App/ModelViewportViewModelTests.cs`.
- [ ] Implement `ModelViewportControl : OpenGlControlBase` (the only Avalonia+GL file in the whole
      milestone, per A1): `OnOpenGlInit` constructs `SilkGlDevice` + `ModelRenderer`, initializes
      against capabilities, raises `RenderUnavailable` on failure; `OnOpenGlRender` renders;
      `OnOpenGlDeinit` disposes renderer + device (deletes every GL name); `OnOpenGlLost` calls
      `Abandon()`/`MarkLost()` with **zero GL calls**.
- [ ] Implement `ModelViewportViewModel`: `RenderCamera` state, `Orbit`/`Pan`/`Dolly`/`Reset`
      commands, `ShowWalkmesh` toggle, full `IDisposable` cleanup.
- [ ] Implement `ModelViewportRegistry` with `MaxConcurrent = 3`, incremented in
      `OnAttachedToVisualTree`, decremented in `OnDetachedFromVisualTree`; a fourth concurrent
      request refuses to initialize and degrades.
- [ ] Add `ModelPreviewTemplate.axaml` as the `DataTemplate` for `ModelViewportViewModel`, merged
      into the `Views/PreviewTemplates/` dictionary from S10.
- [ ] Ensure `PreviewSlotViewModel.Content` is a `ModelViewportViewModel` **only** for successful
      model previews (structural enforcement, not `IsVisible`-based) — `ContentPresenter` then
      realizes no viewport for any non-model slot.
- [ ] Wire `RenderUnavailable` to flip the slot back to `FormattedContent` (existing MDL text
      preview) with the reason surfaced in diagnostics text.

**Exit Criteria** — carries the "only for visible 3D slots" gate:
- [ ] A headless visual-tree walk shows **zero** `ModelViewportControl` instances when all three
      slots are text-family.
- [ ] Exactly one `ModelViewportControl` instance when one slot is Model.
- [ ] Three instances when all three slots are Model.
- [ ] Zero instances again after `ClearSelectionAsync`.
- [ ] A fourth concurrent viewport request is refused by `ModelViewportRegistry`.
- [ ] The control detaches cleanly without `OnOpenGlInit` ever firing under headless test conditions
      (headless has no GL backend — this is asserted, not fought).
- [ ] A `RenderUnavailable` signal flips the slot to `FormattedContent` with the reason present in
      `DiagnosticsText`.

### S12 — Image content: zoom/pan (M5 debt; needs S10)
Owns NEW `src/SRN.CC.App/ViewModels/Preview/ImageContentViewModel.cs` (`ZoomScale`, `PanX`, `PanY`),
`Views/PreviewTemplates/ImagePreviewTemplate.axaml`; NEW
`tests/SRN.CC.Tests/App/ImageContentViewModelTests.cs`.
- [ ] Implement `ImageContentViewModel : PreviewContentViewModel` with observable `ZoomScale`,
      `PanX`, `PanY` properties, built from `res.RawPayload` BGRA bytes (via `TextureDecoder`/image
      dimensions in `FormattedContent`).
- [ ] Implement `ImagePreviewTemplate.axaml`: image surface with render-transform-driven zoom/pan.
- [ ] Add unit tests for zoom/pan property updates and bounds.

**Exit Criteria**
- [ ] `ImageContentViewModelTests.cs` passes for zoom/pan state transitions.
- [ ] Image previews render through the new template with working zoom/pan.

### S13 — Audio content: transport (M5 debt; needs S10)
Owns NEW `src/SRN.CC.App/ViewModels/Preview/AudioContentViewModel.cs` (`PlayCommand`,
`PauseCommand`, `StopCommand`, `PositionProgress`, NAudio player lifetime + disposal),
`Views/PreviewTemplates/AudioPreviewTemplate.axaml`; NEW
`tests/SRN.CC.Tests/App/AudioContentViewModelTests.cs`.
- [ ] Implement `AudioContentViewModel : PreviewContentViewModel` with `PlayCommand`,
      `PauseCommand`, `StopCommand`, `PositionProgress`, wrapping an NAudio wave player over the
      preview's audio stream.
- [ ] Implement `AudioPreviewTemplate.axaml`: transport controls + progress slider.
- [ ] Dispose the player and its streams on reassignment and on slot clear.

**Exit Criteria**
- [ ] Strict slot isolation between audio content instances.
- [ ] The player and its streams dispose on reassignment and on slot clear (no leaked handles).

### S14 — Linked navigation (M5 debt; needs S10)
Owns MODIFY `src/SRN.CC.App/ViewModels/ComparisonPanelViewModel.cs`; NEW
`tests/SRN.CC.Tests/App/LinkedNavigationTests.cs`. Must not touch `PreviewSlotViewModel.cs` or any
content VM — propagate through the `PreviewContentViewModel` base only.
- [ ] Link image zoom/pan navigation across visible image slots by default (per `PLAN.md:120`).
- [ ] Link compatible 3D camera state across visible model slots by default.
- [ ] Link audio position **only** when every visible slot is audio.
- [ ] Leave text/tree/table scrolling unlinked.
- [ ] Implement the link-toggle mechanism surfaced to the UI.

**Exit Criteria**
- [ ] `LinkedNavigationTests.cs` proves: image link propagation, 3D camera link propagation
      (when compatible), audio-only-when-all-slots-audio propagation, and text/tree/table remaining
      unlinked.
- [ ] No edits to `PreviewSlotViewModel.cs` or any content VM file.

### S15 — App wiring (needs S7, S9)
Owns MODIFY `src/SRN.CC.App/App.axaml.cs` (the provider array at lines 51-60 — pass the
`textureSourceAccessor`), `src/SRN.CC.App/ViewModels/MainWindowViewModel.cs` (accessor field + one
line in `LoadWorkspaceStateAsync`). Must not touch any `Preview/` VM, any template, or
`ComparisonPanelView.axaml`.
- [ ] Construct `MdlPreviewProvider` with `textureSourceAccessor: () => _textureSource` in
      `App.axaml.cs`, mirroring how `DependencyLocator` is already lazily constructed.
- [ ] Add the `_textureSource` field to `MainWindowViewModel`, assigned in
      `LoadWorkspaceStateAsync` once a `WorkspaceTextureSource` can be built.
- [ ] Confirm a null accessor result yields an untextured scene plus a diagnostic rather than a
      failure (3D previews still work before a workspace loads).

**Exit Criteria**
- [ ] The parameterless designer constructor still works with a null texture source.

## Notes
- This wave is fully parallel: A3 (S10, wave 2) already made `PreviewSlotViewModel.cs` and
  `ComparisonPanelView.axaml` off-limits to every slice here, and each of S11-S15 owns disjoint new
  files (plus, for S14 and S15, a single already-narrow modify target).
- S11 is the largest and most gate-critical slice in this wave; S12/S13/S14/S15 have no cross-slice
  dependencies among themselves.
