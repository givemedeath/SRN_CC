# Milestone 5 - Phase 2: Preview Provider Implementations

## Scope
Build preview providers for all supported asset families (image, text/table, audio, GFF/ITP, SSF, MDL) with strict safety and format-specific parsing.

## Deliverables
- End-to-end preview support for image, text, audio, tree-like, and model assets.
- Unified output contract for `PreviewResult` metadata and raw payload.
- Runtime safeguards for hostile/malformed data.

## Detailed tasks

### 1. Image Preview Provider
- [ ] Add `src/SRN.CC.Preview/ImagePreviewProvider.cs`.
- [ ] Implement file recognition for `.tga`, `.dds`, `.plt` and existing image families.
- [ ] Add TGA parser support.
- [ ] Add DDS support through `Pfim` image decoder integration.
- [ ] Add PLT support with 10 default dyes when format has no embedded palette:
  - Skin, Hair, Metal1, Metal2, Cloth1, Cloth2, Leather1, Leather2, Tattoo1, Tattoo2.
- [ ] Enforce image safety budget:
  - Max dimension: `4096x4096`.
  - Max decoded size: `64 MiB` RGBA.
- [ ] Output:
  - `RawPayload` uses BGRA bytes.
  - `FormattedContent` carries dimensions as `{width}:{height}`.
- [ ] Add provider-level fail-fast rules:
  - reject non-decodable streams with explicit `PreviewError` classification.
  - short-circuit before full decode if headers indicate oversize.
- [ ] Add deterministic fallback behavior for missing palette:
  - use default dyes only when format does not include palette.
  - keep PLT parse path unchanged for explicit palette formats.

### Image safety and performance checklist
- [ ] Verify total output area and byte-size before decode to avoid over-allocation.
- [ ] Validate stream length where headers expose dimensions.
- [ ] Stream-read in bounded chunks for large images.

### 2. Text Preview Provider
- [ ] Add `src/SRN.CC.Preview/TextPreviewProvider.cs`.
- [ ] Add extension-based family routing for supported textual assets:
  - `2DA`, `MTR`, `TXI`, `SET`, `INI`, `GUI`, `txt`, `shader`.
- [ ] Parse with BOM-aware reader and fallback to CP1252.
- [ ] Auto-detect UTF-8 / CP1252 decoding.
- [ ] Enforce 8 MiB safety budget.
- [ ] Normalize CRLF/LF handling in output preview text.
- [ ] Preserve binary-likeness detection:
  - if decode attempts fail with high invalid-byte ratio, classify as unsupported text binary.
- [ ] Add stable truncation policy:
  - output max preview size with clear “truncated” marker.

### 3. Audio Preview Provider
- [ ] Add `src/SRN.CC.Preview/AudioPreviewProvider.cs`.
- [ ] Add signature/format detection for `WAV` and `BMU`.
- [ ] Detect and skip the 8-byte BMU header by wrapping read stream with offset handling.
- [ ] Produce preview payload that preserves original audio stream semantics for player consumption.
- [ ] Enforce stream-read guardrails for non-seekable inputs by converting them to bounded seekable playback streams.
- [ ] Guarantee scrubbing/progress for all valid WAV/BMU assets:
  - If input is seekable, play directly from it.
  - If input is non-seekable, materialize into an in-memory or temp-file-backed seekable stream (bounded by a strategy agreed by implementation).
  - Verify sample-position mapping is accurate for slider-driven seeks.
- [ ] Emit precise user-facing reason when content is unsupported or malformed.

### 4. Tree Preview Provider
- [ ] Add `src/SRN.CC.Preview/TreePreviewProvider.cs`.
- [ ] Add parser path for GFF-family + ITP files.
- [ ] Render field hierarchy as indented, stable-outline text:
  - deterministic ordering
  - compact node labels
  - explicit unknown-type markers.
- [ ] Add SSF parser path:
  - parse and expose header fields (`Magic`, `Version`, `Entry Count`).
  - iterate loop offsets and decode each entry.
  - render each entry row with 16-byte ResRefs and 4-byte StrRefs.
- [ ] Add overflow-safe offset validation and EOF-safe reads.
- [ ] Return structured errors for truncated/corrupt files instead of crashing.

### 5. MDL Preview Provider
- [ ] Add `src/SRN.CC.Preview/MdlPreviewProvider.cs`.
- [ ] Add parsing route for binary MDL and ASCII MDL variants.
- [ ] Extract model metadata:
  - model name
  - supermodel
  - mesh names and texture references
  - texture count / duplicates summary.
- [ ] Handle malformed header and missing mesh blocks without failing entire preview.
- [ ] Include version/family hints in `FormattedContent`.

## Cross-cutting provider tasks
- [ ] Introduce shared helper(s) for bounded read/peek operations.
- [ ] Add consistent `PreviewResult` envelope mapping across all providers.
- [ ] Add logging for unsupported formats and parsing fail reasons.
- [ ] Add unit test fixtures per format for deterministic parse validation.

## Exit Criteria
- [ ] Each provider returns a preview for valid inputs under format-specific budget limits.
- [ ] All providers return classified failures instead of unhandled exceptions on invalid input.
- [ ] Parsing logic emits deterministic output for identical bytes.
