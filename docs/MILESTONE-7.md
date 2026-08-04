# Milestone 7 — Release Hardening

## Summary

`PLAN.md:212-215` defines Milestone 7 as **Release hardening**: add startup cache/settings/journal/
tool-capability checks, schema migration hooks, operator documentation, and release audit; publish a
self-contained, non-single-file `win-x64` folder **and ZIP** containing only application files,
licenses, and notices.

**Gate:** final acceptance flow and clean-machine smoke test pass; no local corpus, oracle,
GPL-licensed artifact, third-party toolset artifact, cache, project, or absolute source path appears
in release output.

Why now: Milestones 1-6 delivered the product. The *operational* layer around it is largely absent —
there is no logging subsystem at all, settings are dead at runtime, the only startup check swallows
every failure it encounters, there is no release ZIP, no version stamping, and no `README.md`.
Several `PLAN.md:217-230` acceptance bullets have thin or zero coverage. This milestone closes all of
it so 1.0 is genuinely shippable, delivered as parallel subagent slices on one branch.

### Decisions taken with the user

| Decision | Choice |
|---|---|
| Execution model | **Waves on one branch** (`claude/milestone-7-*`), parallel subagents per wave, strict file ownership, review between waves, single PR |
| Breadth | **Full 1.0 close-out** — literal `PLAN.md:212-215`, plus the missing logging subsystem, plus backfilling every thin area of `PLAN.md:217-230` |
| `nwn_erf` oracle | **Not built.** Closed via ADR 0004, the way Milestone 6 closed slice S16 as NO-GO |
| Clean-machine smoke | **CI fresh-runner job** (unzip outside the repo, scripted smoke) **plus a signed-off manual operator checklist** for the real-GPU 3D path. No new NuGet packages |
| Project file extension | **Fix `.srncc` → `.srnccproj`** app-wide to match `PLAN.md:94` and every existing test |

---

## Verified baseline

Verified state of the tree at the start of Milestone 7:

- **Logging does not exist.** Zero `ILogger` or `Microsoft.Extensions.Logging` usages anywhere in
  `src/**`. `PLAN.md:98` requires rolling JSON-line logs at `%LOCALAPPDATA%\SRN.CC\Logs`, ten 10-MiB
  files. The only "logging" is `Program.cs`'s `.LogToTrace()` and the in-memory, UI-only
  `OperationLogViewModel`. Roughly two dozen bare `catch { }` blocks silently swallow cache
  quarantine, settings quarantine, publication rollback, and startup journal recovery.
- **Settings are dead at runtime.** `ISettingsStore` is injected into `MainWindowViewModel`
  (`:25`, `:95`) and never referenced again, so `LastProjectPath`, `RecentProjectPaths`, and
  `NwnInstallOverride` are inert. `NwnInstallLocator` is never invoked by the app — only by
  `tests/SRN.CC.Tests/Catalog/BaseGameCatalogTests.cs`.
- **The only startup check is `App.RecoverStartupPublicationJournals`** (`App.axaml.cs:102`). It
  scans the working directory plus startup args, swallows every failure at three levels
  (`:129`, `:160`, `:173`), and filters on `".srncc"` (`:134`) — the wrong extension.
- **`DiagnosticCode.CorruptedCacheQuarantined` exists** at
  `src/SRN.CC.Core/Diagnostics/DiagnosticCode.cs:16` and is **emitted nowhere**. Cache quarantine is
  entirely silent.
- **No schema migration seam of any kind.** Four scattered version literals: `ProjectStore.cs:19`,
  `SettingsStore.cs:10`, a hard-coded `1` in `SqliteCacheService.EnsureDatabaseInitialized`, and
  `SchemaVersion: "1.0"` / `AppVersion: "1.0.0"` at `ProvenanceManifestGenerator.cs:84-85`.
- **No release packaging.** `Compress-Archive` appears nowhere in the repository. There is no ZIP, no
  `Version`/`Product`/`Company`/`AssemblyVersion` property in any project file, and no assertion
  anywhere that the publish is non-single-file. Self-contained is a bare CLI flag at
  `tools/VerifyBuild.ps1:115`.
- **No operator documentation.** There is no `README.md` at the repository root; `docs/` is 21
  developer-facing milestone documents plus ADRs, compliance records, and evidence.
- **`PLAN.md:143`'s build-start fingerprint recheck was never implemented.**
  `DiagnosticCode.SourceDriftDetected` is declared at `src/SRN.CC.Core/Diagnostics/DiagnosticCode.cs:10`
  and emitted nowhere, and `BuildOrchestrator.ExecuteBuildAsync` performs no fingerprint comparison.
  What actually protects a build today is three other mechanisms: `FileShare.Read` handles held for
  the duration, a per-entry identity and size recheck at `HakAssetSourceReader.cs:254-263`, and the
  independent `BuildVerifier` pass over the finished artifact. This is a pre-existing Milestone 4 gap,
  not one this milestone introduces — see the deferral note in Scope.
- **The provenance manifest emits PascalCase field names.**
  `ProvenanceManifestGenerator.cs:94-98` sets only `WriteIndented` and an `Encoder` on its
  `JsonSerializerOptions`, with **no `PropertyNamingPolicy`**, so `HakSha256Hex` and `GeneratedUtc`
  are the real names — not the camelCase `PLAN.md:159` implies. Every slice asserting on manifest
  content must use the declared casing; changing it would be a breaking format change and is out of
  scope.
- **`SRNCC_REQUIRE_CORPUS` is documented at `PLAN.md:229` and never implemented.** Corpus gating is
  hand-rolled and triplicated across `CorpusIndexAcceptanceTests.cs:22-34`,
  `Render/ModelPreviewCorpusTests.cs:50-60`, and `Render/ModelPreviewWorkingSetTests.cs:39-51`.
- **Test gaps against `PLAN.md:217-230`:** build tests total 5 against ~10 required scenarios, with
  no test file at all for `BuildOrchestrator` or `ProvenanceManifestGenerator`; publication covers 2
  of the 5 `PublicationState` values with `RecoverPendingJournalAsync` untested; and no test walks
  import → resolve → build → verify → publish on real bytes.
  `Scenarios/ControlledAcceptanceScenarioTests.cs:78` stops at rescan and uses
  `DynamicIndexService`/`DynamicHashService` fakes over dummy `.hak` *text* files.

### Verified constraints that shape the design

1. **`Microsoft.Extensions.DependencyInjection` is not pinned anywhere** — `Directory.Packages.props`,
   `eng/dependency-policy.json`, and all seven `packages.lock.json` are clean.
   (`Microsoft.Extensions.DependencyModel 9.0.9` is a different, transitive Avalonia dependency.)
   Adding a DI container means a new NuGet package, a reviewed policy entry with `contentHash`,
   `.nuspec`/`.signature.p7s` verification, and seven regenerated lock files. **A1 is forced, not
   chosen.**
2. `tools/AuditDependencies.ps1:102,161` hard-codes `Count -ne 7` for lock files *and* assets files.
   **Any new project breaks the dependency audit.** No slice in this milestone adds a project.
3. `tools/AuditVendoredSources.ps1` hard-codes `40`/`8` in three places (condition line 97, message
   line 98, success string line 121). **No slice adds a vendored file.**
4. `AuditVendoredSources.ps1` greps every `src/**` and `tests/**` `.cs`/`.csproj`/`.axaml` for
   word-boundary provenance keywords. ADR 0004, the operator documentation, and
   `eng/release-policy.json` are `.md`/`.json` under paths that are not scanned, so they are safe —
   but **no slice may write those keywords into any `.cs`, `.csproj`, or `.axaml`, including
   comments.**
5. `TreatWarningsAsErrors=true` is set solution-wide in `Directory.Build.props`. A partially-landed
   contract is a solution-wide build break, which is why the wave-0 → wave-1 barrier is load-bearing
   rather than ceremonial.
6. `NwnInstallLocator.Locate` is `[SupportedOSPlatform("windows")]`. Calling it from
   `SRN.CC.Infrastructure` without a guard raises CA1416, which under constraint 5 is a build break.
   `if (OperatingSystem.IsWindows())` is recognized by the platform-compatibility analyzer and
   suppresses it without an attribute or a `#pragma`.
7. **`NOTICES.md:23-37` is already complete** — Pfim, all five NAudio packages, and
   Silk.NET.Core/Maths/OpenGL are present. The warning at `docs/MILESTONE-6.md:77` predicting an M7
   release-audit failure is stale. M7 needs a *test* that keeps the table complete, not a fix.
8. **`MainWindowViewModel.cs:402,475` already gates `BuildHakCommand` on `!IsReadOnly`**
   (`CanBuildHak() => _workspaceState != null && !_workspaceState.IsReadOnly`). `PLAN.md:94`'s "Save
   and Build disabled" rule needs a regression test, not a production fix.
9. **`Process.PrivateMemorySize64` is already this repository's private-working-set proxy** —
   `tests/SRN.CC.CorpusTests/Performance/TableViewPerformanceProbe.cs:86` uses it and
   `docs/evidence/tableview-performance-baseline.json` records it as `privateWorkingSetBytes`. The
   30-second idle assertion needs no P/Invoke and no new package.
10. **`MainWindow.axaml:16-23` has only New / Open / Save / Build.** `PLAN.md:104` requires New, Open,
    Save, **Save As**, **Rescan**, Build, and **Settings**. `IProjectStore.SaveAsAsync` has no
    command and there is no settings UI at all. Since this milestone makes settings live for the
    first time, this closes here or 1.0 ships against a shell the plan does not describe.

---

## Architecture decisions

### A1 — No DI container. `App.axaml.cs` shrinks behind a first-party `AppServices`

Per constraint 1, a container is not available. Instead, move the entire `new` graph out of
`OnFrameworkInitializationCompleted` into a first-party composition object:

```csharp
// NEW src/SRN.CC.App/Services/AppServices.cs
public sealed class AppServices : IAsyncDisposable
{
    public static Task<AppServices> CreateAsync(
        AppPaths paths, IReadOnlyList<string> startupArgs, CancellationToken ct = default);
    public IAppLogger Logger { get; }
    public StartupReport StartupReport { get; }
    public MainWindowViewModel CreateMainWindowViewModel();
}
```

`App.axaml.cs` becomes roughly twenty lines: build `AppPaths.Default`, await
`AppServices.CreateAsync`, create the view model, attach the window, replay `StartupReport` into
`OperationLogViewModel`.

**Consequences:** `App.axaml.cs` stops being a hot file permanently — every future wiring change
lands in `AppServices.cs` instead. `AppServices` is constructible headlessly against an `AppPaths`
pointing at a temp directory, which is how the preflight is asserted without launching a window and
how the `--srncc-preflight-only` CI smoke mode works (A6). Zero packaging, policy, lock, or audit
changes.

**Rejected:** a DI container (new package, forbidden by scope, and a five-way serializing edit for a
graph of fifteen hand-constructed singletons that has never needed lifetime management); leaving
construction inline (guarantees a contended file every wave and makes the preflight untestable except
through a real window).

### A2 — Startup checks are a first-party `IStartupCheck` list, and a check may never crash startup

```csharp
// NEW src/SRN.CC.Core/Startup/
public enum StartupCheckSeverity { Ok, Degraded, Blocking }

public sealed record StartupCheckResult(
    string CheckId, StartupCheckSeverity Severity, string Summary,
    IReadOnlyList<string> Details, DiagnosticCode? Code = null);

public interface IStartupCheck
{
    string CheckId { get; }
    Task<StartupCheckResult> RunAsync(CancellationToken cancellationToken = default);
}

public sealed record StartupReport(IReadOnlyList<StartupCheckResult> Results)
{
    public bool HasBlocking { get; }
    public StartupCheckSeverity Worst { get; }
}

public sealed record AppPaths(string Root)      // single source of truth for %LOCALAPPDATA%\SRN.CC
{
    public string CacheDatabasePath { get; }    // Root/cache-v1.sqlite
    public string SettingsPath { get; }         // Root/settings.json
    public string LogDirectory { get; }         // Root/Logs
    public static AppPaths Default { get; }
}
```

`StartupPreflight.RunAsync(checks, logger, ct)` runs each check inside its own `try`; a throwing
check becomes `Degraded` carrying the exception and never propagates. Four checks in
`src/SRN.CC.Infrastructure/Startup/`:

1. **`CacheStartupCheck`** — reads `SqliteCacheService.IsAvailable`/`LastQuarantineReason` (new
   members, A4) and finally emits `DiagnosticCode.CorruptedCacheQuarantined`.
2. **`SettingsStartupCheck`** — `Ok`, `Degraded(quarantined)`, or `Degraded(newer schema, read-only,
   file untouched)`.
3. **`PublicationJournalStartupCheck`** — ports `App.axaml.cs:102-178` verbatim, then adds the
   directories from `settings.RecentProjectPaths` (settings are finally consumed) and reports every
   recovered or rolled-back journal by path instead of swallowing it.
4. **`ToolCapabilityStartupCheck`** — RID and architecture, `AppContext.BaseDirectory`, presence of
   `e_sqlite3.dll` / `av_libglesv2.dll` / `libSkiaSharp.dll` / `libHarfBuzzSharp.dll` (base directory,
   then `runtimes/win-x64/native`), `NwnInstallLocator.Locate(settings.NwnInstallOverride)` behind
   constraint 6's guard, and free space on `AppPaths.Root`.

**GPU capability is explicitly deferred, not skipped.** Probing GL at startup would require creating
a context, which violates Milestone 6's A4 ("`OpenGlControlBase` only for visible 3D slots"). The
check reports `render-gpu: Deferred — probed on first 3D slot`, and `ModelViewportControl` gains one
line logging the `GlCapabilities` record (`GlCapabilities.Probe`, `GlCapabilities.cs:38`) the first
time a device is constructed. Real-GPU confirmation is the manual operator checklist.

**Consequences:** `HasBlocking` is false by construction today — every degradation is survivable.
Keep it in the contract so a future blocking condition has a landing place; a test asserts that no
shipped check returns `Blocking`.

### A3 — First-party JSON-line logging. Synchronous, locked, no background writer

```csharp
// NEW src/SRN.CC.Core/Logging/
public enum LogLevel { Trace, Debug, Info, Warn, Error }

public sealed record LogRecord(
    DateTimeOffset TimestampUtc, LogLevel Level, string Category, string Message,
    string? EventCode, IReadOnlyDictionary<string, string>? Data,
    string? ExceptionType, string? ExceptionMessage);

public interface ILogSink : IDisposable { void Write(LogRecord record); void Flush(); }

public interface IAppLogger
{
    void Log(LogLevel level, string category, string message,
        Exception? exception = null, IReadOnlyDictionary<string, string>? data = null);
}

public static class NullAppLogger { public static readonly IAppLogger Instance; }
```

`JsonLineLogSink` (Infrastructure): the active file is `srncc.log` in `AppPaths.LogDirectory`. Before
each write, if `length + recordBytes > 10 MiB`, close, rename to `srncc.<yyyyMMddTHHmmssfffZ>.log`,
and delete the oldest until at most ten files remain. One `lock` spans the size check, the append,
and the `Flush()`. `FileShare.Read` so operators can tail the file. One record per line, written with
`Utf8JsonWriter`.

**Synchronous, not a background queue.** The application logs at human rates; a lock costs
microseconds. A background writer buys nothing and adds shutdown-ordering bugs plus a "lose the last
N records on crash" failure mode precisely when the log matters most. `AppLogger` fans out to N sinks
and swallows per-sink failures — a logger must never throw — exposing `FailedSinkCount` for a test.

**Injection rule, stated to every slice:** existing services take a **trailing optional**
`IAppLogger? logger = null` defaulting to `NullAppLogger.Instance`. Zero call-site churn and zero
test churn — the same trick Milestone 6's A2 used for `PreviewResult.Payload`. **Never reorder an
existing constructor parameter.**

Silent `catch` sites to wire, verified by grep: `App.axaml.cs:129,160,173`; `SqliteCacheService.cs:290`;
`SettingsStore.cs:60,107,111,134`; `ArtifactPublisher.cs:239,309`; `BuildOrchestrator.cs:185,186`;
`ProjectStore.cs:640,644,672,676,703,707,752`; `MainWindowViewModel.cs:667,689,703`;
`WorkspaceService.cs:385`; `WorkspaceResolver.cs:354`. **Deliberately left silent:**
`DependencyAnalyzer.cs` (nine sites) and `HakReader.cs:273` / `BifReader.cs:100` — these are bounded
per-asset parse failures already surfaced as diagnostics, and logging them would flood a 188k-asset
index.

**`OperationLogViewModel` becomes a sink, not a parallel system.**
`src/SRN.CC.App/Services/ObservableLogSink.cs` implements `ILogSink`, marshals to the UI dispatcher,
filters to `>= Info`, and appends to `OperationLogViewModel.Entries`. `AddEntry(string, string)`
survives for compatibility but delegates to the logger. Add a 2 000-entry ring cap — `Entries` is
unbounded today, which is a leak in a long 188k-row session.

**Rejected:** `Microsoft.Extensions.Logging` (new package, forbidden by scope); Avalonia's
`LogToTrace` sink (no rotation, no JSON, no structure).

### A4 — One centralized schema registry, per-store application, a real seam with a no-op v1 path

```csharp
// NEW src/SRN.CC.Core/Schema/
public enum SchemaKind { Project, Settings, Cache, Manifest }
public enum SchemaOpenMode { Unsupported, Current, ReadOnlyNewer }

public static class SchemaVersions
{
    public const int Project = 1, Settings = 1, Cache = 1;
    public const string Manifest = "1.0";
    public static int Current(SchemaKind kind);
    public static SchemaOpenMode Classify(SchemaKind kind, int fileVersion);
}

public interface IJsonSchemaMigration
{
    SchemaKind Kind { get; }
    int FromVersion { get; }
    int ToVersion { get; }
    JsonObject Apply(JsonObject document);
}

public sealed class SchemaMigrationPipeline
{
    public SchemaMigrationPipeline(SchemaKind kind, IEnumerable<IJsonSchemaMigration> migrations);
    public bool TryUpgrade(JsonObject document, int fromVersion,
        out JsonObject upgraded, out IReadOnlyList<string> applied, out string? error);
}
```

The pipeline ships with **zero** registered migrations, so 1 → 1 is an identity no-op returning
`applied = []`. That is the deliverable: the seam, not speculative migrations. Proof is a **test-only**
`FakeV1ToV2Migration` asserting that a chain composes in order, that a gap (target 3 with only 1 → 2
registered) returns `false` with an explicit error rather than silently skipping, and that an
identity upgrade never mutates the input `JsonObject` — which matters because `ProjectStore`
preserves unknown fields through `ProjectPreferences.RawRootNode`.

This replaces all four scattered literals from the verified baseline. The **cache** is not JSON, so
it gets `Classify` only; its migration mechanism remains the additive pattern that already exists
(`SqliteCacheService.EnsurePreviewCacheSchema`, `:178`), documented in ADR 0005 as the sanctioned
cache upgrade path.

**Rejected:** per-store private consts (the status quo — four sources of truth, already drifting); a
generic migration engine with a discovery mechanism (speculative, unfalsifiable, and more code than
any real migration will ever be).

### A5 — Settings adopt the project-file rule: newer schema is read-only and untouched

Today `SettingsStore.LoadAsync` treats a **newer** `schemaVersion` as corruption (`:29-32` throws,
caught at `:60`, quarantined at `:63`), silently renaming away a future application's settings.
`PLAN.md:94` says newer schemas open read-only; `PLAN.md:96` scopes quarantine to *corrupt* files.

New behaviour: `Current` → read and write; `ReadOnlyNewer` → return defaults with `IsReadOnly = true`
and **never touch the file**; `Unsupported` (version < 1) or unparseable → quarantine to
`.corrupt.<timestamp>` and rebuild, as today.

```csharp
Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);
public sealed record SettingsLoadResult(
    ApplicationSettings Settings, SettingsLoadStatus Status, string? QuarantinedPath);
```

`SaveAsync` throws `InvalidOperationException` when the loaded state was `ReadOnlyNewer`. Blast
radius is verified as exactly four files — `ISettingsStore.cs`, `SettingsStore.cs`,
`SettingsStoreTests.cs`, and the `NotSupportedSettingsStore` fake at
`tests/SRN.CC.Tests/App/MainWindowViewModelTextureSourceWiringTests.cs:192` — all owned by one slice,
with the App consumer landing a wave later.

**Explicitly deferred:** recursive unknown-field preservation for settings. `PLAN.md:94` requires it
for projects only. Recorded in ADR 0005.

### A6 — Deterministic first-party ZIP in PowerShell; `--srncc-preflight-only` for a deterministic smoke

`Compress-Archive` embeds live timestamps and guarantees no entry order, so it cannot produce a
reproducible archive. A .NET packer tool would add a project and break constraint 2. Therefore
`tools/PackRelease.ps1` drives `System.IO.Compression.ZipArchive` directly:

- entries in ordinal-sorted relative-path order, `/` separators, no directory entries;
- `CompressionLevel.Optimal`; every `entry.LastWriteTime` set to `1980-01-01T00:00:00Z`, the DOS-time
  floor (anything earlier throws);
- **`publish-inventory.json` is excluded** — it carries `timestampUtc` (`AuditPublish.ps1:187`) and
  would make the archive unreproducible forever. It is audit evidence, not an application file, so
  excluding it is also exactly what `PLAN.md:214` requires;
- writes `release-manifest.json` beside the archive: version, RID, archive SHA-256, file count, and
  per-file SHA-256.

Determinism is asserted at the **packer** level, not the compiler level: `tools/AuditRelease.ps1`
packs the same publish tree twice and asserts identical SHA-256. Claiming end-to-end reproducible
builds would be unverifiable in a single run; claiming a deterministic packer is both true and useful.

`tools/AuditRelease.ps1 -Root <directory>` is the **only** implementation of the release audit and is
called from three places — `VerifyBuild.ps1`, the CI smoke job, and locally. Over `AuditPublish.ps1`
it adds:

- **a non-single-file assertion**: `SRN.CC.App.exe` *and* a sibling `SRN.CC.App.dll` *and* a loose
  `Avalonia.Base.dll` *and* a file count above 100 — a single-file publish satisfies none of these —
  plus a check that no `PublishSingleFile` property exists in any `.csproj` or `.props`;
- forbidden extensions extended with `.log`, `.srnccproj`, `.srncc`, `.sqlite-wal`, `.sqlite-shm`,
  and `.zip`;
- forbidden fragments extended with `nwn_erf`, the third-party toolset name, `SRNCC_CORPUS_ROOT`,
  `sqlite3_64.dll`, and `verification-runs`, searched across `.dll`/`.exe`/`.json` in both ASCII and
  UTF-16 exactly as `AuditPublish.ps1:151-160` already does. **Never add `srnccproj` as a forbidden
  fragment** — the application legitimately embeds that literal.

`Program.cs` gains a `--srncc-preflight-only` switch (roughly ten lines) that runs
`AppServices.CreateAsync`, writes `StartupReport` as JSON to stdout, and exits 0 without creating a
window. This gives the clean-machine job a *deterministic* assertion — parse the report, assert every
check ran, assert nothing is `Blocking` — independent of whether a GitHub runner can realize an
Avalonia window. The ten-second windowed survival check is a second, separate step.

### A7 — Single version source of truth in `eng/Versions.props`

```xml
<SRNCCVersionPrefix>1.0.0</SRNCCVersionPrefix>
<SRNCCProductName>SRN.CC Asset Curator</SRNCCProductName>
<SRNCCCompany>SRN.CC Authors</SRNCCCompany>
```

`Directory.Build.props` derives `Version`, `AssemblyVersion` (`$(SRNCCVersionPrefix).0`),
`FileVersion`, `InformationalVersion`, `Product`, `Company`, and — critically —
`<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`.
Without that last property the SDK appends `+<commit-sha>`, which would land in every provenance
manifest and make manifests differ between builds of byte-identical content.

`ProvenanceManifestGenerator.cs:84-85` reads `SchemaVersion` from `SchemaVersions.Manifest` and
`AppVersion` from `Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()`.
`PackRelease.ps1` reads `SRNCCVersionPrefix` out of `eng/Versions.props` by XML — never a duplicated
literal — for the archive name `SRN.CC-<version>-win-x64.zip`.

### A8 — The `nwn_erf` oracle is closed as intentionally not built (ADR 0004)

`PLAN.md:228` itself forbids the oracle from being the authoritative duplicate or CP1252 verifier.
`HakRoundTripTests.cs`, `HakReaderTests.cs`, and this milestone's new determinism tests already *are*
that verifier, and they cover CP1252 and duplicate-key cases the oracle's ASCII-only listing cannot.
An opt-in shell-out to a third-party binary would add a tool-discovery path, a new
forbidden-path-fragment risk in release output, and an untestable-in-CI surface — for a check weaker
than the one it would sit beside. Closed the same way Milestone 6 closed S16.

---

## Shared contracts

Every slice codes against this vocabulary. The wave-0 contract slices (S2, S3, S4) are the sole
owners of these files; everything downstream consumes them.

- `src/SRN.CC.Core/Logging/` — `LogLevel`, `LogRecord`, `ILogSink`, `IAppLogger`, `NullAppLogger`
  (A3). Injection is always a trailing optional `IAppLogger? logger = null`.
- `src/SRN.CC.Core/Startup/` — `StartupCheckSeverity`, `StartupCheckResult`, `IStartupCheck`,
  `StartupReport`, `AppPaths` (A2). `AppPaths` is the single source of truth for every
  `%LOCALAPPDATA%\SRN.CC` path; no slice may re-derive one.
- `src/SRN.CC.Core/Schema/` — `SchemaKind`, `SchemaOpenMode`, `SchemaVersions`,
  `IJsonSchemaMigration`, `SchemaMigrationPipeline` (A4). No slice may keep a private schema-version
  constant after wave 1.
- `eng/release-policy.json` (S7) — `zipEntryTimestampUtc`, `zipExcludedFiles`,
  `nonSingleFileMarkers`, `minimumFileCount`, `releaseNameTemplate`. Read by `PackRelease.ps1` and
  `AuditRelease.ps1`; written by neither.
- `tests/SRN.CC.Tests/Build/RealHakFixtureFactory.cs` (S16) — the shared producer of **genuine** HAK
  bytes through the production `HakWriter`, consumed read-only by S19's end-to-end scenario.
- `tests/SRN.CC.CorpusTests/CorpusGate.cs` (S8) — the single gating helper replacing the triplicated
  `[SetUp]` blocks, and the home of `SRNCC_REQUIRE_CORPUS`.

---

## Scope

**In scope:** the four startup checks plus the preflight runner and `AppPaths`; the rolling JSON-line
logging subsystem with rotation, fan-out, and the wiring of every silent `catch` listed in A3; the
centralized schema registry and migration seam with a no-op v1 path; settings newer-schema alignment
and settings finally becoming live at runtime; `SqliteCacheService` never throwing out of its
constructor and finally emitting `CorruptedCacheQuarantined`; publication-journal hardening with
failure injection at all five states and full recovery coverage; version stamping from a single
source; the deterministic release packer, `release-manifest.json`, and the release audit; the
extended `VerifyBuild.ps1` ladder and the CI clean-machine smoke job; operator documentation and a
root `README.md`; ADR 0004 (oracle) and ADR 0005 (this milestone's seams); the `.srncc` → `.srnccproj`
fix; shell completion for Save As, Rescan, and Settings per constraint 10; `SRNCC_REQUIRE_CORPUS`,
the private-working-set-after-30-seconds-idle probe, and a recorded cold-index baseline; and the
end-to-end acceptance flow on real bytes producing a real HAK, plus backfill for the thin resolution,
dependency, build, publication, UI, and preview areas of `PLAN.md:217-230`.

**Explicitly deferred:** the `PLAN.md:143` build-start fingerprint recheck and any emission of
`DiagnosticCode.SourceDriftDetected` — a pre-existing Milestone 4 gap, deferred here because the
build is already protected by held `FileShare.Read` handles, the per-entry identity and size recheck
at `HakAssetSourceReader.cs:254-263`, and the independent verifier pass, and because closing it means
new production behaviour in `BuildOrchestrator` beyond what any Milestone 7 slice owns. S16's
`HakDeterminismTests` covers the observable requirement — a source mutated mid-build fails
verification. S23 must record this as a named plan-versus-code gap rather than letting the mapping
table imply coverage; the `nwn_erf` oracle downloader and its compatibility tests (ADR 0004);
recursive unknown-field preservation for `settings.json` (`PLAN.md:94` requires it for projects
only); a real-GPU automated smoke test — no windowing package is approved in
`eng/dependency-policy.json` and `Avalonia.Headless` has no GL backend, so the real-GPU path is
covered by the signed-off manual operator checklist; startup GL probing (deferred by A2 to preserve
Milestone 6's A4); and everything Milestone 6 deferred at `docs/MILESTONE-6.md:364-370` that is not
listed above — environment maps as a rendered reflection term, skinmesh, emitters, animation
playback, danglymesh, `MdlPartComposer`, supermodel inheritance, a standalone BWM reader, per-slot
PLT dye editing, and MDL light-node lighting.

---

## Work slices and ownership rule

Milestone 7 is delivered as 24 file-disjoint subagent slices (S0-S23) across 5 waves, on one branch,
with a single PR at the end. **Ownership rule, stated to every subagent: you may edit only the files
listed as "Owns" for your slice. Touching a sibling's file is a merge conflict by construction.**

```
WAVE 0 (9 parallel)   S0 docs · S1 oracle ADR · S2 log contracts · S3 startup contracts
                      S4 schema registry · S5 version stamping [after S4] · S6 operator docs
                      S7 release policy data · S8 corpus gate + idle probe

WAVE 1 (8 parallel)   S9 log sink · S10 preflight + 4 checks · S11 settings · S12 cache
                      S13 project store · S14 journal · S15 packer + release audit
                      S16 build determinism + HAK fixture factory

WAVE 2 (2 parallel)   S17 composition root  ·  S18 verifier + CI + clean-machine smoke

WAVE 3 (4 parallel)   S19 end-to-end acceptance · S20 shell completion
                      S21 resolution/dependency backfill · S22 UI/preview backfill

WAVE 4 (1)            S23 evidence + ADR 0005 + manual operator checklist
```

Critical path: **S2/S3/S4 → S9/S10 → S17 → S19 → S23.** The secondary chain that must not fall
behind is **S7 → S15 → S18**; S18 sits in wave 2 alongside S17 with zero file overlap, so it carries
exactly one wave of slack. S15 is the largest slice by line count (two PowerShell scripts, roughly
450 lines) and sits entirely off the critical path — the same shape Milestone 6 used for S8.

### Phase documents

| Phase doc | Wave | Slices |
|---|---|---|
| [`docs/MILESTONE-7-1.md`](MILESTONE-7-1.md) | Wave 0 | S0, S1, S2, S3, S4, S5, S6, S7, S8 |
| [`docs/MILESTONE-7-2.md`](MILESTONE-7-2.md) | Wave 1 | S9, S10, S11, S12, S13, S14, S15, S16 |
| [`docs/MILESTONE-7-3.md`](MILESTONE-7-3.md) | Wave 2 | S17, S18 |
| [`docs/MILESTONE-7-4.md`](MILESTONE-7-4.md) | Wave 3 | S19, S20, S21, S22 |
| [`docs/MILESTONE-7-5.md`](MILESTONE-7-5.md) | Wave 4 | S23 |

### Shared-file contention and how it is serialized

| Hot file | Wanted by | Resolution |
|---|---|---|
| `src/SRN.CC.App/App.axaml.cs` | preflight, logging, settings, cache, extension fix | **A1**: all logic moves into `AppServices.cs`. Edited **once**, by S17 alone, and shrinks to ~20 lines |
| `src/SRN.CC.App/ViewModels/MainWindowViewModel.cs` | S17 and S20 | **Serialized across the wave-2 → wave-3 barrier.** S17 lands first; S20 rebases on it. Never concurrent |
| `src/SRN.CC.App/Views/MainWindow.axaml.cs` | S17 (picker extensions) and S20 (Save As / Settings wiring) | Same wave barrier. `MainWindow.axaml` itself is S20-only |
| `tools/VerifyBuild.ps1` | packaging, release audit, banner, license-copy dedup | **S18 alone.** S15 writes standalone scripts; S18 is the only slice that edits the driver |
| `eng/publish-policy.json` | forbidden fragments and extensions, required files | **S7 alone**, in wave 0, as pure data. S15 and S18 read it and never write it |
| `.github/workflows/verify.yml` | artifact rename, CI flag, smoke job | **S18 alone** — the smoke job needs `needs:` on the verify job to consume the archive artifact, so it cannot live in a separate workflow file owned by a separate slice |
| `tools/AuditPublish.ps1`, `AuditDependencies.ps1`, `AuditVendoredSources.ps1` | — | **Not edited by any slice.** Every new check is data (S7) or lives in the new `AuditRelease.ps1` (S15) |
| `eng/dependency-policy.json`, `Directory.Packages.props`, all seven `packages.lock.json` | — | **Not edited.** No new NuGet package (A1, A3) |
| `Directory.Build.props` | version stamping only | **S5 alone**, wave 0. The milestone's only touch of this file |
| `ArtifactPublisherTests.cs`, `ProjectStoreTests.cs`, `ControlledAcceptanceScenarioTests.cs` | S14, S13, S19 | New sibling files instead of modification — exactly how Milestone 6 kept `PreviewProviderTests.cs` unmodified as its migration-cost proof |
| `docs/evidence/MILESTONE-7-EVIDENCE.md` | everyone | **S23 alone**, wave 4 |

---

## Risks

| Risk | Mitigation |
|---|---|
| A new project or vendored file breaks `AuditDependencies.ps1:102,161` (`Count -ne 7`) or `AuditVendoredSources.ps1` (`40`/`8` at lines 97, 98, 121) | **No slice adds a project or a vendored file.** The deterministic packer is PowerShell (A6), not a .NET tool, specifically for this. Restated in every slice brief |
| `TreatWarningsAsErrors=true` turns a partially-landed logging or startup contract into a solution-wide break | S2/S3/S4 are declarations-only wave-0 slices with their own tests; the wave barrier means no wave-1 slice starts against a half-written contract. The trailing-optional-`IAppLogger` rule (A3) guarantees no existing call site changes |
| `NwnInstallLocator.Locate` is Windows-only; CA1416 under warnings-as-errors is a build break (constraint 6) | Guard with `if (OperatingSystem.IsWindows())`, which the platform-compatibility analyzer recognizes. Non-Windows returns `Degraded("install discovery unavailable on this platform")` |
| A new file contains a disqualifying provenance keyword and fails the vendored-source grep (constraint 4) | ADR 0004 and the operator documentation are `.md`; `eng/release-policy.json` is under `eng/`. Neither is scanned. **No slice may write those keywords into a `.cs`, `.csproj`, or `.axaml`, including comments.** Restated in S1's, S6's, and S7's briefs |
| Multiple slices want `App.axaml.cs` | A1 makes it a ~20-line file edited once by S17; wiring lives in `AppServices.cs` forever after |
| `AssemblyInformationalVersion` silently gains `+<commit-sha>`, poisoning every provenance manifest | `IncludeSourceRevisionInInformationalVersion=false` in `Directory.Build.props` (S5), plus `VersionStampingTests` asserting the runtime attribute contains no `+` |
| Adding `srnccproj` to `forbiddenPathFragments` bricks the publish audit | Add only `nwn_erf`, the toolset name, `SRNCC_CORPUS_ROOT`, `sqlite3_64.dll`, and `verification-runs`. S7's brief states this explicitly, and S18's first local `VerifyBuild.ps1` run is the check |
| `publish-inventory.json`'s `timestampUtc` makes the archive unreproducible | Excluded from the archive (A6). It is audit evidence, not an application file, so excluding it is what `PLAN.md:214` requires |
| The release audit needs a publish tree that `VerifyBuild.ps1` already produces | `AuditRelease.ps1 -Root <dir>` runs against the **existing** `$publishDir` from step [6], after `AuditPublish.ps1` and before `PackRelease.ps1`. No second publish. The CI smoke calls the same script against the extracted archive, so there is exactly one release-audit implementation |
| A GitHub `windows-2025` runner cannot realize an Avalonia window and the smoke job flakes | `--srncc-preflight-only` (A6) provides the deterministic, window-free assertion. The ten-second windowed survival check is a separate step that can be relaxed without losing the gate |
| A 30-second idle probe makes every corpus pass unbearable | A single 30-second sample, `[Category("Performance")]`, additionally gated on `SRNCC_RUN_IDLE_PROBE=1` so it stays off even inside a corpus run |
| The `ISettingsStore` interface change ripples further than expected | Verified as exactly four files, all owned by S11 (A5); the App consumer lands a wave later |
| Making `SqliteCacheService` never throw hides a real failure | `IsAvailable = false` is reported by `CacheStartupCheck` as `Degraded`, logged with `DiagnosticCode.CorruptedCacheQuarantined`, and surfaced in the operation log. Today the same condition is an uncaught throw out of `OnFrameworkInitializationCompleted` — a hard startup crash with no message |

---

## Verification

**Per slice:** `dotnet build SRN.CC.sln -c Release` clean under `TreatWarningsAsErrors`, plus that
slice's own tests via `--filter 'FullyQualifiedName~<OwnedFixture>'`. Slices must not modify tests
owned by another slice.

**Per wave:** `dotnet test SRN.CC.sln -c Release --filter 'Category!=Corpus&Category!=Performance'`
green with zero skips, before starting the next wave.

**Milestone gate — run the master verifier and record its run ID:**

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/VerifyBuild.ps1
```

After S18 the ladder is eleven steps: the existing [1]-[8] unchanged, then **[8b]**
`AuditRelease.ps1 -Root $publishDir`; **[9]** `PackRelease.ps1` producing
`<runDir>/release/SRN.CC-<version>-win-x64.zip` plus `release-manifest.json`, packed twice with the
two SHA-256 values compared; **[10]** extract the archive to a scratch directory outside the
repository and re-run `AuditRelease.ps1` against it; then the existing launch smoke and immutability
recheck. `summary.json` gains `version`, `releaseZipPath`, and `releaseZipSha256`.

**Opt-in corpus / GPU / idle pass** (never run in CI, per the existing pattern):

```bash
dotnet test tests/SRN.CC.CorpusTests -c Release --filter 'Category=Corpus' -p:SRNCCVerificationRuntimeIdentifier=win-x64
```

with `SRNCC_CORPUS_ROOT` set and `SRNCC_RUN_CORPUS=1`. Add `SRNCC_RUN_GPU=1` for the GPU fixtures, or
`SRNCC_RUN_IDLE_PROBE=1` with `--filter 'Category=Performance'` for the idle probe.
`SRNCC_REQUIRE_CORPUS=1` alone is sufficient to force a hard failure when the corpus is missing —
`IsRequired` implies `IsEnabled`, so the requirement cannot be silently defeated.

**Clean-machine smoke:** a `clean-machine-smoke` CI job on a fresh `windows-2025` runner with
`needs:` on the verify job. It sparse-checks out only `tools/` and `eng/`, downloads the release
archive artifact, expands it under `$env:RUNNER_TEMP` outside the repository, asserts
`%LOCALAPPDATA%\SRN.CC` is absent, runs `AuditRelease.ps1` against the extracted tree, runs
`SRN.CC.App.exe --srncc-preflight-only` and parses the emitted `StartupReport` JSON, then performs a
windowed ten-second survival check and asserts that `Logs\srncc.log` and `cache-v1.sqlite` were
created and that the first log line parses as JSON.

**Manual end-to-end** (the gate scenario, via `/run` or a direct launch): first launch on a machine
with no `%LOCALAPPDATA%\SRN.CC` → confirm the preflight report appears in the operation log and that
`Logs\srncc.log` is created and is valid JSON-lines → open a real HAK, resolve a conflict, pin, save
as `.srnccproj`, rescan, build, and confirm the HAK loads in the toolset → corrupt `cache-v1.sqlite`
and relaunch, confirming quarantine and recovery → kill the process mid-publish and relaunch,
confirming journal rollback → **real-GPU 3D path**: select an MDL, orbit/pan/dolly, toggle the
walkmesh, fill three concurrent viewports, confirm missing-texture degradation, and force a
shader-unsupported path to confirm the fallback to text.

**Evidence:** `docs/evidence/MILESTONE-7-EVIDENCE.md` (owned by S23) maps every clause of the
`PLAN.md:212-215` gate **and** every bullet of `PLAN.md:217-230` to a fully-qualified
`TestClass.TestMethodName` — never a prose claim — and records the `VerifyBuild.ps1` run ID, the
release archive SHA-256, build warning and error counts, test pass/fail/skip counts, the corpus
baseline numbers, and the signed-off manual real-GPU operator checklist. It matches the
`docs/evidence/MILESTONE-6-EVIDENCE.md` format.
