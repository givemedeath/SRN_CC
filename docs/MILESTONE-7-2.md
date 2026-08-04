# Milestone 7 - Phase 2: Implementations Behind the Contracts (Wave 1)

## Scope

Eight slices in parallel, each implementing behind a wave-0 contract or building a standalone tool.
Every slice here is file-disjoint by construction: the four store slices (S11, S12, S13, S14) each
own a different Infrastructure file, the two logging and startup slices create new directories, and
S15 writes two brand-new PowerShell scripts without touching any existing one.

This wave establishes the **trailing optional logger** convention that the rest of the milestone
depends on: every existing service gains `IAppLogger? logger = null` as its **last** constructor
parameter, defaulting to `NullAppLogger.Instance`. Zero call sites change, zero tests change. **Never
reorder an existing constructor parameter** — that is what turns a one-file slice into a
solution-wide merge conflict.

Nothing in this wave wires anything into the running application. Composition is S17's job in wave 2.
A wave-1 slice that edits `App.axaml.cs` has taken S17's file.

## Deliverables

- A rolling JSON-line log sink with rotation, a fan-out logger with per-sink failure isolation, and
  the file layout `PLAN.md:98` specifies.
- The preflight runner and all four startup checks, each independently testable and none capable of
  crashing startup.
- Settings aligned to the project-file newer-schema rule, with the interface change contained.
- A cache that never throws out of its constructor and finally emits
  `DiagnosticCode.CorruptedCacheQuarantined`.
- `ProjectStore` routed through the central schema registry with the migration pipeline in the load
  path.
- Publication-journal failure injection at all five states and full recovery coverage.
- `tools/PackRelease.ps1` and `tools/AuditRelease.ps1` as standalone, independently runnable scripts.
- `RealHakFixtureFactory` — the shared producer of genuine HAK bytes that S19's end-to-end scenario
  consumes — plus the build determinism and orchestrator coverage that has no test file today.

## Detailed tasks

### S9 — Rolling JSON-line sink and fan-out logger (needs S2, S3)
Implements A3's runtime half. Owns NEW `src/SRN.CC.Infrastructure/Logging/LogFileSet.cs`,
`JsonLineLogSink.cs`, `AppLogger.cs`; NEW `tests/SRN.CC.Tests/Logging/JsonLineLogSinkTests.cs`,
`LogRotationTests.cs`, `AppLoggerTests.cs`.
- [ ] `LogFileSet` owns rotation naming (`srncc.log` active,
      `srncc.<yyyyMMddTHHmmssfffZ>.log` rotated), ordering, and pruning to at most ten files.
- [ ] `JsonLineLogSink` writes one `LogRecord` per line with `Utf8JsonWriter`, opens with
      `FileShare.Read` so operators can tail, and holds one `lock` spanning the size pre-check, the
      append, and the `Flush()`.
- [ ] Rotate when `length + recordBytes` would exceed the cap; the cap and the file count are
      injectable so tests do not have to write 10 MiB.
- [ ] A record larger than the cap is still written whole rather than dropped or truncated.
- [ ] `AppLogger` fans out to N sinks, isolates per-sink failures — **a logger must never throw** —
      and exposes `FailedSinkCount`.
- [ ] Synchronous by design. No background writer, no queue, no dedicated thread (A3).

**Exit Criteria**
- [ ] Every emitted line is valid JSON containing all required `LogRecord` fields.
- [ ] Eight concurrent writers produce exactly N well-formed lines with no interleaving.
- [ ] With a 4 KiB test cap, the file count never exceeds ten, no file exceeds the cap plus one
      record, and the oldest file is deleted first.
- [ ] A sink whose directory is deleted mid-run does not throw, and `FailedSinkCount` increments.

### S10 — Preflight runner and four startup checks (needs S2, S3)
Implements A2's runtime half. Owns NEW `src/SRN.CC.Infrastructure/Startup/StartupPreflight.cs`,
`CacheStartupCheck.cs`, `SettingsStartupCheck.cs`, `PublicationJournalStartupCheck.cs`,
`ToolCapabilityStartupCheck.cs`; NEW `tests/SRN.CC.Tests/Startup/StartupPreflightTests.cs`,
`CacheStartupCheckTests.cs`, `SettingsStartupCheckTests.cs`,
`PublicationJournalStartupCheckTests.cs`, `ToolCapabilityStartupCheckTests.cs`.
- [ ] `StartupPreflight.RunAsync(checks, logger, ct)` runs each check inside its own `try`; a
      throwing check becomes `Degraded` carrying the exception and never propagates.
- [ ] Every result is logged through `IAppLogger` as it is produced.
- [ ] `CacheStartupCheck` reads `SqliteCacheService.IsAvailable` / `LastQuarantineReason` (S12's new
      members) and emits `DiagnosticCode.CorruptedCacheQuarantined`.
- [ ] `SettingsStartupCheck` distinguishes `Ok`, `Degraded(quarantined)`, and `Degraded(newer schema,
      read-only, file untouched)` using S11's `SettingsLoadResult`.
- [ ] `PublicationJournalStartupCheck` ports `App.axaml.cs:102-178` **verbatim** — same discovery
      rules, same locking — then adds the directories from `settings.RecentProjectPaths` and replaces
      the three bare `catch { }` blocks with logged, per-journal results naming each recovered path.
- [ ] `ToolCapabilityStartupCheck` reports RID and architecture, `AppContext.BaseDirectory`, presence
      of `e_sqlite3.dll` / `av_libglesv2.dll` / `libSkiaSharp.dll` / `libHarfBuzzSharp.dll` (base
      directory first, then `runtimes/win-x64/native`), the `NwnInstallLocator.Locate` result, and
      free space on `AppPaths.Root`.
- [ ] Guard the `NwnInstallLocator.Locate` call with `if (OperatingSystem.IsWindows())`. It is
      `[SupportedOSPlatform("windows")]`, and CA1416 under `TreatWarningsAsErrors` is a build break.
      Non-Windows returns `Degraded("install discovery unavailable on this platform")`.
- [ ] Report GPU capability as `render-gpu: Deferred — probed on first 3D slot`. Do **not** create a
      GL context at startup; that would violate Milestone 6's A4.

**Exit Criteria**
- [ ] A check that throws yields a `Degraded` result rather than a propagated exception, and the
      report still contains results for every other check.
- [ ] No shipped check ever returns `Blocking` — asserted directly.
- [ ] A pending journal in a directory reachable only through `RecentProjectPaths` is recovered.
      This is impossible today because settings are inert.
- [ ] A missing native is reported by name as `Degraded`, never as a failure.

### S11 — Settings newer-schema alignment, migration hook, logger (needs S2, S4)
Implements A5. Owns MODIFY `src/SRN.CC.Core/Services/ISettingsStore.cs`,
`src/SRN.CC.Core/Settings/ApplicationSettings.cs`,
`src/SRN.CC.Infrastructure/Persistence/SettingsStore.cs`,
`tests/SRN.CC.Tests/Persistence/SettingsStoreTests.cs`,
`tests/SRN.CC.Tests/App/MainWindowViewModelTextureSourceWiringTests.cs`; NEW
`src/SRN.CC.Core/Settings/SettingsLoadResult.cs`,
`tests/SRN.CC.Tests/Persistence/SettingsStoreSchemaTests.cs`.
- [ ] `SettingsLoadResult(ApplicationSettings Settings, SettingsLoadStatus Status,
      string? QuarantinedPath)` and a `SettingsLoadStatus` enum.
- [ ] `ISettingsStore.LoadAsync` returns `Task<SettingsLoadResult>`.
- [ ] Route the version check through `SchemaVersions.Classify(SchemaKind.Settings, …)`: `Current` →
      read and write; `ReadOnlyNewer` → return defaults with `IsReadOnly = true` and **never touch
      the file**; `Unsupported` or unparseable → quarantine to `.corrupt.<timestamp>` and rebuild, as
      today.
- [ ] `SaveAsync` throws `InvalidOperationException` when the loaded state was `ReadOnlyNewer`.
- [ ] Consume `AppPaths` instead of re-deriving `%LOCALAPPDATA%\SRN.CC\settings.json`.
- [ ] Run `SchemaMigrationPipeline` (identity for v1) in the load path.
- [ ] Add a trailing `IAppLogger? logger = null` and log at `:60`, `:107`, `:111`, and `:134`.
- [ ] Update the four affected call sites only — including the `NotSupportedSettingsStore` fake at
      `MainWindowViewModelTextureSourceWiringTests.cs:192`. The blast radius is verified as exactly
      these files; if a fifth appears, stop and report it rather than expanding the slice.

**Exit Criteria**
- [ ] A `schemaVersion: 2` settings file yields defaults with `IsReadOnly` set, and the file on disk
      is **byte-identical** afterwards.
- [ ] `SaveAsync` after a read-only load throws.
- [ ] A malformed settings file is still quarantined to `.corrupt.<timestamp>` and rebuilt, with the
      quarantined path reported rather than swallowed.
- [ ] All pre-existing `SettingsStoreTests` assertions still hold under the new return type.

### S12 — Cache never throws from its constructor; emit the quarantine diagnostic (needs S2, S4)
Owns MODIFY `src/SRN.CC.Infrastructure/Cache/SqliteCacheService.cs`,
`tests/SRN.CC.Tests/Cache/SqliteCacheTests.cs`; NEW
`tests/SRN.CC.Tests/Cache/SqliteCacheQuarantineTests.cs`.
- [ ] Add `IsAvailable` and `LastQuarantineReason` to `ISqliteCacheService` and the implementation.
- [ ] Wrap `EnsureDatabaseInitialized`'s quarantine-and-recreate path so a failure sets
      `IsAvailable = false` instead of letting `CreateConnection` throw out of the constructor.
      Today a failed quarantine rename is an uncaught throw out of
      `App.OnFrameworkInitializationCompleted` — a hard startup crash with no message.
- [ ] Every public method short-circuits when `IsAvailable` is false, returning the same "miss"
      result it would return for a cold cache.
- [ ] Route the hard-coded `1` through `SchemaVersions.Classify(SchemaKind.Cache, …)`.
- [ ] Emit `DiagnosticCode.CorruptedCacheQuarantined` on quarantine — it exists at
      `src/SRN.CC.Core/Diagnostics/DiagnosticCode.cs:16` and is emitted nowhere today.
- [ ] Add a trailing `IAppLogger? logger = null` and log at `:290`.
- [ ] Consume `AppPaths.CacheDatabasePath` rather than re-deriving it.

**Exit Criteria**
- [ ] A truncated or garbage `cache-v1.sqlite` is quarantined to `.corrupt.<timestamp>`, a fresh
      database is created, and the diagnostic is emitted.
- [ ] A quarantine rename that **fails** (read-only directory) yields `IsAvailable == false` and
      **no exception** — the test that reproduces today's startup crash.
- [ ] Every existing `SqliteCacheTests` assertion still holds, including LRU eviction and the 2 GiB
      default budget.

### S13 — `ProjectStore` schema seam and logger (needs S2, S4)
Owns MODIFY `src/SRN.CC.Infrastructure/Persistence/ProjectStore.cs`; NEW
`tests/SRN.CC.Tests/Persistence/ProjectStoreSchemaTests.cs`.
**Must not touch** `tests/SRN.CC.Tests/Persistence/ProjectStoreTests.cs`.
- [ ] Replace the private const at `ProjectStore.cs:19` with `SchemaVersions.Project`, and route the
      version branches at `:61` and `:67` through `Classify`.
- [ ] Run `SchemaMigrationPipeline` on the root object **before** validation, preserving
      `ProjectPreferences.RawRootNode` identity for v1.
- [ ] Add a trailing `IAppLogger? logger = null` and log the seven temp-file `catch` sites at
      `:640`, `:644`, `:672`, `:676`, `:703`, `:707`, and `:752`.
- [ ] Do not change corrupt-project behaviour: `LoadAsync` still throws `InvalidOperationException`
      naming the path, and the file is still left untouched. `PLAN.md:96` scopes quarantine to
      settings and cache.

**Exit Criteria**
- [ ] The pipeline runs before validation and preserves unknown fields byte-for-byte through a
      load-save round trip.
- [ ] `schemaVersion: 2` still opens read-only; `schemaVersion: 0` still throws and leaves the file
      untouched.
- [ ] All 22 pre-existing `ProjectStoreTests` assertions pass **unmodified** — that file is
      deliberately not in this slice's `Owns` list, as the migration-cost proof.

### S14 — Publication journal hardening and failure injection (needs S2)
Owns MODIFY `src/SRN.CC.Infrastructure/Build/ArtifactPublisher.cs`; NEW
`tests/SRN.CC.Tests/Build/ArtifactPublisherJournalStateTests.cs`,
`ArtifactPublisherRecoveryTests.cs`.
**Must not touch** `tests/SRN.CC.Tests/Build/ArtifactPublisherTests.cs`.
- [ ] Add a trailing `IAppLogger? logger = null`.
- [ ] Replace `RollbackJournalAsync`'s bare `catch { }` at `:309` and `TryRecoverJournalAsync`'s at
      `:239` with logged, per-operation handling so a partial rollback becomes visible. Today a
      failed rollback is indistinguishable from success.
- [ ] Cover injected failure at **all five** `PublicationState` values — `Prepared`, `BackedUp`,
      `HakReplaced`, `ManifestReplaced`, `Committed`. Only two are covered today.
- [ ] Cover `RecoverPendingJournalAsync`, which has **no test at all** today: each state, the legacy
      fixed-name `publication-journal.json`, a corrupt journal, and recovery attempted while the
      publication lock is held.

**Exit Criteria**
- [ ] Pair rollback restores the previous valid destination byte-for-byte from every one of the five
      states.
- [ ] A cleanup failure after `Committed` still reports overall success.
- [ ] A second recovery over an already-recovered directory is idempotent.
- [ ] A held lock causes recovery to report contention rather than corrupt the destination.
- [ ] All five pre-existing `ArtifactPublisherTests` assertions pass unmodified.

### S15 — Deterministic packer and release audit (needs S5, S7)
Implements A6's script half. Owns NEW `tools/PackRelease.ps1`, `tools/AuditRelease.ps1`.
**Must not touch** `tools/VerifyBuild.ps1` or `tools/AuditPublish.ps1` — S18 owns the driver.
- [ ] `PackRelease.ps1 -PublishDir -OutputDir [-Version]`: read `SRNCCVersionPrefix` from
      `eng/Versions.props` by XML (never a duplicated literal); enumerate and ordinal-sort by relative
      path; exclude per `eng/release-policy.json`; create entries with `/` separators, no directory
      entries, `CompressionLevel.Optimal`, and every `LastWriteTime` set to `1980-01-01T00:00:00Z`;
      emit `release-manifest.json` with version, RID, archive SHA-256, file count, and per-file
      SHA-256.
- [ ] Drive `System.IO.Compression.ZipArchive` directly. `Compress-Archive` embeds live timestamps
      and guarantees no entry order, and a .NET packer tool would add a project and break
      `AuditDependencies.ps1`'s hard-coded `Count -ne 7`.
- [ ] Exclude `publish-inventory.json` from the archive — it carries `timestampUtc`
      (`AuditPublish.ps1:187`) and is audit evidence rather than an application file.
- [ ] `AuditRelease.ps1 -Root <dir> [-ZipPath]`: assert non-single-file (`SRN.CC.App.exe` **and** a
      sibling `SRN.CC.App.dll` **and** a loose `Avalonia.Base.dll` **and** more than 100 files, plus
      no `PublishSingleFile` property in any `.csproj` or `.props`); check required files; check the
      extended forbidden extensions and fragments from `eng/publish-policy.json` across
      `.dll`/`.exe`/`.json` in **both ASCII and UTF-16**, matching `AuditPublish.ps1:151-160`; check
      the license and notices tree; and, when `-ZipPath` is supplied, pack twice and assert identical
      SHA-256.
- [ ] Both scripts follow the existing convention: `$ErrorActionPreference = "Stop"`, parameterized,
      runnable standalone.

**Exit Criteria**
- [ ] Packing the same publish tree twice produces byte-identical archives with identical SHA-256.
- [ ] `AuditRelease.ps1` passes against a publish tree produced by a local `VerifyBuild.ps1` run, and
      **fails loudly** against a tree with an injected `.log` file, an injected absolute path in a
      first-party assembly, and an injected forbidden fragment — verify all three negatives.
- [ ] Neither script writes to `eng/publish-policy.json` or `eng/release-policy.json`.

### S16 — Build determinism and the shared real-HAK fixture factory (needs S2)
Owns NEW `tests/SRN.CC.Tests/Build/RealHakFixtureFactory.cs`, `BuildOrchestratorTests.cs`,
`HakDeterminismTests.cs`; MODIFY `src/SRN.CC.Infrastructure/Build/BuildOrchestrator.cs` (the trailing
logger parameter and the two `catch { }` blocks at `:185-186` only — no behaviour change).
- [ ] `public static class RealHakFixtureFactory` with `FixtureEntry(byte[] ResrefBytes,
      ushort ResourceType, byte[] Payload)`, `DefaultCorpus()`, `WriteHak(path, entries)`, and
      `WriteFolderSource(directory, entries)`, all writing **genuine** HAK bytes through the
      production `HakWriter`.
- [ ] `DefaultCorpus()` covers `mdl`, `tga`, `dds`, `mtr`, `txi`, `2da`, and a type-2078 `lod`
      (unknown but packageable), plus a CP1252-accented resref written as raw bytes, a 16-byte
      boundary resref, a 17-byte name that must truncate, and a zero-byte payload. Take every type id
      from `src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/Common/ResourceTypes.cs` — never invent one.
- [ ] Make the factory `public static` and deterministic (seeded, no clock, no randomness) so S19 can
      consume it read-only in wave 3.
- [ ] `BuildOrchestratorTests`: each of the five preflights fails with its own distinct message —
      output overlapping a source, output inside a source folder, nothing selected, an unresolved
      selection, and the size limit — and temp files are deleted on every path including failure.
      `BuildOrchestrator` has **no test file** today.
- [ ] `HakDeterminismTests`: the same `BuildPlan` packed twice is byte-identical; CP1252-accented
      resrefs round-trip as raw bytes; paths containing spaces work; unknown resource types are
      packaged opaquely; and a source mutated mid-build fails verification.

**Exit Criteria**
- [ ] `RealHakFixtureFactory.WriteHak` output is readable by a fresh `HakReader` with every entry's
      resref bytes preserved exactly.
- [ ] Two `DefaultCorpus()` invocations produce byte-identical files.
- [ ] All five `BuildOrchestrator` preflights are covered with distinct assertions.
- [ ] `BuildOrchestrator`'s behaviour is otherwise unchanged — no existing build test alters.

## Notes

- **The trailing-optional-logger rule is what keeps this wave parallel.** Adding
  `IAppLogger? logger = null` as the last parameter changes zero call sites and zero tests. Inserting
  it anywhere else, or making it required, converts a single-file slice into a solution-wide edit and
  collides with every sibling.
- S11, S12, S13, and S14 each own exactly one Infrastructure file plus new test files. Three of them
  are explicitly forbidden from touching the pre-existing test file for their own subject — those
  files staying green **unmodified** is the evidence that the change was additive.
- S10 depends on member names that S12 (`IsAvailable`, `LastQuarantineReason`) and S11
  (`SettingsLoadResult`) introduce in the same wave. Agree those signatures from this document before
  the wave starts; do not discover them by compiling against a sibling mid-wave.
- S15's scripts must be runnable and verified standalone in this wave. S18 wires them into
  `VerifyBuild.ps1` in wave 2, and a packer that has never been run against a real publish tree makes
  that a two-slice debugging session across a wave barrier.
- S16's factory is a wave-1 deliverable specifically so S19 has it ready at the wave-3 barrier. Treat
  its public surface as a contract: S19 consumes it read-only and may not modify it.
- Nothing in this wave touches `src/SRN.CC.App/`. If a slice believes it needs to, the work belongs
  to S17 in wave 2.
