# Milestone 7 - Phase 3: Composition Root and Release Pipeline (Wave 2)

## Scope

Two slices, fully disjoint, running in parallel. S17 is the milestone's convergence point: everything
wave 1 built behind a contract finally gets constructed, wired, and reachable at runtime. S18 is the
release pipeline: the master verifier grows from nine steps to eleven, CI gains a clean-machine smoke
job on a fresh runner, and the whole thing becomes assertable from a test.

They share no file. S17 lives entirely in `src/SRN.CC.App/` plus its own tests; S18 lives entirely in
`tools/`, `.github/`, plus its own test. That disjointness is deliberate — S18 is on the secondary
chain `S7 → S15 → S18` and carries exactly one wave of slack, so it must never block S17.

This wave is also the barrier that serializes `MainWindowViewModel.cs` and `MainWindow.axaml.cs`:
S17 lands them here, and S20 rebases on top in wave 3. They are never edited concurrently.

## Deliverables

- `AppServices` as the sole composition root, with `App.axaml.cs` reduced to roughly twenty lines.
- Logging live at runtime: a rolling file sink plus an observable sink feeding the operation log.
- The startup preflight running before the window exists, with its report replayed into the UI.
- Settings consumed for the first time since they were written.
- `.srncc` → `.srnccproj` corrected everywhere, including the journal-recovery discovery filter.
- A `--srncc-preflight-only` mode that makes the CI smoke deterministic without a window.
- An eleven-step `VerifyBuild.ps1` that produces and audits a reproducible release archive.
- A `clean-machine-smoke` CI job on a fresh runner, satisfying the `PLAN.md:215` gate clause.

## Detailed tasks

### S17 — App composition root, preflight, logging, live settings, extension fix
Implements A1 and the runtime half of A2, A3, A5, and A6. Owns NEW
`src/SRN.CC.App/Services/AppServices.cs`, `src/SRN.CC.App/Services/ObservableLogSink.cs`; MODIFY
`src/SRN.CC.App/App.axaml.cs`, `src/SRN.CC.App/Program.cs`,
`src/SRN.CC.App/ViewModels/MainWindowViewModel.cs`,
`src/SRN.CC.App/ViewModels/OperationLogViewModel.cs`, `src/SRN.CC.App/Views/MainWindow.axaml.cs`,
`src/SRN.CC.App/Views/ModelViewportControl.cs`; NEW `tests/SRN.CC.Tests/App/AppServicesTests.cs`,
`StartupPreflightWiringTests.cs`, `ProjectExtensionTests.cs`.
- [ ] `AppServices.CreateAsync(AppPaths, IReadOnlyList<string> startupArgs, CancellationToken)`:
      move the entire `new` graph out of `App.axaml.cs:33-91`; construct `JsonLineLogSink` plus
      `ObservableLogSink` into an `AppLogger`; run `StartupPreflight` with all four checks; pass the
      logger into every service that now accepts one.
- [ ] `ObservableLogSink` implements `ILogSink`, marshals to the UI dispatcher, filters to
      `>= Info`, and appends to `OperationLogViewModel.Entries`.
- [ ] Reduce `App.axaml.cs` to roughly twenty lines: build `AppPaths.Default`, await
      `AppServices.CreateAsync`, `CreateMainWindowViewModel()`, attach the window, replay
      `StartupReport`. **Delete** `RecoverStartupPublicationJournals` — `PublicationJournalStartupCheck`
      replaces it.
- [ ] `Program.cs`: add `--srncc-preflight-only`, which runs `AppServices.CreateAsync`, writes
      `StartupReport` as JSON to stdout, and exits 0 without creating a window.
- [ ] Correct `.srncc` → `.srnccproj` at `MainWindowViewModel.cs:218,257` and
      `MainWindow.axaml.cs:28,40,41`. **Leave `.srncc-manifest.json` alone** — it is a different,
      correct literal.
- [ ] `MainWindowViewModel`: consume `ISettingsStore` for real — `LastProjectPath`,
      `RecentProjectPaths`, `NwnInstallOverride` — replay `StartupReport` into the operation log, log
      the `catch` sites at `:667`, `:689`, and `:703`, and implement `OpenOccurrenceAsync` at `:815`,
      which is `throw new NotImplementedException()` today.
- [ ] Wire the two services that exist but are never constructed in production:
      `SqlitePreviewThumbnailCache` into `PreviewEngine`'s optional third parameter
      (`PreviewEngine.cs:21`, null today), and `BaseGameResourceCatalog` into `WorkspaceTextureSource`.
- [ ] `OperationLogViewModel`: add a 2 000-entry ring cap (`Entries` is unbounded today, a leak in a
      long 188k-row session) and delegate `AddEntry` to the logger rather than keeping a parallel
      path.
- [ ] `ModelViewportControl`: one line logging the `GlCapabilities` record the first time a device is
      constructed. Do **not** move probing to startup — that would violate Milestone 6's A4.

**Exit Criteria**
- [ ] `AppServices.CreateAsync` against a temp `AppPaths` creates the log directory, writes preflight
      records, runs all four checks, and **never throws** even when the cache path is unwritable.
- [ ] Zero `".srncc"` string literals remain anywhere in `src/**` outside `.srncc-manifest.json`;
      picker patterns and default filenames are `*.srnccproj`.
- [ ] The startup report is visible in the operation log on first launch.
- [ ] `--srncc-preflight-only` emits parseable JSON, exits 0, and creates no window.
- [ ] `App.axaml.cs` contains no service construction.
- [ ] Every pre-existing App and UI test passes unmodified.

### S18 — Verifier, CI, and clean-machine smoke (needs S15, S5, S7)
Implements A6's pipeline half. Owns MODIFY `tools/VerifyBuild.ps1`, `.github/workflows/verify.yml`;
NEW `tools/SmokeCleanMachine.ps1`, `tests/SRN.CC.Tests/Architecture/ReleasePipelineTests.cs`.
**Must not touch** `tools/PackRelease.ps1` or `tools/AuditRelease.ps1` — S15 owns those.
- [ ] `VerifyBuild.ps1`: retitle the banner, which still reads "Milestones 1-3 Master Verification
      Pass" at `:72`, and renumber to eleven steps.
- [ ] Insert **[8b]** `AuditRelease.ps1 -Root $publishDir` against the **existing** publish tree from
      step [6], after `AuditPublish.ps1`. No second publish.
- [ ] Insert **[9]** `PackRelease.ps1` producing `<runDir>/release/SRN.CC-<version>-win-x64.zip` plus
      `release-manifest.json`, packed twice with the two SHA-256 values compared.
- [ ] Insert **[10]** extract the archive to a scratch directory **outside the repository** and re-run
      `AuditRelease.ps1` against the extracted tree.
- [ ] De-duplicate the hard-coded package versions in the license-copy step at `:127-131` by reading
      them from `eng/dependency-policy.json` instead. They are currently duplicated between the script
      and `eng/publish-policy.json`'s `specialOrigins`.
- [ ] Extend `summary.json` with `version`, `releaseZipPath`, and `releaseZipSha256`.
- [ ] `verify.yml`: set `ContinuousIntegrationBuild: true` (today `Directory.Build.props`'s
      `RestoreLockedMode` condition never fires from the property — locked mode happens only because
      the script passes `--locked-mode` explicitly); rename the stale `milestones-1-2-verification`
      artifact; upload the release archive as its own artifact.
- [ ] Add a `clean-machine-smoke` job: `needs:` the verify job, `windows-2025`, sparse-checkout of
      only `tools/` and `eng/`, `download-artifact`, expand under `$env:RUNNER_TEMP\smoke\app`, run
      `SmokeCleanMachine.ps1`. It must be in the same workflow file because it consumes the verify
      job's artifact.
- [ ] `SmokeCleanMachine.ps1`: assert `%LOCALAPPDATA%\SRN.CC` is absent → `AuditRelease.ps1 -Root
      <extracted>` → `SRN.CC.App.exe --srncc-preflight-only`, parse the JSON report, assert every
      check ran and nothing is `Blocking` → windowed ten-second survival check → assert
      `Logs\srncc.log` and `cache-v1.sqlite` were created and the first log line parses as JSON →
      stop the process.
- [ ] `ReleasePipelineTests.cs`: assert `VerifyBuild.ps1` invokes both new scripts; the workflow
      uploads the archive and has a `clean-machine-smoke` job with `needs:`; `publish-policy.json`
      forbids `.log`, `.pdb`, and `.srnccproj`; and **no `PublishSingleFile` property exists in any
      `.csproj` or `.props`**.

**Exit Criteria**
- [ ] A full local `VerifyBuild.ps1` run passes all eleven steps and writes a `summary.json`
      containing the new fields. Record the run ID.
- [ ] The two packs in step [9] produce identical SHA-256 values.
- [ ] Step [10]'s extracted-tree audit passes, proving the archive and the folder are equivalent.
- [ ] The scratch extraction directory is outside the repository and is cleaned up.
- [ ] `SmokeCleanMachine.ps1` runs successfully against a locally extracted archive before it is
      trusted in CI.

## Notes

- **These two slices must not converge.** S17 owns `src/SRN.CC.App/**` and its own tests; S18 owns
  `tools/`, `.github/`, and its own test. If S18 finds it needs an application change — a different
  exit code, a different stdout shape for `--srncc-preflight-only` — that change belongs to S17, so
  agree the contract from this document before the wave starts.
- The `--srncc-preflight-only` contract is the interface between them: **JSON `StartupReport` on
  stdout, exit code 0, no window.** Both slices code against that sentence.
- S18's first local `VerifyBuild.ps1` run is the real check on S7's extended
  `forbiddenPathFragments`. Adding a fragment the application legitimately embeds fails here, loudly,
  which is exactly where it should fail. `srnccproj` must never be a fragment.
- Wiring `SqlitePreviewThumbnailCache` and `BaseGameResourceCatalog` is in S17 because both are
  already-built services that production has simply never constructed. This is wiring, not new
  behaviour — if either turns out to need a code change beyond construction, stop and report it
  rather than growing the slice.
- After this wave, `MainWindowViewModel.cs` and `MainWindow.axaml.cs` are free for S20. Wave 3 must
  rebase on this wave, never merge around it.
