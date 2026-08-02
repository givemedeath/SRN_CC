# Milestone 1: Bootstrap and Compliance — Validated Plan

Validated on 2026-08-02 against the local `SRN_CC` workspace, .NET SDK 10.0.302,
the local 117-HAK corpus, the upstream `SWLOR_NWN` repository, NuGet package
metadata, and the Avalonia 12.1 API.

## Outcome

Milestone 1 establishes a reproducible, auditable Windows-first .NET 10/Avalonia
foundation. It ends with:

- a Git repository whose first tracked state excludes all corpus and local-only tools;
- a buildable seven-project solution with enforced dependency direction;
- exact SDK, direct-package, transitive-package, and self-contained runtime inputs;
- a selectively vendored and traceable `SWLOR.NWN.Formats` snapshot;
- production-shaped CP1252 identity and HAK read/write primitives proven by fixtures;
- a 187,943-row Avalonia `TableView` virtualization and selection probe;
- repeatable dependency, license, vulnerability, vendored-source, and publish audits;
- a locked restore, Release build, portable test run, and self-contained `win-x64`
  publish that pass locally and in CI.

This milestone does **not** implement corpus indexing, SQLite caching, project
persistence, previews, dependency discovery, OpenGL rendering, audio playback, or
final HAK publication. Those remain in later milestones.

## Validation findings that change the original plan

1. The repository really is uninitialized: `git status` reports that it is not a Git
   repository. `PLAN.md` is not empty, however, so that statement in the product plan
   must be corrected.
2. SDK `10.0.302` is installed. Because package lock files are authoritative, use
   `rollForward: disable`, not `latestPatch`, and set `allowPrerelease: false`.
3. `net10.0` defaults to C# 14. Pin `LangVersion` to `14.0`; the proposed C# 13 setting
   is an unnecessary downgrade and contradicts the target framework default.
4. Avalonia `12.1.0` exists and contains the core `TableView`. `TableView` derives from
   `ListBox`, so ListBox multi-selection is available. TreeDataGrid is not needed.
5. The exact SWLOR pin is
   `8202faa203eddd6f4972104d22ea5740e23f20f7`, verified as the upstream
   `feature/swlor-toolset` tip on the validation date. The local adjacent checkout's
   `8f1ac126...` is an ancestor and is 625 commits behind. Do not use an “or one of
   these commits” pin.
6. At the selected commit the three upstream trees contain 44 Formats files, 9
   portable-test files, and 9 corpus-test files. The format attestation was introduced
   at `f79eff6e0f269573b98e2e68373f5fc9f99b785b` on 2026-07-26, but parser and test
   blobs changed afterward. Treat that attestation as historical baseline evidence,
   not certification of every byte at `8202faa...`; review and record the delta.
   The tree also includes `Common/ModuleWriteLock.cs`, which is unrelated to format
   parsing and outside the stated 40-reader review scope. Exclude it explicitly.
7. The upstream portable tests use `FluentAssertions` 7.2.2. The original package
   list omitted it, so an unadapted CPM restore would fail. Retain 7.2.2 as a
   test-only Apache-2.0 dependency, or rewrite every assertion and record each test
   file as adapted. Retaining it is the lower-risk Milestone 1 choice.
8. Pfim 0.11.4 has only a legacy `licenseUrl` in its NuGet metadata, not an SPDX
   license expression. A policy that blindly rejects unknown expressions would
   reject it. Pfim, Silk.NET, NAudio, and SQLite are not used in this milestone and
   must not be restored yet; review and add them when their features are introduced.
9. “Binary-identical HAK round-trip” is not a valid assertion for arbitrary input
   HAKs when the writer sorts entries and normalizes header metadata. Require a
   semantic read/write round-trip, plus byte-identical results from two writes of the
   same normalized input.
10. A headless test that merely creates 187,943 items does not prove virtualization
    or smooth scrolling. The gate must inspect realized container counts and record
    layout/allocation/timing metrics. Human-perceived smoothness remains a Windows
    smoke check.
11. A self-contained publish necessarily contains .NET runtime and native files, so
    “only application binaries, notices, and licenses” is not a workable allowlist.
    Audit the complete resolved file inventory and reject unexpected or forbidden
    inputs instead.
12. A PowerShell script plus a unit test is not automated compliance by itself. Add a
    Windows CI job that runs the same locked verification entry point.

## Decisions and prerequisites

### Required owner decision

Before creating the root `LICENSE`, the repository owner must approve:

- MIT as the license for first-party `SRN.CC` code; and
- the exact copyright-holder text and initial year.

The SWLOR MIT license and provenance can be retained independently, but they do not
authorize choosing the license for new first-party code.

### Fixed technical decisions

- Initial branch: `main`; no remote is required for Milestone 1.
- Initial target: `net10.0`, `win-x64`, self-contained, non-single-file; runtime
  framework `10.0.10` selected by SDK `10.0.302`.
- SDK: exactly `10.0.302`; C# exactly `14.0`.
- UI: Avalonia `TableView`, not TreeDataGrid.
- SWLOR source pin: exactly `8202faa203eddd6f4972104d22ea5740e23f20f7`.
- Portable tests run on every verification.
- Corpus and performance tests are opt-in. Merely setting a corpus path must not
  accidentally start a 17 GiB test run.
- `SRNCC_RUN_CORPUS=1` enables corpus tests. `SRNCC_REQUIRE_CORPUS=1` implies run and
  fails if the root is absent or invalid. `SRNCC_RUN_PERF=1` enables performance
  probes.

## Repository shape

Create:

```text
SRN.CC.sln
global.json
Directory.Build.props
Directory.Packages.props
NuGet.Config
.editorconfig
.gitattributes
.gitignore
LICENSE                         # only after owner approval
NOTICES.md
src/
  SRN.CC.Core/
  SRN.CC.Formats/
  SRN.CC.Infrastructure/
  SRN.CC.Preview/
  SRN.CC.App/
tests/
  SRN.CC.Tests/
  SRN.CC.CorpusTests/
eng/
  dependency-policy.json
  publish-policy.json
  Versions.props
tools/
  AuditDependencies.ps1
  AuditVendoredSources.ps1
  AuditPublish.ps1
  VerifyBuild.ps1
docs/
  adr/
  compliance/
  evidence/
.github/workflows/verify.yml
```

Use `artifacts/` for generated reports, test results, and publish output. Do not use a
versioned `WORKLOG.md`; Git history, ADRs, and a concise reproducible milestone
evidence record have clearer ownership and less merge noise.

### Project dependency rules

```text
SRN.CC.App            -> Core, Infrastructure, Preview
SRN.CC.Infrastructure -> Core, Formats
SRN.CC.Preview        -> Core, Formats
SRN.CC.Core           -> no project references and no UI dependency
SRN.CC.Formats        -> no project references and no UI dependency
```

`SRN.CC.Tests` may reference the production projects needed by portable and headless
tests. `SRN.CC.CorpusTests` may reference Core, Formats, and App. An architecture test
must parse project references and fail if the production graph violates these rules.

Because Core and Formats remain independent, HAK primitives must not accept
`AssetIdentity` directly. Formats exposes a format-level key containing raw resref
bytes and `ushort` type; Infrastructure maps that key to Core's `AssetIdentity`.

## Phase 1 — Safe repository bootstrap

1. Create `.gitignore` before staging anything. Ignore root `/content/`,
   `/tools/local_only/`, `/artifacts/`, all `bin/`, `obj/`, `.vs/`, IDE user files,
   logs, test results, and runtime cache sidecars.
2. Do **not** globally ignore `*.sqlite`; later source-controlled schema or corruption
   fixtures may legitimately use that extension.
3. Add `.gitattributes` and `.editorconfig` to fix UTF-8 text, line endings, and basic
   C# style before generated files are committed.
4. Initialize `main`, then prove the exclusions with `git check-ignore` and review
   `git status --short` before the first commit. No path under `content/` or
   `tools/local_only/` may appear as tracked or staged.
5. Record an ADR for the Windows-first, self-contained deployment choice and one for
   the Core/Formats independence boundary.

**Gate:** Git reports only intended source/configuration files; a scripted negative
test proves representative corpus and local-tool paths are ignored.

## Phase 2 — Reproducible toolchain and package graph

### SDK and build defaults

`global.json`:

```json
{
  "sdk": {
    "version": "10.0.302",
    "rollForward": "disable",
    "allowPrerelease": false
  }
}
```

`Directory.Build.props` must set:

- `TargetFramework=net10.0`;
- `LangVersion=14.0`;
- nullable and implicit usings enabled;
- deterministic builds and warnings as errors;
- `RestorePackagesWithLockFile=true`;
- `RestoreLockedMode=true` only when `ContinuousIntegrationBuild=true` or when the
  verification script passes `--locked-mode`;
- `NuGetAudit=true`, `NuGetAuditMode=all`, and an explicit severity policy;
- Release publishing without PDB/source-path leakage unless symbols are emitted to a
  separate artifact.

Generate lock files with one unlocked restore after the project graph is complete.
Every later verification uses locked mode. Lock files are committed; `obj/` assets
are not.

Pin the self-contained runtime framework version separately in `eng/Versions.props`
so servicing updates are explicit rather than silently selected by a later SDK.
Validate that the pin agrees with the runtime pack selected by SDK 10.0.302.

### Milestone 1 direct package set

Only restore packages used by Milestone 1:

| Package | Version | Scope |
|---|---:|---|
| Avalonia | 12.1.0 | App/runtime |
| Avalonia.Desktop | 12.1.0 | App/runtime |
| Avalonia.Themes.Fluent | 12.1.0 | App/runtime |
| Avalonia.Diagnostics | 12.1.0 | Debug only, `PrivateAssets=all` |
| Avalonia.Headless.NUnit | 12.1.0 | Test only |
| CommunityToolkit.Mvvm | 8.4.2 | App/runtime |
| FluentAssertions | 7.2.2 | Test only; required by imported portable tests |
| NUnit | 4.6.1 | Test only |
| NUnit3TestAdapter | 6.2.0 | Test only, `PrivateAssets=all` |
| Microsoft.NET.Test.Sdk | 18.8.1 | Test only, `PrivateAssets=all` |

Do not add Microsoft.Data.Sqlite, Silk.NET.OpenGL, Pfim, NAudio, or logging/DI packages
until code in a later milestone uses them. Central version declarations that no
project references are not evidence that a package graph has been restored or
audited.

`NuGet.Config` must clear inherited package sources, add only the NuGet v3 source,
require signed packages, trust the reviewed NuGet.org repository signer certificate
set, and configure the same source for vulnerability audit data.
Never suppress an advisory without a package-specific, dated review entry.

**Gate:** unlocked bootstrap restore creates all lock files; a second locked restore
is a no-op; build/test inputs contain no unapproved package source or floating
version.

## Phase 3 — Selective SWLOR vendoring with provenance

Import only from the Git object identified by the exact pin. Never copy from the
adjacent working tree, because it is stale and modified.

### Included upstream material

- the 40 selected format-reader `.cs` files under `SWLOR.NWN.Formats`;
- the eight portable test `.cs` files under `SWLOR.NWN.Formats.Tests`;
- `FORMAT-PROVENANCE.md` and `FORMAT-REVIEW-ATTESTATION.md`, retained verbatim and
  clearly labeled as upstream records;
- the upstream root `LICENSE.txt`, copied to a stable third-party-license location.

### Explicit exclusions

- `Common/ModuleWriteLock.cs`: unrelated module-mutation coordination, outside the
  40-reader attestation scope, and unnecessary for SRN.CC formats;
- upstream `.csproj` files: replace with SRN.CC CPM-aware project files;
- `SWLOR.NWN.Formats.Corpus.Tests`: tied to the upstream licensed integration
  environment and not a portable dependency of Milestone 1;
- every Radoub path, object, history item, namespace, assembly, and binary.

Preserve upstream namespaces and SPDX headers. Place vendored code below a visibly
named `Vendored/SWLOR.NWN.Formats/` subtree; place first-party HAK code outside that
subtree. Do not mass-rename namespaces.

Create `vendored-sources-manifest.json` with, for every included or explicitly
excluded upstream item:

- repository URL and exact 40-character commit;
- upstream path and Git blob SHA-1;
- destination path, or exclusion reason;
- destination SHA-256;
- status: `verbatim`, `adapted`, or `excluded`;
- for adaptations, a concise reason and review reference.

Add `VENDORING.md` explaining that upstream provenance/attestation is preserved as
historical evidence and is not a new legal opinion for SRN.CC. The source audit must
recompute Git blob IDs for verbatim files and SHA-256 for all destinations.

Use `f79eff6e0f269573b98e2e68373f5fc9f99b785b` as the historical attestation anchor
and review every selected source/test change from that commit through
`8202faa203eddd6f4972104d22ea5740e23f20f7`. Record the changed-path list, reviewer,
findings, dispositions, and test evidence in SRN.CC's own vendoring review. Do not edit
the upstream attestation to imply that this later delta was part of its original
review.

Run all 41 upstream portable test cases with zero skips. If package integration
requires changing test source, record each modified file as adapted and show that the
assertion semantics are unchanged.

**Gate:** source audit and the SRN.CC post-attestation delta review pass, the Formats
project has no external package or project reference, all 41 imported portable tests
pass, and no prohibited Radoub/GPL path or identifier exists outside deliberate audit
assertions.

## Phase 4 — Core identity and HAK compatibility slice

### Asset identity

Implement an immutable `AssetIdentity` in Core:

- register `CodePagesEncodingProvider` once;
- encode input text with Windows-1252 and exception fallbacks;
- require 1–16 encoded bytes;
- reject NUL, `/`, and `\`;
- fold only byte values for ASCII `A`–`Z` to `a`–`z`;
- preserve original occurrence bytes/text outside identity for display and
  diagnostics;
- compare/hash canonical resref bytes plus `ushort ResourceType`; extension is not
  part of identity.

Test empty, 1/16/17-byte limits, CP1252 accents, unencodable Unicode, case-only
differences, punctuation, separators, NUL, and all `ushort` type boundaries.

### Formats/Core bridge

Formats preserves the raw 16-byte HAK resref field and exposes the trimmed bytes plus
the `ushort` type. Conversion to `AssetIdentity` is explicit and fails diagnostically
for invalid or non-round-trippable names; it must never silently replace bytes.

### HAK primitives

Implement first-party, production-shaped primitives sufficient for a compatibility
slice:

- parse/write the ERF V1.0 layout with `HAK ` file type;
- use the actual header fields: language count, localized-string size, entry count,
  localized/key/resource offsets, build year/day, description strref, and reserved
  bytes;
- use checked 64-bit arithmetic while validating table bounds;
- preserve arbitrary `ushort` resource types and zero-length payloads;
- reject malformed key/resource indexes and out-of-range payload regions;
- expose bounded payload streams; never extract to a directory;
- accept an already validated format-level key rather than referencing Core;
- reject duplicate output keys;
- normalize writer metadata and reserved bytes to zero;
- sort unique entries by numeric type, then canonical CP1252 resref bytes;
- write payloads through a bounded buffer without holding an entire payload in
  memory;
- reject estimated output at or above the chosen legacy single-HAK limit before
  writing.

The full adversarial reader, overlap analysis, source fingerprints, and corpus-wide
indexing remain Milestone 2.

Tests must prove:

- CP1252 resrefs survive a semantic write/read round-trip as exact bytes;
- unknown type values and zero-byte payloads survive;
- payload bytes and sizes are unchanged;
- two normalized writes from identical inputs are byte-identical;
- changing input enumeration order does not change output;
- a deliberately metadata-rich input is semantically preserved but is not falsely
  required to reproduce byte-for-byte after normalization;
- truncated headers/tables and out-of-range payloads fail safely.

**Gate:** all identity and HAK compatibility tests pass without using `nwn_erf` or any
local-only binary as a runtime/test dependency. An external tool may be used later as
an opt-in ASCII compatibility oracle only.

## Phase 5 — Avalonia shell and 187,943-row virtualization probe

Create the minimal desktop app, Fluent theme, main window, and a central `TableView`
with multiple row selection. Use lightweight immutable synthetic rows and the same
column shapes planned for the curation grid. Sorting/filtering belongs to the view
model; do not claim TableView supplies it automatically.

Do not assume that setting an `ItemsPanel` proves virtualization. First use the
Avalonia 12.1 default TableView theme, then override with `VirtualizingStackPanel`
only if the probe shows it is needed and supported.

Split verification into:

1. **Portable headless functional test:** create 187,943 rows, realize a bounded
   viewport, scroll to the middle and end, exercise multi-selection, and assert that
   realized row containers remain proportional to the viewport rather than the data
   set. This is a hard CI gate.
2. **Opt-in performance probe:** record row-source construction time, managed
   allocation, first layout time, realized-container high-water mark, sort/filter
   timings, private working set, OS, CPU, SDK, and configuration to
   `artifacts/performance/tableview.json`. Run at least five measured iterations and
   report median and worst values. Establish the first accepted reference-machine
   baseline; later runs fail on an explicitly approved regression tolerance rather
   than a universal `<100 ms` claim.
3. **Windows Release smoke:** the master verifier launches the audited self-contained
   executable, while the headless interaction test exercises middle/end scrolling,
   multiple selection, and resize. Record the measured reference-machine baseline in
   `docs/evidence/milestone-1.md`. A human visual pass remains useful release UX
   evidence but is not the sole automated gate.

No test should allocate preview payloads, images, or per-row controls for all rows.

**Gate:** functional container-bound assertions pass in CI, a reproducible baseline
JSON is captured on the reference machine, and the Release manual smoke check has no
visible long UI stalls.

## Phase 6 — Compliance and supply-chain verification

### Dependency policy

`eng/dependency-policy.json` is package-specific, not merely a list of acceptable
license names. Each exact direct/transitive package records:

- package ID and resolved version;
- runtime, development, or test scope;
- SPDX expression or a reviewed exact-package exception;
- repository/license evidence and review date;
- whether its license/notice must ship;
- lock-file content hash or package hash.

`AuditDependencies.ps1` reads every project lock/assets graph, not just direct
`PackageReference` elements. It fails on an unlisted package/version, changed license
metadata, unknown source, vulnerable package at the chosen severity, missing review,
or unexpected project/assembly reference. Legacy `licenseUrl` metadata requires a
package/version-specific exception with hashed evidence; it is never accepted as
“public domain” by heuristic.

Keep network/package-cache inspection in the audit script. Unit tests should test the
policy parser and decisions with fixtures; they should not masquerade as the actual
environment audit.

### Notices and runtime

Generate or verify `NOTICES.md` from the resolved **distributed** graph. Include the
first-party license, SWLOR license/attribution, all required runtime package notices,
and the .NET runtime `LICENSE.txt` and `ThirdPartyNotices.txt` appropriate to the
pinned self-contained runtime. Test/dev-only packages remain in the compliance report
but do not need to ship unless their license requires it.

### Publish audit

Publish to `artifacts/publish/win-x64`, never into a project `bin` directory. Generate
`publish-inventory.json` containing relative path, size, SHA-256, and origin for every
file. Derive allowed managed/native dependencies from the app `.deps.json`, runtime
pack, and approved package graph.

Fail when the publish contains:

- `.hak`, `.erf`, `.mod`, corpus/project/cache data, or local oracle/tool files;
- test assemblies, `Avalonia.Diagnostics`, or test/dev packages;
- any unapproved managed/native assembly;
- a workspace absolute path, `content` path, `tools/local_only` path, or outside-project
  source path embedded in a distributable file;
- a missing required license/notice;
- a file whose hash/origin cannot be explained by the inventory.

This is a deny-forbidden-plus-resolved-origin policy, not a brittle filename-only
allowlist.

### CI

Add one Windows verification workflow, with third-party actions pinned by immutable
commit SHA. It installs SDK 10.0.302, performs locked restore, builds Release, runs
portable/headless tests, runs all three audits, publishes self-contained `win-x64`,
and uploads reports/inventory. It never checks out or uploads `content/` or
`tools/local_only/`. Corpus/performance jobs remain opt-in and run only on an
authorized machine with the licensed corpus.

## Verification entry point

`tools/VerifyBuild.ps1` is the single local/CI entry point and stops on the first
failed process. In order it:

1. proves SDK `10.0.302` was selected;
2. runs the complete seven-project solution restore in locked `win-x64` mode, using
   the same project-level RID property later used by build and test;
3. verifies ignore rules and that no corpus, local-tool, or artifact path is tracked;
4. runs dependency/license/vulnerability and vendored-source audits;
5. builds the solution Release with `--no-restore`;
6. runs portable and headless functional tests with `--no-build` while excluding
   Corpus and Performance categories;
7. publishes App Release, `win-x64`, self-contained, non-single-file, with
   `--no-restore` to the current unique verification-run directory and adds the
   reviewed license/notice set;
8. runs the publish inventory/audit;
9. verifies lock files and vendored source files did not change;
10. writes a machine-readable summary and complete evidence beneath a unique
   `artifacts/verification-runs/<run-id>/` directory.

The script must preserve the previous artifact directory until a new verification is
successful, or use a unique run directory, so a failed run does not masquerade as a
valid publish.

## Acceptance matrix

| Area | Required evidence |
|---|---|
| Repository | `main` initialized; corpus/local tools ignored and untracked |
| Toolchain | exact SDK/C# pins; committed lock files; locked restore succeeds |
| Architecture | seven projects build; production reference rules pass |
| Vendoring | exact commit/blob manifest; 40 selected reader sources; post-attestation delta reviewed; 41 portable tests pass with zero skips |
| Identity | strict CP1252 and bytewise ASCII folding tests pass |
| HAK | semantic round-trip, exact payload bytes, deterministic normalized writes, safe malformed-input failures |
| UI | 187,943-row headless container bound passes; performance baseline and Windows smoke evidence recorded |
| Dependencies | every resolved package/version/scope/license reviewed; vulnerability policy passes |
| Publish | self-contained `win-x64` inventory fully explained; required notices present; forbidden content absent |
| Automation | the same `VerifyBuild.ps1` passes locally and in Windows CI |

## Milestone exit criteria

Milestone 1 is complete only when all acceptance evidence is committed or reproducibly
generated, the owner licensing decision is recorded, `tools/VerifyBuild.ps1` exits
zero from a clean checkout with package/network access, and the produced publish
inventory contains no local corpus, tool, test, development, or unapproved file.
