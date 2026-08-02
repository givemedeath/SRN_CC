# Complete Milestones 1 and 2

## Summary

Milestone 1 is already implemented on `main`. The current baseline restores in locked mode, builds Release successfully, and passes all 83 portable/headless tests. Preserve it as a regression gate instead of rebuilding it.

Milestone 2 will deliver a headless, production-ready indexing vertical slice:

- Defensive HAK and KEY/BIF parsing with independently owned bounded streams.
- Recursive loose-folder indexing and canonical resource-type mapping.
- Stable source, occurrence, locator, fingerprint, progress, and diagnostic contracts.
- Fingerprint-driven SQLite caching with corruption recovery and LRU enforcement.
- Windows NWN:EE install discovery and `nwn_base.key` lookup.
- Exact corpus acceptance against 117 HAKs and 187,943 entries.

Resolution, project persistence, curation UI integration, previews, and final HAK publication remain Milestones 3–5.

## Public Contracts and Behavioral Decisions

- Add immutable Core types:

  - `AssetSource`: `Guid Id`, HAK/folder kind, full path, priority ordinal where zero is lowest, availability, and optional fingerprint.
  - `AssetOccurrence`: valid `AssetIdentity`, source ID, typed locator, original name/raw resref, size, validation state, optional extension metadata, and nullable SHA-256. Milestone 2 never computes payload hashes.
  - `OccurrenceLocator`: `HakEntryLocator(int EntryIndex)` or `FolderFileLocator(string NormalizedRelativePath)`.
  - `IndexedAssetRecord`: wraps a valid occurrence or a non-packageable diagnostic row. This keeps unknown folder extensions visible without making `AssetIdentity` nullable.
  - `SourceIndexSnapshot`: source, fingerprint, ordered records, diagnostics, cache-hit flag, and scan statistics.
  - `SourceFingerprint`: typed HAK/folder value with algorithm version and an immutable 32-byte digest.
  - `IndexProgress`: source ID, phase, completed count, nullable total, and current item.
  - Stable diagnostic codes rather than tests or UI depending on exception messages.

- Add these interfaces:

  - `IResourceTypeRegistry.TryGetType(extension, out ushort)` and `TryGetExtension(type, out string)`.
  - `IAssetSourceReader.GetFingerprintAsync`, `IndexAsync`, and `OpenOccurrenceAsync`.
  - `IAssetIndexService.IndexAsync`, which selects the reader, checks the cache, detects source drift, and commits complete snapshots.
  - `IBaseGameResourceCatalog.Contains` and `OpenAsync` for KEY/BIF fallback resources.

- Resource-type behavior:

  - Wrap the vendored NWN:EE type table; type `2078` maps only to `lod`.
  - Folder extensions are case-insensitive and exclude the leading dot.
  - Accept canonical extensions and decimal `.NNNN`/`.rNNNN` forms containing 1–5 digits that parse to `0..65535`; leading zeros are allowed.
  - Unknown nonnumeric extensions become visible, unpackageable diagnostic records.
  - HAK entries accept every `ushort` type, including unregistered types and `65535`.

- Source error policy:

  - Missing or inaccessible roots return an unavailable snapshot and do not replace a valid cache entry.
  - A structurally invalid HAK or invalid HAK resref invalidates that source because no trustworthy locator/identity set can be produced.
  - Folder file-level failures become diagnostic records while other files continue indexing; inability to enumerate the root invalidates the source.
  - Cancellation and detected source drift never commit a partial snapshot.
  - Retry one complete scan after source drift; a second drift returns a changed-source failure.

## Implementation Changes

1. **Protect and extend the Milestone 1 baseline**

   - Create `codex/milestones-1-2` from `main`; the current worktree is detached.
   - Run the existing master verifier before substantive changes and retain its evidence.
   - Add only `Microsoft.Data.Sqlite` 10.0.10 to Infrastructure. Centrally pin the resolved SQLitePCLRaw family to 2.1.12 so locked restore does not select the vulnerable/deprecated 2.1.11 native package. The direct package and dependency floor are documented by [NuGet](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.10).
   - Regenerate all affected lockfiles once, then restore only in locked mode.
   - Extend dependency, license, vulnerability, notice, and publish policies for the exact resulting SQLite graph and native `e_sqlite3.dll`. Do not add DI, logging, preview, or rendering packages in this milestone.
   - Update the master verification script and CI artifact naming to cover “Milestones 1–2” while retaining every Milestone 1 audit, publish, and smoke gate.

2. **Harden Formats and bounded I/O**

   - Replace the current shared-stream HAK payload wrapper with a reusable owned bounded read stream that supports synchronous/asynchronous reads and seeking, rejects writes, cannot seek outside its range, and disposes its underlying file handle.
   - Preserve the stream-based parser for fixtures, but make production payload opening path-based so every call receives an independent file stream.
   - Expand HAK validation to cover:

     - Exact `HAK `/`V1.0` header and fixed 160-byte header bounds.
     - Localized-string count/size consistency and every localized record.
     - Checked 64-bit table arithmetic, explicit metadata allocation budget, resource-ID bounds, and file bounds.
     - Header/localized/key/resource table non-overlap.
     - Payloads not overlapping metadata or partially overlapping each other.
     - Exact shared payload ranges as valid; partial overlaps as invalid.
     - Zero-byte resources, nonsequential resource IDs, duplicate identities, and all `ushort` types.
     - Duplicate identities retained as separate entries keyed by original key-table index.

   - Keep the deterministic writer’s ordering by numeric type then canonical CP1252 resref. Add async progress/cancellation, pooled buffering, exact payload-size enforcement, and reject estimated output at or above `Int32.MaxValue`. The writer must not dispose caller-owned payload streams.
   - Extend the vendored resource table with non-ambiguous `Try...` methods so type `65535` is not confused with the legacy `Invalid` sentinel.
   - Adapt the vendored KEY/BIF readers to preserve raw CP1252 resrefs and expose bounded resource streams without loading BIF payloads into memory. Keep the existing bounded byte-array helper only for compatibility.
   - Mark every changed vendored file as `adapted` in the source manifest, record the reason/review reference, update its SHA-256, and add an adaptation review. Preserve the upstream commit/blob identities and existing portable tests.

3. **Implement source readers, fingerprints, and orchestration**

   - `HakAssetSourceReader` parses only the archive tables and creates one occurrence per key-table entry; locators use the key-table index, not resource ID.
   - `FolderAssetSourceReader` recursively indexes regular files, never follows reparse points, normalizes relative locators to `/`, and verifies the resolved full path remains under the source root before opening it.
   - Sort folder records ordinally by normalized relative path. Preserve case, accents, spaces, punctuation, original extension, and nested-path provenance.
   - HAK fingerprints contain length, last-write UTC ticks, and SHA-256 over the fixed header plus localized/key/resource table bytes in semantic order.
   - Folder fingerprints hash an algorithm-version prefix followed by every ordinally sorted relative path, mapping/validation status, mapped type, size, and last-write UTC ticks using length-prefixed encoding.
   - Fingerprints deliberately exclude payload bytes. Document and test that they are change detectors, while Milestone 4 builds will recompute selected payload hashes.
   - Check file metadata before and after HAK scanning; compute a second folder inventory after a cache miss. Retry once if the source changes.
   - Report monotonic progress. HAK total is known from the header; folder total may remain null until inventory enumeration completes.
   - `OpenOccurrenceAsync` must reopen the current source, validate locator/range/expected size, and return an independently owned seekable stream.

4. **Implement SQLite cache-v1**

   - Store the cache at `%LOCALAPPDATA%\SRN.CC\cache-v1.sqlite`, with an injectable path and size limit for tests.
   - Schema version 1 contains:

     - `schema_info`.
     - `source_snapshots`: fingerprint key, kind/components, timestamps, record count, logical byte size, and last-access UTC.
     - `asset_records`: snapshot ID, stable sequence, locator fields, original/raw/canonical resref data, nullable type, original extension, size, payload offset where applicable, validation code, and diagnostic.
     - Supporting indexes for fingerprint lookup and LRU order.

   - Cache snapshots are source-ID neutral. On load, materialize records with the caller’s `AssetSource.Id`, allowing identical source instances to reuse metadata without collapsing their provenance.
   - Configure WAL, foreign keys, `synchronous=NORMAL`, a five-second busy timeout, pooled short-lived read connections, and a single `SemaphoreSlim`-serialized writer. Do not use SQLite shared-cache mode with WAL, following [Microsoft’s connection guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings).
   - Insert or replace an entire snapshot in one transaction. Cancellation, parser failure, or drift rolls back the transaction.
   - Enforce a 2-GiB logical LRU after writes, excluding the entry currently being written. Use incremental auto-vacuum and WAL checkpointing so eviction also controls physical growth; tests inject a small limit.
   - On failed `quick_check`, unreadable schema, unsupported cache schema, or migration failure: close/clear pools, rename the database plus WAL/SHM sidecars with one UTC corruption suffix, and create a clean cache. Cache recovery must never modify source files.
   - A cache hit still requires a current fingerprint; path or timestamp alone is insufficient.

5. **Implement base-game KEY/BIF lookup and install discovery**

   - `NwnInstallLocator` accepts an explicit install root first. A valid root must contain `data\nwn_base.key`.
   - An invalid explicit override returns a diagnostic and does not silently choose another installation; auto-discovered candidates may still be returned as suggestions.
   - Without an override, probe:

     - Default Steam location.
     - Steam paths from HKCU/HKLM and `libraryfolders.vdf`, using app ID `704450`.
     - GOG game registry entries under 32/64-bit HKCU/HKLM views whose game name identifies Neverwinter Nights.
     - Every candidate is normalized, deduplicated, and validated by `data\nwn_base.key`; registry and file-access failures become diagnostics rather than exceptions.

   - `BaseGameResourceCatalog` loads only `nwn_base.key` in this milestone, builds an identity-to-KEY-location dictionary, lazily caches BIF metadata, constrains KEY-declared BIF paths to the install root, and opens payloads as independent bounded streams.
   - Curated-source precedence and dependency resolution are not implemented yet; this catalog is only the base-resource lookup foundation for later milestones.

6. **Evidence and documentation**

   - Add a Milestone 2 implementation/evidence record describing APIs, schema, package review, cache location, corpus machine, timings, memory, and the successful verification run ID.
   - Keep generated SQLite databases, test results, performance JSON, corpus paths, and source assets under ignored artifact/local-data locations.
   - Do not replace the Milestone 1 synthetic UI table with real indexed data; UI composition begins in Milestone 4.

## Test and Acceptance Plan

- Portable HAK tests cover malformed signatures/versions, localized strings, overflow/allocation bombs, table/payload bounds, metadata overlap, partial versus exact shared ranges, invalid resource IDs/resrefs, unknown types, zero-byte entries, duplicate entry indexes, independent stream ownership, cancellation, deterministic writing, short/long payload streams, and the `Int32.MaxValue` boundary.
- Folder tests cover recursion, canonical and numeric extensions, `.65535`, unknown extensions, CP1252 validation, case-only and nested duplicates, path containment, reparse-point skipping, inaccessible files, deterministic ordering, progress, and cancellation.
- Fingerprint tests prove metadata/table changes invalidate HAK fingerprints, inventory changes invalidate folder fingerprints, payload-only changes with restored metadata do not, and scans detect concurrent source changes.
- Cache tests cover cold write/warm hit equivalence, source-ID rematerialization, atomic cancellation, serialized writers with concurrent readers, fingerprint invalidation, schema rebuild, corruption quarantine, sidecar handling, and LRU eviction under an injected low limit.
- KEY/BIF tests cover raw CP1252 resrefs, missing/malformed tables, missing BIFs, path traversal, lazy metadata reuse, bounded stream reads/seeks, manual install override, and mocked Steam/GOG candidates. An opt-in local smoke lookup opens a representative base-game resource such as `dag01_a01_01.wok`.
- Existing Milestone 1 tests, vendored portable tests, architecture checks, dependency audits, publish audit, and hidden application launch smoke must remain green.
- Corpus acceptance uses `SRNCC_CORPUS_ROOT` and requires:

  - Exactly 117 HAK instances and 187,943 occurrences without extraction.
  - Exactly 212 type-2078 entries mapped to `lod`.
  - Exactly four archives with internal duplicate identities: two `nwncq.hak` instances, `udp2_off_int.hak`, and `udp2_off_pl.hak`.
  - Duplicate occurrences retain distinct HAK entry-index locators.
  - No corpus file, timestamp, directory membership, or payload is modified.
  - Three cold application-cache runs, each using a fresh cache database but normal OS caching; recorded median under 10 seconds on the reference machine.
  - Warm-cache results exactly equal cold results.
  - After indexing, disposal, GC, and 30 seconds idle, private working set below 750 MiB.

- Final completion requires a clean worktree after generated artifacts are ignored, plus successful runs of:

  - Standard locked restore, audits, Release build, portable tests, self-contained publish, publish audit, and launch smoke.
  - Corpus verification with `SRNCC_RUN_CORPUS=1`, `SRNCC_REQUIRE_CORPUS=1`, and `SRNCC_RUN_PERF=1`.
  - Vendored-source audit showing all unchanged files still verbatim and every KEY/BIF/resource-table adaptation explicitly reviewed.

## Assumptions

- Windows `win-x64`, .NET SDK 10.0.302, runtime 10.0.10, Avalonia 12.1.0, and the existing MIT licensing decision remain fixed.
- The corpus path is always supplied through `SRNCC_CORPUS_ROOT`; no local absolute path is committed.
- Inputs are read-only and never extracted or modified.
- Cache data is disposable and may be rebuilt; project data does not exist until Milestone 3.
- Unknown HAK types remain packageable, while unknown folder extensions remain diagnostic-only.
- Base-game resources satisfy future lookup needs but are never imported or cached as payload blobs.
