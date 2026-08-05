# Milestone 7 - Phase 3: Composition Root and Release Pipeline (Wave 2)

## Scope

Two slices, fully disjoint, running in parallel. S17 is the milestone's convergence point: everything
wave 1 built behind a contract finally gets constructed, wired, and reachable at runtime. S18 is the
release pipeline: the master verifier grows from nine steps to eleven, CI gains a clean-machine smoke
job on a fresh runner, and the whole thing becomes assertable from a test.

They share no file. S17 lives entirely in `src/SRN.CC.App/` plus its own tests; S18 lives entirely in
`tools/`, `.github/`, plus its own test. That disjointness is deliberate â€” S18 is on the secondary
chain `S7 â†’ S15 â†’ S18` and carries exactly one wave of slack, so it must never block S17.

This wave is also the barrier that serializes `MainWindowViewModel.cs` and `MainWindow.axaml.cs`:
S17 lands them here, and S20 rebases on top in wave 3. They are never edited concurrently.

## Deliverables

- `AppServices` as the sole composition root, with `App.axaml.cs` reduced to roughly twenty lines.
- Logging live at runtime: a rolling file sink plus an observable sink feeding the operation log.
- The startup preflight running before the window exists, with its report replayed into the UI.
- Settings consumed for the first time since they were written.
- `.srncc` â†’ `.srnccproj` corrected everywhere, including the journal-recovery discovery filter.
- A `--srncc-preflight-only` mode that makes the CI smoke deterministic without a window.
- An eleven-step `VerifyBuild.ps1` that produces and audits a reproducible release archive.
- A `clean-machine-smoke` CI job on a fresh runner, satisfying the `PLAN.md:215` gate clause.

## Detailed tasks

### S17 â€” App composition root, preflight, logging, live settings, extension fix
Implements A1 and the runtime half of A2, A3, A5, and A6. Owns NEW
`src/SRN.CC.App/Services/AppServices.cs`, `src/SRN.CC.App/Services/ObservableLogSink.cs`; MODIFY
`src/SRN.CC.App/App.axaml.cs`, `src/SRN.CC.App/Program.cs`,
`src/SRN.CC.App/ViewModels/MainWindowViewModel.cs`,
`src/SRN.CC.App/ViewModels/OperationLogViewModel.cs`, `src/SRN.CC.App/Views/MainWindow.axaml.cs`,
`src/SRN.CC.App/Views/ModelViewportControl.cs`; NEW `tests/SRN.CC.Tests/App/AppServicesTests.cs`,
`StartupPreflightWiringTests.cs`, `ProjectExtensionTests.cs`.
- [x] `AppServices.CreateAsync(AppPaths, IReadOnlyList<string> startupArgs, CancellationToken)`:
      move the entire `new` graph out of `App.axaml.cs:33-91`; construct `JsonLineLogSink` plus
      `ObservableLogSink` into an `AppLogger`; run `StartupPreflight` with all four checks; pass the
      logger into every service that now accepts one.
- [x] `ObservableLogSink` implements `ILogSink`, marshals to the UI dispatcher, filters to
      `>= Info`, and appends to `OperationLogViewModel.Entries`.
- [x] Reduce `App.axaml.cs` to roughly twenty lines: build `AppPaths.Default`, await
      `AppServices.CreateAsync`, `CreateMainWindowViewModel()`, attach the window, replay
      `StartupReport`. **Delete** `RecoverStartupPublicationJournals` â€” `PublicationJournalStartupCheck`
      replaces it.
- [x] `Program.cs`: add `--srncc-preflight-only`, which runs `AppServices.CreateAsync`, writes
      `StartupReport` as JSON to stdout, and exits 0 without creating a window.
- [x] Correct `.srncc` â†’ `.srnccproj` at `MainWindowViewModel.cs:218,257` and
      `MainWindow.axaml.cs:28,40,41`. **Leave `.srncc-manifest.json` alone** â€” it is a different,
      correct literal.
- [x] `MainWindowViewModel`: consume `ISettingsStore` for real â€” `LastProjectPath`,
      `RecentProjectPaths`, `NwnInstallOverride` â€” replay `StartupReport` into the operation log, log
      the `catch` sites at `:667`, `:689`, and `:703`, and implement `OpenOccurrenceAsync` at `:815`,
      which is `throw new NotImplementedException()` today.
- [x] Wire the two services that exist but are never constructed in production:
      `SqlitePreviewThumbnailCache` into `PreviewEngine`'s optional third parameter
      (`PreviewEngine.cs:21`, null today), and `BaseGameResourceCatalog` into `WorkspaceTextureSource`.
- [x] `OperationLogViewModel`: add a 2 000-entry ring cap (`Entries` is unbounded today, a leak in a
      long 188k-row session) and delegate `AddEntry` to the logger rather than keeping a parallel
      path.
- [x] `ModelViewportControl`: one line logging the `GlCapabilities` record the first time a device is
      constructed. Do **not** move probing to startup â€” that would violate Milestone 6's A4.

**Exit Criteria**
- [x] `AppServices.CreateAsync` against a temp `AppPaths` creates the log directory, writes preflight
      records, runs all four checks, and **never throws** even when the cache path is unwritable.
- [x] Zero `".srncc"` string literals remain anywhere in `src/**` outside `.srncc-manifest.json`;
      picker patterns and default filenames are `*.srnccproj`.
- [x] The startup report is visible in the operation log on first launch.
- [x] `--srncc-preflight-only` emits parseable JSON, exits 0, and creates no window.
- [x] `App.axaml.cs` contains no service construction.
- [x] Every pre-existing App and UI test passes unmodified.

### S18 â€” Verifier, CI, and clean-machine smoke (needs S15, S5, S7)
Implements A6's pipeline half. Owns MODIFY `tools/VerifyBuild.ps1`, `.github/workflows/verify.yml`;
NEW `tools/SmokeCleanMachine.ps1`, `tests/SRN.CC.Tests/Architecture/ReleasePipelineTests.cs`.
**Must not touch** `tools/PackRelease.ps1` or `tools/AuditRelease.ps1` â€” S15 owns those.
- [x] `VerifyBuild.ps1`: retitle the banner, which still reads "Milestones 1-3 Master Verification
      Pass" at `:72`, and renumber to eleven steps.
- [x] Insert **[8b]** `AuditRelease.ps1 -Root $publishDir` against the **existing** publish tree from
      step [6], after `AuditPublish.ps1`. No second publish.
- [x] Insert **[9]** `PackRelease.ps1` producing `<runDir>/release/SRN.CC-<version>-win-x64.zip` plus
      `release-manifest.json`, packed twice with the two SHA-256 values compared.
- [x] Insert **[10]** extract the archive to a scratch directory **outside the repository** and re-run
      `AuditRelease.ps1` against the extracted tree.
- [x] De-duplicate the hard-coded package versions in the license-copy step at `:127-131` by reading
      them from `eng/dependency-policy.json` instead. They are currently duplicated between the script
      and `eng/publish-policy.json`'s `specialOrigins`.
- [x] Extend `summary.json` with `version`, `releaseZipPath`, and `releaseZipSha256`.
- [x] `verify.yml`: set `ContinuousIntegrationBuild: true` (today `Directory.Build.props`'s
      `RestoreLockedMode` condition never fires from the property â€” locked mode happens only because
      the script passes `--locked-mode` explicitly); rename the stale `milestones-1-2-verification`
      artifact; upload the release archive as its own artifact.
- [x] Add a `clean-machine-smoke` job: `needs:` the verify job, `windows-2025`, sparse-checkout of
      only `tools/` and `eng/`, `download-artifact`, expand under `$env:RUNNER_TEMP\smoke\app`, run
      `SmokeCleanMachine.ps1`. It must be in the same workflow file because it consumes the verify
      job's artifact.
- [x] `SmokeCleanMachine.ps1`: assert `%LOCALAPPDATA%\SRN.CC` is absent â†’ `AuditRelease.ps1 -Root
      <extracted>` â†’ `SRN.CC.App.exe --srncc-preflight-only`, parse the JSON report, assert every
      check ran and nothing is `Blocking` â†’ windowed ten-second survival check â†’ assert
      `Logs\srncc.log` and `cache-v1.sqlite` were created and the first log line parses as JSON â†’
      stop the process.
- [x] `ReleasePipelineTests.cs`: assert `VerifyBuild.ps1` invokes both new scripts; the workflow
      uploads the archive and has a `clean-machine-smoke` job with `needs:`; `publish-policy.json`
      forbids `.log`, `.pdb`, and `.srnccproj`; and **no `PublishSingleFile` property exists in any
      `.csproj` or `.props`**.

**Exit Criteria**
- [x] A full local `VerifyBuild.ps1` run passes all eleven steps and writes a `summary.json`
      containing the new fields. Record the run ID.
- [x] The two packs in step [9] produce identical SHA-256 values.
- [x] Step [10]'s extracted-tree audit passes, proving the archive and the folder are equivalent.
- [x] The scratch extraction directory is outside the repository and is cleaned up.
- [x] `SmokeCleanMachine.ps1` runs successfully against a locally extracted archive before it is
      trusted in CI.

## Notes

- **These two slices must not converge.** S17 owns `src/SRN.CC.App/**` and its own tests; S18 owns
  `tools/`, `.github/`, and its own test. If S18 finds it needs an application change â€” a different
  exit code, a different stdout shape for `--srncc-preflight-only` â€” that change belongs to S17, so
  agree the contract from this document before the wave starts.
- The `--srncc-preflight-only` contract is the interface between them: **JSON `StartupReport` on
  stdout, exit code 0, no window.** Both slices code against that sentence.
- S18's first local `VerifyBuild.ps1` run is the real check on S7's extended
  `forbiddenPathFragments`. Adding a fragment the application legitimately embeds fails here, loudly,
  which is exactly where it should fail. `srnccproj` must never be a fragment.
- Wiring `SqlitePreviewThumbnailCache` and `BaseGameResourceCatalog` is in S17 because both are
  already-built services that production has simply never constructed. This is wiring, not new
  behaviour â€” if either turns out to need a code change beyond construction, stop and report it
  rather than growing the slice.
- After this wave, `MainWindowViewModel.cs` and `MainWindow.axaml.cs` are free for S20. Wave 3 must
  rebase on this wave, never merge around it.

## Wave 2 outcome

Both slices landed with no file overlap. Barrier: **778 passed / 0 failed / 0 skipped** under
`Category!=Corpus&Category!=Performance`, from a 742 baseline — 23 from S17, 13 from S18. Build clean
under `TreatWarningsAsErrors`. Gate run **`20260804T212248Z-1592`** passes all eleven steps; release
ZIP `SRN.CC-1.0.0-win-x64.zip`, SHA-256
`c7697d4a2f8bea77749432366f3ae0d8a0066ba0ce4d02acd47ead3cfebaf939`, identical across both packs.

Four corrections to this document, recorded so wave 3 does not inherit them:

- **The ladder has no launch-smoke step, and never did.** This document described eleven steps as
  `[1]`–`[8]`, `[8b]`, `[9]`, `[10]`, "then the existing launch smoke and immutability recheck" —
  which is twelve, and assumes a launch smoke that `VerifyBuild.ps1` has never contained. The
  pre-existing ladder was nine steps ending at the immutability recheck. Eleven is correct: the three
  new steps insert before the old `[9]`, which renumbers to `[11]`. The launch smoke lives in
  `SmokeCleanMachine.ps1` and CI, which is the right place for it — it needs a release tree.
- **The gate was blocked by a defect neither slice owned.** Step `[8]` failed with
  `First-party output 'SRN.CC.Infrastructure.dll' contains an absolute build/source path`. The cause
  was a literal `C:\Program Files (x86)\Steam\...` default-Steam probe in
  `NwnInstallLocator.ProbeAutoDiscoveryCandidates`, present since Milestone 2 and only now detected,
  because `AuditPublish.ps1`'s narrowed pattern and UTF-16 scan landed the same day. .NET stores
  string literals UTF-16 in the `#US` heap, so the ASCII scan missed it and the unicode scan did not.
  Fixed at the source — the path is now derived from
  `Environment.SpecialFolder.ProgramFilesX86` via `Path.Combine` — rather than by loosening the
  audit, which no slice is permitted to edit. That also fixes a real discovery hole: a machine whose
  Program Files (x86) is not on `C:` never got the probe.
- **`SmokeCleanMachine.ps1` did not run before it was declared done.** Its own exit criterion
  required a successful local run first. Running it surfaced two defects, both since fixed:
  `Start-Process -PassThru` returns a `Process` whose `ExitCode` and `HasExited` read back as `$null`
  unless `.Handle` is dereferenced before the process exits, so the preflight step failed with a
  blank exit code — this would have failed in CI too; and under `-AllowExistingAppData` the script
  asserted `cache-v1.sqlite`'s mtime had advanced, which it never does, because opening an existing
  healthy database journals into the `-wal` sidecar. Freshness is now asserted only for the log,
  which is appended per record. The script passes end-to-end: release audit, window-free preflight
  with all four checks `Ok`, ten-second windowed survival, JSON first log line.
- **`-AllowExistingAppData` is undocumented above.** The script grew a switch this document did not
  specify, so that a developer machine that already has `%LOCALAPPDATA%\SRN.CC` can run the smoke.
  CI must never pass it; `ReleasePipelineTests` pins the clean-machine precondition.
