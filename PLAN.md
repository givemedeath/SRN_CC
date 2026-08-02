# NWN:EE Asset Curator — Validated Executable Build Plan

## Summary and validated corrections

Build a Windows-first .NET 10/Avalonia application that imports HAKs and loose-resource folders, resolves conflicts, compares up to three candidates, previews resources and dependencies, and publishes one verified HAK plus a deterministic provenance manifest.

Validated baseline:

- The repository contains no Git metadata or application code; `PLAN.md` is empty. Bootstrap is required.
- .NET SDK `10.0.302` is installed.
- The corpus contains 117 HAK files, 187,943 entries, 15.175 GiB of HAK data, and 17.148 GiB overall.
- Four HAK file instances contain duplicate identities: both copies of `nwncq.hak`, `udp2_off_int.hak`, and `udp2_off_pl.hak`.
- Pin [Avalonia 12.1.0](https://www.nuget.org/packages/Avalonia/12.1.0), not nonexistent `12.1.1`. Core [TableView](https://docs.avaloniaui.net/api/avalonia/controls/tableview) supports read-only tabular presentation and ListBox-based multi-selection. Do not reference TreeDataGrid, which is [Avalonia Pro](https://docs.avaloniaui.net/controls/data-display/structured-data/treedatagrid).
- The referenced [SWLOR commit `8202faa…`](https://github.com/givemedeath/SWLOR_NWN/commit/8202faa203eddd6f4972104d22ea5740e23f20f7) is valid, MIT-licensed, and contains the standalone dependency-free `SWLOR.NWN.Formats`; the local adjacent checkout is merely stale.
- Resource type `2078` is canonically `.lod`, not BFX. Do not invent a BFX mapping.
- Use a first-party streaming HAK writer. The official `nwn_erf 2.1.2` encodes non-ASCII staging filenames as UTF-8 rather than CP1252, buffers each resource while packing, and can skip invalid directory entries while returning exit code zero.
- Keep `nwn_erf` only as an opt-in development compatibility oracle; it is not a runtime or release dependency.
- Known-format preview/parser failures are warnings. Builds block only on container, identity, resolution, source-integrity, packaging, or verification failures.

## Architecture and behavioral contracts

### Solution and dependency boundary

Initialize Git on `main`, without a remote, and create:

- `SRN.CC.App`: Avalonia shell, views/view models, composition root, Windows entry point, and OpenGL controls.
- `SRN.CC.Core`: identities, sources, occurrences, resolution, conflicts, pins, selections, project-neutral build contracts.
- `SRN.CC.Formats`: adapted SWLOR readers plus first-party HAK reader/writer.
- `SRN.CC.Infrastructure`: indexing, SQLite, project/settings storage, KEY/BIF lookup, logging, build orchestration, verification, and publication.
- `SRN.CC.Preview`: preview providers, dependency analysis, CPU-side render data, and scene preparation.
- `SRN.CC.Tests`: portable unit, integration, view-model, format, and headless UI tests.
- `SRN.CC.CorpusTests`: opt-in corpus, performance, and OpenGL smoke tests.

Dependency direction is `App → Infrastructure/Preview/Core`, `Infrastructure/Preview → Core/Formats`, while `Core` and `Formats` remain independent of UI and each other.

Pin SDK `10.0.302` with `rollForward: latestPatch`, central package management, nullable references, deterministic builds, warnings as errors, and lock files. Pin:

- Avalonia, Desktop, Fluent Theme, Diagnostics, and Headless.NUnit `12.1.0`.
- CommunityToolkit.Mvvm `8.4.2`.
- Microsoft.Extensions.DependencyInjection and Logging `10.0.10`.
- Microsoft.Data.Sqlite `10.0.10`.
- Silk.NET.OpenGL `2.23.0`, Pfim `0.11.4`, NAudio `2.3.0`.
- NUnit `4.6.1`, NUnit3TestAdapter `6.2.0`, Microsoft.NET.Test.Sdk `18.8.1`.

Vendor `SWLOR.NWN.Formats`, its portable tests, provenance, and review attestations from `8202faa…`. Maintain a source manifest containing original path, commit, blob SHA, destination, and adaptation status. Later renderer intake uses the same pin and only first-party render/viewport files required for previews. No Radoub source, history, namespaces, assemblies, binaries, or project references may be imported.

The compliance check must inspect direct/transitive NuGet licenses, project references, resolved assemblies, vendored-source manifests, and publish output. Reject copyleft and unknown licenses; allow only explicitly reviewed permissive/public-domain dependencies.

### Identity, sources, conflicts, and public contracts

Register `CodePagesEncodingProvider` and define canonical identity as:

- Resref encodes strictly to Windows-1252 in 1–16 bytes.
- Reject NUL, `/`, and `\`; retain otherwise legal spaces, accents, punctuation, and underscores.
- Canonicalize only ASCII `A–Z` bytes to lowercase. Do not use Unicode-wide lowercasing.
- `AssetIdentity` equality is ordinal over the canonical resref plus `ushort ResourceType`; extension is metadata only.
- Preserve the original bytes/text on each occurrence for display and case-only diagnostics.

Use the SWLOR/NWN:EE resource-type table. Folder indexing accepts canonical extensions plus `.NNNN` and `.rNNNN` numeric forms. Unknown nonnumeric extensions remain visible diagnostic rows and are unpackageable. HAK entries accept all `ushort` types, including unregistered types.

Stable contracts:

- `AssetSource`: GUID, HAK/folder kind, path, ordinal where 0 is lowest priority, availability, fingerprint.
- `AssetOccurrence`: identity, source ID, HAK-entry-index or normalized relative-path locator, original name/location, size, validation state, optional SHA-256.
- `CuratedAsset`: occurrences, resolved/pinned winner, selected state, hash state, and independent conflict flags.
- `WinnerPin`: identity, source ID, locator, and required payload SHA-256.
- `PreviewRequest`/`PreviewResult`: occurrence, family, immutable payload, dependencies, diagnostics, and cancellation.
- Immutable `BuildPlan`, `BuildArtifact`, `BuildVerificationReport`, and `PublicationResult`.
- `IAssetSourceReader`, `IWorkspaceResolver`, `IResourceTypeRegistry`, `IPreviewProvider`, `IDependencyAnalyzer`, `IProjectStore`, `IAssetPacker`, `IBuildVerifier`, and `IArtifactPublisher`.

HAK indexing must validate signature/version, localized strings, counts, all table arithmetic, key resource IDs, table/file bounds, resource ranges, allocation ceilings, and partial range overlaps. Duplicate identities remain separate occurrences by entry index. `OpenOccurrenceAsync` returns an independently owned, bounded, seekable stream that cannot escape its validated range. Never extract an archive to index or preview it.

Fingerprints are change detectors, not payload evidence:

- HAK: length, last-write UTC, and SHA-256 over the header/localized/key/resource table regions.
- Folder: SHA-256 over sorted relative path, mapping/validation status, size, and last-write UTC.
- Build-time payload hashes are always recomputed.

Resolution rules:

1. Highest-priority available source wins automatically; the UI displays highest priority at the top.
2. A valid pin overrides source priority.
3. One occurrence within the winning source resolves directly.
4. Same-source duplicates with identical hashes resolve to the lowest deterministic locator.
5. Differing hashes or read/hash errors remain unresolved until a readable occurrence is pinned.
6. Missing, changed, or ambiguous pins remain invalid; never fall back silently.
7. Pin reattachment tries exact source/identity/locator with matching hash, then a unique source/identity/hash match.
8. Hash only multi-occurrence groups during browsing; hash every selected payload during builds.

All resolved packageable identities start selected. Persist `selectionDefault` and sparse per-identity overrides. New identities follow the default; filtered include/exclude writes only necessary overrides; “Exclude All” sets the default false and clears overrides.

### Persistence, UI, previews, and dependencies

Use schema-versioned `.srnccproj` JSON containing sources, path kind/value, priorities, fingerprints, selection state, pins, output settings, filters, and comparison preferences. Use arrays with explicit `{resref, resourceType}` fields rather than encoded composite dictionary keys. Preserve unknown fields recursively for schema 1. Newer schemas open read-only with Save and Build disabled. Save atomically using a sibling temporary file, durable flush, and replace/rename.

Paths and storage:

- Cache: `%LOCALAPPDATA%\SRN.CC\cache-v1.sqlite`, WAL, serialized writes, 2-GiB LRU, compressed PNG thumbnails rather than GPU/decoded handles.
- Settings: `%LOCALAPPDATA%\SRN.CC\settings.json`.
- Rolling JSON-line logs: `%LOCALAPPDATA%\SRN.CC\Logs`, ten 10-MiB files.
- Corrupt cache/settings files are renamed with a timestamp and rebuilt; corrupt projects are left untouched and reported.

The single-window shell contains:

- Left: source stack, highest priority first, availability, reorder, relocation, and rescan.
- Center: `TableView` with `VirtualizingStackPanel`, `SelectionMode="Multiple"`, lightweight rows, selection model, view-model sorting/filtering, and selected/resref/type/winner/origin/hash/size/diagnostic columns.
- Right: one-to-three stable preview slots.
- Bottom: progress, cancellation, warnings, and expandable operation log.
- Top: New, Open, Save, Save As, Rescan, Build, and Settings.

Comparison behavior:

- Occurrence mode compares up to three occurrences of one identity.
- Resolved mode compares winners of up to three selected identities.
- Newly selected items fill empty slots in selection order. Overflow selection remains selected for bulk work.
- Deselecting a slotted item promotes the oldest selected overflow item; explicit Replace Slot can override this.
- Switching comparison mode clears slot assignments but preserves applicable grid selection.
- Pin actions hash the chosen occurrence before saving the pin.
- Debounce previews by 150 ms, cancel superseded requests, and allow at most three concurrent preview jobs.
- Link image navigation and compatible 3D cameras by default; link audio position only when every visible slot is audio; leave text/tree/table scrolling unlinked.

Preview order:

1. Metadata and bounded hex fallback for every readable occurrence.
2. TGA/DDS/PLT images with alpha, mip metadata, dyes, zoom, and linked navigation.
3. Text and 2DA/MTR/TXI/SET/INI/GUI/shader views with UTF-8/CP1252 detection.
4. Streaming WAV/BMU playback; validate and remove the eight-byte BMU header.
5. GFF-family/ITP trees and SSF structure.
6. MDL metadata/dependency parsing.
7. Static MDL and WOK/PWK/DWK rendering in the advanced milestone.

Safety budgets are 1 MiB for hex, 8 MiB for text, 4096×4096/64 MiB RGBA per image, and 64 MiB parser allocations unless a stricter imported reader limit applies. Audio streams instead of loading whole files. Preview limits produce diagnostics, not build blockers.

Dependencies cover MDL textures/materials/supermodels, MTR maps, same-resref TXI, SET models/WOKs, and same-resref WOK/PWK/DWK companions. Resolve curated sources first and base-game KEY/BIF second. Base resources satisfy previews but are never auto-packaged. “Add available dependencies” computes a cycle-safe transitive closure, previews count/bytes/unresolved assets, and selects only imported identities after confirmation.

### First-party build, verification, and publication

At build start:

- Freeze selected identities, occurrence locators, fingerprints, known hashes, sizes, output settings, and source order into `BuildPlan`.
- Block invalid identities, unresolved winners/pins, unavailable sources, source drift, unreadable/truncated payloads, output/source path overlap, insufficient disk space, or estimated HAK size at/above `Int32.MaxValue`.
- Treat dependency gaps and known-format preview/parser failures as warnings.
- Acquire read-sharing source handles that prevent writes/deletes during the build, then recheck fingerprints.
- Reject output paths equal to any HAK source or located inside a folder source.

The first-party writer:

- Writes directly to a unique temporary HAK in the destination directory; no payload extraction or filename staging.
- Emits `HAK `/`V1.0`, zero localized strings, zero build year/day/description, and zeroed reserved bytes.
- Sorts entries deterministically by numeric type then canonical CP1252 resref bytes.
- Writes raw CP1252 resrefs padded to 16 bytes and sequential resource IDs.
- Streams each bounded occurrence once while computing SHA-256 and enforcing its expected size and any pin/collision hash.
- Supports zero-byte and unregistered-type resources directly.
- Produces byte-identical HAKs for identical selections and payloads.
- Blocks rather than splitting output that exceeds the legacy single-HAK limit.

The verifier independently reopens the temporary HAK and requires a valid structure, exact unique identity set, exact sizes, and payload SHA-256 equality. It also recomputes the full HAK hash.

Write `<basename>.srncc-manifest.json` with deterministic property/resource ordering and only `generatedUtc` varying. Include schema/app versions, HAK SHA-256, each identity/type/size/hash, source display label, permitted project-relative provenance, and pin status. Never emit an outside-project absolute source path by default.

Publication uses a durable journal with `Prepared`, `BackedUp`, `HakReplaced`, `ManifestReplaced`, and `Committed` states:

1. Create both verified temporary files in the destination directory.
2. Preflight destination locks and create recoverable backups.
3. Replace the HAK first, then the manifest as the commit marker.
4. Reopen the final pair and verify the manifest’s HAK hash.
5. Mark committed, then remove backups and journal.
6. On failure or startup recovery, roll back both files. Once commit begins, cancellation is deferred until commit or rollback completes.

The release has no `nwn_erf` dependency. An opt-in downloader may fetch [2.1.2](https://github.com/niv/neverwinter.nim/releases/tag/2.1.2) to ignored local storage, verifying:

- Windows x64 archive SHA-256 `b00501cc57adc63392f17d460d712edcdcbe35cb37f7d7257ab23806ed86aed1`.
- `nwn_erf.exe` SHA-256 `134c3c3f08dd21caf6bee932bfa731d2371bccae93e434d169a4a4d22f1bc8d`.
- Required `sqlite3_64.dll` SHA-256 `e0c5df0f6142c9aa8dd6a4c9cfe56fae3902373d8eb2f71827164ad7856bcf74`.

Only those two files enter the ignored oracle directory; the release archive and unrelated/GPL compiler binaries never enter the repository or product.

## Execution milestones

1. **Bootstrap and compliance**
   - Initialize Git, solution/projects, build settings, ignore rules, MIT license, notices, execution/work logs, and verification/publish scripts.
   - Pin packages and lock files; add dependency/source/publish audits.
   - Import the exact SWLOR formats snapshot and portable tests.
   - Prove CP1252 HAK round-trip and a 188k-row virtualized TableView/selection spike.
   - Gate: locked restore, build, tests, and self-contained `win-x64` shell publish succeed without `content`.

2. **Formats, indexing, and cache**
   - Implement identity encoding, resource registry, defensive HAK reader/writer, folder reader, bounded streams, progress/cancellation, fingerprints, and SQLite index persistence.
   - Adapt KEY/BIF readers and Windows NWN-install discovery with a manual override.
   - Gate: index all 117 HAKs and exactly 187,943 entries without extraction; preserve all four duplicate file instances and all 212 type-2078 entries.

3. **Resolution and project persistence**
   - Implement priority resolution, lazy collision hashing, flags, pins/rebinding, selection defaults/overrides, rescans, relocation, and changed-input reports.
   - Implement schema-1 project/settings persistence and newer-schema read-only behavior.
   - Gate: restart and controlled source move/change reproduce source order, filters, selections, pins, invalid pins, and unresolved states.

4. **Curation shell and verified build vertical slice**
   - Implement the main shell, virtualized table, filters/bulk selection, both three-slot comparison modes, metadata/hex previews, progress/cancellation, first-party build/verify, manifest, transactional publication, and recovery.
   - Gate: complete import → resolve/pin → save/reopen → rescan → build → verify → publish flow while preserving an existing output through injected failures.

5. **Basic previews and dependencies — curation MVP**
   - Add image, text/table, audio, GFF/ITP, SSF, and MDL-metadata providers plus dependency analysis and base-game fallback.
   - Enforce budgets, slot isolation, CPU-cache sharing, and linked navigation.
   - Gate: compare one-to-three occurrences/winners, inspect dependencies, add imported closure, and produce a verified HAK despite isolated preview failures.

6. **Advanced 3D previews**
   - Adapt only required first-party SWLOR render-neutral and Avalonia/Silk.NET viewport components.
   - Support ASCII/binary MDL geometry, transforms, static materials/textures, PLT/MTR/environment maps, transparency, lighting, camera controls, and WOK/PWK/DWK surfaces.
   - Create `OpenGlControlBase` only for visible 3D slots; GPU objects remain context-owned and disposable.
   - Gate: representative one-to-three-slot corpus previews survive unsupported features/context failures without leaks or cross-context handles.

7. **Release hardening**
   - Add startup cache/settings/journal/tool-capability checks, schema migration hooks, operator documentation, and release audit.
   - Publish a self-contained, non-single-file `win-x64` folder and ZIP containing only application files, licenses, and notices.
   - Gate: final acceptance flow and clean-machine smoke test pass; no local corpus, oracle, GPL/Radoub artifact, cache, project, or absolute source path appears in release output.

## Test and acceptance plan

- Identity tests: ASCII folding, CP1252 accents, 16-byte/17-byte boundaries, punctuation, NUL/separators, extension independence, and raw-byte HAK round trips.
- HAK tests: valid/duplicate keys, nonsequential resource IDs, shared ranges, truncated/overlapping tables, overflow, allocation bombs, unknown types, zero-byte entries, deterministic writing, and the `<2 GiB` boundary.
- Resolution tests: priority, unavailable sources, identical/conflicting duplicates, hash errors, case-only flags, pins, changed pins, reattachment, defaults, override compaction, and rescans.
- Persistence tests: relative/absolute paths, recursive unknown-field preservation, newer schema, atomic failure, corrupt cache/settings, and source relocation.
- UI tests: sorting/filtering, 188k rows, bulk selection, stable/overflow slots, replacement/promotion, mode switching, pinning, cancellation, and linked-state rules.
- Preview tests: valid/truncated/malformed/canceled/oversized inputs for every provider; one successful, one failed, and one canceled concurrent slot.
- Dependency tests: curated precedence, base fallback, missing references, cycles, closure size/count, unresolved dependencies, and no automatic base-game packaging.
- Build tests: source mutation, locked files, paths with spaces, CP1252 resrefs, unknown types, insufficient space, size-limit rejection, repeated byte-identical HAK output, and exact manifest hashing.
- Publication tests: inject failure at every journal state; verify pair rollback, startup recovery, lock handling, and preservation of the previous valid destination.
- Oracle tests: opt-in `nwn_erf -t` compatibility checks for ASCII fixtures/output only; never use its listing as the authoritative duplicate or CP1252 verifier.
- Corpus/performance tests use `SRNCC_CORPUS_ROOT`; `SRNCC_REQUIRE_CORPUS=1` fails if absent. Measure cold application-cache indexing—not OS disk-cache eviction—as median of three runs under 10 seconds on a recorded reference-machine baseline. After indexing and 30 seconds idle, private working set must remain below 750 MiB.
- Full 1.0 acceptance additionally requires representative model/walkmesh rendering and context-disposal tests; use semantic geometry/material assertions and adapter-dependent smoke tests, not cross-GPU pixel equality.

## Assumptions and defaults

- Output is exactly one HAK plus `<basename>.srncc-manifest.json`; multi-HAK splitting is out of scope.
- Inputs are read-only and never modified.
- Unknown resource types are opaque but packageable; unsupported folder extensions are not.
- Preview/parser failure and missing dependencies warn but do not block packaging.
- No content merging, renaming, editing, module modification, TLK generation, or automatic base-resource copying.
- Windows `win-x64` is the only supported/tested initial platform.
- The curation MVP ends after basic previews/dependencies and verified publication. Static model/walkmesh rendering is required for full 1.0.
