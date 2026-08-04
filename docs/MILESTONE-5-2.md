# Milestone 5 - Phase 2: Preview Provider Implementations

## Scope
Build preview providers for all supported asset families (image, text/table, audio, GFF/ITP, SSF, MDL) with strict safety and format-specific parsing.

## Deliverables
- End-to-end preview support for image, text, audio, tree-like, and model assets.
- Unified output contract for `PreviewResult` metadata and raw payload.
- Runtime safeguards for hostile/malformed data.

## Detailed tasks

### 1. Image Preview Provider
- [x] Add `src/SRN.CC.Preview/ImagePreviewProvider.cs`.
- [x] Implement file recognition for `.tga`, `.dds`, `.plt` and existing image families.
- [x] Add TGA parser support.
- [x] Add DDS support through `Pfim` image decoder integration.
- [x] Add PLT support with 10 default dyes when format has no embedded palette:
  - Skin, Hair, Metal1, Metal2, Cloth1, Cloth2, Leather1, Leather2, Tattoo1, Tattoo2.
- [x] Enforce image safety budget:
  - Max dimension: `4096x4096`.
  - Max decoded size: `64 MiB` RGBA.
- [x] Output:
  - `RawPayload` uses BGRA bytes.
  - `FormattedContent` carries dimensions as `{width}:{height}`.
- [x] Add provider-level fail-fast rules:
  - reject non-decodable streams with explicit `PreviewError` classification.
  - short-circuit before full decode if headers indicate oversize.
- [x] Add deterministic fallback behavior for missing palette:
  - use default dyes only when format does not include palette.
  - keep PLT parse path unchanged for explicit palette formats.

### Image safety and performance checklist
- [x] Verify total output area and byte-size before decode to avoid over-allocation.
- [x] Validate stream length where headers expose dimensions.
- [x] Stream-read in bounded chunks for large images.

### 2. Text Preview Provider
- [x] Add `src/SRN.CC.Preview/TextPreviewProvider.cs`.
- [x] Add extension-based family routing for supported textual assets:
  - `2DA`, `MTR`, `TXI`, `SET`, `INI`, `GUI`, `txt`, `shader`.
- [x] Parse with BOM-aware reader and fallback to CP1252.
- [x] Auto-detect UTF-8 / CP1252 decoding.
- [x] Enforce 8 MiB safety budget.
- [x] Normalize CRLF/LF handling in output preview text.
- [x] Preserve binary-likeness detection:
  - if decode attempts fail with high invalid-byte ratio, classify as unsupported text binary.
- [x] Add stable truncation policy:
  - output max preview size with clear “truncated” marker.

### 3. Audio Preview Provider
- [x] Add `src/SRN.CC.Preview/AudioPreviewProvider.cs`.
- [x] Add signature/format detection for `WAV` and `BMU`.
- [x] Detect and skip the 8-byte BMU header by wrapping read stream with offset handling.
- [x] Produce preview payload that preserves original audio stream semantics for player consumption.
- [x] Enforce stream-read guardrails for non-seekable inputs by converting them to bounded seekable playback streams.
- [x] Guarantee scrubbing/progress for all valid WAV/BMU assets:
  - If input is seekable, play directly from it.
  - If input is non-seekable, materialize into an in-memory or temp-file-backed seekable stream (bounded by a strategy agreed by implementation).
  - Verify sample-position mapping is accurate for slider-driven seeks.
- [x] Emit precise user-facing reason when content is unsupported or malformed.

### 4. Tree Preview Provider
- [x] Add `src/SRN.CC.Preview/TreePreviewProvider.cs`.
- [x] Add parser path for GFF-family + ITP files.
- [x] Render field hierarchy as indented, stable-outline text:
  - deterministic ordering
  - compact node labels
  - explicit unknown-type markers.
- [x] Add SSF parser path:
  - parse and expose header fields (`Magic`, `Version`, `Entry Count`).
  - iterate loop offsets and decode each entry.
  - render each entry row with 16-byte ResRefs and 4-byte StrRefs.
- [x] Add overflow-safe offset validation and EOF-safe reads.
- [x] Return structured errors for truncated/corrupt files instead of crashing.

### 5. MDL Preview Provider
- [x] Add `src/SRN.CC.Preview/MdlPreviewProvider.cs`.
- [x] Add parsing route for binary MDL and ASCII MDL variants.
- [x] Extract model metadata:
  - model name
  - supermodel
  - mesh names and texture references
  - texture count / duplicates summary.
- [x] Handle malformed header and missing mesh blocks without failing entire preview.
- [x] Include version/family hints in `FormattedContent`.

## Cross-cutting provider tasks
- [x] Introduce shared helper(s) for bounded read/peek operations.
- [x] Add consistent `PreviewResult` envelope mapping across all providers.
- [x] Add logging for unsupported formats and parsing fail reasons.
- [x] Add unit test fixtures per format for deterministic parse validation.

## Exit Criteria
- [x] Each provider returns a preview for valid inputs under format-specific budget limits.
- [x] All providers return classified failures instead of unhandled exceptions on invalid input.
- [x] Parsing logic emits deterministic output for identical bytes.

---

> Reconciled against delivered code during Milestone 5 completion pass; see `docs/MILESTONE-5-3.md` for the Phase 3 completion summary.
