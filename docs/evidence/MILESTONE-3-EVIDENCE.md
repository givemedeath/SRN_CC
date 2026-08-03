# Milestone 3 Verification Evidence: Resolution and Project Persistence

## Summary

Milestone 3 implements the headless workspace layer that converts source snapshots into deterministic curated assets, resolves asset occurrences by source priority or validated winner pins, persists selections and project state to `.srnccproj` JSON with forward-compatible unknown node preservation, and persists application settings to `%LOCALAPPDATA%\SRN.CC\settings.json` with corruption quarantine.

---

## 1. Implemented Core and Infrastructure Contracts

### Core Contracts (`SRN.CC.Core`)
- `ResolutionStatus`: Enum (`Resolved`, `UnresolvedDuplicate`, `InvalidPin`, `Unavailable`, `Unpackageable`).
- `WinnerPin`: Immutable record binding an `AssetIdentity`, `SourceId`, `OccurrenceLocator`, and 32-byte SHA-256 payload hash.
- `CuratedAsset`: Immutable record representing resolved asset state across all sources, containing resolution status, winner occurrence, optional pin, selection state, and independent conflict flags (`HasCrossSourceCollision`, `HasSameSourceDuplicate`, `HasDifferingPayloads`, `HasCaseOnlyNamingDifference`, `HasUnreadableOccurrence`, `HasInvalidPin`).
- `SelectionState`: Immutable record with `DefaultSelected` boolean and sparse `AssetIdentity` overrides. Includes override compaction and bulk toggle operations (`IncludeAll`, `ExcludeAll`).
- `WorkspaceState`: Immutable workspace model aggregating ordered sources (priority ordinals 0..n-1), latest snapshots, curated assets, selection state, winner pins, project preferences, and read-only status.
- `ChangedInputReport`: Immutable change tracking model for source additions/removals, availability transitions, fingerprint changes, identity additions/removals, winner changes, pin reattachments, pin invalidations, and selection changes.
- Interfaces: `IWorkspaceResolver`, `IWorkspaceService`, `ISourceReaderDispatcher`, `IStreamingHashService`, `IProjectStore`, `ISettingsStore`.

### Infrastructure Implementations (`SRN.CC.Infrastructure`)
- `WorkspaceResolver`: Deterministic resolution algorithm applying source priority order, winner pin overrides, lazy hashing for winning-source duplicates, locator tie-breaking, and independent diagnostic flag calculation.
- `WorkspaceService`: Thread-safe state orchestrator serializing initial indexing, reordering, rescan, relocation, pin/unpin, and selection mutations with copy-on-success state transitions.
- `AssetHashCache`: Thread-safe in-memory cache keyed by `(SourceId, Identity, Locator, Size, Fingerprint)` with invalidation on rescan, relocate, availability transition, or fingerprint change.
- `ProjectStore`: Atomic JSON persistence for `.srnccproj` (schema version 1) preserving unknown properties via `JsonNode` trees, relative path calculation against project directory, and read-only opening for newer schema versions.
- `SettingsStore`: Persists `%LOCALAPPDATA%\SRN.CC\settings.json` with corruption quarantine (renaming corrupt settings files to `.corrupt.<timestamp>` suffix and falling back to clean defaults).

---

## 2. Project Schema Example (`.srnccproj`)

```json
{
  "schemaVersion": 1,
  "sources": [
    {
      "id": "7fa2a3e1-2092-4f40-a309-8bc8a9b20011",
      "kind": "hak",
      "path": {
        "kind": "relative",
        "value": "content/swlor_res_v1.hak"
      },
      "fingerprint": {
        "kind": "hak",
        "algorithmVersion": 1,
        "digest": "a1b2c3d4e5f67890123456789abcdef0123456789abcdef0123456789abcdef0"
      }
    }
  ],
  "selectionState": {
    "defaultSelected": true,
    "overrides": [
      {
        "resref": "nw_magicbook",
        "resourceType": 2000,
        "selected": false
      }
    ]
  },
  "pins": [
    {
      "resref": "custom_icon",
      "resourceType": 2000,
      "sourceId": "7fa2a3e1-2092-4f40-a309-8bc8a9b20011",
      "sha256": "99887766554433221100aabbccddeeff99887766554433221100aabbccddeeff",
      "locator": {
        "kind": "hakEntry",
        "index": 12
      }
    }
  ],
  "outputSettings": {
    "targetHak": "output_merged.hak"
  },
  "filters": {},
  "comparisonPreferences": {}
}
```

---

## 3. Controlled Acceptance Scenario Results

The 6-step controlled acceptance scenario was verified in `ControlledAcceptanceScenarioTests.cs`:
1. **Multi-Source Indexing**: Two sources indexed with priority ordinals 0 and 1 containing duplicate identity conflicts. Priority 0 source won by default.
2. **Pinning & Selection**: Pinned lower-priority source occurrence and applied sparse selection override. Saved project state to disk.
3. **Persisted State Reload**: Reloaded workspace state from `.srnccproj`. Source order, winner pin override, and sparse selection overrides were preserved byte-for-byte.
4. **Source Relocation**: Moved source file and invoked `RelocateSourceAsync`. Verified source ID, priority, selection state, and pin validity were preserved.
5. **Payload Alteration & Rescan**: Modified payload bytes of pinned occurrence and rescanned source. Verified pin status transitioned to `InvalidPin`, blocking auto-resolution and generating `PinInvalidations` in `ChangedInputReport`.
6. **Payload Restoration & Reattachment**: Placed matching payload bytes under a unique new locator in the same source and rescanned. Verified deterministic pin reattachment to the new locator and return to `Resolved` status.

---

## 4. Verification Pass Results

- **Solution Build**: 0 Warnings, 0 Errors (`Release/win-x64`).
- **Functional Tests**: 143 passed, 0 failed, 0 skipped (`SRN.CC.Tests`).
- **Dependency & Vendored Audits**: PASSED.
- **Publish Audit**: PASSED (`win-x64` self-contained application).
