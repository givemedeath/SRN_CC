# Milestone 3: Resolution and Project Persistence

## Summary

Implement the headless workspace layer that converts Milestone 2 source snapshots into deterministic curated assets, resolves winners by source priority or validated pins, persists selections and project state, and survives restart, source relocation, and source changes.

Milestone 3 does not replace the synthetic Avalonia table, add preview providers, or implement HAK building. Its deliverable is a tested Core/Infrastructure API suitable for Milestone 4 UI composition.

## Public Contracts and State Model

- Add Core models:
  - `WorkspaceState`: ordered sources, latest snapshots, curated assets, selection state, project preferences, and read-only status.
  - `CuratedAsset`: identity, all occurrences, resolved occurrence, optional pin, selected state, hash state, and independent conflict flags.
  - `WinnerPin`: identity, source ID, locator, and required 32-byte SHA-256.
  - `SelectionState`: `DefaultSelected` plus sparse identity overrides.
  - `ResolutionStatus`: `Resolved`, `UnresolvedDuplicate`, `InvalidPin`, `Unavailable`, or `Unpackageable`.
  - Flags for cross-source collision, same-source duplicate, differing payloads, case-only naming difference, unreadable occurrence, and invalid pin.
  - `ChangedInputReport`: source additions/removals, availability transitions, fingerprint changes, identity additions/removals, winner changes, pin reattachments/invalidations, and selection changes caused by newly discovered identities.
- Add services:
  - `IWorkspaceResolver.ResolveAsync(sources, snapshots, pins, selectionState, cancellationToken)`.
  - `IWorkspaceService` for initial indexing, source reorder, rescan, relocation, pin/unpin, and selection operations.
  - `IProjectStore.LoadAsync`, `SaveAsync`, and `SaveAsAsync`.
  - `ISettingsStore.LoadAsync` and `SaveAsync`.
  - A source-reader dispatcher capable of opening an occurrence by source kind so hashing logic does not construct concrete readers directly.
- Keep Core independent of Infrastructure and UI. Put resolution rules and immutable state models in Core; hashing, indexing orchestration, path handling, and JSON persistence belong in Infrastructure.
- Use `AssetIdentity` fields explicitly in persisted arrays. Never serialize identities or locators as composite dictionary keys.

## Resolution and Curation Behavior

1. Group valid, packageable occurrences by `AssetIdentity`; retain every occurrence and sort sources by ascending `PriorityOrdinal`, then source ID as a deterministic tie-breaker. Normalize ordinals to contiguous `0..n-1` after reorder.
2. Without a pin, choose the highest-priority available source containing the identity:
   - One occurrence in that source resolves immediately.
   - Multiple occurrences trigger lazy SHA-256 hashing only for that winning source’s occurrences.
   - If every occurrence hashes successfully and all hashes match, select the lowest deterministic locator: HAK entry index numerically or folder relative path ordinally.
   - Differing hashes, hash/read failures, or ambiguous occurrences leave the identity unresolved. Never fall through to a lower-priority source.
3. A pin overrides source priority:
   - Validate source ID, identity, locator, readability, and payload SHA-256.
   - Try the exact locator first; if its hash does not match, search the same source and identity for exactly one occurrence with the stored hash.
   - Reattach to that unique occurrence and return the updated locator.
   - Zero or multiple matching occurrences make the pin invalid. Invalid pins remain visible and block resolution; never silently revert to automatic priority.
4. Cache computed hashes in the in-memory workspace by source ID, identity, locator, size, and current source fingerprint. Invalidate them when that source is rescanned, relocated, becomes unavailable, or receives a different fingerprint. Do not persist general collision hashes as source-integrity evidence.
5. Derive independent diagnostics and flags:
   - Cross-source collision when an identity occurs in multiple sources.
   - Same-source duplicate when it occurs more than once in any source.
   - Case-only difference when original CP1252 resrefs differ but canonical identity matches.
   - Payload conflict only after relevant occurrences have differing hashes.
   - Read/hash failures must identify the affected source and locator.
6. Selection behavior:
   - New resolved/packageable identities follow `DefaultSelected`; unresolved or unpackageable identities are never effectively selected.
   - A sparse override applies only when its value differs from the default.
   - Per-identity include/exclude adds or removes an override as appropriate.
   - “Include All” sets the default true and clears overrides; “Exclude All” sets it false and clears overrides.
   - Filtered bulk operations modify only matching identities and compact redundant overrides.
   - Preserve overrides for temporarily missing identities so a later rescan can restore user intent.
7. Rescan all sources independently, preserving source IDs and last valid snapshots:
   - An unavailable or failed scan marks the source unavailable but must not destroy its prior cached metadata.
   - Re-resolve the complete workspace only after all requested scans finish successfully or return an unavailable result.
   - Cancellation leaves the prior `WorkspaceState` unchanged.
   - Produce a deterministic `ChangedInputReport` by comparing pre/post source state and resolution outcomes.
8. Relocation accepts a source ID and candidate path:
   - Require the same source kind and a successful scan before committing the new path.
   - Preserve source ID, priority, selection state, and pins.
   - Commit the relocation even when the fingerprint differs, but surface it as changed input and revalidate all affected pins.
   - Invalid candidates leave the existing workspace unchanged.

## Project and Settings Persistence

- Implement schema-1 `.srnccproj` JSON with:
  - `schemaVersion`, ordered sources, source IDs/kinds, path representation, stored fingerprints, selection default and overrides, pins, output settings, filters, and comparison preferences.
  - Source paths represented as `{ kind: "relative"|"absolute", value }`. Save paths relative to the project directory when they are contained by it; otherwise use normalized absolute paths. Recalculate paths on Save As.
  - Fingerprints represented by source kind, algorithm version, and lowercase hexadecimal digest.
  - Locators represented as tagged objects for HAK entry index or folder relative path.
  - Identity-bearing collections represented as arrays with explicit `resref` and numeric `resourceType`.
- Loading schema 1:
  - Validate required fields, duplicate source IDs, source kinds, ordinals, identities, hashes, and locator shapes.
  - Resolve relative paths against the project file’s directory.
  - Preserve unknown JSON properties recursively by retaining the parsed `JsonNode` tree and overlaying changed known values during save.
  - Load stored filters and comparison preferences even though the Milestone 3 UI does not consume them.
  - Index/rescan sources and then apply stored selections and pins; stored fingerprints are comparison data, not trusted current state.
- Newer project schemas:
  - Parse enough metadata to open the project as read-only without discarding its JSON.
  - Disable mutation, Save, and future Build capability through explicit state flags.
  - Permit Save As only if it preserves the original newer-schema document byte-for-byte; do not downgrade it.
- Malformed or unsupported project data returns actionable diagnostics and never modifies or quarantines the project file.
- Save writable projects atomically:
  - Serialize deterministic, indented UTF-8 JSON with stable property and collection ordering.
  - Write a unique sibling temporary file, flush it durably, and replace/move it over the destination.
  - On failure, retain the previous project and remove only the operation’s temporary file when possible.
- Implement schema-1 `%LOCALAPPDATA%\SRN.CC\settings.json` with injectable paths for tests. Persist only application-level values: last project path, recent project paths, and optional NWN install override.
- Missing settings produce defaults. Malformed or unsupported settings are closed, renamed with one UTC corruption suffix, and replaced with defaults; include any same-operation temporary file in cleanup. Project-specific filters, selections, pins, and source order must not leak into settings.
- Use only framework JSON support; add no NuGet dependencies.

## Implementation Sequence

1. Create `codex/milestone-3` from the current Milestone 2 HEAD and run the existing master verifier as the regression baseline.
2. Add Core identities, resolution results, pin, selection, change-report, and service contracts with immutable collections or defensive copies.
3. Implement the source-reader dispatcher, streaming SHA-256 service, deterministic resolver, and hash cache.
4. Implement workspace orchestration for initial load, reorder, rescan, relocation, pinning, and selection mutation using copy-on-success state transitions.
5. Implement project DTO/JSON translation, recursive unknown-field preservation, schema handling, path conversion, validation, and atomic saves.
6. Implement settings persistence and corruption quarantine.
7. Add Milestone 3 evidence documenting contracts, schema examples, recovery behavior, controlled move/change results, and verification run ID. Update verification labels/artifacts to “Milestones 1–3” without weakening prior gates.

## Test and Acceptance Plan

- Resolution tests:
  - Priority ordering, unavailable higher-priority sources, cross-source collisions, and deterministic tie-breaking.
  - Identical same-source duplicates, differing duplicates, zero-byte payloads, read/hash failures, and cancellation.
  - Verify only winning-source duplicates are lazily hashed.
  - Case-only names and distinct CP1252 identities.
- Pin tests:
  - Exact match, priority override, changed payload, missing source, missing locator, unique hash reattachment, ambiguous hash reattachment, unreadable occurrence, and unpinning.
  - Invalid pins remain blocking and never auto-resolve.
- Selection tests:
  - Defaults, sparse overrides, override compaction, include/exclude all, filtered bulk changes, new identities after rescan, temporarily missing identities, and unresolved identities.
- Workspace tests:
  - Reorder changes winners without changing source IDs.
  - Successful and failed rescans, cancellation rollback, unavailable-source recovery, relocation with same/different fingerprint, and deterministic changed-input reports.
  - Concurrent requests are serialized or rejected so no partial state is observable.
- Project tests:
  - Full schema-1 round trip, deterministic serialization, relative/absolute paths, Save As path recalculation, all locator variants, stored fingerprints, pins, filters, comparison preferences, and output settings.
  - Recursive preservation of unknown root, object, and array-item properties.
  - Missing/invalid fields, duplicate source IDs, malformed hashes, corrupt JSON, atomic replacement failure, and preservation of the prior file.
  - Newer schema opens read-only with mutation/Save/Build disabled and cannot be downgraded.
- Settings tests:
  - Defaults, round trip, injected path, corrupt/unsupported quarantine, side-effect-free load failure, and recent-path normalization.
- Controlled acceptance scenario:
  1. Index at least two fixtures with priority and duplicate conflicts.
  2. Resolve, pin a non-default occurrence, change selections, and save.
  3. Restart from persisted files and reproduce source order, filters, selections, winners, and pin state.
  4. Move one source and relocate it while preserving its source ID and valid pin.
  5. Change a pinned payload and rescan; confirm the changed-input report and invalid blocking pin.
  6. Restore the matching payload under a unique new locator; confirm deterministic pin reattachment.
- Retain all Milestone 1–2 portable tests, architecture checks, locked restore, audits, Release build, self-contained publish audit, and launch smoke. Corpus tests remain opt-in and must prove Milestone 3 resolution over the existing 117-HAK/187,943-occurrence index without modifying corpus data.

## Assumptions

- Milestone 2 at the current detached HEAD is the implementation baseline; implementation begins by creating `codex/milestone-3`.
- Priority ordinal `0` is highest priority.
- The existing fingerprint remains a change detector, not proof that payload bytes are unchanged.
- Milestone 3 exposes headless APIs only; the real curation UI, comparison slots, previews, build pipeline, publication, and journal recovery remain later milestones.
- Unknown HAK resource types remain packageable; unknown folder extensions remain diagnostic-only and do not enter resolution.
