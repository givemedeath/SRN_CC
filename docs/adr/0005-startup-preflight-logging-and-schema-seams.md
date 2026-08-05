# 5. Startup Preflight, Logging, and Schema Seams

* Status: Accepted
* Date: 2026-08-05

## Context and Problem Statement

Milestone 7 hardens the application for release. That required four things the codebase did not have:
a startup path that checks its own persisted state before the shell opens, a way to observe what the
application did after the fact, a seam for evolving persisted schemas without breaking existing
files, and a release pipeline that produces a byte-reproducible artifact containing nothing it should
not ship.

Each of those is normally solved by taking a dependency — a DI container, a logging framework, a
migration library, a packaging tool. This repository cannot take dependencies cheaply. Every package
is centrally pinned in `Directory.Packages.props`, reviewed into `eng/dependency-policy.json` with a
content hash and a signature expectation, and locked across seven `packages.lock.json` files that CI
restores in locked mode. `tools/AuditDependencies.ps1` verifies all of it and fails the build on
drift.

So the real question was not "which library" but "what is the smallest first-party seam that
satisfies the gate, and what constraint does it impose on everything after it". This record exists so
a future milestone can act on those constraints without reading Milestone 7's five phase documents.

## Decision Drivers

* The dependency budget is real and asymmetric. Adding one package costs a policy entry, a content
  hash, signature verification, and seven regenerated lock files — and every future restore pays for
  it. Writing a hundred lines of first-party code costs a review.
* `PLAN.md:215` forbids a local corpus, an oracle, a GPL or Radoub artifact, a cache, a project file,
  or an absolute source path from appearing anywhere in release output. Anything that ships must be
  explainable file by file.
* A first launch must reach a usable window. A startup check exists to report and to recover, never
  to refuse to start.
* Logging had to reach into constructors across `Core`, `Formats`, `Infrastructure`, `Preview`, and
  `App` without a breaking signature change in any of them.
* Persisted schemas are already at v1 in three places — the project file, the settings file, and the
  preview cache. The seam had to exist before the first change needed it, or the first change would
  have to invent it under pressure.
* `tools/AuditDependencies.ps1` audits a hard-coded project count. Adding a project is not a neutral
  act.

## Considered Options

1. Adopt the conventional stack: `Microsoft.Extensions.DependencyInjection` for composition,
   `Microsoft.Extensions.Logging` with a file sink for observability, a migration library for
   schemas, and a .NET tool for packaging.
2. Adopt nothing; keep composition implicit, log to the operation log only, version schemas ad hoc,
   and pack with whatever the SDK produces.
3. Write first-party seams sized to the gate: an explicit composition root, a synchronous JSON-line
   logger, a centralized schema-version registry with an empty migration pipeline, and a PowerShell
   packer.

## Decision Outcome

Chosen option: **Option 3 — first-party seams sized to the gate.**

Concretely:

* **No DI container.** `SRN.CC.App/Services/AppServices.cs` is one explicit composition root that
  builds the object graph and runs the startup checks in order.
* **`IStartupCheck` plus `AppPaths`.** A list of checks, not a framework. `AppPaths` is the single
  place any `%LOCALAPPDATA%\SRN.CC` location is derived. No shipped check may return `Blocking`.
* **First-party synchronous JSON-line logging.** `AppLogger` over `JsonLineLogSink`, one JSON object
  per line, rolling across ten files of ten mebibytes via `LogFileSet`.
* **`SchemaVersions` as the single source of schema truth**, with `SchemaMigrationPipeline` shipping
  zero migrations.
* **Settings adopt the project file's newer-schema rule**: a file whose schema version exceeds the
  current one opens read-only.
* **A PowerShell release packer.** `tools/PackRelease.ps1` and `tools/AuditRelease.ps1`, plus
  `--srncc-preflight-only` so the clean-machine smoke runner can assert the startup report without a
  window.
* **One version source**, `eng/Versions.props`.

### Consequences

These are the durable constraints. They outlive Milestone 7.

**No DI container is used, and adding one is not a small change.**
`Microsoft.Extensions.DependencyInjection` is not pinned. Adding it means a policy entry, a content
hash, signature verification, and seven regenerated lock files — before a single service is
registered. A future milestone that wants a container must budget for that, and must weigh it against
the fact that an explicit composition root is also the thing that makes the startup order auditable:
`AppServicesTests.CreateAsync_RunsAllFourChecksInTheOrderTheHandoffRequires` asserts an order that a
container would resolve implicitly.

**Logging is first-party and synchronous by design.** Synchronous because the failures worth logging
are the ones that happen while the application is dying, and a buffered async sink loses exactly
those. First-party because a logging framework is a runtime dependency that must then appear in
`NOTICES.md`, in the dependency policy, and in the shipped tree — for behaviour that is a hundred
lines. The cost is accepted deliberately: a hot path that logs per-item will pay for it. Do not add
per-item logging to indexing or traversal without measuring.

**The trailing-optional `IAppLogger? logger = null` convention is load-bearing.** Every component
that logs takes the logger as its *last* parameter, defaulted to null. That is what allowed logging
to reach across five projects without breaking a single existing call site or test. Keep it: a logger
promoted to a required or non-trailing parameter breaks every caller and every test fixture that
constructs the type.

**`SchemaVersions` is the single source of schema truth.** No component may hard-code a schema
number or compare versions itself. The pipeline ships empty on purpose — the mechanism is proven by a
synthetic two-step chain (`SchemaMigrationPipelineTests`), so the first real schema change is a
registration rather than an architecture decision. `MigrationCount_AsShipped_IsZero` will fail when
the first migration lands; updating it is the intended signal, not an obstacle.

**The additive `EnsurePreviewCacheSchema` pattern is the cache's sanctioned upgrade path.** The
preview cache is a rebuildable derived artifact, so it upgrades by adding what is missing rather than
by migrating. A cache that cannot be understood is quarantined and rebuilt. Do not introduce a
destructive cache migration; quarantine-and-rebuild is always available and always correct.

**Settings follow the project file's newer-schema rule.** A newer schema opens read-only. It is never
upgraded in place, never downgraded, and never refused. An older build must not corrupt a newer
build's settings, and the read-only path is what guarantees it.

**The release packer is PowerShell specifically because a .NET tool would add a project**, and
`tools/AuditDependencies.ps1` audits a hard-coded project count — so a packaging tool would break the
dependency gate to fix the packaging gate. The packer is deterministic by construction: ordinal-sorted
entries and every ZIP timestamp pinned to `1980-01-01T00:00:00Z`. `VerifyBuild.ps1` packs twice into
separate directories and compares SHA-256 rather than trusting that property.

**GPU capability is not probed at startup.** Deferred to preserve Milestone 6's context-ownership
decision (ADR 0003): probing GL at startup would require a context outside the
`OpenGlControlBase` lifetime that owns every GPU object. The consequence is that GPU problems surface
when a 3D slot opens, not at launch, and the real-GPU path is covered by the manual operator
checklist in `docs/evidence/MILESTONE-7-EVIDENCE.md` rather than by an automated test.

### Rejected options

**Option 1 — the conventional stack.** Rejected on dependency cost, not on quality. Four packages,
each with transitive closures, each needing a policy entry with a content hash and signature
expectation, each regenerating seven lock files, and each having to be explained file by file in the
shipped tree against `PLAN.md:215`. The behaviour actually required — build a graph, run four checks,
write JSON lines to a rolling file, compare an integer, and zip a folder deterministically — did not
justify it. The judgement is specific to this repository's dependency economics and would flip in a
codebase where adding a package is cheap.

**Option 2 — adopt nothing.** Rejected because it fails the gate. Without an explicit composition
root the startup order is unassertable; without a log there is no post-hoc evidence for the recovery
paths the milestone claims; without a version registry the first schema change becomes a
compatibility break; and without a deterministic packer the release archive cannot be reproduced,
which is the property that makes the audit meaningful in the first place.

**A blocking startup check.** Rejected outright. Every failure mode the four checks detect —
corrupt settings, a corrupt cache, a journal left mid-publish — has a recovery that leaves the
application usable. A check that refuses to start turns a recoverable state into a support incident.
`StartupPreflightTests.NoShippedCheckEverReturnsBlocking_AssertedOverTheRealFour` asserts this over
the real four checks rather than over a stub, so a future check cannot quietly acquire the power.
