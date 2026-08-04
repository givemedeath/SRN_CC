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
- [x] `LogFileSet` owns rotation naming (`srncc.log` active,
      `srncc.<yyyyMMddTHHmmssfffZ>.log` rotated), ordering, and pruning to at most ten files.
- [x] `JsonLineLogSink` writes one `LogRecord` per line with `Utf8JsonWriter`, opens with
      `FileShare.Read` so operators can tail, and holds one `lock` spanning the size pre-check, the
      append, and the `Flush()`.
- [x] Rotate when `length + recordBytes` would exceed the cap; the cap and the file count are
      injectable so tests do not have to write 10 MiB.
- [x] A record larger than the cap is still written whole rather than dropped or truncated.
- [x] `AppLogger` fans out to N sinks, isolates per-sink failures — **a logger must never throw** —
      and exposes `FailedSinkCount`.
- [x] Synchronous by design. No background writer, no queue, no dedicated thread (A3).

**Exit Criteria**
- [x] Every emitted line is valid JSON containing all required `LogRecord` fields.
- [x] Eight concurrent writers produce exactly N well-formed lines with no interleaving.
- [x] With a 4 KiB test cap, the file count never exceeds ten, no file exceeds the cap plus one
      record, and the oldest file is deleted first.
- [x] A sink whose directory is deleted mid-run does not throw, and `FailedSinkCount` increments.

### S10 — Preflight runner and four startup checks (needs S2, S3)
Implements A2's runtime half. Owns NEW `src/SRN.CC.Infrastructure/Startup/StartupPreflight.cs`,
`CacheStartupCheck.cs`, `SettingsStartupCheck.cs`, `PublicationJournalStartupCheck.cs`,
`ToolCapabilityStartupCheck.cs`; NEW `tests/SRN.CC.Tests/Startup/StartupPreflightTests.cs`,
`CacheStartupCheckTests.cs`, `SettingsStartupCheckTests.cs`,
`PublicationJournalStartupCheckTests.cs`, `ToolCapabilityStartupCheckTests.cs`.
- [x] `StartupPreflight.RunAsync(checks, logger, ct)` runs each check inside its own `try`; a
      throwing check becomes `Degraded` carrying the exception and never propagates.
- [x] Every result is logged through `IAppLogger` as it is produced.
- [x] `CacheStartupCheck` reads `SqliteCacheService.IsAvailable` / `LastQuarantineReason` (S12's new
      members) and emits `DiagnosticCode.CorruptedCacheQuarantined`.
- [x] `SettingsStartupCheck` distinguishes `Ok`, `Degraded(quarantined)`, and `Degraded(newer schema,
      read-only, file untouched)` using S11's `SettingsLoadResult`.
- [x] `PublicationJournalStartupCheck` ports `App.axaml.cs:102-178` **verbatim** — same discovery
      rules, same locking — then adds the directories from `settings.RecentProjectPaths` and replaces
      the three bare `catch { }` blocks with logged, per-journal results naming each recovered path.
- [x] `ToolCapabilityStartupCheck` reports RID and architecture, `AppContext.BaseDirectory`, presence
      of `e_sqlite3.dll` / `av_libglesv2.dll` / `libSkiaSharp.dll` / `libHarfBuzzSharp.dll` (base
      directory first, then `runtimes/win-x64/native`), the `NwnInstallLocator.Locate` result, and
      free space on `AppPaths.Root`.
- [x] Guard the `NwnInstallLocator.Locate` call with `if (OperatingSystem.IsWindows())`. It is
      `[SupportedOSPlatform("windows")]`, and CA1416 under `TreatWarningsAsErrors` is a build break.
      Non-Windows returns `Degraded("install discovery unavailable on this platform")`.
- [x] Report GPU capability as `render-gpu: Deferred — probed on first 3D slot`. Do **not** create a
      GL context at startup; that would violate Milestone 6's A4.

**Exit Criteria**
- [x] A check that throws yields a `Degraded` result rather than a propagated exception, and the
      report still contains results for every other check.
- [x] No shipped check ever returns `Blocking` — asserted directly.
- [x] A pending journal in a directory reachable only through `RecentProjectPaths` is recovered.
      This is impossible today because settings are inert.
- [x] A missing native is reported by name as `Degraded`, never as a failure.

### S11 — Settings newer-schema alignment, migration hook, logger (needs S2, S4)
Implements A5. Owns MODIFY `src/SRN.CC.Core/Services/ISettingsStore.cs`,
`src/SRN.CC.Core/Settings/ApplicationSettings.cs`,
`src/SRN.CC.Infrastructure/Persistence/SettingsStore.cs`,
`tests/SRN.CC.Tests/Persistence/SettingsStoreTests.cs`,
`tests/SRN.CC.Tests/App/MainWindowViewModelTextureSourceWiringTests.cs`; NEW
`src/SRN.CC.Core/Settings/SettingsLoadResult.cs`,
`tests/SRN.CC.Tests/Persistence/SettingsStoreSchemaTests.cs`.
- [x] `SettingsLoadResult(ApplicationSettings Settings, SettingsLoadStatus Status,
      string? QuarantinedPath)` and a `SettingsLoadStatus` enum.
- [x] `ISettingsStore.LoadAsync` returns `Task<SettingsLoadResult>`.
- [x] Route the version check through `SchemaVersions.Classify(SchemaKind.Settings, …)`: `Current` →
      read and write; `ReadOnlyNewer` → return defaults with `IsReadOnly = true` and **never touch
      the file**; `Unsupported` or unparseable → quarantine to `.corrupt.<timestamp>` and rebuild, as
      today.
- [x] `SaveAsync` throws `InvalidOperationException` when the loaded state was `ReadOnlyNewer`.
- [x] Consume `AppPaths` instead of re-deriving `%LOCALAPPDATA%\SRN.CC\settings.json`.
- [x] Run `SchemaMigrationPipeline` (identity for v1) in the load path.
- [x] Add a trailing `IAppLogger? logger = null` and log at `:60`, `:107`, `:111`, and `:134`.
- [x] Update the four affected call sites only — including the `NotSupportedSettingsStore` fake at
      `MainWindowViewModelTextureSourceWiringTests.cs:192`. The blast radius is verified as exactly
      these files; if a fifth appears, stop and report it rather than expanding the slice.

**Exit Criteria**
- [x] A `schemaVersion: 2` settings file yields defaults with `IsReadOnly` set, and the file on disk
      is **byte-identical** afterwards.
- [x] `SaveAsync` after a read-only load throws.
- [x] A malformed settings file is still quarantined to `.corrupt.<timestamp>` and rebuilt, with the
      quarantined path reported rather than swallowed.
- [x] All pre-existing `SettingsStoreTests` assertions still hold under the new return type.

### S12 — Cache never throws from its constructor; emit the quarantine diagnostic (needs S2, S4)
Owns MODIFY `src/SRN.CC.Infrastructure/Cache/SqliteCacheService.cs`,
`tests/SRN.CC.Tests/Cache/SqliteCacheTests.cs`; NEW
`tests/SRN.CC.Tests/Cache/SqliteCacheQuarantineTests.cs`.
- [x] Add `IsAvailable` and `LastQuarantineReason` to `ISqliteCacheService` and the implementation.
- [x] Wrap `EnsureDatabaseInitialized`'s quarantine-and-recreate path so a failure sets
      `IsAvailable = false` instead of letting `CreateConnection` throw out of the constructor.
      Today a failed quarantine rename is an uncaught throw out of
      `App.OnFrameworkInitializationCompleted` — a hard startup crash with no message.
- [x] Every public method short-circuits when `IsAvailable` is false, returning the same "miss"
      result it would return for a cold cache.
- [x] Route the hard-coded `1` through `SchemaVersions.Classify(SchemaKind.Cache, …)`.
- [x] Emit `DiagnosticCode.CorruptedCacheQuarantined` on quarantine — it exists at
      `src/SRN.CC.Core/Diagnostics/DiagnosticCode.cs:16` and is emitted nowhere today.
- [x] Add a trailing `IAppLogger? logger = null` and log at `:290`.
- [x] Consume `AppPaths.CacheDatabasePath` rather than re-deriving it.

**Exit Criteria**
- [x] A truncated or garbage `cache-v1.sqlite` is quarantined to `.corrupt.<timestamp>`, a fresh
      database is created, and the diagnostic is emitted.
- [x] A quarantine rename that **fails** (read-only directory) yields `IsAvailable == false` and
      **no exception** — the test that reproduces today's startup crash.
- [x] Every existing `SqliteCacheTests` assertion still holds, including LRU eviction and the 2 GiB
      default budget.

### S13 — `ProjectStore` schema seam and logger (needs S2, S4)
Owns MODIFY `src/SRN.CC.Infrastructure/Persistence/ProjectStore.cs`; NEW
`tests/SRN.CC.Tests/Persistence/ProjectStoreSchemaTests.cs`.
**Must not touch** `tests/SRN.CC.Tests/Persistence/ProjectStoreTests.cs`.
- [x] Replace the private const at `ProjectStore.cs:19` with `SchemaVersions.Project`, and route the
      version branches at `:61` and `:67` through `Classify`.
- [x] Run `SchemaMigrationPipeline` on the root object **before** validation, preserving
      `ProjectPreferences.RawRootNode` identity for v1.
- [x] Add a trailing `IAppLogger? logger = null` and log the seven temp-file `catch` sites at
      `:640`, `:644`, `:672`, `:676`, `:703`, `:707`, and `:752`.
- [x] Do not change corrupt-project behaviour: `LoadAsync` still throws `InvalidOperationException`
      naming the path, and the file is still left untouched. `PLAN.md:96` scopes quarantine to
      settings and cache.

**Exit Criteria**
- [x] The pipeline runs before validation and preserves unknown fields byte-for-byte through a
      load-save round trip.
- [x] `schemaVersion: 2` still opens read-only; `schemaVersion: 0` still throws and leaves the file
      untouched.
- [x] All 22 pre-existing `ProjectStoreTests` assertions pass **unmodified** — that file is
      deliberately not in this slice's `Owns` list, as the migration-cost proof.

### S14 — Publication journal hardening and failure injection (needs S2)
Owns MODIFY `src/SRN.CC.Infrastructure/Build/ArtifactPublisher.cs`; NEW
`tests/SRN.CC.Tests/Build/ArtifactPublisherJournalStateTests.cs`,
`ArtifactPublisherRecoveryTests.cs`.
**Must not touch** `tests/SRN.CC.Tests/Build/ArtifactPublisherTests.cs`.
- [x] Add a trailing `IAppLogger? logger = null`.
- [x] Replace `RollbackJournalAsync`'s bare `catch { }` at `:309` and `TryRecoverJournalAsync`'s at
      `:239` with logged, per-operation handling so a partial rollback becomes visible. Today a
      failed rollback is indistinguishable from success.
- [x] Cover injected failure at **all five** `PublicationState` values — `Prepared`, `BackedUp`,
      `HakReplaced`, `ManifestReplaced`, `Committed`. Only two are covered today.
- [x] Cover `RecoverPendingJournalAsync`, which has **no test at all** today: each state, the legacy
      fixed-name `publication-journal.json`, a corrupt journal, and recovery attempted while the
      publication lock is held.

**Exit Criteria**
- [x] Pair rollback restores the previous valid destination byte-for-byte from every one of the five
      states.
- [x] A cleanup failure after `Committed` still reports overall success.
- [x] A second recovery over an already-recovered directory is idempotent.
- [x] A held lock causes recovery to report contention rather than corrupt the destination.
- [x] All five pre-existing `ArtifactPublisherTests` assertions pass unmodified.

### S15 — Deterministic packer and release audit (needs S5, S7)
Implements A6's script half. Owns NEW `tools/PackRelease.ps1`, `tools/AuditRelease.ps1`.
**Must not touch** `tools/VerifyBuild.ps1` or `tools/AuditPublish.ps1` — S18 owns the driver.
- [x] `PackRelease.ps1 -PublishDir -OutputDir [-Version]`: read `SRNCCVersionPrefix` from
      `eng/Versions.props` by XML (never a duplicated literal); enumerate and ordinal-sort by relative
      path; exclude per `eng/release-policy.json`; create entries with `/` separators, no directory
      entries, `CompressionLevel.Optimal`, and every `LastWriteTime` set to `1980-01-01T00:00:00Z`;
      emit `release-manifest.json` with version, RID, archive SHA-256, file count, and per-file
      SHA-256.
- [x] Drive `System.IO.Compression.ZipArchive` directly. `Compress-Archive` embeds live timestamps
      and guarantees no entry order, and a .NET packer tool would add a project and break
      `AuditDependencies.ps1`'s hard-coded `Count -ne 7`.
- [x] Exclude `publish-inventory.json` from the archive — it carries `timestampUtc`
      (`AuditPublish.ps1:187`) and is audit evidence rather than an application file.
- [x] `AuditRelease.ps1 -Root <dir> [-ZipPath]`: assert non-single-file (`SRN.CC.App.exe` **and** a
      sibling `SRN.CC.App.dll` **and** a loose `Avalonia.Base.dll` **and** more than 100 files, plus
      no `PublishSingleFile` property in any `.csproj` or `.props`); check required files; check the
      extended forbidden extensions and fragments from `eng/publish-policy.json` across
      `.dll`/`.exe`/`.json` in **both ASCII and UTF-16**, matching `AuditPublish.ps1:151-160`; check
      the license and notices tree; and, when `-ZipPath` is supplied, pack twice and assert identical
      SHA-256.
- [x] Both scripts follow the existing convention: `$ErrorActionPreference = "Stop"`, parameterized,
      runnable standalone.
- [x] **Do not add `specialOrigins` entries for native libraries.** S7 briefly added entries for
      `av_libglesv2.dll`, `libSkiaSharp.dll`, and `libHarfBuzzSharp.dll`; code review removed them.
      All three are already origin-resolved from the deps manifest's `native` asset group
      (`AuditPublish.ps1:102-107`), so an entry is strictly redundant, and `Add-Origin`
      (lines 24-31) raises an *ambiguous origins* violation whenever a `specialOrigins` value
      differs from the deps-derived `nuget:<Package>/<Version>` string. A frozen literal therefore
      turns the next package version bump into an audit failure that names a policy file instead of
      the bump. The pre-existing `e_sqlite3.dll` native carries no entry for exactly this reason.
      `AuditRelease.ps1` must resolve origins from the deps manifest and treat `specialOrigins` as
      an override of last resort.

**Exit Criteria**
- [x] Packing the same publish tree twice produces byte-identical archives with identical SHA-256.
- [x] `AuditRelease.ps1` passes against a publish tree produced by a local `VerifyBuild.ps1` run, and
      **fails loudly** against a tree with an injected `.log` file, an injected absolute path in a
      first-party assembly, and an injected forbidden fragment — verify all three negatives.
- [x] Neither script writes to `eng/publish-policy.json` or `eng/release-policy.json`.

### S16 — Build determinism and the shared real-HAK fixture factory (needs S2)
Owns NEW `tests/SRN.CC.Tests/Build/RealHakFixtureFactory.cs`, `BuildOrchestratorTests.cs`,
`HakDeterminismTests.cs`; MODIFY `src/SRN.CC.Infrastructure/Build/BuildOrchestrator.cs` (the trailing
logger parameter and the two `catch { }` blocks at `:185-186` only — no behaviour change).
- [x] `public static class RealHakFixtureFactory` with `FixtureEntry(byte[] ResrefBytes,
      ushort ResourceType, byte[] Payload)`, `DefaultCorpus()`, `WriteHak(path, entries)`, and
      `WriteFolderSource(directory, entries)`, all writing **genuine** HAK bytes through the
      production `HakWriter`.
- [x] `DefaultCorpus()` covers `mdl`, `tga`, `dds`, `mtr`, `txi`, `2da`, and a type-2078 `lod`
      (~~unknown but packageable~~ — **corrected**: 2078 *is* known and maps to `lod`; the
      genuinely-unnameable case is covered separately by discovering the first id in 2000..2199 for
      which `ResourceTypeRegistry.TryGetExtension` returns false. See Wave 1 outcomes below), plus a
      CP1252-accented resref written as raw bytes, a 16-byte
      boundary resref, a 17-byte name that must truncate, and a zero-byte payload. Take every type id
      from `src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/Common/ResourceTypes.cs` — never invent one.
- [x] Make the factory `public static` and deterministic (seeded, no clock, no randomness) so S19 can
      consume it read-only in wave 3.
- [x] `BuildOrchestratorTests`: each of the five preflights fails with its own distinct message —
      output overlapping a source, output inside a source folder, nothing selected, an unresolved
      selection, and the size limit — and temp files are deleted on every path including failure.
      `BuildOrchestrator` has **no test file** today.
- [x] `HakDeterminismTests`: the same `BuildPlan` packed twice is byte-identical; CP1252-accented
      resrefs round-trip as raw bytes; paths containing spaces work; unknown resource types are
      packaged opaquely; and a source mutated mid-build fails verification.

**Exit Criteria**
- [x] `RealHakFixtureFactory.WriteHak` output is readable by a fresh `HakReader` with every entry's
      resref bytes preserved exactly.
- [x] Two `DefaultCorpus()` invocations produce byte-identical files.
- [x] All five `BuildOrchestrator` preflights are covered with distinct assertions.
- [x] `BuildOrchestrator`'s behaviour is otherwise unchanged — no existing build test alters.

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

## Wave 1 outcomes — corrections to this plan and to the parent documents

Recorded as they were found. Each entry is either a factual error in the plan or a design decision
that diverges from `docs/MILESTONE-7.md` and therefore needs to reach ADR 0005 (S23).

### Plan facts that were wrong

- **`2078 lod` is not "unknown but packageable."** S16's brief and the S19 corpus description both
  describe type 2078 as unknown to the registry. It is present in the vendored `ResourceTypes` table
  and `ResourceTypeRegistry` names it `LOD`. S16 kept 2078 in the corpus as specified and additionally
  covered the genuinely-unnameable case by **discovering** the first id in 2000..2199 for which
  `ResourceTypeRegistry.TryGetExtension` returns false — 2004 today, a real gap in the vendored table.
  Discovery rather than a hardcoded 2004, so the test does not rot when the table is filled in.
  **S19's brief must be corrected before wave 3.**
- **`ProjectStoreTests.cs` has 21 `[Test]` methods, not 22.** The 22nd `[Test`-prefixed attribute is
  the class's own `[TestFixture]`. All 21 pass unmodified.
- **`ArtifactPublisherTests.cs` has 4 `[Test]` methods, not 5.** The first carries 6 assertions. All 4
  pass unmodified.
- **The wave-1 test baseline was 582, not 546**, once wave-0 slices had landed. The verified figure at
  the wave-1 barrier is 742.

### Divergences from `docs/MILESTONE-7.md` that ADR 0005 must record

- **A3 amendment — `JsonLineLogSink` does not hold the log file open between records.** Each `Write`
  opens append and closes inside the same `lock` that spans the size pre-check. A held handle would
  make "the log directory is deleted mid-run" unsurvivable by construction rather than testable,
  because Windows refuses to rename or delete a directory containing an open handle; and nothing is
  ever sitting in a buffer when the process dies, which is the same argument A3 uses to reject a
  background writer. Cost is one open/close per record at human logging rates. `Flush()` is therefore
  a documented no-op.
- **A5 amendment — `SettingsStore` guards saves two ways, not one.** A5 specifies only that `SaveAsync`
  throws when the loaded state was `ReadOnlyNewer`. The `IsReadOnly` flag lives on the settings object,
  so a caller that constructs fresh defaults can still overwrite a newer file. S11 added a path-keyed
  set of paths whose last load was `ReadOnlyNewer`, cleared on any subsequent non-read-only load of
  that path so a deleted or downgraded file is not permanently locked. This is per-store mutable state
  keyed by path and needs to be justified in the ADR rather than left as an undocumented behaviour.
- **A2 refinement — a *successful* cache quarantine still reports `Degraded`.** S12's `IsAvailable`
  goes false only when the quarantine/rebuild itself fails, while `LastQuarantineReason` is set on any
  quarantine. `CacheStartupCheck` therefore reports `Degraded` in both cases with distinct summaries:
  the user lost their cache even when the app recovered cleanly, and must be told.
- **A2 refinement — a recovered publication journal reports `Degraded`, not `Ok`.** An interrupted
  publish that had to be rolled back is a user-visible event.

### Handover notes S18 needs in wave 2

- **The publish tree has 253 files; 252 ship.** `publish-inventory.json` is the sole exclusion, so
  `AuditRelease.ps1` counts 252 and the archive holds 252 entries. `minimumFileCount: 100` has ample
  headroom.
- **`AuditPublish.ps1` *writes* `publish-inventory.json` into the publish dir.** Both new scripts
  exclude it by policy, so publish-audit-before-pack and pack-before-publish-audit yield the same
  archive hash — but neither script deletes it, and it must stay excluded or the ZIP becomes
  unreproducible forever.
- **`AuditRelease.ps1 -ZipPath` re-packs twice**, so a driver step that packs once and then audits
  with `-ZipPath` costs three packs (~30 s each against a 49 MB archive). Wire `-ZipPath` into the
  release job rather than the default local path if that is too slow.
- `AuditRelease.ps1` derives the repo root from `$PSScriptRoot`'s parent, exactly as
  `AuditPublish.ps1` does, so it must stay in `tools/`. Its `PublishSingleFile` scan walks the whole
  repo from there and is independent of `-Root`.
- Leave `PackRelease.ps1 -Version` unset in the driver; it exists for release candidates, and setting
  it defeats single-sourcing the version from `eng/Versions.props`.

### Latent bugs found by the slices (all fixed in wave 1)

- **`App.axaml.cs:139` compared `Path.GetExtension(arg)` against `".srncc"` while project files are
  `.srnccproj`.** The comparison never matched, so the entire `targetHak` branch — which adds a
  project's configured output directory to the journal recovery set — was unreachable dead code. A
  publish interrupted while writing to an output directory outside the project folder was never
  recovered and never could be. `PublicationJournalStartupCheck.ProjectFileExtension` fixes it.
  **The loop only closes when S17 renames the app's own extension in wave 2**; until then the fixed
  branch matches nothing in practice, and S17's `ProjectExtensionTests` is what proves it.
- **`SqliteCacheService.CreateConnection` leaked an OS handle** when `Open()` or the opening pragmas
  threw, so the quarantine rename it depends on failed with a sharing violation. Found because S12's
  test used a real corrupt file rather than a mock.
- **`RecoverPendingJournalAsync` returned `true` after a rollback that restored nothing** — the
  swallowed exception aborted the rollback body and the method still reported success.
- **A committed transaction whose backup cleanup failed was reported as *not* recovered** — the
  opposite of `PublishAsync`, which correctly treats post-commit cleanup as best-effort.
- **`HakExistedBefore == true` with a missing backup file was a silent no-op** — rollback left the new
  artifact in place and reported success.
- **The bare `catch {}` in `RollbackJournalAsync` was concealing three bugs and preventing a fourth.**
  Once each rollback step became independent, cleanup would have deleted the backups and journal after
  a *failed* restore, destroying the only remaining route to recovery; the old code was saved from
  this only because the exception aborted the block before cleanup ran. Backups and the journal are
  now retained whenever the destination is not restored, and a later recovery pass finishes the job.
