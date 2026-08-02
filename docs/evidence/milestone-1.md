# Milestone 1 Evidence Record: Bootstrap and Compliance

- **Validation date:** 2026-08-02
- **Target:** Windows 11, .NET SDK 10.0.302, runtime pack 10.0.10, `win-x64`
- **SWLOR pin:** `8202faa203eddd6f4972104d22ea5740e23f20f7`
- **Owner licensing decision:** MIT, `Copyright (c) 2026 SRN.CC Authors`, as recorded in the root `LICENSE`
- **Local verification entry point:** `powershell -File tools/VerifyBuild.ps1`
- **Successful local run:** `20260802T220933Z-26452`

## Acceptance matrix results

| Area | Status | Reproducible evidence |
|---|---|---|
| Repository bootstrap | **PASSED** | `VerifyBuild.ps1` proves `/content/`, `/tools/local_only/`, and `/artifacts/` are ignored and untracked. |
| Toolchain and locks | **PASSED** | SDK 10.0.302 and runtime 10.0.10 are pinned. All seven lock files use the same `win-x64` graph; locked restore completes without mutation. |
| Architecture | **PASSED** | Seven projects build in Release. The dependency audit and architecture tests enforce the exact project-reference graph. |
| Vendoring | **PASSED** | 51 verbatim destinations reproduce their recorded Git blob IDs: 40 format sources, 8 portable tests, 2 upstream records, and 1 license. ModuleWriteLock, upstream project files, and the licensed corpus-test tree are explicitly excluded. |
| CP1252 identity | **PASSED** | Tests cover 1/16/17-byte boundaries, ASCII-only folding, CP1252 bytes, unencodable Unicode, NUL/separators, punctuation, immutable occurrence data, equality, and `ushort` boundaries. |
| HAK compatibility | **PASSED** | Tests cover semantic payload round-trip, non-sequential resource IDs, invalid indices, file type/version, CP1252 bytes, unknown types, zero-length payloads, case-equivalent duplicates, deterministic ordering, metadata normalization, malformed ranges, and the legacy size limit. |
| UI virtualization | **PASSED** | Headless test forces layout, scrolls to row 93,971 and row 187,942, verifies `TableViewRow` realization remains below 200, exercises multi-selection, and resizes the window. |
| Performance baseline | **PASSED** | Five measured Release iterations are committed in [`tableview-performance-baseline.json`](tableview-performance-baseline.json). Median first layout was 62.49 ms; realized-row high-water mark was 19; median private working set was 163.12 MiB. |
| Dependency compliance | **PASSED** | The audit checks 49 exact direct/transitive package versions, scopes, hashes, signatures, license metadata or reviewed file exception, evidence URLs, sources, NuGet audit settings/results, and project references. |
| Publish compliance | **PASSED** | Self-contained `win-x64` publish explained and SHA-256 hashed all 235 files from the project, approved NuGet packages, and runtime pack. Required first-party, SWLOR, .NET, ANGLE, SkiaSharp, HarfBuzzSharp, and CommunityToolkit licenses/notices were present; test/development/local files were absent. |
| Automation | **PASSED LOCALLY** | The single entry point completed locked restore, audits, Release build, 83 portable/headless tests with zero skips, publish, resolved-origin inventory, and hidden Release launch smoke. GitHub Actions runs the same entry point and uploads the complete run evidence. |

## UI smoke evidence

The previous subjective manual-only assertion has been replaced by reproducible
evidence that can run on every Windows verification host:

1. the headless functional test performs layout, middle/end scrolling,
   multi-selection, and resize while measuring actual `TableViewRow` containers;
2. the opt-in five-iteration performance probe records construction, allocation,
   first layout, two-scroll layout, realization high-water, sort/filter, and private
   working-set measurements; and
3. the master verification launches the audited self-contained Release executable
   and confirms that it remains alive before terminating the exact smoke process.

A human visual check remains useful release UX evidence, but it is no longer the
only proof of the Milestone 1 behavior.

## Reproduction

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/VerifyBuild.ps1

$env:SRNCC_RUN_PERF = '1'
dotnet test tests/SRN.CC.CorpusTests/SRN.CC.CorpusTests.csproj `
  -c Release --no-build --filter 'Category=Performance' `
  -p:SRNCCVerificationRuntimeIdentifier=win-x64
```

Each verification uses a unique directory beneath `artifacts/verification-runs/`.
A failed run cannot overwrite or masquerade as earlier successful evidence.
