# Milestone 6 - Phase 3: Provider Pivot and Slot Content Surface (Wave 2)

## Scope

The serial pivot wave. `MdlPreviewProvider` starts attaching real `RenderScene` payloads to
`PreviewResult`, backed by a scene cache; then, once that lands, the App-side slot content surface
is refactored from a single read-only `TextBox` into polymorphic per-family content (A3). These two
slices are **serial**, not parallel — S10 needs S9's `Payload`-bearing `PreviewResult` to build
`PreviewContentFactory` against, and S10 is deliberately the only slice in the whole milestone that
touches `PreviewSlotViewModel.cs` and `ComparisonPanelView.axaml`, which is what makes wave 3 fully
parallel.

## Deliverables

- `MdlPreviewProvider` attaching `ModelScenePayload` to `PreviewResult.Payload` on success, with the
  existing text-metadata and header-fallback paths fully preserved as the degradation target.
- `ModelSceneCache` keyed by `(SourceId, Locator, Sha256)`, bounded by `ModelSceneCacheBudgetBytes`.
- The A3 polymorphic slot content surface: `PreviewContentViewModel` base, `PreviewContentFactory`,
  and the `PreviewSlotViewModel`/`ComparisonPanelView.axaml` edits that make every later
  family-specific slice (S11-S14) additive-only.

## Detailed tasks

### S9 — MdlPreviewProvider upgrade + scene cache (needs S6b, S7, S2)
Owns MODIFY `src/SRN.CC.Preview/MdlPreviewProvider.cs`; NEW
`src/SRN.CC.Preview/Render/ModelSceneCache.cs`; NEW
`tests/SRN.CC.Tests/Preview/Render/ModelSceneCacheTests.cs`,
`tests/SRN.CC.Tests/Preview/MdlPreviewProviderTests.cs`.
- [ ] Build a `RenderScene` via `MdlSceneBuilder`/`IMdlSceneBuilder` and wrap it in
      `ModelScenePayload`, attached to `PreviewResult.Payload` on every successful parse.
- [ ] Keep every existing text-metadata and header-fallback path in `MdlPreviewProvider` fully
      intact — this is the degradation target when scene building or rendering fails.
- [ ] Implement `ModelSceneCache` keyed by `(SourceId, Locator, Sha256)`, enforcing
      `ModelSceneCacheBudgetBytes` with eviction.
- [ ] Ensure concurrent requests for the same occurrence coalesce to a single parse + build.

**Exit Criteria**
- [ ] `FormattedContent` is byte-identical to today's output for a fixture (regression-locked).
- [ ] `Payload` is non-null on success and null on the header-fallback path.
- [ ] Three concurrent requests for the same occurrence produce exactly one parse and one build.
- [ ] Cache eviction respects `ModelSceneCacheBudgetBytes`.

### S10 — Slot content surface refactor *(hard prerequisite; the only slice touching these files)*
Implements A3. Owns NEW `src/SRN.CC.App/ViewModels/Preview/PreviewContentViewModel.cs`,
`TextContentViewModel.cs`, `PreviewContentFactory.cs`; MODIFY
`src/SRN.CC.App/ViewModels/PreviewSlotViewModel.cs` (add `Content`, read
`res.Payload`/`res.RawPayload`, dispose prior content on reassignment),
`src/SRN.CC.App/Views/ComparisonPanelView.axaml` (swap the `TextBox` for
`ContentControl Content="{Binding Content}"` + merged `Views/PreviewTemplates/` dictionary),
`ComparisonPanelView.axaml.cs`; NEW `src/SRN.CC.App/Views/PreviewTemplates/TextPreviewTemplate.axaml`.
- [ ] Declare abstract `PreviewContentViewModel : ObservableObject, IDisposable` with
      `abstract PreviewFamily Family { get; }`.
- [ ] Implement `TextContentViewModel` as the first concrete subclass (Text family), matching current
      text-preview behavior exactly.
- [ ] Implement `PreviewContentFactory` building the right `PreviewContentViewModel` subclass from
      `res.Payload`/`res.RawPayload`/`res.Family`.
- [ ] Add `PreviewSlotViewModel.Content` (`PreviewContentViewModel?`) as the one new observable
      property; dispose the previous value on reassignment.
- [ ] Edit `ComparisonPanelView.axaml` **once**: replace the read-only Consolas `TextBox` with
      `ContentControl Content="{Binding Content}"`, merge a `Views/PreviewTemplates/` resource
      dictionary.
- [ ] Add `TextPreviewTemplate.axaml` as the `DataTemplate` for `TextContentViewModel`.
- [ ] Update `ComparisonPanelView.axaml.cs` only as needed to support the merged dictionary / content
      control wiring.

**Exit Criteria**
- [ ] Text previews render exactly as before through the new `Content`/`ContentControl` path.
- [ ] All existing UI tests are green.
- [ ] Reassignment of `PreviewSlotViewModel.Content` disposes the prior content.
- [ ] After this lands, wave 3 slices never collide with each other or with this slice's files.

## Notes
- This wave is **serial**: S10 depends on S9's `Payload`-bearing `PreviewResult` design landing
  first (even though S10 itself only needs the `PreviewResult.Payload`/`RawPayload` shape from S2/S9,
  not S9's scene-cache internals).
- S10 is explicitly the *only* slice across the whole milestone permitted to touch
  `PreviewSlotViewModel.cs` and `ComparisonPanelView.axaml` — every other slice that needs slot or
  panel behavior does so through the `PreviewContentViewModel` base or its own template file.
