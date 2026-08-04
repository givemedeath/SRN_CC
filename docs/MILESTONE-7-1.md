# Milestone 7 - Phase 1: Contracts, Policy Data, and Documentation (Wave 0)

## Scope

The widest and cheapest wave. Nine slices run fully in parallel because eight of them create only new
files and the ninth (S5) owns the milestone's single touch of `Directory.Build.props`. Three of these
slices (S2, S3, S4) are **hard prerequisites** for wave 1: every wave-1 implementation compiles
against `IAppLogger`, `IStartupCheck`, `AppPaths`, and `SchemaVersions`. Under
`TreatWarningsAsErrors=true` a half-landed contract is a solution-wide build break, so the wave-0 →
wave-1 barrier is load-bearing, not ceremonial.

No slice in this wave — or in this milestone — may add a project or a vendored source file.
`tools/AuditDependencies.ps1:102,161` hard-codes `Count -ne 7` and `tools/AuditVendoredSources.ps1`
hard-codes `40`/`8` at three sites.

## Deliverables

- The Milestone 7 document set: this file plus the parent and the four remaining phase documents.
- ADR 0004 closing the `nwn_erf` oracle as intentionally not built.
- Three Core contract families with no implementations: logging (A3), startup (A2), and schema (A4).
- Version stamping from a single source in `eng/Versions.props`, consumed by the provenance manifest.
- Operator documentation and a root `README.md` — the first user-facing documentation in the
  repository.
- Release policy data (`eng/publish-policy.json` extensions plus a new `eng/release-policy.json`)
  that wave 1's scripts read and never write.
- `SRNCC_REQUIRE_CORPUS`, a single corpus gating helper replacing three duplicated `[SetUp]` blocks,
  the 30-second idle probe, and a recorded cold-index reference baseline.

## Detailed tasks

### S0 — Milestone document set
Owns NEW `docs/MILESTONE-7.md`, `docs/MILESTONE-7-1.md`, `docs/MILESTONE-7-2.md`,
`docs/MILESTONE-7-3.md`, `docs/MILESTONE-7-4.md`, `docs/MILESTONE-7-5.md`.
- [x] Parent document with Summary, Decisions, Verified baseline, Verified constraints, Architecture
      decisions A1-A8, Shared contracts, Scope, Work slices, Risks, and Verification.
- [x] One phase document per wave, each with Scope, Deliverables, Detailed tasks with `Owns`
      declarations and per-slice Exit Criteria, and Notes.

**Exit Criteria**
- [x] Every slice S0-S23 appears in exactly one phase document with a complete `Owns` list.
- [x] No two concurrently-running slices declare the same file.

### S1 — Oracle NO-BUILD ADR
Owns NEW `docs/adr/0004-nwn-erf-oracle-intentionally-not-built.md`.
- [ ] Follow the MADR shape used by `docs/adr/0003-*.md`: `## Context and Problem Statement`,
      `## Decision Drivers`, `## Considered Options`, `## Decision Outcome`, `### Consequences`,
      `### Rejected options`.
- [ ] Record that `PLAN.md:170-176` specifies an opt-in downloader with three pinned SHA-256 values
      and `PLAN.md:228` specifies opt-in compatibility tests, and that neither is built.
- [ ] Record the reasoning: `PLAN.md:228` itself forbids the oracle from being the authoritative
      duplicate or CP1252 verifier; `HakRoundTripTests.cs`, `HakReaderTests.cs`, and S16's new
      determinism tests already are that verifier and cover cases the oracle's ASCII-only listing
      cannot; and a shell-out would add a tool-discovery path, a forbidden-fragment risk in release
      output, and an untestable-in-CI surface.
- [ ] Record the reopening condition, the way `docs/adr/0003-*.md:182` defines a compliance gate.

**Exit Criteria**
- [ ] The ADR names the specific tests that discharge the oracle's intended role.
- [ ] No provenance keyword subject to the `AuditVendoredSources.ps1` grep appears in any `.cs`,
      `.csproj`, or `.axaml` as a result of this slice. This ADR is `.md` and is not scanned.

### S2 — Core logging contracts *(hard prerequisite)*
Implements A3's contract half. Owns NEW `src/SRN.CC.Core/Logging/LogLevel.cs`, `LogRecord.cs`,
`ILogSink.cs`, `IAppLogger.cs`, `NullAppLogger.cs`; NEW
`tests/SRN.CC.Tests/Core/Logging/LogContractTests.cs`.
- [ ] `enum LogLevel { Trace, Debug, Info, Warn, Error }`.
- [ ] `sealed record LogRecord(DateTimeOffset TimestampUtc, LogLevel Level, string Category,
      string Message, string? EventCode, IReadOnlyDictionary<string,string>? Data,
      string? ExceptionType, string? ExceptionMessage)`.
- [ ] `ILogSink : IDisposable` with `void Write(in LogRecord record)` and `void Flush()`.
- [ ] `IAppLogger.Log(LogLevel, string category, string message, Exception? = null,
      IReadOnlyDictionary<string,string>? = null)`.
- [ ] `NullAppLogger.Instance` as a no-op singleton — the default for every trailing optional
      `IAppLogger?` parameter added anywhere in this milestone.
- [ ] Declarations only. No sink implementation, no file I/O, no rotation in this slice.

**Exit Criteria**
- [ ] A `LogRecord` whose `Message` contains `\n` serializes to exactly one physical line.
- [ ] `NullAppLogger.Instance.Log(...)` never throws for any input, including a null exception and
      an empty category.
- [ ] `SRN.CC.Core` still has no UI, Formats, or Infrastructure dependency; `ArchitectureTests`
      needs no edit.

### S3 — Core startup contracts and `AppPaths` *(hard prerequisite)*
Implements A2's contract half. Owns NEW `src/SRN.CC.Core/Startup/StartupCheckSeverity.cs`,
`StartupCheckResult.cs`, `IStartupCheck.cs`, `StartupReport.cs`, `AppPaths.cs`; NEW
`tests/SRN.CC.Tests/Core/Startup/StartupReportTests.cs`, `AppPathsTests.cs`.
- [ ] `enum StartupCheckSeverity { Ok, Degraded, Blocking }`.
- [ ] `sealed record StartupCheckResult(string CheckId, StartupCheckSeverity Severity,
      string Summary, IReadOnlyList<string> Details, DiagnosticCode? Code = null)`.
- [ ] `IStartupCheck` with `string CheckId { get; }` and
      `Task<StartupCheckResult> RunAsync(CancellationToken cancellationToken = default)`.
- [ ] `sealed record StartupReport(IReadOnlyList<StartupCheckResult> Results)` exposing `HasBlocking`
      and `Worst`.
- [ ] `sealed record AppPaths(string Root)` with `CacheDatabasePath` (`Root/cache-v1.sqlite`),
      `SettingsPath` (`Root/settings.json`), `LogDirectory` (`Root/Logs`), and `static Default`
      resolving `%LOCALAPPDATA%\SRN.CC`.
- [ ] Declarations only. No preflight runner and no concrete check in this slice.

**Exit Criteria**
- [ ] `AppPaths.Default.CacheDatabasePath` equals the literal path `SqliteCacheService` computes
      today, and `AppPaths.Default.SettingsPath` equals `SettingsStore.DefaultSettingsPath` — asserted
      directly, so wave 1 cannot silently relocate user data.
- [ ] `StartupReport.Worst` returns the maximum severity across results and `Ok` for an empty list.
- [ ] `HasBlocking` is true if and only if at least one result is `Blocking`.

### S4 — Schema registry and migration seam *(hard prerequisite)*
Implements A4. Owns NEW `src/SRN.CC.Core/Schema/SchemaKind.cs`, `SchemaOpenMode.cs`,
`SchemaVersions.cs`, `IJsonSchemaMigration.cs`, `SchemaMigrationPipeline.cs`; NEW
`tests/SRN.CC.Tests/Core/Schema/SchemaVersionsTests.cs`, `SchemaMigrationPipelineTests.cs`.
- [ ] `enum SchemaKind { Project, Settings, Cache, Manifest }`,
      `enum SchemaOpenMode { Unsupported, Current, ReadOnlyNewer }`.
- [ ] `static class SchemaVersions` with `Project = 1`, `Settings = 1`, `Cache = 1`,
      `Manifest = "1.0"`, plus `Current(SchemaKind)` and `Classify(SchemaKind, int fileVersion)`.
- [ ] `IJsonSchemaMigration` with `Kind`, `FromVersion`, `ToVersion`, and
      `JsonObject Apply(JsonObject document)`.
- [ ] `SchemaMigrationPipeline(SchemaKind, IEnumerable<IJsonSchemaMigration>)` with
      `bool TryUpgrade(JsonObject document, int fromVersion, out JsonObject upgraded,
      out IReadOnlyList<string> applied, out string? error)`, composing registered migrations in
      ascending version order and returning `false` with an explicit error on a gap.
- [ ] Ship **zero** registered migrations. 1 → 1 is an identity no-op returning `applied = []`.
- [ ] Add a **test-only** `FakeV1ToV2Migration` (and a 2 → 3 sibling) inside the test file, never in
      production code.

**Exit Criteria**
- [ ] A 1 → 2 → 3 chain applies both migrations in order and reports both in `applied`.
- [ ] Requesting version 3 with only 1 → 2 registered returns `false` with an error naming the
      missing step, rather than silently skipping it.
- [ ] An identity upgrade does not mutate the input `JsonObject` — asserted by reference and by
      serialized text, because `ProjectStore` preserves unknown fields through
      `ProjectPreferences.RawRootNode`.
- [ ] `Classify` truth table verified for versions 0, 1, and 2 across all four `SchemaKind` values.

### S5 — Version stamping and manifest version source *(after S4 — consumes `SchemaVersions.Manifest`)*
Implements A7. Owns MODIFY `eng/Versions.props`, `Directory.Build.props`,
`src/SRN.CC.Infrastructure/Build/ProvenanceManifestGenerator.cs`; NEW
`tests/SRN.CC.Tests/Build/ProvenanceManifestGeneratorTests.cs`,
`tests/SRN.CC.Tests/Architecture/VersionStampingTests.cs`.
- [ ] Add `SRNCCVersionPrefix` (`1.0.0`), `SRNCCProductName`, and `SRNCCCompany` to
      `eng/Versions.props`.
- [ ] In `Directory.Build.props`, derive `Version`, `AssemblyVersion` (`$(SRNCCVersionPrefix).0`),
      `FileVersion`, `InformationalVersion`, `Product`, and `Company`, and set
      `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`.
- [ ] Replace the `"1.0"` / `"1.0.0"` literals at `ProvenanceManifestGenerator.cs:84-85` with
      `SchemaVersions.Manifest` and a cached read of
      `Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()`.
- [ ] This is the milestone's **only** edit to `Directory.Build.props`.

**Exit Criteria**
- [ ] Two manifest generations over identical input differ only in `generatedUtc`.
- [ ] `appVersion` equals the runtime informational version and contains no `+` character.
- [ ] `eng/Versions.props`'s `SRNCCVersionPrefix` equals the runtime `AssemblyInformationalVersion`
      for all five production assemblies.
- [ ] No `[A-Za-z]:\` sequence appears anywhere in generated manifest text.
- [ ] `dotnet build -c Release` is clean; no existing test changes behaviour.

### S6 — Operator documentation
Owns NEW `README.md`; NEW `docs/operator/INSTALL-AND-RUN.md`, `FILE-LOCATIONS.md`, `RECOVERY.md`,
`PROJECT-FILE-FORMAT.md`, `MANIFEST-FORMAT.md`, `GPU-REQUIREMENTS.md`, `TROUBLESHOOTING.md`.
- [ ] `README.md`: what the application is, supported platform (`win-x64` only), how to obtain and
      run the release, and a table of contents linking into `docs/operator/`.
- [ ] `INSTALL-AND-RUN.md`: extracting the release archive, first launch, what the preflight reports,
      and the fact that inputs are read-only and never modified.
- [ ] `FILE-LOCATIONS.md`: the `%LOCALAPPDATA%\SRN.CC` layout — `cache-v1.sqlite`, `settings.json`,
      `Logs\` — plus what is safe to delete and what is not.
- [ ] `RECOVERY.md`: what a quarantined cache or settings file means and how to recover; how an
      interrupted publish is rolled back at startup; what a `.publication-journal.json` file is.
- [ ] `PROJECT-FILE-FORMAT.md`: the `.srnccproj` schema-1 shape for humans, the newer-schema
      read-only rule, and the unknown-field preservation guarantee.
- [ ] `MANIFEST-FORMAT.md`: the `<basename>.srncc-manifest.json` fields and how to verify a published
      HAK against it by hand.
- [ ] `GPU-REQUIREMENTS.md`: rendering goes through ANGLE (OpenGL ES via EGL) on Windows; what
      happens when the capability probe falls short; the three-viewport limit.
- [ ] `TROUBLESHOOTING.md`: reading `Logs\srncc.log`, common degraded-preflight messages, and what
      warns versus what blocks a build.
- [ ] Do not write any provenance keyword subject to the `AuditVendoredSources.ps1` grep into a
      `.cs`, `.csproj`, or `.axaml`. These files are `.md` and are not scanned.

**Exit Criteria**
- [ ] Every path, file name, and environment variable named in the documentation exists in the code
      as written — verified by opening the referencing source, not from memory.
- [ ] `README.md` links resolve relative to the repository root.
- [ ] Nothing in `docs/operator/` describes a feature this milestone defers.

### S7 — Release policy data (no scripts)
Owns MODIFY `eng/publish-policy.json`; NEW `eng/release-policy.json`.
- [ ] Append `av_libglesv2.dll`, `libSkiaSharp.dll`, and `libHarfBuzzSharp.dll` to `requiredFiles`,
      and add matching `specialOrigins` entries for the three natives.
- [ ] Append `.log`, `.srnccproj`, `.srncc`, `.sqlite-wal`, `.sqlite-shm`, and `.zip` to
      `forbiddenExtensions`.
- [ ] Append `nwn_erf`, the third-party toolset name, `SRNCC_CORPUS_ROOT`, `sqlite3_64.dll`, and
      `verification-runs` to `forbiddenPathFragments`. **Never add `srnccproj`** — the application
      legitimately embeds that literal and adding it bricks the publish audit.
- [ ] Create `eng/release-policy.json` with `zipEntryTimestampUtc`, `zipExcludedFiles`
      (`["publish-inventory.json"]`), `nonSingleFileMarkers`, `minimumFileCount`, and
      `releaseNameTemplate`.
- [ ] Pure data only. This slice writes no PowerShell.

**Exit Criteria**
- [ ] `tools/VerifyBuild.ps1` still passes end to end with the extended policy — run it locally
      before declaring the slice done. Adding a fragment that the application legitimately embeds is
      the failure mode this check exists to catch.
- [ ] `eng/release-policy.json` parses and every key is consumed by a wave-1 script's documented
      contract in `docs/MILESTONE-7-2.md`.

### S8 — Corpus gate, `SRNCC_REQUIRE_CORPUS`, idle probe, recorded baseline
Owns NEW `tests/SRN.CC.CorpusTests/CorpusGate.cs`, `tests/SRN.CC.CorpusTests/IdleWorkingSetTests.cs`,
`docs/evidence/corpus-index-baseline.json`; MODIFY
`tests/SRN.CC.CorpusTests/CorpusIndexAcceptanceTests.cs`,
`tests/SRN.CC.CorpusTests/Render/ModelPreviewCorpusTests.cs`,
`tests/SRN.CC.CorpusTests/Render/ModelPreviewWorkingSetTests.cs`.
- [ ] `internal static class CorpusGate` with `IsRequired` (`SRNCC_REQUIRE_CORPUS`), `IsEnabled`
      (`SRNCC_RUN_CORPUS` **or** `IsRequired`), `Root` (`SRNCC_CORPUS_ROOT`), `RequireCorpusRoot()`,
      `RequireGpu()` (`SRNCC_RUN_GPU`), and `RequireIdleProbe()` (`SRNCC_RUN_IDLE_PROBE`).
- [ ] `RequireCorpusRoot()` precedence, in this order: `IsRequired` and the root is missing or does
      not exist → `Assert.Fail` naming the missing variable; `!IsEnabled` → `Assert.Ignore`;
      otherwise return the root. `IsRequired` implies `IsEnabled`, so `SRNCC_REQUIRE_CORPUS=1` alone
      is sufficient and cannot be silently defeated.
- [ ] Replace the triplicated `[SetUp]` gating at `CorpusIndexAcceptanceTests.cs:22-34`,
      `Render/ModelPreviewCorpusTests.cs:50-60`, and `Render/ModelPreviewWorkingSetTests.cs:39-51`
      with `CorpusGate` calls. Do not change any existing assertion or threshold.
- [ ] `IdleWorkingSetTests.cs`, `[Category("Performance")]` and additionally gated on
      `RequireIdleProbe()`: index the corpus once through a real `SqliteCacheService` and
      `AssetIndexService` → `GC.Collect(2, GCCollectionMode.Aggressive, blocking: true,
      compacting: true)` and `GC.WaitForPendingFinalizers()` → `await Task.Delay(30s)` with nothing in
      flight → sample **without** a second forced collect, because that is what "idle" means →
      record both `Process.PrivateMemorySize64` and `Process.WorkingSet64` → assert both below
      750 MiB.
- [ ] `docs/evidence/corpus-index-baseline.json`, shaped like
      `docs/evidence/tableview-performance-baseline.json`: `schemaVersion`, `timestampUtc`,
      `environment`, `corpus{hakCount:117, occurrenceCount:187943, type2078Count:212,
      duplicateArchiveCount:4}`, `coldIndex{runs, medianSeconds, thresholdSeconds:10.0}`, and
      `idle{privateBytes, workingSetBytes, thresholdBytes}`.

**Exit Criteria**
- [ ] With no corpus environment variables set, every corpus fixture still ignores exactly as it does
      today — the default `Category!=Corpus&Category!=Performance` run is unchanged.
- [ ] `SRNCC_REQUIRE_CORPUS=1` with no `SRNCC_CORPUS_ROOT` produces a **failure** naming
      `SRNCC_CORPUS_ROOT`, not an ignore.
- [ ] The idle probe stays skipped inside a plain corpus run unless `SRNCC_RUN_IDLE_PROBE=1` is set.
- [ ] `docs/evidence/corpus-index-baseline.json` is populated from a real recorded run, with the
      machine environment captured. If the corpus is unavailable to the slice, leave the numeric
      fields null and say so explicitly — do not invent values.

## Notes

- S2, S3, and S4 are **declarations only**. A wave-0 slice that ships an implementation makes the
  wave-1 owner's file list wrong. Sink behaviour belongs to S9, checks to S10, and store integration
  to S11/S12/S13.
- S5 is the milestone's only touch of `Directory.Build.props`. Any other slice needing a build
  property must route the request through S5 before the wave-0 barrier closes.
- S5 is the wave's **only** intra-wave dependency: it consumes `SchemaVersions.Manifest` from S4.
  Everything else here is genuinely order-free. S5's `eng/Versions.props` and `Directory.Build.props`
  work can start immediately; only the `ProvenanceManifestGenerator.cs` edit waits on S4.
- S7 is pure data precisely so that S15 and S18 can read the policy without contending for the file.
  If a wave-1 script needs a new policy key, it belongs in `eng/release-policy.json` — but S7 has
  already closed by then, so the key must be agreed here.
- S8 must not alter any existing corpus assertion or threshold. It is a gating refactor plus two new
  artifacts; changing a threshold in the same slice would make a later regression untraceable.
- S6's documentation is written against the code as it will exist **after** wave 3. Where a document
  describes behaviour that lands later (the settings dialog, the preflight report in the operation
  log), verify the wording against the phase document that owns it, and re-verify during S23's
  evidence pass.
