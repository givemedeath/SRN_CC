# Milestone 6 - Phase 2: Scene Building, Textures, GL Device, Thumbnail Cache (Wave 1)

## Scope

Build everything that turns the wave-0 contracts into working subsystems: the MDL → `RenderScene`
builder, the texture resolution pipeline bridging Core/Preview/App, the GL device abstraction plus
`ModelRenderer` (the largest slice by line count, but off the critical path), and the
`PreviewEngine` thumbnail cache wiring left over from Milestone 5. All four slices are parallel;
S6b, S7, and S8 each depend only on wave-0 outputs (S6a, S3, S2, S4), and S8b depends on nothing new.

## Deliverables

- `MdlSceneBuilder` implementing `IMdlSceneBuilder`: transform composition, walkmesh/artwork split,
  material dedup, normal generation, blend-mode classification, unsupported-feature reporting,
  budget enforcement.
- The five model-scene budget constants added to `PreviewStreamHelpers.cs` (owned exclusively by
  S6b for the whole milestone).
- `ITextureSource` (Core), `TextureResolver` (Preview), `WorkspaceTextureSource` (App): full
  texture-name resolution ladder with workspace-over-base-game precedence.
- `src/SRN.CC.Preview/Render/Gl/` — the complete `IGlDevice` contract, `SilkGlDevice`,
  `FakeGlDevice`, and `ModelRenderer`, dual GLES-3.0/desktop-GL-3.3 shader variants, and the bulk of
  the milestone's leak/context-loss/cross-context gate evidence.
- `IPreviewThumbnailCache` wired into `PreviewEngine`, backed by `SqlitePreviewThumbnailCache`.

## Detailed tasks

### S6b — MDL scene builder (needs S6a, S3, S2)
Owns NEW `src/SRN.CC.Preview/Render/MdlSceneBuilder.cs`; **MODIFY
`src/SRN.CC.Preview/PreviewStreamHelpers.cs` (exclusive for the whole milestone — the five budget
constants)**; NEW `tests/SRN.CC.Tests/Preview/Render/MdlSceneBuilderTests.cs`.
- [ ] Implement `IMdlSceneBuilder.BuildAsync`: walk `GeometryRoot`, compose world transforms from
      `Position`/`Orientation`/`Scale` through ancestors (rest pose only).
- [ ] Split `MdlTrimeshNode.IsWalkmesh` nodes into `WalkmeshMeshes`; everything else into
      `ArtworkMeshes`.
- [ ] Deduplicate materials across meshes into `RenderScene.Materials`, indexed by `MaterialIndex`.
- [ ] Generate face normals when a node's `Normals` array is empty.
- [ ] Classify `MaterialBlendMode` (Opaque/AlphaTest/AlphaBlend) from texture alpha presence and TXI
      hints.
- [ ] Populate `RenderScene.UnsupportedFeatures` for `MdlSkinmeshNode`, `MdlEmitterNode`, and
      `Animations` without failing the build.
- [ ] Enforce `SceneBuildBudget` (CPU bytes, texture bytes, max textures, max draw calls); on
      exceeding a budget, mark the offending materials untextured and emit a diagnostic rather than
      throwing.
- [ ] Append the five model-scene budget constants
      (`ModelSceneCpuBudgetBytes`, `ModelSceneCacheBudgetBytes`, `ModelTextureSetBudgetBytes`,
      `ModelMaxTextures`, `ModelMaxDrawCalls`) to `PreviewStreamHelpers.cs` in the existing const
      style.
- [ ] Support cancellation mid-walk.
- [ ] Support a null `ITextureSource` producing an untextured scene plus diagnostics rather than
      failing.

**Exit Criteria**
- [ ] Semantic assertions on synthetic ASCII and binary fixtures: vertex/face counts, three-level
      transform composition, walkmesh/artwork partition, material dedup count.
- [ ] Budget-exceeded degradation happens without exception.
- [ ] Mid-walk cancellation is honored.
- [ ] Null-`ITextureSource` case produces an untextured scene plus diagnostics.

### S7 — Texture pipeline (needs S3, S4, S6a)
Owns NEW `src/SRN.CC.Core/Services/ITextureSource.cs`,
`src/SRN.CC.Preview/Render/TextureResolver.cs`,
`src/SRN.CC.App/Services/WorkspaceTextureSource.cs`; NEW
`tests/SRN.CC.Tests/Preview/Render/TextureResolverTests.cs`,
`tests/SRN.CC.Tests/App/Services/WorkspaceTextureSourceTests.cs`. Must not touch `App.axaml.cs` or
`MainWindowViewModel.cs` (S15 owns wiring) or `DependencyLocator.cs`.
- [ ] Declare `ITextureSource.OpenTextureAsync(resref, TextureKind, CancellationToken)` returning
      `TextureLookupResult?` and never throwing.
- [ ] Declare `TextureKind` (Diffuse, Lightmap, Material, EnvironmentMap, TextureInfo), `TextureOrigin`
      (Workspace, BaseGame), and `TextureLookupResult` (Identity, ResourceType, Payload, Origin).
- [ ] Implement `TextureResolver` (Preview) against `ITextureSource` only: resolution ladder per name
      is `<name>.mtr` (2072, whose `texture0`/`bumpmap`/`envmap` override the MDL `Bitmap`) →
      `<name>.dds` → `<name>.tga` → `<name>.plt` → `<name>.txi` (2022) sidecar for
      `envmaptexture`/`blending`/`isbumpmap` flags.
- [ ] Resolve any discovered `envmap` name through the same ladder and report it only (no shading).
- [ ] Implement `WorkspaceTextureSource` (App) over `WorkspaceState` + `ISourceReaderDispatcher` +
      optional base-game catalog: curated workspace first, base-game KEY/BIF second.
- [ ] Enforce texture byte and count caps (`ModelTextureSetBudgetBytes`, `ModelMaxTextures` from S6b).

**Exit Criteria**
- [ ] Precedence proven: MTR overrides Bitmap; DDS over TGA over PLT.
- [ ] Workspace beats base game.
- [ ] Unresolved names land in `RenderMaterial.UnresolvedTextures` and never throw.
- [ ] Texture byte and count caps are enforced.

### S8 — GL device abstraction + renderer (needs S6a only — off the critical path)
Owns NEW `src/SRN.CC.Preview/Render/Gl/` — `IGlDevice.cs`, `GlContextId.cs`, `GlHandle.cs`,
`GlCapabilities.cs`, `GlDrawCall.cs`, `GlResourceRegistry.cs`, `SilkGlDevice.cs`, `ModelRenderer.cs`,
`ShaderSources.cs`, `FakeGlDevice.cs`; NEW `tests/SRN.CC.Tests/Preview/Render/Gl/*`.
- [ ] Declare `GlContextId` (monotonic `Interlocked.Increment`-based `readonly record struct`),
      `GlHandle` (Context + Name + Kind), `GlResourceKind` enum.
- [ ] Declare `GlCapabilities` (IsEmbeddedProfile, MajorVersion, MinorVersion,
      HasVertexArrayObjects, HasNonPowerOfTwoTextures, MaxTextureSize, GlslVersionDirective,
      RendererName) and land the capability **probe before any shader work**.
- [ ] Implement `IGlDevice`: `CreateBuffer`, `CreateTexture2D`, `CreateProgram` (`.Name == 0` on link
      failure, `linkLog` out param), `Delete`, `Draw`, `MarkLost` (every subsequent op becomes a
      no-op), `LiveResources` (leak-assertion hook).
- [ ] Implement `SilkGlDevice : IGlDevice` wrapping `Silk.NET.OpenGL.GL.GetApi(Func<string, IntPtr>)`.
- [ ] Ship dual GLES-3.0 / desktop-GL-3.3 shader variants in `ShaderSources.cs`, selected by the
      capability probe (constraint 4 — ANGLE gives GLES via EGL on Windows).
- [ ] Implement `FakeGlDevice : IGlDevice` (public, for both test projects):
      `FakeGlDevice(GlCapabilities? capabilities = null, bool failProgramLink = false)`,
      `Calls` (`IReadOnlyList<GlCallRecord>`), `SimulateContextLoss()`.
- [ ] Implement `ModelRenderer`: `ContextId`, `State` (Uninitialized/Ready/Unsupported/Stale/
      Disposed), `UnsupportedReason`, `LastFrameDrawCallCount`, `LastFrameBoundTextureNames`,
      `Initialize(capabilities)`, `Upload(scene)`, `Render(framebuffer, camera, px, py,
      showWalkmesh)`, `Abandon()` (forget, **zero GL calls**), `Dispose()` (delete every name).
- [ ] Add the cross-context guard at every renderer entry point:
      `if (_device.IsLost || handle.Context != _device.ContextId) { State = RendererState.Stale; return; }`.

**Exit Criteria** — this slice carries most of the gate, all under plain NUnit with no Avalonia and
no GPU:
- [ ] Dispose-after-upload leaves `LiveResources` empty.
- [ ] 50 upload/dispose cycles leave `LiveResources` empty.
- [ ] `SimulateContextLoss()` then `Abandon()` issues **zero** GL calls and leaves `LiveResources`
      empty.
- [ ] A renderer built on device A and used against device B issues zero calls and reports `Stale`.
- [ ] `failProgramLink: true` yields `RendererInitResult(false, reason)` and never throws.
- [ ] A `MaxTextureSize` below the scene's largest texture yields `Unsupported` with a reason.
- [ ] Draw-call count and bound-texture names match the scene's material set.
- [ ] `showWalkmesh: false` produces zero draw calls sourced from `WalkmeshMeshes`.

### S8b — PreviewEngine thumbnail cache (M5 debt; needs nothing)
Owns MODIFY `src/SRN.CC.Preview/PreviewEngine.cs`; NEW
`src/SRN.CC.Preview/IPreviewThumbnailCache.cs`,
`src/SRN.CC.Infrastructure/Cache/SqlitePreviewThumbnailCache.cs`; NEW
`tests/SRN.CC.Tests/Cache/PreviewThumbnailCacheTests.cs`.
- [ ] Declare `IPreviewThumbnailCache` in `SRN.CC.Preview` (thin interface over
      `ISqliteCacheService.TryGetPreviewAsync`/`SavePreviewAsync`).
- [ ] Implement `SqlitePreviewThumbnailCache` in `SRN.CC.Infrastructure` over the existing
      `preview_cache` table and LRU eviction.
- [ ] Wire `IPreviewThumbnailCache` into `PreviewEngine` — **not** into any slot VM — keeping cache
      concerns out of the contended UI files.
- [ ] Ensure `IPreviewPayload` is never passed to the cache: only PNG bytes/dimensions are stored.

**Exit Criteria**
- [ ] An image preview hits the cache on second request for the same source fingerprint + locator.
- [ ] A fingerprint change invalidates the cached entry.
- [ ] `IPreviewPayload` is **never** written to the cache — assert this explicitly in a test.

## Notes
- S8 is the largest slice by line count in the whole milestone but sits entirely off the critical
  path (`S2 → S6a → S6b → S9 → S10 → S11 → S17`), which is why S6 was split into S6a/S6b in wave 0
  and wave 1 respectively.
- S6b has exclusive ownership of `PreviewStreamHelpers.cs` for the **entire milestone**, not just
  this wave — no other slice may touch it, even later.
- S7 must not touch `App.axaml.cs`, `MainWindowViewModel.cs`, or `DependencyLocator.cs`; wiring the
  texture source accessor into those files is S15's job in wave 3.
