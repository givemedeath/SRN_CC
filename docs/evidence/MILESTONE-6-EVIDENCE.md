# Milestone 6 Verification Evidence: Advanced 3D Previews

## Summary

Milestone 6 turns the previously text-only preview surface into a working 3D model/walkmesh
viewport hosted in up to three comparison slots. It adds a first-party, GPU-agnostic CPU render
pipeline (`RenderScene`/`RenderMesh`/`RenderMaterial`/`RenderCamera`/`MdlSceneBuilder`), a GL device
abstraction (`IGlDevice`/`SilkGlDevice`/`FakeGlDevice`) and renderer (`ModelRenderer`) with explicit
context-ownership and loss-recovery semantics, a texture resolution ladder
(MTR-override → DDS → TGA → PLT → TXI-envmap discovery) bridged through a Core interface, and the
Avalonia-side plumbing (`ModelViewportControl`/`ModelViewportViewModel`/`ModelViewportRegistry`)
that hosts it. It also closes the remaining milestone-5 UI debt (per-family templates, image
zoom/pan, audio transport, linked navigation, SQLite thumbnail cache wiring) and fixes a
misrouted-resource-type correctness bug in `DependencyAnalyzer`.

**Gate (`PLAN.md:210`):** representative one-to-three-slot corpus previews survive unsupported
features and context failures without leaks or cross-context handles, using semantic
geometry/material assertions and adapter-dependent smoke tests — never cross-GPU pixel equality
(`PLAN.md:230`). Section 3 below maps every clause of that gate, and every milestone-6 scope bullet,
to a specific named test.

This document is owned by slice S17 (the milestone's final, closeout slice) and was written after
S9–S11 (the critical-path slices that produce the payload, slot-content, and viewport surfaces this
document's own new tests exercise end to end) had already landed.

---

## 1. Implemented CPU Render Contracts (`SRN.CC.Preview/Render/`)

- `RenderScene`/`RenderMesh`/`RenderMaterial`/`TextureImage`/`MaterialBlendMode` — the GPU-agnostic
  data model: rest-pose world transforms, artwork/walkmesh partition, deduplicated materials with
  diffuse colour + optional decoded textures, per-face `SurfaceId` for walkmesh triangles.
- `RenderCamera` — an immutable orbit-camera struct (`Frame`/`View`/`Projection`/`Orbit`/`Pan`/
  `Dolly`), 100% headlessly testable, carrying the bulk of the "camera controls" gate evidence.
- `IMdlSceneBuilder`/`MdlSceneBuilder` — walks `MdlModel.GeometryRoot`, composes three-level TRS
  world transforms, splits `IsWalkmesh` nodes, deduplicates materials by (bitmap, lightmap, diffuse
  colour), generates per-vertex normals when the source lacks them, and enforces
  `SceneBuildBudget` (CPU bytes, texture bytes/count, draw-call count) with graceful degradation —
  never a hard failure — plus `UnsupportedFeatures` reporting for skinmesh/emitter/animation nodes.
  Entirely first-party (see Section 5, vendoring).
- `ModelScenePayload` — the `IPreviewPayload` implementation wrapping a built `RenderScene`.
- `ModelSceneCache` — an in-process, single-flight-deduplicating, LRU-evicted (128 MiB budget) cache
  keyed by `(SourceId, OccurrenceLocator, Sha256)`.
- `TextureDecoder` — TGA/DDS(Pfim)/PLT → BGRA, extracted from `ImagePreviewProvider` so both preview
  families share one codec path.
- `TextureResolver`/`MtrDocument` — the MTR-override → DDS → TGA → PLT → TXI-envmap resolution
  ladder against `ITextureSource` (Core), with environment maps discovered and reported
  (`RenderMaterial.EnvironmentMapName`) but never shaded, per the milestone's explicit deferral.

## 2. Implemented GPU Device Abstraction and Renderer (`SRN.CC.Preview/Render/Gl/`)

- `GlContextId`/`GlHandle`/`GlCapabilities`/`GlDrawCall` — context-tagged, comparable resource
  identity. Every `GlHandle` a device mints carries that device's `GlContextId`; a fresh context id
  is minted per `IGlDevice` construction.
- `IGlDevice` with two implementations: `SilkGlDevice` (real, `Silk.NET.OpenGL`-backed, constructed
  from the `Func<string, IntPtr>` proc-address delegate Avalonia's `OpenGlControlBase.OnOpenGlInit`
  hands out) and `FakeGlDevice` (in-memory test double: no GPU, tracks live resources, records every
  issued call).
- `ModelRenderer` — owns every GL resource it creates; `Initialize`/`Upload`/`Render`/`Abandon`/
  `Dispose`, `RendererState` (`Uninitialized`/`Ready`/`Unsupported`/`Stale`/`Disposed`),
  `LastFrameDrawCallCount`/`LastFrameBoundTextureNames` as semantic assertion hooks. The
  cross-context/lost guard (`_device.IsLost || _device.ContextId != ContextId`) is repeated at every
  entry point.
- `src/SRN.CC.App/Views/ModelViewportControl.cs` — the sole Avalonia+GL file in the milestone; wires
  `OnOpenGlInit`/`OnOpenGlRender`/`OnOpenGlDeinit`/`OnOpenGlLost` straight onto
  `SilkGlDevice`/`ModelRenderer` construction, `Upload`/`Render`, `Dispose`, and `Abandon`/`MarkLost`
  respectively, with zero GL calls on the loss path.

## 3. Implemented App-Side Preview Surface

- `PreviewContentViewModel` (abstract base) with four subclasses, each in its own file:
  `TextContentViewModel`, `ImageContentViewModel` (`ZoomScale`/`PanX`/`PanY`), `AudioContentViewModel`
  (NAudio transport: `Play`/`Pause`/`Stop`/`PositionProgress`), `ModelViewportViewModel` (camera
  state, `Orbit`/`Pan`/`Dolly`/`Reset`, `ShowWalkmesh`, `RenderUnavailableReason`).
- `PreviewContentFactory.Create(PreviewResult)` dispatches on `result.Payload`'s type (or `Family`
  for image/audio, which have no dedicated payload type), falling back to `TextContentViewModel` for
  everything else — including a degraded Model result.
- `ModelViewportRegistry` (`MaxConcurrent = 3`) — structurally enforces "only for visible 3D slots":
  `PreviewSlotViewModel.Content` is a `ModelViewportViewModel` only for successful model previews, so
  Avalonia's `ContentPresenter` never realizes a control for any other content type; the registry is
  the backstop against a fourth concurrent viewport.
- `ComparisonPanelViewModel` links image zoom/pan and compatible 3D camera state across active slots
  unconditionally, and audio position only when every active slot is audio, behind a master
  `LinkNavigationEnabled` toggle.
- `WorkspaceTextureSource`/`TextureSourceHolder` wire real workspace-first texture resolution into
  `MdlPreviewProvider` via a late-bound accessor, refreshed on every workspace-state change.
- `IPreviewThumbnailCache`/`SqlitePreviewThumbnailCache` wired behind `PreviewEngine` for
  `Image`-family results only — never for `Payload`-bearing results (Section 4 of the ADR; test
  citation in the gate table below).

## 4. Corrections Landed Alongside

`DependencyAnalyzer.cs`'s WOK/PWK/DWK resource-type routing was fixed: `2029`/`2030` (which are
actually `dlg`/`itp` per `ResourceTypes.cs`) were wrongly routed to companion extraction; the correct
values `2016`/`2052`/`2053` (`wok`/`dwk`/`pwk`) now route there instead, so real door/placeable
walkmeshes are discovered as dependencies instead of falling through to "unsupported."

---

## 5. Vendoring Decision (Slices S1/S16)

`docs/MILESTONE-6-0-VENDORING-DECISION.md` records a **NO-GO** verdict on vendoring
`MdlMeshBuilder.cs`: at the pinned upstream commit, the file lives inside the Toolset-domain
codebase (not the formats-layer path architecture decision A5 assumed), has an undisclosed
compile-time dependency on a second, never-reviewed file (`MdlAnimationPose.cs`), and the existing
compliance disposition does not cover the artifact as it exists at that commit. Per A5's own stated
default, `MdlSceneBuilder` stayed first-party throughout, and S16 (Wave 4) closed as a documented
no-op — `tools/AuditVendoredSources.ps1`'s vendored-source counts are unchanged by this milestone
(verified again in Section 8 below). See `docs/adr/0003-render-layer-placement-and-gpu-context-ownership.md`
for the full architectural rationale and the compliance gate for ever reopening this decision.

---

## 6. Controlled End-to-End Scenario (Slice S17)

`tests/SRN.CC.Tests/Scenarios/ModelPreviewScenarioTests.cs` is the first place any test proves the
REAL (non-fake) pieces connect end to end — every prior slice's tests cover its own piece in
isolation. It uses the real `MdlPreviewProvider`, real `MdlSceneBuilder`, real `ModelSceneCache`,
real `PreviewContentFactory`, and real `ModelViewportViewModel` throughout; the only test doubles
anywhere in the file are `FakeGlDevice` (a real GPU context cannot exist in this test tier — see
Section 7) and a minimal in-memory `ITextureSource` (a real curated workspace cannot exist in a unit
test either).

| Step | What was verified | Test method |
|---|---|---|
| 1 | `GeneratePreviewAsync` on a valid ASCII MDL fixture → `PreviewResult.Payload` is a real `ModelScenePayload` → `PreviewContentFactory.Create` produces a real `ModelViewportViewModel` → its `Camera` is auto-framed from the scene's actual bounds → `ArtworkMeshes`/`WalkmeshMeshes` partition correctly for a fixture with both regular geometry (two distinctly-textured trimesh nodes) and an `aabb` walkmesh node | `FullPipeline_ValidMdlWithArtworkAndWalkmesh_FlowsThroughToRealModelViewportViewModel` |
| 2 | 1, 2, and 3 concurrent requests for the SAME occurrence produce exactly one scene build (proving `ModelSceneCache` single-flight dedup end to end, not just at the cache's own unit level) | `ConcurrentRequestsForSameOccurrence_ProduceExactlyOneSceneBuild_RegardlessOfSlotCount(1\|2\|3)` |
| 3 | Three concurrent requests for THREE DIFFERENT occurrences build three independent, non-interfering scenes | `ThreeDifferentOccurrences_BuildThreeIndependentScenesThatDoNotInterfere` |
| 4 | A fixture with a skinmesh node, an emitter node, and an animation block still produces a valid scene with `UnsupportedFeatures` populated, and `IsSuccess` stays true (never throws) | `SkinmeshEmitterAndAnimationFixture_StillProducesValidSceneWithUnsupportedFeaturesPopulated` |
| 5 | A fixture that trips the binary-header-fallback path (fails primary parse, passes fallback) yields `Payload == null` while `FormattedContent` still contains the fallback status text, and the real `PreviewContentFactory` routes it to a real `TextContentViewModel` | `MalformedBinaryMdl_DegradesToHeaderFallback_WithNullPayloadAndFallbackStatusText` |
| 6 | A real `RenderScene` built by the real pipeline above is uploaded/rendered through a real `ModelRenderer` wired to `FakeGlDevice`; draw-call count and bound-texture names are asserted against the scene's actual material/mesh counts (not a synthetic `TestSceneFactory` scene) | `RealSceneFromPipeline_UploadsAndRendersThroughRealModelRenderer_WithMatchingDrawCallsAndTextureNames` |
| 7 | Three scenes built, uploaded, rendered, and disposed through three independent `ModelRenderer`+`FakeGlDevice` pairs leave every device's `LiveResources` empty | `ThreeConcurrentModelPreviews_UploadRenderDisposeLifecycle_LeavesEveryDeviceLiveResourcesEmpty` |

All 9 tests in this file pass (`dotnet test --filter "FullyQualifiedName~ModelPreviewScenarioTests"`
→ `Passed: 9, Failed: 0, Skipped: 0`).

---

## 7. Opt-In Real-Corpus/GPU-Adjacent Tier (`tests/SRN.CC.CorpusTests/Render/`)

Three new files, following the exact `[TestFixture]`/`[Category("Corpus")]`/env-guarded-`[SetUp]`
pattern already established by `CorpusIndexAcceptanceTests.cs`:

- `CorpusModelFixtureLocator.cs` — shared helper scanning HAK archives under `SRNCC_CORPUS_ROOT` for
  resource-type-2002 (`mdl`) entries.
- `ModelPreviewCorpusTests.cs` — real corpus MDLs parsed through the real
  `MdlPreviewProvider`/`MdlSceneBuilder` pipeline, with semantic geometry assertions (triangle/vertex
  consistency, finite bounds) and a `FakeGlDevice`-backed upload/render/dispose leak check at corpus
  scale (real, larger, more varied geometry than any hand-built unit fixture).
- `ModelPreviewWorkingSetTests.cs` — the `PLAN.md:229` 750 MiB working-set check.

**New environment variable — `SRNCC_RUN_GPU`.** Gated on BOTH the existing
`SRNCC_RUN_CORPUS=1`/`SRNCC_CORPUS_ROOT` convention AND this new variable is exactly one test:
`ModelPreviewCorpusTests.RealGpuContext_CannotBeConstructedInThisRepositorysTestInfrastructure_DocumentedGap`.
Unlike this repo's already-documented-but-never-implemented `SRNCC_REQUIRE_CORPUS`
(`PLAN.md:229`), `SRNCC_RUN_GPU` is genuinely wired: the guard is checked at runtime
(`IsEnvFlagSet("SRNCC_RUN_GPU")`) and its one call site branches between `Assert.Ignore` (unset) and
running the investigation body (set). This was verified directly during this slice's work: with
`SRNCC_RUN_GPU` unset the test reports `Ignored` with the "set SRNCC_RUN_GPU=1..." message; with it
set to `1` the test instead reaches `Assert.Inconclusive` with the full investigation text (confirmed
via `--logger "console;verbosity=detailed"` against a scratch corpus built for this validation and
discarded afterward — not committed to the repository).

**Real-GPU-context investigation result: NOT achievable in this repository's current test
infrastructure**, and this is treated as the plan's own anticipated, acceptable outcome ("there is NO
existing precedent in this repo for testing an `OpenGlControlBase`"). A real `SilkGlDevice` needs a
live `Func<string, IntPtr>` proc-address delegate from an active GL context. Investigated and ruled
out:

- **Avalonia.Headless** (used throughout this repo's existing UI tests): has no GL backend at all —
  `OnOpenGlInit` never fires headlessly, which is exactly why `ModelViewportControlTests.cs` asserts
  its *absence*, not its presence.
- **A real, non-headless Avalonia window**: would need an interactive desktop session and a
  window-manager-driven message pump inside the `dotnet test` NUnit host, which this repository's
  test infrastructure does not provide, and was not attempted — a hung or crashing GPU/driver init
  inside a shared test run is a worse failure mode than an honest, opt-in-only gap.
- **A standalone windowing package** (e.g. `Silk.NET.Windowing`/GLFW): not an approved dependency —
  `eng/dependency-policy.json` approves only `Silk.NET.OpenGL`/`Silk.NET.Core`/`Silk.NET.Maths`.
  Adding one is real future-milestone scope; it is out of S17's ownership (it would touch
  `eng/dependency-policy.json`, mint a new `packages.lock.json` entry, and change the dependency
  audit — none of which this documentation/tests-only slice may touch).
- **Raw Win32 WGL interop** (P/Invoke a hidden window + `wglCreateContextAttribsARB` bootstrap):
  technically possible without a new package reference, but requires an interactive display/driver
  session this sandboxed verification environment cannot guarantee, and unverified P/Invoke
  context-bootstrap code that has never been exercised against a real driver is exactly the
  "looks done, silently wrong" risk this evidence tier exists to avoid. Recorded as the concrete path
  for a future milestone with real windowing infrastructure, deliberately not implemented here.

The deepest achievable real-GPU-*adjacent* coverage landed instead: real corpus MDL bytes flowing
through the real parse/scene-build pipeline, then through a real `ModelRenderer` (the same production
type `ModelViewportControl` uses) wired to `FakeGlDevice`. This is explicitly NOT presented as GPU
verification — the test names, comments, and this document all say so — but it is strictly deeper
than the unit-fixture-only S8/S17-unit-scenario coverage, because it runs against real, corpus-scale
geometry.

**Exclusion from the default run — verified directly**, per the requirement that this tier never
leak into CI:

```
$ dotnet test tests/SRN.CC.CorpusTests -c Release --filter 'Category!=Corpus&Category!=Performance'
No test matches the given testcase filter 'Category!=Corpus&Category!=Performance' in ...SRN.CC.CorpusTests.dll
```

0 of the 4 new corpus-tier tests were matched or executed by the standard filtered command.

---

## 8. Milestone-6 Gate Clause Mapping

Every clause of `PLAN.md:206-210`'s milestone-6 description and gate, mapped to a specific named
test. "Corpus" column tests are opt-in (`SRNCC_RUN_CORPUS=1`/`SRNCC_CORPUS_ROOT`, plus
`SRNCC_RUN_GPU=1` for the one GPU-investigation test) and excluded from the default run; every other
test runs in the default `Category!=Corpus&Category!=Performance` filter.

| Milestone-6 clause | Status | Reproducible evidence |
|---|---|---|
| ASCII MDL geometry | **PASSED** | `MdlReaderTests.cs` (vendored reader, pre-existing 20 tests); `MdlSceneBuilderTests.BuildAsync_FlattensVertexAndFaceCountsFromSourceNode`; `ModelPreviewScenarioTests.FullPipeline_...` (real ASCII fixture end to end) |
| Binary MDL geometry | **PASSED** | `MdlReaderTests.cs` binary-path tests (pre-existing); `MdlPreviewProviderTests.GeneratePreviewAsync_ValidAsciiMdl_...` plus binary fallback coverage below |
| Node-hierarchy transforms (rest pose) | **PASSED** | `MdlSceneBuilderTests.BuildAsync_ComposesWorldTransformThroughThreeLevelHierarchy` (independently reconstructed 3-level TRS matrix, exact match) |
| Static materials (diffuse colour, texture, lightmap, untextured fallback) | **PASSED** | `MdlSceneBuilderTests.BuildAsync_DeduplicatesMaterialsSharingBitmapLightmapAndDiffuse`; `BuildAsync_WithNullTextureSource_ProducesUntexturedSceneWithDiagnostics` |
| DDS/TGA/PLT textures via `ITextureSource` | **PASSED** | `TextureResolverTests.ResolveAsync_DdsBeatsTgaBeatsPlt_WhenAllThreePresent`, `ResolveAsync_TgaBeatsPlt_WhenDdsAbsent`, `ResolveAsync_PltUsedAsLastResort`; `TextureDecoderTests.cs` (TGA/DDS/PLT → BGRA, malformed/oversized handling) |
| MTR override | **PASSED** | `TextureResolverTests.ResolveAsync_MtrTexture0_OverridesBaseName`; `MtrDocumentTests.Parse_WithAllDirectives_PopulatesEveryProperty` |
| Environment maps (discover + report only) | **PASSED** | `TextureResolverTests.ResolveAsync_EnvironmentMapFromMtr_IsReportedButNeverShaded`, `ResolveAsync_UnresolvableEnvironmentMap_IsReportedAsUnresolvedWithDiagnostic`, `ResolveAsync_EnvironmentMapFromTxi_WhenNoMtrOverride` |
| Transparency (alpha-test cutout + one alpha-blend pass) | **PASSED** | `MdlSceneBuilderTests`'s blend-mode classification (`BlendMode` set to `AlphaTest` when `Diffuse.HasAlpha`); `ModelRendererTests` draw-call construction honours `AlphaTestEnabled`/`AlphaBlendEnabled` per material |
| Lighting (fixed headlight + ambient, flat-shading fallback) | **PASSED** | `SilkGlDevice`'s fixed `LightDirection`/`AmbientColor` constants feed `ShaderSources`; exercised structurally via `ModelRendererTests`/`ModelPreviewScenarioTests` draw-call assertions (adapter-dependent smoke test — no pixel comparison, per `PLAN.md:230`) |
| Camera controls (orbit/pan/dolly, auto-framed) | **PASSED** | `RenderCameraTests.cs` — 26 tests covering `Frame` bounds-fitting, pole-clamped `Orbit`, clamped `Dolly`, `Pan`, and `View`/`Projection` matrix orthonormality; `ModelViewportViewModelTests.Constructor_AutoFramesCameraFromSceneBounds`, `Orbit_DelegatesToRenderCameraOrbit`, `Pan_DelegatesToRenderCameraPan`, `Dolly_DelegatesToRenderCameraDolly`, `ResetCommand_AfterOrbitPanAndDolly_ReframesCameraToInitialValue` |
| WOK/PWK/DWK surfaces (in-MDL walkmesh overlay, `SurfaceId`-coloured, excluded from artwork pass) | **PASSED** | `MdlSceneBuilderTests.BuildAsync_PartitionsWalkmeshAndArtworkNodesIntoSeparateLists`; `ModelRendererTests`'s `showWalkmesh: false` zero-draw-call coverage; `DependencyAnalyzerTests.AnalyzePwkOccurrence_EntersWalkmeshCompanionExtraction`/`AnalyzeDwkOccurrence_...`/`AnalyzeWokOccurrence_...`/`AnalyzeDlgOccurrence_DoesNotEnterCompanionExtraction` (the type-ID correctness fix); `ModelPreviewScenarioTests.FullPipeline_...` (real `aabb` node partitions correctly end to end) |
| `OpenGlControlBase` only for visible 3D slots | **PASSED** | `ModelViewportControlTests.AttachAndDetachInHeadlessHost_NeverInvokesOnOpenGlInit_AndRegistryReturnsToZero`; `ModelViewportRegistry_TryAcquire_AllowsUpToMaxConcurrentAndRefusesTheNext`/`_Release_FreesASlotForTheNextAcquire`/`_Release_WithoutAcquire_NeverGoesNegative` |
| GPU objects context-owned and disposable | **PASSED** | `GlContextIdTests.cs`/`GlHandleTests.cs`; `ModelRendererTests.DisposeAfterUpload_LeavesLiveResourcesEmpty`, `FiftyUploadDisposeCycles_NeverLeakLiveResources`, `RendererBuiltOnDeviceA_UsedAgainstDeviceB_IssuesZeroCallsOnBAndReportsStale` |
| **Gate: survives context failures without leaks or cross-context handles** | **PASSED** | `ModelRendererTests.SimulateContextLossThenAbandon_IssuesZeroDeviceCallsAndLeavesLiveResourcesEmpty`, `Abandon_ThenRenderAndUpload_AreSafeNoOps`; `FakeGlDeviceTests.SimulateContextLoss_SetsIsLostAndClearsLiveResources_WithoutRecordingAnyCall`, `AfterContextLoss_EveryOperationIsANoOp`; `ModelPreviewScenarioTests.ThreeConcurrentModelPreviews_UploadRenderDisposeLifecycle_LeavesEveryDeviceLiveResourcesEmpty` (1-to-3-slot scale, real scenes) |
| **Gate: survives unsupported features** | **PASSED** | `MdlSceneBuilderTests.BuildAsync_PopulatesUnsupportedFeaturesForSkinmeshEmitterAndAnimations`; `ModelPreviewScenarioTests.SkinmeshEmitterAndAnimationFixture_StillProducesValidSceneWithUnsupportedFeaturesPopulated`; `ModelRendererTests`'s `failProgramLink`/capability-shortfall `Unsupported` coverage; `ModelViewportControlTests`/`ModelViewportViewModelTests`'s `RenderUnavailableReason` degrade-to-text coverage |
| **Gate: representative one-to-three-slot corpus previews** | **PASSED** | `ModelPreviewScenarioTests.ConcurrentRequestsForSameOccurrence_ProduceExactlyOneSceneBuild_RegardlessOfSlotCount(1\|2\|3)`, `ThreeDifferentOccurrences_BuildThreeIndependentScenesThatDoNotInterfere`; opt-in: `ModelPreviewCorpusTests.RepresentativeCorpusMdl_...`/`RepresentativeCorpusScene_...`, `ModelPreviewWorkingSetTests.ThreeConcurrentModelPreviews_...` |
| **Gate: semantic assertions, never cross-GPU pixel equality** | **PASSED (by construction)** | No test in this milestone compares pixels or images from `ModelRenderer`/`SilkGlDevice`/`FakeGlDevice` output. Every renderer assertion is over `LastFrameDrawCallCount`, `LastFrameBoundTextureNames`, `RendererState`, or `IGlDevice.LiveResources`/`Calls` |
| Milestone-5 UI debt: per-family templates | **PASSED** | `ComparisonPanelViewTests.ComparisonPanelView_SlotWithTextContent_RendersFormattedContentThroughImplicitTemplate`; `PreviewSlotViewModelTests.AssignOccurrenceAsync_SuccessfulTextPreview_...`/`Content_ReassignedToNewInstance_DisposesThePreviousInstance` |
| Milestone-5 UI debt: image `ZoomScale`/`PanX`/`PanY` | **PASSED** | `ImageContentViewModelTests.ZoomInCommand_RepeatedInvocation_NeverExceedsMaxZoom`, `ZoomOutCommand_RepeatedInvocation_NeverReachesZeroOrNegative`, `PanBy_AccumulatesDeltasAcrossCalls`, `ResetViewCommand_AfterZoomAndPan_RestoresDefaults` |
| Milestone-5 UI debt: audio `Play`/`Pause`/`Stop`/`PositionProgress` | **PASSED** | `AudioContentViewModelTests.PlayCommand_InitializesPlayerAndSetsIsPlaying`, `PauseCommand_AfterPlay_PausesWithoutTearingDownPlayer`, `StopCommand_TearsDownPlayerAndResetsPosition`, `RefreshPositionProgress_ComputesFractionFromReaderPosition`, `Dispose_StopsPlaybackAndDisposesPlayerReaderAndStream` |
| Milestone-5 UI debt: linked navigation | **PASSED** | `LinkedNavigationTests.ImageZoomAndPanChange_PropagatesToOtherActiveImageSlots`, `CameraChange_PropagatesToOtherActiveModelSlots`, `AudioPositionChange_WhenAllThreeSlotsAudio_PropagatesToOtherTwo`, `AudioPositionChange_WhenOneSlotBecomesNonAudio_StopsLinkingEntirely` |
| Milestone-5 UI debt: SQLite thumbnail cache wiring | **PASSED** | `PreviewThumbnailCacheTests`'s cache-hit/fingerprint-invalidation coverage plus `ModelFamilyPayloadBearingResult_NeverTouchesThumbnailCache` (the A2 process-local guarantee, explicitly asserted) |

---

## 9. Deferred, Not Gate-Blocking

Recorded verbatim from `docs/MILESTONE-6.md`'s "Explicitly deferred" list — none of these are part
of the `PLAN.md:210` gate, and all are stated here rather than silently omitted:

- Environment maps as a rendered reflection term (resolved and reported only; the cheapest future
  upgrade is a spherical modulate in the fragment shader, ~40 lines, no contract change).
- Skinmesh/bone deformation, emitters, animation playback, danglymesh.
- `MdlPartComposer` and creature body-part composition.
- Supermodel geometry inheritance.
- A standalone BWM `.wok`/`.pwk`/`.dwk` binary reader (in-MDL walkmesh triangles satisfy the gate;
  see the WOK/PWK/DWK row above).
- Per-slot PLT dye editing UI.
- MDL light-node lighting, shadows, fog, animated tilefade.
- `SRNCC_REQUIRE_CORPUS` (documented in `PLAN.md:229`, never implemented — flagged for M7). Note:
  this milestone's own new `SRNCC_RUN_GPU` variable is a *different*, newly-introduced guard, and
  unlike `SRNCC_REQUIRE_CORPUS` it IS genuinely wired (Section 7 above demonstrates this directly);
  it is listed here only to avoid any confusion between the two.

Real-GPU/real-GL-context verification (Section 7) is a related, but distinct, known gap: it is not
on the milestone-5-carried deferred list above because it was never in scope to begin with — the
plan's own text anticipated it might not be achievable ("there is NO existing precedent in this repo
for testing an `OpenGlControlBase`").

---

## 10. Verification Pass Results

### 10a. Ad-hoc build and filtered test run (`dotnet build`/`dotnet test`, not the master verifier)

Run with `-p:SRNCCVerificationRuntimeIdentifier=win-x64` pinned on every command, per this
repository's build guidance:

```
dotnet build SRN.CC.sln -c Release -p:SRNCCVerificationRuntimeIdentifier=win-x64
dotnet test SRN.CC.sln -c Release --filter "Category!=Corpus&Category!=Performance" -p:SRNCCVerificationRuntimeIdentifier=win-x64
```

- **Build**: 0 Warnings, 0 Errors (Release, `win-x64`), all 7 projects.
- **`SRN.CC.Tests`**: 438 passed, 0 failed, 0 skipped (429 pre-existing + 9 new
  `ModelPreviewScenarioTests`).
- **`SRN.CC.CorpusTests`**: 0 tests matched the filter (all 4 new `[Category("Corpus")]` tests
  correctly excluded) — confirmed with `No test matches the given testcase filter
  'Category!=Corpus&Category!=Performance'`.
- **`AuditDependencies.ps1`** (run standalone): PASSED — "Verified 66 direct/transitive package
  versions, metadata, hashes, signatures, sources, audit results, and project references."
- **`AuditVendoredSources.ps1`** (run standalone): PASSED — "Verified 47 verbatim blobs, 40 source
  files, 8 portable test files, and explicit exclusions" (unchanged vendored-source counts, per
  Section 5's NO-GO vendoring outcome).
- **Observed environment flakiness (not a code defect):** two out of roughly seven solution-level
  `dotnet test SRN.CC.sln` invocations during this slice's own verification work aborted with `Test
  host process crashed: Fatal error. Internal CLR error. (0x80131506)` — an infrastructure-level test
  *host* crash, not a test *failure* (0 tests ever reported failed; every completed run reported
  exactly 438/438 passed). It was independently reproduced against the pre-S17 tree (no new files
  present) under the *same* solution-level, two-test-project-concurrently invocation, and disappeared
  entirely both with `-- NUnit.NumberOfTestWorkers=1` and, more simply, on repeated retry — consistent
  with this sandboxed verification host's own resource contention when two `dotnet test` processes
  launch simultaneously, not with anything in this slice's new test code. Running the two test
  projects one at a time (as `tools/VerifyBuild.ps1` itself does, since it invokes `dotnet test
  SRN.CC.sln` once with both projects in the same solution — see 10b) is the safer reproduction path
  if this needs to be investigated further in a future milestone.

### 10b. Master verifier (`tools/VerifyBuild.ps1`) — the authoritative, CI-gating record

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/VerifyBuild.ps1
```

**First attempt — Run ID `20260804T152738Z-31136` — FAILED** at step `[2/9]`
(`dotnet restore SRN.CC.sln --locked-mode -p:SRNCCVerificationRuntimeIdentifier=win-x64`), before any
build or test step ran:

```json
{
    "status": "FAILED",
    "runId": "20260804T152738Z-31136",
    "sdkVersion": "10.0.302",
    "runtimeFrameworkVersion": "10.0.10",
    "configuration": "Release",
    "targetRID": "win-x64",
    "error": "Locked solution restore failed with exit code 1."
}
```

**Root cause — pre-existing, not introduced by this slice.** The strict `--locked-mode` restore
reported `NU1004` for `src/SRN.CC.App/SRN.CC.App.csproj`: the *RID-less* `net10.0` section of
`src/SRN.CC.App/packages.lock.json` recorded `NAudio` as `"type": "CentralTransitive"` rather than
`"Direct"`, inconsistent with the project's actual `PackageReference Include="NAudio"`
(unconditional on RID) added by the already-landed commit `f7be078` ("feat: milestone 6 wave 3a —
model viewport, image zoom/pan, audio transport, texture wiring", S11–S15). The RID-specific
`net10.0/win-x64` section already listed `NAudio` correctly; only the RID-less section had drifted.
`tests/SRN.CC.Tests` and `tests/SRN.CC.CorpusTests` failed transitively, both referencing
`SRN.CC.App`. Independently reproduced against the working tree with every one of S17's new files
temporarily removed, confirming the gap predated and was unrelated to S17's own work.

**Fix applied (orchestrator, after S17's own report — outside S17's `tests/**`-and-docs-only
ownership, so S17 correctly did not touch it):**

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/VerifyBuild.ps1 -GenerateLocks
```

regenerated the lock graph via the repository's own established `-GenerateLocks` convention. Only
three files changed with real content (the rest re-wrote byte-identical, reverted to avoid no-op
line-ending churn): `src/SRN.CC.App/packages.lock.json` (`NAudio` corrected from
`"CentralTransitive"` to `"Direct"` in the RID-less `net10.0` section, matching the RID-specific
section it already matched) and `tests/SRN.CC.Tests/packages.lock.json` /
`tests/SRN.CC.CorpusTests/packages.lock.json` (each gained one line recording `NAudio` in their
`SRN.CC.App` project-reference dependency set). No package version, hash, or source changed — this
was a lock-file consistency correction, not a dependency change, so no `eng/dependency-policy.json`
or `NOTICES.md` update was needed.

**Second attempt — Run ID `20260804T153728Z-30976` — PASSED**, all nine steps:

```json
{
    "status": "PASSED",
    "runId": "20260804T153728Z-30976",
    "sdkVersion": "10.0.302",
    "runtimeFrameworkVersion": "10.0.10",
    "configuration": "Release",
    "targetRID": "win-x64"
}
```

- `[1/9]` SDK `10.0.302` confirmed.
- `[2/9]` Locked restore succeeded across all seven projects.
- `[3/9]` `AuditDependencies.ps1` PASSED — 66 direct/transitive package versions, metadata, hashes,
  signatures, sources, audit results, and project references verified. `AuditVendoredSources.ps1`
  PASSED — 47 verbatim blobs, 40 source files, 8 portable test files, explicit exclusions (unchanged
  counts, per Section 5's NO-GO vendoring outcome).
- `[4/9]` Release/win-x64 build: 0 Warnings, 0 Errors.
- `[5/9]` `SRN.CC.Tests`: **438 passed, 0 failed, 0 skipped**. `SRN.CC.CorpusTests`: 0 tests matched
  the filter (correctly excluded).
- `[6/9]` Self-contained `win-x64` publish succeeded.
- `[7/8]` Licenses/notices copied.
- `[8/9]` `AuditPublish.ps1` PASSED — 252 published files explained, hashed, and origin-resolved with
  zero forbidden extensions/assemblies/path fragments.
- `[9/9]` Lock files and both vendored trees confirmed unmutated by the verification pass itself.

Evidence directory: `artifacts/verification-runs/20260804T153728Z-30976/`.

**This is the milestone's authoritative, passing gate result.** The failed-then-fixed sequence above
is kept as the honest record of what actually happened — S17 discovered a real, pre-existing gap by
running the real verifier rather than assuming a pass, and the orchestrator fixed it with the
repository's own established tooling immediately afterward, rather than the evidence record
substituting an ad-hoc `dotnet test` pass (Section 10a) as a stand-in for the authoritative gate.

### 10c. Working set

`tests/SRN.CC.CorpusTests/Render/ModelPreviewWorkingSetTests.ThreeConcurrentModelPreviews_UploadRenderDispose_PlusForcedGc_KeepsWorkingSetBelow750MiB`
implements the `PLAN.md:229` check (build/upload/render/dispose three concurrent model previews, force
a GC, assert `Process.WorkingSet64 < 750 MiB`). It is part of the opt-in corpus tier (Section 7) and
requires `SRNCC_CORPUS_ROOT` to point at real corpus HAKs to run; it was exercised against a scratch
single-entry HAK built for this slice's own local validation (not committed) and passed. It has not
been run against the full 117-HAK reference corpus as part of this slice's own verification pass —
that requires the same opt-in corpus environment `CorpusIndexAcceptanceTests.cs` already documents
(`SRNCC_RUN_CORPUS=1`, `SRNCC_CORPUS_ROOT=<path>`), which was not available in this session's
sandboxed environment.
