# Milestone 5 - Phase 1: Foundation and Infrastructure

## Scope
Prepare shared dependencies and preview infrastructure primitives required by all later phases.

## Objectives
- Centralize package management for preview features.
- Introduce dependency analysis interface and wire it into core contracts.
- Add preview cache persistence with LRU cleanup behavior.

## Deliverables
- Package and DI foundations required by Milestone 5 providers and UI features.
- Persistent preview cache API in `SqliteCacheService`.
- Stable interface contract for dependency analysis.

## Why this phase comes first
- Every provider and UI feature depends on these primitives (`IDependencyAnalyzer`, media packages, cache service methods).
- Prevents later changes from having conflicting package versions or cache schema drift.

## Detailed tasks

### 1. Central Package Management
- [x] Update `Directory.Packages.props` in a dedicated `media-preview` block.
- [x] Add `Pfim` package (`0.11.4`) for DDS decoding.
- [x] Add `NAudio` package (`2.3.0`) for WAV/BMU playback.
- [x] Add `Silk.NET.OpenGL` package (`2.23.0`) where preview pipelines require OpenGL bindings.
- [x] Confirm all affected projects reference versions only via `Directory.Packages.props` (no hard-coded versions).

### 2. Core Interfaces and Registrations
- [x] Add `src/SRN.CC.Core/Services/IDependencyAnalyzer.cs`.
- [x] Define:
  - `AnalyzeDependenciesAsync(AssetOccurrence occurrence, Stream stream, CancellationToken cancellationToken = default)`.
  - Return type: `IReadOnlySet<AssetIdentity>`.
- [x] Add XML docs explaining:
  - cycle-safe expectations.
  - stream ownership (caller retains/disposing policy).
  - supported families for this phase and fallback behavior.
- [x] Register service in the existing app composition root.
  - Add interface-to-implementation binding for the concrete analyzer in the same release window.
  - Ensure registration lifetime aligns with existing analyzers/providers.
- [x] Add a compile-time verification task/target if project currently enforces interface registration parity.

### 3. Preview Cache Service
- [x] Modify `src/SRN.CC.Infrastructure/Cache/SqliteCacheService.cs`.
- [x] Add `preview_cache` table:
  - `source_fingerprint` BLOB
  - `locator` TEXT
  - `width` INTEGER
  - `height` INTEGER
  - `png_bytes` BLOB
  - `last_access_utc` TEXT
- [x] Add migration hook so existing installations with no `preview_cache` table create it on startup.
- [x] Add composite unique index on `(source_fingerprint, locator)` and size-appropriate query indices.
- [x] Implement `TryGetPreviewAsync(...)`:
  - returns preview hit/miss.
  - reads `width`, `height`, `png_bytes` in one query.
  - updates `last_access_utc` on cache hit.
- [x] Implement `SavePreviewAsync(...)`:
  - upsert semantics by composite key.
  - updates `last_access_utc`.
  - avoids partial writes on exceptions.
- [x] Update cache cleanup policy:
  - include preview table bytes in total tracked size.
  - use age/access-based eviction strategy consistent with existing cache behavior.
  - include bounded-batch delete loop to keep DB operations safe under pressure.

## Implementation notes
- Use the existing cache connection/disposal patterns in `SqliteCacheService`; do not introduce ad-hoc SQLite factories.
- Treat `source_fingerprint` as a stable binary hash for source identity to avoid locator collision across equivalent files in different packs.
- Keep all schema SQL in constants near other table definitions for one-time review.

## Exit Criteria
- [x] Build succeeds with updated package references in all consuming projects.
- [x] `preview_cache` table is created at runtime if absent.
- [x] Cache API methods return deterministic results and preserve existing cache behaviors.
- [x] No API breaking change in current preview/caching call sites.

## Notes
- Keep requirements from this phase independent of UI implementation.
- Preserve compatibility with existing cache startup and migration behavior.

---

> Reconciled against delivered code during Milestone 5 completion pass; see `docs/MILESTONE-5-3.md` for the Phase 3 completion summary.
