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
- [ ] If S1's verdict is NO-GO: skip all of the above; instead append a two-paragraph rationale to
      `docs/MILESTONE-6-0-VENDORING-DECISION.md` and make no other change.

**Exit Criteria**
- [ ] `AuditVendoredSources.ps1` passes at 41 sources (GO path) — or nothing else in the repo changed
      (NO-GO path).
- [ ] `git hash-object` on the copied file equals the manifest's recorded `gitBlobSha1` (GO path).
- [ ] Vendored-upstream tests still pass (GO path).
- [ ] **S6b's `MdlSceneBuilderTests.cs` re-run unchanged and green**, proving the swap is
      behaviour-preserving (GO path).

### S17 — Corpus, integration, evidence, ADR (needs S9, S10, S11)
Owns NEW `tests/SRN.CC.CorpusTests/Render/*`,
`tests/SRN.CC.Tests/Scenarios/ModelPreviewScenarioTests.cs`,
`docs/evidence/MILESTONE-6-EVIDENCE.md`,
`docs/adr/0003-render-layer-placement-and-gpu-context-ownership.md`. Must not touch production
source.
- [ ] Add corpus tests under `tests/SRN.CC.CorpusTests/Render/` exercising representative
      one-to-three-slot corpus MDLs end to end (parse → scene build → texture resolve → render).
- [ ] Tag all corpus tests `[Category("Corpus")]` plus an `SRNCC_RUN_GPU` env guard with
      `Assert.Ignore`, mirroring the existing `SRNCC_RUN_CORPUS` pattern, so
      `VerifyBuild.ps1`'s `--filter 'Category!=Corpus&Category!=Performance'` skips them by default.
- [ ] Keep all corpus/scenario assertions semantic — draw-call counts, bound-texture names,
      bounding-box fit — **never cross-GPU pixel equality**.
- [ ] Add `ModelPreviewScenarioTests.cs` covering the end-to-end scenario: unsupported-feature
      degradation and context-failure survival without leaks or cross-context handles.
- [ ] Add a working-set check after three concurrent model previews plus a forced GC, against
      `PLAN.md:229`'s 750 MiB idle target.
- [ ] Write `docs/evidence/MILESTONE-6-EVIDENCE.md` mapping every clause of the `PLAN.md:210` gate to
      a named test, and record the `VerifyBuild.ps1` run ID, build warning/error counts, and test
      pass/fail/skip counts, matching the `docs/evidence/MILESTONE-3-EVIDENCE.md` format.
- [ ] Write `docs/adr/0003-render-layer-placement-and-gpu-context-ownership.md` stating: render-layer
      placement and why no new project (constraint 1); GPU code in Preview with the
      `Func<string,IntPtr>` bridge as the sole Avalonia coupling; `IPreviewPayload` as process-local
      and never cached; and the deliberate non-import of upstream Toolset render files plus the
      compliance gate for ever reversing that.

**Exit Criteria**
- [ ] Corpus tests are semantic (geometry/material assertions, adapter-dependent smoke tests), never
      cross-GPU pixel equality, per `PLAN.md:230`.
- [ ] `VerifyBuild.ps1`'s default filter skips all `[Category("Corpus")]` tests with zero impact on
      the standard test run.
- [ ] Working-set check after three concurrent previews + forced GC is recorded against the 750 MiB
      target.
- [ ] `docs/evidence/MILESTONE-6-EVIDENCE.md` maps every `PLAN.md:210` gate clause to a named test and
      records a `VerifyBuild.ps1` run ID.
- [ ] ADR 0003 covers all four required points listed above.
- [ ] No production source file is touched by this slice.

## Notes
- S16 and S17 are independent of each other; S16 depends only on S1 (wave 0) and S6b (wave 1); S17
  depends on S9, S10 (wave 2), and S11 (wave 3).
- This is the final wave. After both slices land and `VerifyBuild.ps1` is green, the milestone
  branch is ready for a single PR per the execution model in `docs/MILESTONE-6.md`.
