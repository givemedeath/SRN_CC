# 3. Render Layer Placement and GPU Context Ownership

* Status: Accepted
* Date: 2026-08-04

## Context and Problem Statement

Milestone 6 adds a CPU-side MDL scene builder, a GL device abstraction, a GPU renderer, and an
Avalonia viewport control on top of the five-project architecture fixed by ADR 0002
(`SRN.CC.Core`, `SRN.CC.Formats`, `SRN.CC.Infrastructure`, `SRN.CC.Preview`, `SRN.CC.App`). Three
questions had to be settled before any render code could land: where does GPU-facing code live
without corrupting the existing Core/Formats independence boundary or the UI-free status of
`SRN.CC.Preview`; how does a typed scene payload travel through `PreviewResult` without leaking
into the SQLite `preview_cache` table, which is scoped to PNG thumbnails only; and how is a GL
context's lifetime owned so that "survives context failures without leaks or cross-context
handles" (`PLAN.md:210`) is a mechanically checkable property rather than a code-review promise.

## Decision Drivers

* `tools/AuditDependencies.ps1:102,161` hard-codes `$lockFiles.Count -ne 7` and
  `$assetsFiles.Count -ne 7`; `eng/dependency-policy.json`'s `approvedProjectReferences` map has
  exactly seven keys (the five `src/` projects plus `tests/SRN.CC.Tests` and
  `tests/SRN.CC.CorpusTests`); `SRN.CC.sln` and `ArchitectureTests.cs`'s explicit
  `SRN.CC.Core.csproj`/`SRN.CC.Formats.csproj`/`SRN.CC.Infrastructure.csproj`/
  `SRN.CC.Preview.csproj`/`SRN.CC.App.csproj` reference-graph assertions all enumerate the project
  set by name. Any new project is a five-way simultaneous edit (audit counts, policy map, solution
  file, architecture test, plus a brand-new `packages.lock.json`) before a single line of render
  code is written.
* ADR 0002's dependency-direction rules must not be violated: `SRN.CC.Core` accepts no project
  references and no UI references; `SRN.CC.Preview` may depend on `Core`/`Formats` only, never on
  `SRN.CC.App` or any Avalonia package.
* `docs/compliance/FORMAT-PROVENANCE.md:17-20` disqualifies the format-library author from making
  an independent-author declaration for the five upstream SWLOR render-replacement files (of which
  `MdlMeshBuilder.cs` is one), and `tools/AuditVendoredSources.ps1` greps every `src/**`/`tests/**`
  `.cs`/`.csproj`/`.axaml` file for a disqualifying provenance keyword and `GPL` at word boundaries.
* `PLAN.md:210`'s gate requires "GPU objects context-owned and disposable" and "survive... context
  failures without leaks or cross-context handles" — a property that needs a structural mechanism
  (a comparable context identity, not a boolean flag) to be assertable without a real GPU.
* `IPreviewPayload` (`src/SRN.CC.Core/Preview/IPreviewPayload.cs`) must never reach the
  `preview_cache` SQLite table (`src/SRN.CC.Infrastructure/Cache/SqliteCacheService.cs`), which is
  schema-fixed to PNG bytes (`png_bytes`, `width`, `height`) and has no column for arbitrary scene
  geometry.

## Considered Options

1. A new `SRN.CC.Render` project hosting the CPU scene model, the GL device abstraction, and the
   renderer, referenced by both `SRN.CC.Preview` (to attach scene payloads) and `SRN.CC.App` (to
   host the viewport control).
2. All render code — CPU scene model, GL device abstraction, renderer — inside
   `SRN.CC.Preview/Render/` and `Render/Gl/`, with only the Avalonia `OpenGlControlBase` subclass
   living in `SRN.CC.App`.
3. Render types folded directly into `SRN.CC.Core`, alongside the identity/curation domain model.

## Decision Outcome

Chosen option: **Option 2 — the entire render layer lives in `SRN.CC.Preview`; only
`ModelViewportControl` lives in `SRN.CC.App`.**

Concretely:

* `src/SRN.CC.Preview/Render/{RenderScene,RenderMesh,RenderMaterial,TextureImage,
  MaterialBlendMode,RenderCamera,IMdlSceneBuilder,MdlSceneBuilder,ModelScenePayload,
  ModelSceneCache,TextureDecoder,TextureResolver,MtrDocument}.cs` — the CPU-side, GPU-agnostic
  scene model and builder. Depends only on `SRN.CC.Core`/`SRN.CC.Formats`, per ADR 0002; zero
  Avalonia references.
* `src/SRN.CC.Preview/Render/Gl/{IGlDevice,GlContextId,GlHandle,GlCapabilities,GlDrawCall,
  GlResourceRegistry,ShaderSources,SilkGlDevice,FakeGlDevice,ModelRenderer}.cs` — the GL device
  abstraction and renderer. Depends on `Silk.NET.OpenGL` (already pinned and approved in
  `eng/dependency-policy.json`, previously with zero consuming code) but still zero Avalonia
  references.
* `src/SRN.CC.App/Views/ModelViewportControl.cs` — an `Avalonia.OpenGL.Controls.OpenGlControlBase`
  subclass, and the **only** file in the entire milestone that imports both an `Avalonia.OpenGL`
  namespace and an `SRN.CC.Preview.Render`/`SRN.CC.Preview.Render.Gl` namespace.

### Rejected options

**Option 1 (new `SRN.CC.Render` project)** was rejected because every one of the decision drivers
above becomes a required edit for isolation that folder boundaries already provide inside
`SRN.CC.Preview`: `AuditDependencies.ps1`'s two hard-coded `Count -ne 7` checks,
`eng/dependency-policy.json`'s `approvedProjectReferences` map, `ArchitectureTests.cs`'s
reference-graph assertions, `SRN.CC.sln`, and a brand-new `packages.lock.json` (itself a locked-mode
restore risk per this repo's own build guidance). None of that buys additional dependency-direction
safety: `SRN.CC.Preview` already sits at exactly the layer (depends on `Core`/`Formats`, depended on
by `App`) a render project would occupy, and folders inside one project are free.

**Option 3 (render types in `SRN.CC.Core`)** was rejected because ADR 0002 scopes `SRN.CC.Core` to
identity/curation domain rules with zero project references and zero UI references. `RenderScene`
and friends are neither identity nor curation data — they are Preview-layer, GPU-facing derived
data built *from* an already-identified `AssetOccurrence`'s parsed MDL bytes. Moving them into Core
would also pull `Silk.NET.OpenGL`-adjacent concerns (even indirectly, via anything the renderer
touches) toward the one project every other project depends on.

### The sole Avalonia/GL coupling point

Per constraint 5 recorded in `docs/MILESTONE-6.md`, Avalonia's `OpenGlControlBase` exposes
`OnOpenGlInit(GlInterface gl)`, and `gl.GetProcAddress` is a `Func<string, IntPtr>` — exactly the
constructor signature `Silk.NET.OpenGL.GL.GetApi(Func<string, IntPtr>)` accepts. `SilkGlDevice`'s
public constructor is `SilkGlDevice(Func<string, IntPtr> getProcAddress)`
(`src/SRN.CC.Preview/Render/Gl/SilkGlDevice.cs:42`), so `ModelViewportControl.OnOpenGlInit` is:

```csharp
protected override void OnOpenGlInit(GlInterface gl)
{
    var device = new SilkGlDevice(gl.GetProcAddress);   // Preview type; delegate + ints only
    var renderer = new ModelRenderer(device);
    ...
}
```

No Avalonia type (`GlInterface`, `OpenGlControlBase`, `Point`, `PointerEventArgs`, ...) is ever
passed into `SRN.CC.Preview`; no Preview/render type (`RenderScene`, `IGlDevice`, `ModelRenderer`,
...) is referenced from any other `SRN.CC.App` file. `ArchitectureTests.cs` needed no edit for this
milestone, and `Silk.NET.OpenGL` finally has consuming code without ever having been re-approved or
re-scoped in `eng/dependency-policy.json`.

## `IPreviewPayload` is process-local, never persisted

`IPreviewPayload` (`src/SRN.CC.Core/Preview/IPreviewPayload.cs`) is a marker interface — `string
PayloadKind`, `long ApproximateByteSize` — added as a trailing, defaulted member on
`PreviewResult` (`src/SRN.CC.Core/Preview/PreviewResult.cs`). `ModelScenePayload`
(`src/SRN.CC.Preview/Render/ModelScenePayload.cs`) is its only production implementation and wraps
a `RenderScene` by reference, not by any serializable form.

Two independent boundaries keep this payload out of persistent storage:

1. **`PreviewEngine`'s thumbnail cache is wired for `Image`-family results only.**
   `IPreviewThumbnailCache` (`src/SRN.CC.Preview/IPreviewThumbnailCache.cs`) sits behind
   `PreviewEngine` (`src/SRN.CC.Preview/PreviewEngine.cs`), never behind any slot view model, and
   its SQLite-backed implementation
   (`src/SRN.CC.Infrastructure/Cache/SqlitePreviewThumbnailCache.cs`) writes to the `preview_cache`
   table, whose schema (`SqliteCacheService.cs:184-198`) is fixed to `png_bytes`/`width`/`height` —
   there is no column, and never was, for arbitrary scene geometry.
2. **`PreviewThumbnailCacheTests.ModelFamilyPayloadBearingResult_NeverTouchesThumbnailCache`**
   (`tests/SRN.CC.Tests/Cache/PreviewThumbnailCacheTests.cs:210`) asserts this explicitly: a
   `PreviewResult` with a non-null `Payload` produces `cache.TryGetCallCount == 0` and
   `cache.SaveCallCount == 0` — the cache is never even consulted for a payload-bearing result, let
   alone written to.

Scene reuse across repeated/concurrent requests for the same occurrence instead goes through
`ModelSceneCache` (`src/SRN.CC.Preview/Render/ModelSceneCache.cs`) — an in-process,
single-flight-deduplicating, budget-evicted (`ModelSceneCacheBudgetBytes = 128 MiB`) dictionary
keyed by `(SourceId, OccurrenceLocator, Sha256)`. It holds live `RenderScene` object references in
memory for the lifetime of the process and is discarded, not persisted, on shutdown. This contrast
— `ModelSceneCache` in-process-only vs. `preview_cache` on-disk-and-PNG-only — is the entire point
of keeping `IPreviewPayload` a Core marker interface rather than a concrete, (de)serializable type:
nothing about its shape invites a persistence layer to reach for it.

## Deliberate non-import of upstream Toolset render files

`docs/MILESTONE-6-0-VENDORING-DECISION.md` records a **NO-GO** verdict on vendoring
`MdlMeshBuilder.cs`, reached via an upstream fetch/verify spike (slice S1) against the pinned SWLOR
commit `8202faa203eddd6f4972104d22ea5740e23f20f7`:

* At that pin, `MdlMeshBuilder.cs` does not live under `SWLOR.NWN.Formats/Mdl/` as architecture
  decision A5 assumed — it lives at `SWLOR.Toolset.Domain/Render/MdlMeshBuilder.cs`, inside the
  Toolset-domain codebase this milestone already decided not to vendor, and that directory also
  contains `REPLACEMENT-PROVENANCE.md` recording that this file is one of upstream's own "five
  render files replaced as part of the external format dependency removal" from a GPL-licensed
  predecessor library — the same disqualifying history `docs/compliance/FORMAT-PROVENANCE.md:17-20`
  already recorded for this repository's format-library author.
* `MdlMeshBuilder.cs` does not compile standalone: it requires `PosedNode` and
  `MdlAnimationPose.SampledAnimation`/three static methods, all defined in a second,
  never-reviewed Toolset-domain file (`MdlAnimationPose.cs`) that
  `docs/compliance/swlor-post-attestation-delta-review.md` never dispositioned.
* The existing "Accept (Verbatim)" disposition in that delta review was written against a diff at
  a different upstream commit and a different upstream path; it does not cover the artifact as it
  exists at the pinned commit.

Per architecture decision A5's own stated default ("if the spike returns NO-GO, `MdlSceneBuilder`
simply stays first-party and nothing else moves"), `MdlSceneBuilder`
(`src/SRN.CC.Preview/Render/MdlSceneBuilder.cs`) was built entirely first-party from the start, and
`tools/AuditVendoredSources.ps1`'s hard-coded `40`/`8` source/portable-test counts are unchanged by
this milestone. This is independently reinforced by the plan's separate, unconditional decision
(A5's second paragraph, restated in `docs/MILESTONE-6.md`'s Architecture decisions) never to vendor
any `SWLOR.Toolset/` Avalonia/Silk viewport code at all, for three compounding reasons: the
unresolved independent-author declaration; `AuditVendoredSources.ps1`'s word-boundary grep for a
disqualifying provenance keyword and `GPL` across every production and test source file; and the
fact that upstream's viewport is coupled to its own app shell and would need adaptation regardless,
which the vendoring manifest schema treats as requiring a fresh compliance review (`adaptationReason`
+ `reviewReference`) either way.

**Compliance gate to reopen this decision in a future milestone:** at minimum, (1) an updated
`docs/compliance/swlor-post-attestation-delta-review.md` entry recording the correct
`SWLOR.Toolset.Domain/Render/` upstream paths for both `MdlMeshBuilder.cs` and
`MdlAnimationPose.cs`; (2) a fresh independent-author and independent-reviewer disposition per
architecture decision A5, since the existing one does not cover either file as they exist at the
pinned commit; and only then (3) the mechanical `eng/vendored-sources-manifest.json` entry plus the
three `tools/AuditVendoredSources.ps1` count-site edits (the `-ne 40` condition, its violation
message, and the `"40 source files"` success string, all moving to 41).

## GPU context ownership/lifetime contract

The gate's "GPU objects context-owned and disposable" and "survives... context failures without
leaks or cross-context handles" clauses are satisfied structurally, not by convention:

```csharp
public readonly record struct GlContextId(long Value)
{
    public static GlContextId Next();   // Interlocked.Increment; monotonic, process-wide
    public static readonly GlContextId None;
}

public readonly record struct GlHandle(GlContextId Context, uint Name, GlResourceKind Kind);
```

Every `IGlDevice` (`SilkGlDevice`, `FakeGlDevice`) mints a fresh `GlContextId` at construction —
once per `OnOpenGlInit`, i.e. once per real or simulated GL context. Every `GlHandle` a device hands
out carries that same `GlContextId`. `ModelRenderer` captures its device's `ContextId` once at
construction and repeats a one-line guard at every entry point (`Initialize`, `Upload`, `Render`):

```csharp
if (_device.IsLost || _device.ContextId != ContextId) { State = RendererState.Stale; return; }
```

This makes "no cross-context handles" directly, mechanically assertable without any real GPU:
`ModelRendererTests.RendererBuiltOnDeviceA_UsedAgainstDeviceB_IssuesZeroCallsOnBAndReportsStale`
(`tests/SRN.CC.Tests/Preview/Render/Gl/ModelRendererTests.cs`) builds a renderer on device A,
re-points it at device B (a different `GlContextId`, from the monotonic counter) via a
test-only `AttachDeviceForTesting` seam, and asserts device B receives zero calls while the
renderer reports `RendererState.Stale`.

The two teardown paths are asymmetric by design, per architecture decision A4:

* **`Dispose()` (normal teardown, `OnOpenGlDeinit`)** — deletes every GL name the renderer created,
  via the still-live device, then marks `RendererState.Disposed`.
* **`Abandon()` (context loss, `OnOpenGlLost`)** — forgets every tracked resource **without issuing
  a single device call**, then marks `RendererState.Stale`. On real context loss the driver has
  already invalidated every name in that context; calling `glDelete*` against a dead context is how
  a driver call crashes or raises a spurious error, not how a clean release happens.

`FakeGlDevice.LiveResources` (backed by `GlResourceRegistry`) is the leak-assertion hook exercised
throughout the test suite at three scales: unit (`ModelRendererTests`, 50 upload/dispose cycles plus
a simulated-context-loss-then-`Abandon` case that asserts zero device calls), scenario
(`tests/SRN.CC.Tests/Scenarios/ModelPreviewScenarioTests.cs`'s
`ThreeConcurrentModelPreviews_UploadRenderDisposeLifecycle_LeavesEveryDeviceLiveResourcesEmpty`,
covering the full CPU-pipeline-to-GPU-upload seam for one-to-three real scenes), and corpus
(`tests/SRN.CC.CorpusTests/Render/ModelPreviewCorpusTests.cs`, the same check against real,
corpus-scale geometry). `ModelViewportControl` (`src/SRN.CC.App/Views/ModelViewportControl.cs`)
wires the two Avalonia lifecycle callbacks straight onto these two methods with no logic of its
own: `OnOpenGlDeinit` calls `_renderer?.Dispose(); _device?.Dispose();`, `OnOpenGlLost` calls
`_renderer?.Abandon(); _device?.MarkLost();`.

## Consequences

* Positive: zero edits to `eng/dependency-policy.json`, `tools/AuditDependencies.ps1`,
  `SRN.CC.sln`, `ArchitectureTests.cs`, or any `packages.lock.json` were needed anywhere in this
  milestone's five waves — the single largest source of merge contention a parallel-subagent plan
  could have hit was designed away before slicing began.
* Positive: the entire render layer (CPU scene model, GL device abstraction, renderer) is testable
  with plain NUnit and no GPU, via `FakeGlDevice` — `SilkGlDevice` is exercised only inside
  `ModelViewportControl`, the one file this decision deliberately keeps thin.
* Positive: `IPreviewPayload`'s process-local scope means a future change to `RenderScene`'s shape
  never touches a persistence/migration concern — there is no schema to migrate.
* Negative: real-GPU/real-GL-context verification remains a documented gap in this repository's
  test infrastructure (no windowing package is an approved dependency; Avalonia.Headless has no GL
  backend) — see
  `ModelPreviewCorpusTests.RealGpuContext_CannotBeConstructedInThisRepositorysTestInfrastructure_DocumentedGap`
  for the investigation and its negative result, and `docs/evidence/MILESTONE-6-EVIDENCE.md` for how
  this is scoped against `PLAN.md:230`'s "adapter-dependent smoke tests, not cross-GPU pixel
  equality" acceptance bar.
* Negative: reopening the `MdlMeshBuilder.cs` vendoring question requires the compliance work listed
  above before any mechanical audit-count change — this is accepted as the cost of not vendoring
  code this milestone's own investigation found improperly scoped for review.
