# Milestone 6 - Phase 1: Foundation (Wave 0)

## Scope

Land every prerequisite the later waves build on: the milestone docs themselves, the upstream
vendoring spike, the `PreviewResult` payload contract, the texture-decode extraction, the
`DependencyAnalyzer` correctness fix plus MTR parser, `NOTICES.md` hygiene, and the declarations-only
CPU render contracts plus fully-implemented camera. All seven slices in this wave are parallel except
S6a, which sequences after S2 within the wave (it consumes `IPreviewPayload`/`PreviewResult`
indirectly through `ModelScenePayload`'s eventual use, and the plan states the ordering explicitly).

`docs/MILESTONE-6-0-VENDORING-DECISION.md` is owned by S1, not by this phase doc set.

## Deliverables

- `docs/MILESTONE-6.md` and `docs/MILESTONE-6-1.md` … `docs/MILESTONE-6-5.md` recording decisions
  A1-A8 and the verbatim scope/deferred lists (this document set).
- `docs/MILESTONE-6-0-VENDORING-DECISION.md` with a GO/NO-GO verdict for vendoring `MdlMeshBuilder.cs`.
- `IPreviewPayload` marker interface and the one-line `PreviewResult.Payload` addition.
- `TextureDecoder` extracted from `ImagePreviewProvider`, with the existing image-provider test
  (`PreviewProviderTests.cs:67`) left unmodified and green.
- Corrected `DependencyAnalyzer` resource-type routing (`2052=dwk`, `2053=pwk`, `2016=wok` split out
  of the txi branch) plus a new bounded `MtrDocument` parser wired into MTR/TXI/envmap extraction.
- `NOTICES.md` covering every `"scope": "runtime"` package in `eng/dependency-policy.json`.
- Declarations-only `RenderScene`/`RenderMesh`/`RenderMaterial`/`IMdlSceneBuilder`/`ModelScenePayload`
  types, plus a fully implemented, unit-tested `RenderCamera`.

## Detailed tasks

### S0 — Milestone documentation scaffold
Owns `docs/MILESTONE-6.md`, `docs/MILESTONE-6-1.md` … `docs/MILESTONE-6-5.md`. Must not touch
`docs/MILESTONE-6-0-VENDORING-DECISION.md` (S1's file) or any other repo file.
- [x] Write `docs/MILESTONE-6.md`: Summary, verified baseline, architecture decisions A1-A8
      (condensed but complete), full Scope section (in-scope MVP + verbatim deferred list), table of
      contents to the five phase docs.
- [x] Write `docs/MILESTONE-6-1.md` through `docs/MILESTONE-6-5.md`, one per wave, each with Scope /
      Deliverables / per-slice detailed `- [ ]` tasks / Exit Criteria, matching the M5 phase-doc
      convention (`docs/MILESTONE-5-1.md` … `-5-5.md`).
- [x] Confirm every slice S0-S17 appears under exactly one phase doc.
- No tests owned by this slice.

### S1 — Upstream fetch/verify spike
Owns `docs/MILESTONE-6-0-VENDORING-DECISION.md`. Must not touch any source, manifest, or tool. Blocks
only S16 (wave 4); every other slice proceeds regardless of this spike's outcome.
- [ ] Clone `https://github.com/givemedeath/SWLOR_NWN` and check out commit
      `8202faa203eddd6f4972104d22ea5740e23f20f7`.
- [ ] Compute `git rev-parse HEAD:SWLOR.NWN.Formats/Mdl/MdlMeshBuilder.cs` → record as `gitBlobSha1`.
- [ ] Compute `Get-FileHash -Algorithm SHA256` on the same file → record as `sha256`.
- [ ] Enumerate every file under `SWLOR.Toolset/`.
- [ ] Grep every candidate vendoring file for word-boundary matches on the disqualifying provenance
      keyword, `GPL`, `Avalonia`, and `Silk`.
- [ ] Determine whether `MdlMeshBuilder.cs` references any upstream type not already present under
      `src/SRN.CC.Formats/Vendored/.../Mdl/`.
- [ ] Record both hashes, the Toolset inventory with grep results, the transitive-dependency verdict,
      and an explicit GO/NO-GO recommendation for S16 in
      `docs/MILESTONE-6-0-VENDORING-DECISION.md`.

**Exit Criteria**
- [ ] `gitBlobSha1` and `sha256` for `MdlMeshBuilder.cs` are recorded and ready to paste into a
      vendoring manifest.
- [ ] `SWLOR.Toolset/` inventory and grep results are recorded, with a clear rationale for why none of
      it is vendored (A5).
- [ ] Transitive-dependency verdict for `MdlMeshBuilder.cs` is explicit (self-contained vs. needs
      additional upstream types).
- [ ] A GO/NO-GO verdict for S16 is stated unambiguously.

### S2 — Core payload contract *(hard prerequisite)*
Owns NEW `src/SRN.CC.Core/Preview/IPreviewPayload.cs`; MODIFY
`src/SRN.CC.Core/Preview/PreviewResult.cs` (one line). Must not touch any provider, `PreviewEngine`,
or any VM.
- [ ] Add `IPreviewPayload` with `string PayloadKind { get; }` and `long ApproximateByteSize { get; }`.
- [ ] Add the trailing optional `IPreviewPayload? Payload = null` parameter to the `PreviewResult`
      record, after `Diagnostics`.
- [ ] Verify zero other files require edits: all seven providers, both `PreviewEngine` failure paths,
      and every existing test must compile unchanged.
- [ ] Add a test proving the default is null and `with { Payload = x }` round-trips.

**Exit Criteria**
- [ ] Build clean under `TreatWarningsAsErrors`.
- [ ] Entire existing test suite green with **zero test edits** — the proof that migration cost is
      nil.
- [ ] New round-trip test for `Payload` passes.

### S3 — Texture decoder extraction *(hard prerequisite)*
Owns NEW `src/SRN.CC.Preview/Render/TextureDecoder.cs`; MODIFY
`src/SRN.CC.Preview/ImagePreviewProvider.cs`; NEW
`tests/SRN.CC.Tests/Preview/Render/TextureDecoderTests.cs`. Must not touch
`PreviewStreamHelpers.cs` (owned exclusively by S6b for the whole milestone),
`MdlPreviewProvider.cs`, or `DependencyAnalyzer.cs`.
- [ ] Extract `ImagePreviewProvider`'s private TGA/DDS(Pfim)/PLT → BGRA decode paths into
      `internal static class TextureDecoder`.
- [ ] Expose `TryDecodeToBgra(ReadOnlySpan<byte> data, string extension, out DecodedTexture result, out string? error)`.
- [ ] Update `ImagePreviewProvider` to call `TextureDecoder` instead of its private paths, preserving
      existing behavior byte-for-byte (default dyes, safety budgets, BGRA output format).
- [ ] Add `TextureDecoderTests.cs` covering TGA, DDS, and PLT decode paths plus error cases.

**Exit Criteria**
- [ ] `PreviewProviderTests.cs` is **unmodified** and green, including line 67's
      `RawPayload?.Length == 32` assertion.
- [ ] `TextureDecoderTests.cs` passes for all three formats plus malformed-input cases.

### S4 — DependencyAnalyzer correctness + MTR parser
Owns MODIFY `src/SRN.CC.Preview/DependencyAnalyzer.cs`; NEW
`src/SRN.CC.Preview/Render/MtrDocument.cs`; MODIFY
`tests/SRN.CC.Tests/Preview/DependencyAnalyzerTests.cs`; NEW
`tests/SRN.CC.Tests/Preview/Render/MtrDocumentTests.cs`.
- [ ] Fix `DependencyAnalyzer.cs:68-70`: route `2052` (dwk) and `2053` (pwk) to companion extraction
      instead of the wrong `2029`/`2030` values.
- [ ] Split `2016` (wok) out of the txi branch into its own companion-extraction routing.
- [ ] Add `MtrDocument`: a bounded line-oriented key/value parser for `texture0..texture3`, `envmap`,
      `bumpmap`, `parameter`, `renderhint`, `customshadervs/fs`, tolerant of unknown directives and
      comments, with bounded handling of malformed input.
- [ ] Route MTR/TXI/envmap reference extraction in `DependencyAnalyzer` through the same
      `MtrDocument` parser so extraction and rendering can never disagree.

**Exit Criteria**
- [ ] Regression test proves a `.dlg` (2029) occurrence no longer enters companion extraction.
- [ ] Regression test proves a `.pwk` (2053) occurrence now correctly enters companion extraction.
- [ ] Type-ID table used by the analyzer is asserted against `ResourceTypes.cs`.
- [ ] MTR parse matrix covers unknown directives, comments, and bounded malformed input without
      throwing.

### S5 — NOTICES hygiene
Owns MODIFY `NOTICES.md`.
- [ ] Add rows for Pfim, the NAudio family, and all three Silk.NET packages to the resolved-runtime
      packages table.
- [ ] Cross-check every `"scope": "runtime"` entry in `eng/dependency-policy.json` against the
      table for completeness.

**Exit Criteria**
- [ ] Every `"scope": "runtime"` entry in `eng/dependency-policy.json` (Pfim, NAudio family, Silk.NET
      family) has a corresponding row in the resolved-runtime-packages table.

### S6a — CPU render contracts + camera *(sequence after S2 within the wave)*
Owns NEW
`src/SRN.CC.Preview/Render/{RenderScene,RenderMesh,RenderMaterial,RenderCamera,IMdlSceneBuilder,ModelScenePayload}.cs`;
NEW `tests/SRN.CC.Tests/Preview/Render/RenderCameraTests.cs`. Declarations only for the scene/mesh/
material/builder types — no builder logic in this slice — except `RenderCamera`, which is fully
implemented.
- [ ] Declare `RenderScene` with `ModelName`, `SuperModel`, `IsAsciiSource`, `BoundsMinimum`,
      `BoundsMaximum`, `Radius`, `ArtworkMeshes`, `WalkmeshMeshes`, `Materials`,
      `UnsupportedFeatures`, `Diagnostics`, `ApproximateByteSize`.
- [ ] Declare `RenderMesh` with `NodeName`, `WorldTransform`, `Positions`, `Normals`, `TexCoords`,
      `ushort[] Indices`, `MaterialIndex`, `IsWalkmesh`, `FaceSurfaceIds`, `TileFade`.
- [ ] Declare `RenderMaterial` with `Name`, `DiffuseColor`, `Diffuse`, `Lightmap`,
      `EnvironmentMapName`, `BlendMode`, `AlphaTestThreshold`, `UnresolvedTextures`; declare
      `TextureImage` record and `MaterialBlendMode` enum.
- [ ] Declare `IMdlSceneBuilder.BuildAsync(MdlModel, bool isAsciiSource, ITextureSource?,
      SceneBuildBudget, CancellationToken)` and `SceneBuildBudget` record.
- [ ] Declare `ModelScenePayload` implementing `IPreviewPayload` (from S2).
- [ ] Fully implement `RenderCamera` as an immutable `readonly record struct`: `Frame(boundsMin,
      boundsMax, radius)`, `View`, `Projection(aspect)`, `Orbit(deltaYaw, deltaPitch)` with pole
      clamping, `Dolly(factor)` with clamping, `Pan(worldDelta)`.
- [ ] Write `RenderCameraTests.cs` covering framing math, orbit pole clamping, dolly clamping, and
      view/projection matrix orthonormality.

**Exit Criteria**
- [ ] Solution builds with the new declarations-only types in place.
- [ ] Camera framing, pole clamping on orbit, dolly clamping, and matrix orthonormality are all
      covered by passing tests — this is a large slice of the "camera controls" gate evidence.

## Notes
- S2 and S3 are hard prerequisites for later waves (`Payload` channel, texture decode reuse) and
  must land clean with zero collateral test edits.
- S6a is sequenced after S2 within this wave because `ModelScenePayload` implements
  `IPreviewPayload`; it does not depend on S3, S4, S5, or S1 in any way.
- S1's spike result only gates S16 in wave 4 — it never blocks this wave or waves 1-3.
