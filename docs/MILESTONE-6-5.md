# Milestone 6 - Phase 5: Vendoring Execution, Corpus, Evidence, ADR (Wave 4)

## Scope

Close out the milestone: execute the vendoring swap conditional on S1's spike verdict, and produce
the corpus/integration test suite, the evidence document, and the architecture decision record that
together prove the gate. Both slices are parallel and both depend on prior-wave work only, not on
each other.

## Deliverables

- Either a vendored `MdlMeshBuilder.cs` (if S1 = GO) with all three `AuditVendoredSources.ps1` sites
  updated and `MdlSceneBuilder.cs` switched to use it, or (if NO-GO) a two-paragraph rationale
  appended to `docs/MILESTONE-6-0-VENDORING-DECISION.md` and no other change.
- Corpus and scenario integration tests under `tests/SRN.CC.CorpusTests/Render/` and
  `tests/SRN.CC.Tests/Scenarios/`.
- `docs/evidence/MILESTONE-6-EVIDENCE.md` mapping every clause of the `PLAN.md:210` gate to a named
  test.
- `docs/adr/0003-render-layer-placement-and-gpu-context-ownership.md`.

## Detailed tasks

### S16 — Vendoring execution *(conditional on S1 = GO)*
Owns NEW `src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/Mdl/MdlMeshBuilder.cs`; MODIFY
`eng/vendored-sources-manifest.json`, `tools/AuditVendoredSources.ps1` (**all three sites**: the
`-ne 40` condition at line 97, the violation message at line 98, and the `"40 source files"` success
string at line 121), `docs/compliance/VENDORING.md`, `src/SRN.CC.Preview/Render/MdlSceneBuilder.cs`.
- [ ] Confirm S1's verdict in `docs/MILESTONE-6-0-VENDORING-DECISION.md` is GO before starting any
      of the steps below.
- [ ] Copy `MdlMeshBuilder.cs` verbatim into
      `src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/Mdl/MdlMeshBuilder.cs`.
- [ ] Add the manifest entry to `eng/vendored-sources-manifest.json` with the `gitBlobSha1` recorded
      by S1.
- [ ] Update `tools/AuditVendoredSources.ps1` at all three sites: the `-ne 40` condition (line 97),
      the violation message (line 98), and the `"40 source files"` success string (line 121) — all
      moving to 41.
- [ ] Update `docs/compliance/VENDORING.md` to record the new vendored file and its disposition
      (`Accept (Verbatim)`, per `docs/compliance/swlor-post-attestation-delta-review.md:19`).
- [ ] Switch `src/SRN.CC.Preview/Render/MdlSceneBuilder.cs` to call the vendored `MdlMeshBuilder`
      instead of its first-party equivalent, preserving identical output.
- [ ] Re-grep the copied file for the disqualifying provenance keyword and `GPL` before committing.
- [x] If S1's verdict is NO-GO: skip all of the above; instead append a two-paragraph rationale to
      `docs/MILESTONE-6-0-VENDORING-DECISION.md` and make no other change. (S1 returned NO-GO; the
      closure section is already appended to that file — see its "S16 ... closed as a no-op per this
      NO-GO" section.)

**Exit Criteria**
- [x] `AuditVendoredSources.ps1` passes at 41 sources (GO path) — or nothing else in the repo changed
      (NO-GO path). NO-GO path taken: `AuditVendoredSources.ps1` still passes at the original 40
      source files / 8 portable test files (re-verified during S17's own verification pass).
- [ ] `git hash-object` on the copied file equals the manifest's recorded `gitBlobSha1` (GO path only
      — not applicable; NO-GO path taken).
- [ ] Vendored-upstream tests still pass (GO path only — not applicable; NO-GO path taken).
- [ ] **S6b's `MdlSceneBuilderTests.cs` re-run unchanged and green**, proving the swap is
      behaviour-preserving (GO path only — not applicable; NO-GO path taken, but see S17's Progress
      Summary below: `MdlSceneBuilderTests.cs` is green regardless, since the builder was always
      first-party).

### S17 — Corpus, integration, evidence, ADR (needs S9, S10, S11)
Owns NEW `tests/SRN.CC.CorpusTests/Render/*`,
`tests/SRN.CC.Tests/Scenarios/ModelPreviewScenarioTests.cs`,
`docs/evidence/MILESTONE-6-EVIDENCE.md`,
`docs/adr/0003-render-layer-placement-and-gpu-context-ownership.md`. Must not touch production
source.
- [x] Add corpus tests under `tests/SRN.CC.CorpusTests/Render/` exercising representative
      one-to-three-slot corpus MDLs end to end (parse → scene build → texture resolve → render).
- [x] Tag all corpus tests `[Category("Corpus")]` plus an `SRNCC_RUN_GPU` env guard with
      `Assert.Ignore`, mirroring the existing `SRNCC_RUN_CORPUS` pattern, so
      `VerifyBuild.ps1`'s `--filter 'Category!=Corpus&Category!=Performance'` skips them by default.
- [x] Keep all corpus/scenario assertions semantic — draw-call counts, bound-texture names,
      bounding-box fit — **never cross-GPU pixel equality**.
- [x] Add `ModelPreviewScenarioTests.cs` covering the end-to-end scenario: unsupported-feature
      degradation and context-failure survival without leaks or cross-context handles.
- [x] Add a working-set check after three concurrent model previews plus a forced GC, against
      `PLAN.md:229`'s 750 MiB idle target.
- [x] Write `docs/evidence/MILESTONE-6-EVIDENCE.md` mapping every clause of the `PLAN.md:210` gate to
      a named test, and record the `VerifyBuild.ps1` run ID, build warning/error counts, and test
      pass/fail/skip counts, matching the `docs/evidence/MILESTONE-3-EVIDENCE.md` format.
- [x] Write `docs/adr/0003-render-layer-placement-and-gpu-context-ownership.md` stating: render-layer
      placement and why no new project (constraint 1); GPU code in Preview with the
      `Func<string,IntPtr>` bridge as the sole Avalonia coupling; `IPreviewPayload` as process-local
      and never cached; and the deliberate non-import of upstream Toolset render files plus the
      compliance gate for ever reversing that.

**Exit Criteria**
- [x] Corpus tests are semantic (geometry/material assertions, adapter-dependent smoke tests), never
      cross-GPU pixel equality, per `PLAN.md:230`.
- [x] `VerifyBuild.ps1`'s default filter skips all `[Category("Corpus")]` tests with zero impact on
      the standard test run. (Directly verified: `dotnet test tests/SRN.CC.CorpusTests --filter
      'Category!=Corpus&Category!=Performance'` matches 0 tests.)
- [x] Working-set check after three concurrent previews + forced GC is recorded against the 750 MiB
      target.
- [x] `docs/evidence/MILESTONE-6-EVIDENCE.md` maps every `PLAN.md:210` gate clause to a named test and
      records a `VerifyBuild.ps1` run ID. (Run ID `20260804T152738Z-31136` recorded — see that
      document's Section 10b for why the run's status is FAILED for a pre-existing, out-of-scope
      reason, and this doc's own Progress Summary below for the short version.)
- [x] ADR 0003 covers all four required points listed above.
- [x] No production source file is touched by this slice.

## Notes
- S16 and S17 are independent of each other; S16 depends only on S1 (wave 0) and S6b (wave 1); S17
  depends on S9, S10 (wave 2), and S11 (wave 3).
- This is the final wave. After both slices land and `VerifyBuild.ps1` is green, the milestone
  branch is ready for a single PR per the execution model in `docs/MILESTONE-6.md`.

## Progress Summary

### ✓ S16 CLOSED — NO-GO path (no-op, as designed)
S1's spike (`docs/MILESTONE-6-0-VENDORING-DECISION.md`) returned NO-GO before this wave started;
`MdlSceneBuilder` was first-party from S6b onward, so S16 required no further code change. Its
closure rationale is appended to that same decision document. Re-verified during S17's own
verification pass: `AuditVendoredSources.ps1` still passes at the original 40 source files / 8
portable test files.

### ✓ S17 COMPLETE — Corpus, integration, evidence, ADR
- **`tests/SRN.CC.Tests/Scenarios/ModelPreviewScenarioTests.cs`** (NEW, 9 tests, all green): the
  first end-to-end proof that the real `MdlPreviewProvider` → `MdlSceneBuilder` → `ModelSceneCache`
  → `PreviewContentFactory` → `ModelViewportViewModel` pipeline connects, plus the CPU-scene →
  `FakeGlDevice` GPU-upload seam and a 1-to-3-slot no-leak lifecycle check, all against real (not
  synthetic S8-fixture) scenes.
- **`tests/SRN.CC.CorpusTests/Render/*`** (NEW, 3 files, 4 opt-in tests): real-corpus MDL
  parse/scene-build/render coverage, a genuinely-wired `SRNCC_RUN_GPU` guard whose one gated test
  documents — as a real, run-once-opted-in `Assert.Inconclusive` result, not just a comment — why a
  real `SilkGlDevice`/GPU-context test is not achievable in this repository's current test
  infrastructure, and the `PLAN.md:229` 750 MiB working-set check. Directly verified excluded from
  the default `Category!=Corpus&Category!=Performance` filter (0 tests matched).
- **`docs/evidence/MILESTONE-6-EVIDENCE.md`** (NEW): maps every `PLAN.md:210` gate clause and every
  milestone-6 scope bullet to a named test, records the deferred-scope list verbatim, and records
  `tools/VerifyBuild.ps1` run `20260804T152738Z-31136` — status **FAILED** at the locked-mode
  restore step, root-caused to a pre-existing `src/SRN.CC.App/packages.lock.json` inconsistency
  (missing `NAudio` in the RID-less `net10.0` section) introduced by the already-landed S11–S15
  commit, independently confirmed unrelated to S17 by reproducing it with S17's files removed. This
  is out of S17's ownership to fix (`src/**` is off-limits to this slice); flagged for the owner of
  `src/SRN.CC.App/` to regenerate and commit the corrected lock files before merge.
- **`docs/adr/0003-render-layer-placement-and-gpu-context-ownership.md`** (NEW): covers render-layer
  placement (and why no `SRN.CC.Render` project), the `Func<string,IntPtr>` bridge as the sole
  Avalonia/render-layer coupling, `IPreviewPayload`'s process-local scope, the deliberate non-import
  of upstream Toolset render files (citing the S1 NO-GO finding), and the `GlContextId`/`Abandon`
  vs. `Dispose` mechanism satisfying the gate's leak/cross-context-handle requirement.
- Ad-hoc verification (not the master verifier, but the same commands/flags this repo's build
  guidance specifies): `dotnet build SRN.CC.sln -c Release -p:SRNCCVerificationRuntimeIdentifier=win-x64`
  — 0 Warnings, 0 Errors; `dotnet test SRN.CC.sln -c Release --filter
  "Category!=Corpus&Category!=Performance" -p:SRNCCVerificationRuntimeIdentifier=win-x64` — 438
  passed, 0 failed, 0 skipped (429 pre-existing + 9 new); `AuditDependencies.ps1`/
  `AuditVendoredSources.ps1` PASSED standalone.
- No production source file (`src/**`) was touched by this slice; `git status --short --
  '*packages.lock.json'` is clean in this slice's final state.
