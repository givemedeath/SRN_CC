# Milestone 7 - Phase 5: Closeout (Wave 4)

## Scope

One slice. Every implementation has landed and every test is green; what remains is proving it
against the gate in a form a reviewer can check without rerunning the milestone.

This wave is deliberately serial and singular. The evidence document maps gate clauses to **named
tests**, so every test it names must already exist and pass — which is exactly why it cannot run
concurrently with wave 3. ADR 0005 records the seams this milestone introduced, so it must be written
after those seams survived contact with the composition root.

## Deliverables

- `docs/evidence/MILESTONE-7-EVIDENCE.md` — the gate-clause and acceptance-plan mapping, in the
  `docs/evidence/MILESTONE-6-EVIDENCE.md` format.
- `docs/adr/0005-startup-preflight-logging-and-schema-seams.md` — the architectural record for A1
  through A7, with the deferrals stated explicitly.
- The signed-off manual real-GPU operator checklist, carried inside the evidence document.

## Detailed tasks

### S23 — Evidence, architecture ADR, manual operator checklist
Owns NEW `docs/evidence/MILESTONE-7-EVIDENCE.md`; NEW
`docs/adr/0005-startup-preflight-logging-and-schema-seams.md`.
- [x] Follow the `docs/evidence/MILESTONE-6-EVIDENCE.md` structure: numbered sections, a gate-clause
      mapping table, a deferred-and-not-gate-blocking section, and a verification-pass results
      section split into the ad-hoc run and the authoritative `VerifyBuild.ps1` record.
- [x] Map **every clause** of `PLAN.md:212-215` to a fully-qualified `TestClass.TestMethodName` in a
      `| Milestone-7 clause | Status | Reproducible evidence |` table. Use `**PASSED**` or
      `**PASSED (by construction)**`. **Never a prose claim** where a test exists, and never a test
      name where one does not.
- [x] Map **every bullet** of `PLAN.md:217-230` the same way — identity, HAK, resolution,
      persistence, UI, preview, dependency, build, publication, oracle, and corpus/performance. The
      oracle row cites ADR 0004 as a contract-supported rejection, not a test.
- [x] Record the `tools/VerifyBuild.ps1` run ID from the final eleven-step pass, plus build warning
      and error counts and test pass/fail/skip counts.
- [x] Record the release archive name, its SHA-256, and its file count, cross-referenced against
      `release-manifest.json`.
- [x] Record the corpus baseline numbers from `docs/evidence/corpus-index-baseline.json` — 117 HAKs,
      187,943 occurrences, 212 type-2078 entries, 4 duplicate archives, the cold-index median against
      the ten-second threshold, and the private and total working set after 30 seconds idle against
      the 750 MiB threshold — together with the reference machine's environment.
- [x] Carry the **manual real-GPU operator checklist** with an explicit sign-off line. Cover: first
      launch on a machine with no `%LOCALAPPDATA%\SRN.CC`, confirming the preflight report reaches the
      operation log and `Logs\srncc.log` is created as valid JSON-lines; open a real HAK, resolve a
      conflict, pin, save as `.srnccproj`, rescan, build, and confirm the HAK loads in the toolset;
      corrupt `cache-v1.sqlite` and relaunch, confirming quarantine and recovery; kill the process
      mid-publish and relaunch, confirming journal rollback; and the 3D path — select an MDL,
      orbit/pan/dolly, toggle the walkmesh, fill three concurrent viewports, confirm missing-texture
      degradation, and force a shader-unsupported path to confirm the text fallback.
- [x] Record what is **deferred and not gate-blocking**, restating the parent document's deferral
      list verbatim so the two cannot drift: the `PLAN.md:143` build-start fingerprint recheck and
      `DiagnosticCode.SourceDriftDetected`, the `nwn_erf` oracle (ADR 0004), recursive unknown-field
      preservation for settings, an automated real-GPU smoke test, startup GL probing, and the
      Milestone 6 render deferrals that remain open.
- [x] Record the two **plan-versus-code divergences** found during wave 0, so the mapping table
      cannot imply coverage that does not exist:
      1. `PLAN.md:143` requires a fingerprint recheck at build start.
         `DiagnosticCode.SourceDriftDetected` (`DiagnosticCode.cs:10`) is emitted nowhere and
         `BuildOrchestrator` performs no such comparison. State what does protect the build instead
         (held `FileShare.Read` handles, the per-entry recheck at `HakAssetSourceReader.cs:254-263`,
         and the independent verifier pass) and cite `HakDeterminismTests` for the observable
         requirement.
      2. `PLAN.md:159` implies camelCase manifest fields; the generator emits PascalCase because no
         `PropertyNamingPolicy` is set (`ProvenanceManifestGenerator.cs:94-98`). The tests and
         `docs/operator/MANIFEST-FORMAT.md` follow the code. Note that `PLAN.md`, not the code, is
         the inaccurate artifact here.
      3. Supporting detail for the ADR 0004 row: the `nwn_erf.exe` SHA-256 pin at `PLAN.md:173` is
         63 hex characters, not 64, so the specified downloader was never implementable as
         transcribed. This strengthens the rejection but is not its primary basis.
- [x] ADR 0005 in the MADR shape used by `docs/adr/0003-*.md`: `## Context and Problem Statement`,
      `## Decision Drivers`, `## Considered Options`, `## Decision Outcome`, `### Consequences`,
      `### Rejected options`.
- [x] ADR 0005 must record, as durable constraints rather than milestone trivia: that no DI container
      is used and why (`Microsoft.Extensions.DependencyInjection` is not pinned, and adding it means a
      policy entry, a content hash, signature verification, and seven regenerated lock files); that
      logging is first-party and synchronous by design; that the trailing-optional-`IAppLogger`
      convention exists to keep constructor changes non-breaking; that `SchemaVersions` is the single
      source of schema truth and the additive `EnsurePreviewCacheSchema` pattern is the cache's
      sanctioned upgrade path; that settings follow the project-file newer-schema rule; and that the
      release packer is PowerShell specifically because a .NET tool would add a project and break
      `AuditDependencies.ps1`'s hard-coded project count.
- [x] Do not write any provenance keyword subject to the `AuditVendoredSources.ps1` grep into a
      `.cs`, `.csproj`, or `.axaml`. Both files here are `.md` and are not scanned.

**Exit Criteria**
- [x] Every gate clause and every acceptance-plan bullet has a row, and every row that claims a test
      names one that exists and passes — verified by running the named filter, not by inspection.
- [x] The recorded `VerifyBuild.ps1` run ID exists under `artifacts/verification-runs/` and its
      `summary.json` reports `status` success along with the new `version`, `releaseZipPath`, and
      `releaseZipSha256` fields.
- [x] The recorded archive SHA-256 matches `release-manifest.json` and matches a fresh independent
      repack of the same publish tree.
- [x] The manual checklist is either signed off with a date, or explicitly marked unrun with the
      reason — never silently left ambiguous.
- [x] ADR 0005's constraints are stated so a future milestone can act on them without reading this
      milestone's phase documents.

## Wave 4 outcome

Both deliverables landed: `docs/evidence/MILESTONE-7-EVIDENCE.md` and
`docs/adr/0005-startup-preflight-logging-and-schema-seams.md`.

Wave 4 also closed two code gaps wave 3 had recorded rather than fixed, because the evidence document
could not otherwise name a test for `PLAN.md:225` or `PLAN.md:221` (commit `9082dc8`): closure size
and count budgets in `DependencyTraversalEngine`, and `DependencyLocator`'s blindness to pins. Sixteen
tests landed, and three more with the banner-rendering tests below; the suite finished at 1024
passing, 0 failing.

**Authoritative record:** `tools/VerifyBuild.ps1` run `20260805T132457Z-12736`, status SUCCESS, on a
clean tree (`gitStatus` empty). Version 1.0.0; `SRN.CC-1.0.0-win-x64.zip`, 252 files, SHA-256
`447517ce93b59e45eda0ea5914cdd3bdfc31aae50416c03a49d0de8c09c5f451`, reproduced byte-identically by
the independent repack in step 9 **and, before the review fix below changed the shipped bytes, by three successive full passes on clean trees**. Build 0 warnings
/ 0 errors; tests 1024 passed / 0 failed / 0 skipped.

**Found while verifying, not while building.** The truncation banner added to
`ConfirmDependenciesDialog` was unverified: no project sets `AvaloniaUseCompiledBindingsByDefault`,
so `x:DataType` is declarative only and an unresolvable binding path fails silently at runtime while
still compiling. Three headless tests now render the real dialog
(`UI.ConfirmDependenciesDialogTests`), the load-bearing one being the null-`Summary` case, since
`IsVisible` defaults to `true`. The same pass also corrected a wrong startup-check order stated in an
early draft of the evidence document. Both are recorded because "the build is green" was, in this
codebase, not evidence for either claim.

**Two items are ticked as recorded, not as measured.** Both were closed the way this document's own
notes require — stated plainly rather than left ambiguous — and both remain open work for an
environment that can run them:

- **The corpus baseline is unmeasured.** `SRNCC_CORPUS_ROOT` is unset and no corpus is present, so
  `corpus-index-baseline.json` still reads `"recorded": false`. The evidence document records the
  structural constants, marks the cold-index median and the idle working set as not measured, and
  marks `PLAN.md:229`'s clauses **unverified** rather than passed.
- **The manual real-GPU operator checklist is unrun.** It is carried in full in section 13 of the
  evidence document with an unsigned sign-off line and an explicit reason: no corpus, no toolset, no
  verified GPU adapter. Items 1, 3 and 4 have partial automated analogues, which are named; item 5
  has the least substitute and says so.

One further caveat is recorded rather than glossed: the live clean-machine smoke run used
`-AllowExistingAppData`, because `%LOCALAPPDATA%\SRN.CC` already existed on the development machine.
The genuinely-clean first-launch path is covered by the `clean-machine-smoke` CI job, which never
passes the switch.

## Notes

- **This wave is serial for a reason.** The evidence document's value is that every row is checkable.
  Writing it against tests that are still landing produces rows that were true at the time of writing
  and false at merge.
- Follow the review closure policy in `AGENTS.md`: every resolved review thread needs either a
  traceable fixing commit or a documented, contract-supported rejection. A resolved UI state alone is
  never closure evidence. The oracle row in the mapping table is the canonical example of a
  contract-supported rejection — it cites ADR 0004 and `PLAN.md:228`.
- Milestone 6's evidence document is the format reference, and `docs/MILESTONE-6-5.md` is the
  reference for how a closeout wave records a slice closed as a deliberate no-op. If any Milestone 7
  slice closes that way, record it the same way.
- If the manual checklist cannot be run — no GPU, no toolset, no corpus — say so plainly in the
  evidence document and mark the affected gate clauses as unverified. `PLAN.md:230` requires
  adapter-dependent smoke tests rather than cross-GPU pixel equality precisely because this path is
  environment-bound. An honest gap is auditable; an implied pass is not.
- The parent document's "Explicitly deferred" list and this document's deferred section must match
  word for word. If they diverge during closeout, the parent is wrong and should be corrected in the
  same commit.
