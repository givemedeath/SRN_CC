# Milestone 4: Curation Shell and Verified Build Vertical Slice

## Summary

Milestone 4 integrates the headless workspace foundation built in Milestone 3 with a fully functional Avalonia curation UI shell (`SRN.CC.App`), initial preview providers (`SRN.CC.Preview`), and an end-to-end verified, transactionally published first-party build pipeline (`SRN.CC.Infrastructure`).

## Deliverables & Components

### 1. Avalonia Curation Shell UI (`SRN.CC.App`)
- **Left Pane (`SourceStackView`)**: Priority ordering display, availability status indicators, source reordering (Move Up / Down), relocation, rescan, and source addition.
- **Center Pane (`AssetTableView`)**: Virtualized `TableView` backed by `VirtualizingStackPanel`, multi-selection, resref/type/origin/hash/size/diagnostic columns, text filtering, conflict filtering, and bulk selection controls (Include All / Exclude All).
- **Right Pane (`ComparisonPanelView`)**: 3-slot preview panel supporting both **Occurrence Mode** (comparing up to 3 occurrences of a single identity) and **Resolved Mode** (comparing winning occurrences of up to 3 selected identities). Supports selection overflow promotion, mode toggling, slot replacement, and instant occurrence pinning.
- **Bottom Bar & Log Drawer (`StatusBarView` & `OperationLogView`)**: Live progress bar, async cancellation, warning badges, and expandable operational log drawer.
- **Top Toolbar**: New Project, Open, Save, Save As, Rescan, Build HAK.

### 2. Preview Engine Foundation (`SRN.CC.Preview`)
- Core contracts: `PreviewRequest`, `PreviewResult`, `IPreviewProvider`, `PreviewFamily`.
- `MetadataPreviewProvider`: Formats identity, type, source, locator, size, hash, and validation state.
- `BoundedHexPreviewProvider`: Bounded hex dumper enforcing 1 MiB safety budget with offset, hex bytes, and CP1252/ASCII text columns.
- `PreviewEngine`: Preview debouncing (150ms), task cancellation, and max 3 concurrent job limiter.

### 3. First-Party Build, Verification & Transactional Publication (`SRN.CC.Infrastructure`)
- `BuildPlan`: Frozen build plan representation with preflight validation (blocks on invalid pins, unresolved winners, unavailable sources, output/source path overlap, disk space, or >=2 GiB size).
- `AssetPacker`: Wraps first-party `HakWriter` to stream payload streams directly into a temporary `.tmp` HAK file without extraction.
- `BuildVerifier`: Reopens `.tmp` HAK with `HakReader`, re-verifies header, key table sorting, sizes, payload SHA-256 hashes against `BuildPlan`, and overall HAK hash.
- `ProvenanceManifestGenerator`: Emits `<basename>.srncc-manifest.json` with deterministic ordering, app/schema versions, HAK hash, entry provenance (source label, relative locator, size, hash, pin status), omitting outside-project absolute paths.
- `ArtifactPublisher`: Transactional publication via durable journal (`publication-journal.json`) supporting states `Prepared` -> `BackedUp` -> `HakReplaced` -> `ManifestReplaced` -> `Committed` with full rollback and startup recovery.
- `BuildOrchestrator`: Preflights workspace state, executes packing, verification, manifest generation, and publication.

## Verification Results

- All 170 unit, integration, UI view-model, build packing, verification, provenance manifest, and publication journal tests passed cleanly.
