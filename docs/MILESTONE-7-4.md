# Milestone 7 - Phase 4: End-to-End Acceptance, Shell Completion, and Test Backfill (Wave 3)

## Scope

Four slices in parallel, closing the gate's hardest clause and every remaining thin area of
`PLAN.md:217-230`. S19 is the critical-path slice: the "final acceptance flow" the gate names has no
implementation today, and `Scenarios/ControlledAcceptanceScenarioTests.cs:78` stops at rescan using
`DynamicIndexService`/`DynamicHashService` fakes over dummy `.hak` *text* files. S20 closes the shell
gap from constraint 10. S21 and S22 are pure test backfill against named `PLAN.md` bullets.

S20 is the only slice here that touches production App files, and it does so only after S17 landed
them in wave 2 — the wave barrier is what makes `MainWindowViewModel.cs` safe to edit. S19, S21, and
S22 create new test files almost exclusively.

## Deliverables

- A real end-to-end acceptance test: import → resolve/pin → save/reopen → rescan → build → verify →
  publish, on real bytes, producing a real HAK, through the entirely real service graph.
- Save As, Rescan, and a Settings dialog in the shell, plus the read-only command-gating regression
  test constraint 8 calls for.
- Backfill for the thin half of `PLAN.md:220` (rescans, pin reattachment, override compaction) and
  `PLAN.md:225` (closure size, cycles, missing references).
- Backfill for `PLAN.md:222` (188k-row virtualization) and `PLAN.md:223` (the full preview provider
  matrix), plus a test that keeps `NOTICES.md` complete.

## Detailed tasks

### S19 — Real end-to-end acceptance flow (needs S16, S17)
Owns NEW `tests/SRN.CC.Tests/Scenarios/FullAcceptanceScenarioTests.cs`.
**Must not touch** `tests/SRN.CC.Tests/Scenarios/ControlledAcceptanceScenarioTests.cs`; consumes
S16's `RealHakFixtureFactory` **read-only**.
- [ ] **Uncategorized**, so the default `Category!=Corpus&Category!=Performance` filter runs it in
      CI. Target under ten seconds. Everything under one temp root; no corpus, no GPU, no Avalonia.
- [ ] Build the service graph entirely from **real** implementations — `ResourceTypeRegistry`,
      `SqliteCacheService`, `AssetIndexService`, `SourceReaderDispatcher`, `AssetHashCache`,
      `WorkspaceResolver`, `WorkspaceService`, `ProjectStore`, and a `BuildOrchestrator` over
      `AssetPacker` / `BuildVerifier` / `ProvenanceManifestGenerator` / `ArtifactPublisher`. No fakes.
- [ ] **Step 1 — import.** Write `base.hak` (7 entries, priority 0), `override.hak` (4 entries,
      priority 1, containing one byte-identical duplicate and one differing-hash conflict), and a
      folder source `loose assets/` (2 files, priority 2, one path containing a space). Assert the
      identity count, the priority winners, the identical duplicate auto-resolving to the lowest
      deterministic locator, and the differing pair reported as conflicted.
- [ ] **Step 2 — resolve and pin.** Read the losing occurrence's real bytes through
      `dispatcher.OpenOccurrenceAsync`, hash them, pin. Assert the winner switched. Deselect one
      identity through the selection override path.
- [ ] **Step 3 — save and reopen.** Save to `acceptance.srnccproj`, load, restore state, and assert
      source order, the pin, and the selection overrides all survive. Then save the reloaded state to
      a second path and assert the two files are **byte-identical** — a round-trip stability property
      nothing asserts today.
- [ ] **Step 4 — rescan.** Rewrite `override.hak` with the pinned payload moved to a different entry
      index. Rescan and assert the reattachment report contains the identity and that the pin's
      locator moved.
- [ ] **Step 5 — build.** Execute a build and assert success plus the presence of both `curated.hak`
      and `curated.srncc-manifest.json`.
- [ ] **Step 6 — independent verify.** Reopen the output with a **fresh** `HakReader`. Assert the
      entry count equals the selected count; the deselected identity is absent; every payload's bytes
      equal the bytes re-read from the original source through a **fresh** `SourceReaderDispatcher`;
      and resref bytes are the original CP1252 bytes, not re-encoded.
- [ ] **Step 7 — manifest.** Assert `HakSha256Hex` equals a freshly computed SHA-256 of the file;
      every per-resource `Sha256Hex` equals a freshly computed payload hash; `AppVersion` equals the
      runtime `AssemblyInformationalVersion`; and no `[A-Za-z]:\` sequence appears outside the
      project directory.
      **Note the casing.** `ProvenanceManifestGenerator.cs:94-98` builds its `JsonSerializerOptions`
      with only `WriteIndented` and an `Encoder` — **no `PropertyNamingPolicy`** — so the emitted
      field names are the record's declared PascalCase (`SchemaVersion`, `AppVersion`,
      `GeneratedUtc`, `HakFileName`, `HakSha256Hex`, `TotalEntries`, `TotalSizeBytes`, and
      `Resources[].{Resref, ResourceType, ResourceTypeName, SourceLabel, OriginLocator, SizeBytes,
      Sha256Hex, IsPinned}`), **not** the camelCase that `PLAN.md:159` and earlier drafts of this
      document implied. Assert against the real names. Do **not** "fix" the casing — that would be a
      breaking manifest-format change outside this milestone's scope, and
      `docs/operator/MANIFEST-FORMAT.md` already documents the PascalCase form.
- [ ] **Step 8 — repeat-build determinism.** Build again to a **different directory with the same
      filename**, so `hakFileName` is identical. Assert the two HAK files are byte-identical and the
      two manifests are identical after replacing `GeneratedUtc`. This is the `PLAN.md:224` clause
      with no test today.
- [ ] **Step 9 — publication integrity.** Assert no journal or backup residue remains after step 5.
      Then hand-craft a `HakReplaced`-state journal plus a backup pair and call
      `RecoverPendingJournalAsync`. Assert the previous valid HAK is restored byte-for-byte and the
      journal is gone.
- [ ] **Step 10 — startup preflight over real residue.** Construct `StartupPreflight` with
      `PublicationJournalStartupCheck` pointed at the output directory while a pending journal
      exists. Assert the report names the recovered journal and that a corresponding log line exists
      in the temp `AppPaths.LogDirectory`.

**Exit Criteria**
- [ ] All ten steps pass in a single test run under ten seconds, in CI, with no corpus and no GPU.
- [ ] The test constructs no fake or stub of any production service.
- [ ] Deleting the temp root leaves no residue anywhere else on the machine, including
      `%LOCALAPPDATA%`.
- [ ] `ControlledAcceptanceScenarioTests.cs` is unmodified and still green.

### S20 — Shell completion: Save As, Rescan, Settings (needs S17)
Closes constraint 10. Owns MODIFY `src/SRN.CC.App/ViewModels/MainWindowViewModel.cs`,
`src/SRN.CC.App/Views/MainWindow.axaml`, `src/SRN.CC.App/Views/MainWindow.axaml.cs`; NEW
`src/SRN.CC.App/Views/SettingsDialog.axaml`, `SettingsDialog.axaml.cs`,
`src/SRN.CC.App/ViewModels/SettingsDialogViewModel.cs`; NEW
`tests/SRN.CC.Tests/UI/MainWindowShellTests.cs`,
`tests/SRN.CC.Tests/App/SettingsDialogViewModelTests.cs`.
- [ ] Add a `SaveProjectAs` command over the existing `IProjectStore.SaveAsAsync`, which has no
      command today, with the same read-only gating as Save.
- [ ] Add a `Rescan` command over the existing rescan path, surfacing the changed-input report.
- [ ] Add a Settings dialog over the now-live `ISettingsStore`: the NWN install override with the
      `NwnInstallLocator` auto-discovery result shown as context, plus recent projects. Respect
      `SettingsLoadStatus.ReadOnlyNewer` by disabling save and saying why.
- [ ] Add all three to the toolbar at `MainWindow.axaml:16-23`, which currently has only New, Open,
      Save, and Build.
- [ ] Rebase on S17's version of `MainWindowViewModel.cs` and `MainWindow.axaml.cs`. Never merge
      around wave 2.

**Exit Criteria**
- [ ] Headless: New, Open, Save, Save As, Rescan, Build, and Settings are all present and bound.
- [ ] A `schemaVersion: 2` project leaves **both** `SaveProjectCommand.CanExecute` and
      `BuildHakCommand.CanExecute` false — the `PLAN.md:94` regression test that constraint 8 calls
      for, since the production gate already exists at `MainWindowViewModel.cs:402,475`.
- [ ] The Settings dialog round-trips `NwnInstallOverride` through `ISettingsStore` and the value is
      observable on the next load.
- [ ] Every pre-existing App and UI test passes unmodified.

### S21 — Resolution and dependency backfill
Owns MODIFY `src/SRN.CC.Preview/DependencyTraversalEngine.cs`; NEW
`tests/SRN.CC.Tests/Core/ResolutionRescanTests.cs`, `SelectionOverrideCompactionTests.cs`,
`PinReattachmentTests.cs`; NEW `tests/SRN.CC.Tests/Preview/DependencyClosureTests.cs`.
- [ ] Close the `totalBytes: 0` TODO at `DependencyTraversalEngine.cs:167` by tracking occurrence
      sizes during traversal.
- [ ] `ResolutionRescanTests`: rescan with a source changed, a source unavailable, and a source
      relocated; the changed-input report contents.
- [ ] `PinReattachmentTests`: the full ladder from `PLAN.md`'s rule 7 — exact source/identity/locator
      with a matching hash first, then a unique source/identity/hash match — plus every case where
      reattachment must **fail** and leave the pin invalid rather than falling back silently.
- [ ] `SelectionOverrideCompactionTests`: a filtered include/exclude writes only the necessary
      overrides; "Exclude All" sets the default false and **clears** overrides; a new identity
      follows the current default.
- [ ] `DependencyClosureTests`: closure size and count limits, cycles, missing references,
      unresolved dependencies, no automatic base-game packaging, and the now-tracked `totalBytes`.

**Exit Criteria**
- [ ] Every one of the twelve cases listed at `PLAN.md:221` has a named test.
- [ ] Every case listed at `PLAN.md:225` has a named test.
- [ ] `totalBytes` is non-zero for a closure with known sizes and the change breaks no existing
      dependency test.

### S22 — UI headless and preview matrix backfill
Owns NEW `tests/SRN.CC.Tests/UI/TableViewLargeDatasetTests.cs`,
`ComparisonPanelSlotLifecycleTests.cs`; NEW `tests/SRN.CC.Tests/Preview/PreviewProviderMatrixTests.cs`;
NEW `tests/SRN.CC.Tests/Architecture/NoticesCompletenessTests.cs`.
- [ ] `TableViewLargeDatasetTests`: with 188k rows, the realized-row high-water mark stays bounded;
      sort, filter, and bulk selection do not realize the full set; selection survives a filter
      change. Only two virtualization tests exist today.
- [ ] `ComparisonPanelSlotLifecycleTests`: stable and overflow slots, replacement and promotion, mode
      switching, cancellation, and the linked-state rules from `PLAN.md:222`.
- [ ] `PreviewProviderMatrixTests`: valid, truncated, malformed, canceled, and oversized inputs for
      **every** provider, plus one successful, one failed, and one canceled concurrent slot —
      `PLAN.md:223` in full.
- [ ] `NoticesCompletenessTests`: every `"scope": "runtime"` package in `eng/dependency-policy.json`
      has a row in `NOTICES.md`. This turns Milestone 6's observation — that `AuditDependencies.ps1`
      checks the `mustShipNotice` flag but never the file — into a real assertion. Per constraint 7
      the table is already complete, so this test must pass on first run; if it fails, the table
      drifted and the fix belongs here.

**Exit Criteria**
- [ ] The 188k-row test completes within the existing headless test time budget and does not realize
      the full row set.
- [ ] Every provider appears in the matrix with all five input conditions.
- [ ] `NoticesCompletenessTests` passes without modifying `NOTICES.md`.

## Notes

- **S19 is the gate.** "Final acceptance flow" is the clause with the least existing coverage and the
  most evidentiary weight. If a step cannot be asserted as written, report it rather than weakening
  the assertion — a green test that checks less than it claims is worse for S23's evidence pass than
  a documented gap.
- S19 consumes `RealHakFixtureFactory` read-only. If the factory needs a new fixture entry, that is a
  finding to report, not an edit to make — the file belongs to S16.
- S20 is the only slice in this wave touching production App files, and only because wave 2 already
  landed them. Rebase on wave 2; never merge around it.
- S21's `DependencyTraversalEngine` edit is the milestone's only production change in
  `SRN.CC.Preview`. Keep it to the `totalBytes` tracking — no adjacent refactoring.
- S22's `NoticesCompletenessTests` is expected to pass immediately. Its value is regression
  protection for the release audit, not a fix.
- All four slices must leave every pre-existing test unmodified. This wave is additive by
  construction; a slice that needs to change an existing assertion has found a real behavioural
  change and should report it before proceeding.
