# Milestone 6 — Advanced 3D Previews

## Summary

`PLAN.md:206-210` defines Milestone 6 as **Advanced 3D previews**: adapt only the required
first-party SWLOR render-neutral and Avalonia/Silk.NET viewport components; support ASCII/binary
MDL geometry, transforms, static materials/textures, PLT/MTR/environment maps, transparency,
lighting, camera controls, and WOK/PWK/DWK surfaces; create `OpenGlControlBase` only for visible 3D
slots with GPU objects context-owned and disposable.

**Gate:** representative one-to-three-slot corpus previews survive unsupported features and context
failures without leaks or cross-context handles. `PLAN.md:230` further requires acceptance to use
*semantic geometry/material assertions and adapter-dependent smoke tests, not cross-GPU pixel
equality*.

Why now: Milestone 5 shipped preview providers but left the preview surface **text-only**. This
milestone turns that into a working 3D model/walkmesh viewport in up to three comparison slots, and
closes the remaining Milestone 5 UI debt (per-family templates, image zoom/pan, audio transport,
linked navigation, thumbnail cache wiring) in the same pass, delivered as parallel subagent slices
on one branch.

### Decisions taken with the user

| Decision | Choice |
|---|---|
| Execution model | **Waves on one branch** (`claude/milestone-6-*`), parallel subagents per wave, strict file ownership, review between waves, single PR |
| Environment maps | **Discover and report only** — resolve names from MTR/TXI, expose `RenderMaterial.EnvironmentMapName`, shade diffuse only; recorded as explicitly deferred |
| Milestone 5 UI debt | **Close all of it** — templates, zoom/pan, audio transport, linked navigation, SQLite thumbnail cache |

---

## Verified baseline

Verified state of the tree at the start of Milestone 6:

- **`MdlReader` already parses full ASCII and binary geometry** (vertices, normals, UVs, faces,
  node hierarchy, walkmesh flags), hardened with `GuardedBinaryReader`/`AllocationBudget`, 20 tests
  green. `MdlPreviewProvider.cs` parses all of it and **throws the geometry away**, emitting text
  only.
- **`Silk.NET.OpenGL 2.23.0` is pinned**, referenced by `SRN.CC.Preview`, locked, and approved in
  `eng/dependency-policy.json` — with **zero consuming code anywhere in the repo**.
- **`PreviewResult` has no typed payload channel** (only `byte[]? RawPayload`, which is read in
  exactly one place: `tests/SRN.CC.Tests/Preview/PreviewProviderTests.cs:67`).
- **`ComparisonPanelView.axaml:45-47` renders every slot as a single read-only Consolas `TextBox`.**
  There is nowhere to host a viewport, an image, or an audio transport.
- **`ISqliteCacheService.TryGetPreviewAsync`/`SavePreviewAsync` exist** with a full `preview_cache`
  table and LRU eviction, and have **zero callers**.

### Verified constraints that shape the design

1. `tools/AuditDependencies.ps1:102,161` hard-codes `Count -ne 7` for lock files *and* assets files.
   **Any new project breaks the dependency audit**, on top of `ArchitectureTests`,
   `eng/dependency-policy.json` `approvedProjectReferences`, `SRN.CC.sln`, and a new lock file.
2. `tools/AuditVendoredSources.ps1` hard-codes `40`/`8` in three places (condition line 97, message
   line 98, success string line 121). Any new vendored file requires all three.
3. `docs/compliance/FORMAT-PROVENANCE.md:17-20` records that historical render-file exposure
   **"disqualifies the same author from making the plan's independent-author declaration for the
   five replacement render files."** Compounding this, `AuditVendoredSources.ps1` greps every
   `src/**` and `tests/**` `.cs`/`.csproj`/`.axaml` for word-boundary `Radoub` or `GPL` — a vendored
   Toolset render file with a provenance comment fails instantly. **Do not write those two words
   into any source or test file, including comments.**
4. Avalonia on Windows renders through ANGLE (`Avalonia.Angle.Windows.Natives` is in
   `src/SRN.CC.App/packages.lock.json` and `eng/publish-policy.json`), so the GL context is
   **OpenGL ES via EGL**, not desktop GL. Treat this as a capability probe, never an assumption.
5. `OpenGlControlBase` (Avalonia 12.1.0, `Avalonia.OpenGL.Controls`) exposes `OnOpenGlInit`,
   `OnOpenGlRender`, `OnOpenGlDeinit`, **`OnOpenGlLost`**, `RequestNextFrameRendering`, and
   `GlInterface.GetProcAddress`. `Silk.NET.OpenGL.GL.GetApi(Func<string, IntPtr>)` accepts exactly
   that delegate — so **no Avalonia type ever has to cross into the render layer**.
6. `MdlFace.VertexIndex0/1/2` are `ushort` and `MdlReader.MaximumVertices == ushort.MaxValue`.
   Use `ushort` indices end-to-end; this sidesteps `GL_OES_element_index_uint` on weak ES drivers.
7. `src/SRN.CC.Preview/DependencyAnalyzer.cs:68-70` is wrong: it routes `2029`/`2030` to companion
   extraction, but `ResourceTypes.cs:23,30` confirms `2029=dlg`, `2030=itp`; the real values are
   `2052=dwk`, `2053=pwk` (and `2016=wok`). Harmless today only because `ExtractCompanionDependencies`
   returns an empty set.
8. `NOTICES.md` is stale: Pfim, NAudio, and all three Silk.NET packages are `mustShipNotice: true`
   in `eng/dependency-policy.json` but absent from the notices table. `AuditDependencies.ps1` checks
   the flag, not the file — so nothing fails today, but M7's release audit will.

---

## Architecture decisions

### A1 — No new project. Render layer lives in `SRN.CC.Preview`; only the control lives in `SRN.CC.App`

CPU render data, the mesh builder, the GL device abstraction, **and the renderer** all go in
`src/SRN.CC.Preview/Render/` and `Render/Gl/`. `SRN.CC.App/Views/ModelViewportControl.cs` is the
only file that sees both Avalonia and GL, and it couples via constraint 5:

```csharp
protected override void OnOpenGlInit(GlInterface gl)
    => _device = new SilkGlDevice(gl.GetProcAddress);   // Preview type; delegate + ints only
```

**Consequences:** Preview stays UI-free so `ArchitectureTests` needs no edit; `Silk.NET.OpenGL`
stays where it already is and finally gets used; and there are **zero edits to
`eng/dependency-policy.json`, `tools/AuditDependencies.ps1`, `SRN.CC.sln`, or any
`packages.lock.json`** — removing the biggest serialization point from a parallel plan.

**Rejected:** a `SRN.CC.Render` project (costs an audit-count edit, a policy edit, an
architecture-test edit, a solution edit, and a new lock file, for isolation that folders already
provide); render types in Core (ADR 0002 scopes Core to identity/curation domain rules).

### A2 — One trailing optional member on `PreviewResult`, typed as a Core marker interface

```csharp
// NEW src/SRN.CC.Core/Preview/IPreviewPayload.cs
public interface IPreviewPayload
{
    string PayloadKind { get; }        // e.g. "model-scene/v1"
    long ApproximateByteSize { get; }
}

// MODIFY src/SRN.CC.Core/Preview/PreviewResult.cs — one added line
public sealed record PreviewResult(
    AssetOccurrence Occurrence, PreviewFamily Family, bool IsSuccess,
    string? MetadataText, byte[]? RawPayload, string? FormattedContent,
    string? ErrorMessage, IReadOnlyList<string> Diagnostics,
    IPreviewPayload? Payload = null);      // NEW
```

Migration cost is effectively zero: the member is trailing and defaulted, so all seven providers,
both `PreviewEngine` failure paths, and every test compile unchanged. No test constructs,
deconstructs, or equality-compares a `PreviewResult`, so the `Deconstruct` arity change is inert.

A marker interface rather than a concrete scene type keeps geometry out of Core. **Hard rule for
ADR 0003:** `IPreviewPayload` is process-local and must never enter `preview_cache`, which stores
PNGs; scene reuse goes through the in-process `ModelSceneCache`.

### A3 — Polymorphic slot content, and per-family templates in separate files

This is what makes "close all M5 UI debt" parallelizable. Rather than piling zoom/pan/audio/model
state onto `PreviewSlotViewModel` and stuffing four templates into one AXAML file — which would
make both files uncontendable bottlenecks — introduce:

```csharp
// src/SRN.CC.App/ViewModels/Preview/PreviewContentViewModel.cs  (abstract base, IDisposable)
public abstract class PreviewContentViewModel : ObservableObject, IDisposable
{
    public abstract PreviewFamily Family { get; }
}
```

with one subclass per family, **each in its own file** (`TextContentViewModel`,
`ImageContentViewModel`, `AudioContentViewModel`, `ModelViewportViewModel`), and one `DataTemplate`
per family in its **own** `.axaml` ResourceDictionary under `src/SRN.CC.App/Views/PreviewTemplates/`,
merged into `ComparisonPanelView.axaml`.

`PreviewSlotViewModel` then gains exactly one new observable property —
`PreviewContentViewModel? Content` — built from `res.Payload`/`res.RawPayload` by a small
`PreviewContentFactory`, disposing the previous value on reassignment. `ComparisonPanelView.axaml`
is edited **once**, to swap the `TextBox` for a `ContentControl Content="{Binding Content}"` plus
the merged dictionary. After that, every family slice owns only its own VM file and its own template
file, and they never collide.

### A4 — GPU lifetime and context ownership

```
ModelViewportControl : OpenGlControlBase        (App — the only Avalonia+GL file, ~150 lines)
  OnOpenGlInit(gl)     -> _device   = new SilkGlDevice(gl.GetProcAddress);
                          _renderer = new ModelRenderer(_device);
                          if (!_renderer.Initialize(_device.Capabilities).IsSupported)
                               RaiseRenderUnavailable(reason);   // VM falls back to text
                          else _renderer.Upload(Scene);
  OnOpenGlRender(gl,fb)-> _renderer?.Render(fb, Camera, w, h, ShowWalkmesh);
  OnOpenGlDeinit(gl)   -> _renderer?.Dispose(); _device?.Dispose();  // deletes every GL name
  OnOpenGlLost()       -> _renderer?.Abandon(); _device?.MarkLost(); // FORGETS names, zero GL calls
```

`Abandon` issuing no GL calls is the core of "survives context failures" — on context loss the
driver already freed the names, and calling `glDeleteBuffers` on a dead context is how you get a
crash or a spurious error.

Contracts, all in `src/SRN.CC.Preview/Render/Gl/`, all Avalonia-free:

```csharp
public readonly record struct GlContextId(long Value)   // Next() = Interlocked.Increment
{ public static GlContextId Next(); public static readonly GlContextId None; }

public readonly record struct GlHandle(GlContextId Context, uint Name, GlResourceKind Kind);
public enum GlResourceKind { Buffer, VertexArray, Texture, Program, Shader }

public sealed record GlCapabilities(
    bool IsEmbeddedProfile, int MajorVersion, int MinorVersion,
    bool HasVertexArrayObjects, bool HasNonPowerOfTwoTextures,
    int MaxTextureSize, string GlslVersionDirective, string RendererName);

public interface IGlDevice : IDisposable
{
    GlContextId ContextId { get; }
    bool IsLost { get; }
    GlCapabilities Capabilities { get; }
    GlHandle CreateBuffer(GlBufferTarget target, ReadOnlySpan<byte> data);
    GlHandle CreateTexture2D(int width, int height, ReadOnlySpan<byte> bgra, bool hasAlpha);
    GlHandle CreateProgram(string vs, string fs, out string? linkLog);   // .Name == 0 on failure
    void Delete(GlHandle handle);
    void Draw(in GlDrawCall call);
    void MarkLost();                                    // every subsequent op becomes a no-op
    IReadOnlyCollection<GlHandle> LiveResources { get; } // leak-assertion hook
}

public sealed class SilkGlDevice : IGlDevice { public SilkGlDevice(Func<string, IntPtr> getProc); }
public sealed class FakeGlDevice  : IGlDevice   // public, so both test projects can use it
{
    public FakeGlDevice(GlCapabilities? capabilities = null, bool failProgramLink = false);
    public IReadOnlyList<GlCallRecord> Calls { get; }
    public void SimulateContextLoss();
}
```

The cross-context guard is one line, repeated at every renderer entry point:

```csharp
if (_device.IsLost || handle.Context != _device.ContextId) { State = RendererState.Stale; return; }
```

That makes "no cross-context handles" directly assertable: build renderer R on device A, dispose A,
hand R to device B (different `GlContextId` from the monotonic counter), assert `B.Calls` is empty
and `R.State == Stale`.

```csharp
public sealed class ModelRenderer : IDisposable
{
    public ModelRenderer(IGlDevice device);
    public GlContextId ContextId { get; }
    public RendererState State { get; }              // Uninitialized|Ready|Unsupported|Stale|Disposed
    public string? UnsupportedReason { get; }
    public int LastFrameDrawCallCount { get; }                    // semantic assertion hook
    public IReadOnlyList<string> LastFrameBoundTextureNames { get; }  // semantic assertion hook
    public RendererInitResult Initialize(GlCapabilities capabilities);
    public void Upload(RenderScene scene);
    public void Render(int framebuffer, in RenderCamera camera, int px, int py, bool showWalkmesh);
    public void Abandon();   // forget, no GL calls
    public void Dispose();   // delete every name
}
```

**"Only for visible 3D slots" is enforced structurally, not with `IsVisible`** — `IsVisible=false`
still constructs the control and still creates a context on first attach. Instead
`PreviewSlotViewModel.Content` is a `ModelViewportViewModel` only for successful model previews;
Avalonia's `ContentPresenter` realizes no child for any other content type. Backstop: a
`ModelViewportRegistry` with `MaxConcurrent = 3`, incremented in `OnAttachedToVisualTree` and
decremented in `OnDetachedFromVisualTree`; a fourth request refuses to initialize and degrades.

**Unsupported features degrade at two layers.** CPU: `RenderScene.UnsupportedFeatures` is populated
by the builder (skinmesh, emitters, animations, danglymesh) and surfaced as diagnostics. GPU: shader
link failure, capability shortfall, or texture-upload failure returns
`RendererInitResult(false, reason)`, the control raises `RenderUnavailable`, and the slot falls back
to the **existing MDL text preview**, which `MdlPreviewProvider` still produces on every success
path. Exercisable headlessly via `new FakeGlDevice(failProgramLink: true)`.

### A5 — Vendoring: `MdlMeshBuilder` conditionally; Toolset viewport code, no

- **`MdlMeshBuilder.cs`** sits under `SWLOR.NWN.Formats/Mdl/`, is render-neutral by construction,
  and `docs/compliance/swlor-post-attestation-delta-review.md:19` already dispositions it
  **Accept (Verbatim)**. Vendor it — *conditional on a fetch/verify spike* (plan mode cannot fetch).
- **`MdlPartComposer.cs`** grafts partial robes onto composite creature roots — not gate-relevant.
  Defer; its disposition is already recorded, so a later milestone needs no new review.
- **The `SWLOR.Toolset/` Avalonia/Silk viewport code: do not vendor. Implement first-party.** Three
  independent reasons: the unresolved independent-author declaration (constraint 3); the
  provenance-keyword word-boundary grep across all production and test sources; and the fact that
  upstream's viewport is coupled to its own app shell, so it would need adaptation anyway — which
  under the manifest schema demands `adaptationReason` + `reviewReference`, i.e. a new compliance
  review either way.

Vendoring is sequenced **last** so an audit-count change never blocks a sibling slice's test loop,
and because every downstream slice depends on the `RenderScene` *contract*, not on its producer. If
the spike returns NO-GO, `MdlSceneBuilder` simply stays first-party and nothing else moves.

### A6 — Walkmesh: no standalone BWM reader; fix the type IDs

Walkmesh triangles already arrive via `MdlTrimeshNode.IsWalkmesh` (ASCII `node aabb`/`pwk`/`dwk` and
the binary AABB flag) with per-face `MdlFace.SurfaceId`. Rendering those as a toggleable,
surface-id-coloured overlay excluded from the artwork draw list satisfies "WOK/PWK/DWK surfaces"
under `PLAN.md:230`'s semantic-assertion bar. A standalone BWM reader would drag in its own
hardening, fuzz surface, and manifest questions for marginal gate value — defer explicitly.

Fixing `DependencyAnalyzer.cs:68-70` **is** in scope: it is a two-value correctness fix in a file M6
must edit anyway, and today real door/placeable walkmeshes fall through to "unsupported" while
`.dlg` and `.itp` are misrouted into companion extraction.

### A7 — Texture resolution bridges layers via a Core interface

```csharp
// NEW src/SRN.CC.Core/Services/ITextureSource.cs
public interface ITextureSource
{
    /// Curated workspace first, base-game KEY/BIF second. Null when unresolved. Never throws.
    Task<TextureLookupResult?> OpenTextureAsync(
        string resref, TextureKind kind, CancellationToken cancellationToken = default);
}
public enum TextureKind { Diffuse, Lightmap, Material, EnvironmentMap, TextureInfo }
public enum TextureOrigin { Workspace, BaseGame }
public sealed record TextureLookupResult(
    AssetIdentity Identity, ushort ResourceType, Stream Payload, TextureOrigin Origin);
```

`TextureResolver` (Preview) sees only `ITextureSource`; `WorkspaceTextureSource` (App) implements it
over `WorkspaceState` + `ISourceReaderDispatcher` + the optional base-game catalog. Per texture name
the ladder is: `<name>.mtr` (2072, whose `texture0`/`bumpmap`/`envmap` **override** the MDL `Bitmap`)
→ `<name>.dds` → `<name>.tga` → `<name>.plt` → `<name>.txi` (2022) sidecar for
`envmaptexture`/`blending`/`isbumpmap` flags. Any `envmap` name discovered is resolved by the same
ladder and **reported only** (decision: discover + report).

Wiring uses a late-bound accessor because `App.axaml.cs:51-60` constructs providers before any
workspace exists — `new MdlPreviewProvider(registry, textureSourceAccessor: () => _textureSource)`,
with the field assigned in `MainWindowViewModel.LoadWorkspaceStateAsync`. This mirrors how
`DependencyLocator` is already constructed lazily. A null accessor result yields an untextured scene
plus a diagnostic, so 3D previews still work before a workspace loads and in the designer
constructor.

**Decoding reuses, never duplicates:** extract `ImagePreviewProvider`'s private TGA/DDS(Pfim)/PLT →
BGRA paths into `internal static class TextureDecoder` and have the provider call it.

### A8 — MTR parser lives in Preview, not Formats

No MTR reader exists. MTR is line-oriented text, not binary; a bounded key/value parser
(`texture0..texture3`, `envmap`, `bumpmap`, `parameter`, `renderhint`, `customshadervs/fs`) is ~80
lines. Putting it at `src/SRN.CC.Preview/Render/MtrDocument.cs` rather than in `SRN.CC.Formats`
avoids the vendored-tree immutability recheck in `VerifyBuild.ps1` and the 40/41 count churn
entirely — and it is honestly a preview-layer heuristic, not an attested format reader. Wire the
same parser into `DependencyAnalyzer.ExtractMtrDependencies` so the two cannot disagree.

---

## Shared contracts

Every slice codes against this vocabulary. See the plan file's "Shared contracts" section for the
full listing; the durable summary is:

- `src/SRN.CC.Preview/Render/RenderScene.cs` — `RenderScene`, `RenderMesh`, `RenderMaterial`,
  `TextureImage`, `MaterialBlendMode`, `IMdlSceneBuilder`, `SceneBuildBudget`.
- `src/SRN.CC.Preview/Render/RenderCamera.cs` — immutable `RenderCamera` struct with `Frame`,
  `View`, `Projection`, `Orbit`, `Dolly`, `Pan`; pure math, 100% headlessly testable, and the bulk of
  the "camera controls" gate evidence.
- Budgets appended to `PreviewStreamHelpers` in the existing const style (chosen so three 48 MiB
  scenes plus a 128 MiB shared cache stay well inside `PLAN.md:229`'s 750 MiB idle target):

  ```csharp
  public const long ModelSceneCpuBudgetBytes   = 48L * 1024 * 1024;  // per scene
  public const long ModelSceneCacheBudgetBytes = 128L * 1024 * 1024; // shared across slots
  public const long ModelTextureSetBudgetBytes = 32L * 1024 * 1024;  // per scene
  public const int  ModelMaxTextures           = 64;
  public const int  ModelMaxDrawCalls          = 4096;
  ```

  Exceeding any budget marks the offending materials untextured and emits a diagnostic — never a
  hard failure.

---

## Scope

**In scope (MVP for the gate):** ASCII+binary MDL geometry → `RenderScene`; node-hierarchy
transforms at rest pose only; static materials (diffuse colour, diffuse texture, lightmap,
untextured fallback to `MdlTrimeshNode.Diffuse`); DDS/TGA/PLT textures with default dyes via
`ITextureSource`; alpha-test cutout plus one back-to-front alpha-blend pass; one fixed headlight
directional light plus ambient with flat-shading fallback; orbit/pan/dolly camera auto-framed from
model bounds; in-MDL walkmesh overlay coloured by `SurfaceId`; `OpenGlControlBase` only for visible
model slots with full ownership/disposal/loss handling; graceful degradation to the existing MDL
text preview on every failure path; minimal MTR (`texture0`/`envmap` override) and TXI discovery.
Plus all milestone-5 UI debt: per-family templates, image `ZoomScale`/`PanX`/`PanY`, audio
`Play`/`Pause`/`Stop`/`PositionProgress`, linked navigation, SQLite thumbnail cache wiring.

**Explicitly deferred:** environment maps as a rendered reflection term (resolved and reported only;
the cheapest future upgrade is a spherical modulate in the fragment shader, ~40 lines, no contract
change); skinmesh/bone deformation, emitters, animation playback, danglymesh; `MdlPartComposer` and
creature body-part composition; supermodel geometry inheritance; a standalone BWM
`.wok`/`.pwk`/`.dwk` binary reader; per-slot PLT dye editing UI; MDL light-node lighting, shadows,
fog, animated tilefade; `SRNCC_REQUIRE_CORPUS` (documented in `PLAN.md:229`, never implemented — flag
for M7).

---

## Work slices and ownership rule

Milestone 6 is delivered as 18 file-disjoint subagent slices (S0-S17) across 5 waves, on one branch,
with a single PR at the end. **Ownership rule, stated to every subagent: you may edit only the files
listed as "Owns" for your slice. Touching a sibling's file is a merge conflict by construction.**

```
WAVE 0 (7 parallel)   S0 docs · S1 spike · S2 Core payload · S3 tex decoder
                      S4 dep analyzer · S5 notices · S6a render contracts [after S2]

WAVE 1 (4 parallel)   S6b scene builder · S7 texture pipeline · S8 GL device+renderer
                      S8b thumbnail cache

WAVE 2 (serial)       S9 MdlPreviewProvider + scene cache  →  S10 slot content surface

WAVE 3 (5 parallel)   S11 model viewport · S12 image zoom/pan · S13 audio transport
                      S14 linked navigation · S15 app wiring

WAVE 4 (2 parallel)   S16 vendoring (if GO) · S17 corpus + evidence + ADR
```

Critical path: **S2 → S6a → S6b → S9 → S10 → S11 → S17.** S8 is the largest slice by line count but
sits off the critical path entirely — which is the whole reason S6 is split into S6a/S6b.

### Phase documents

| Phase doc | Wave | Slices |
|---|---|---|
| [`docs/MILESTONE-6-1.md`](MILESTONE-6-1.md) | Wave 0 | S0, S1, S2, S3, S4, S5, S6a |
| [`docs/MILESTONE-6-2.md`](MILESTONE-6-2.md) | Wave 1 | S6b, S7, S8, S8b |
| [`docs/MILESTONE-6-3.md`](MILESTONE-6-3.md) | Wave 2 | S9, S10 |
| [`docs/MILESTONE-6-4.md`](MILESTONE-6-4.md) | Wave 3 | S11, S12, S13, S14, S15 |
| [`docs/MILESTONE-6-5.md`](MILESTONE-6-5.md) | Wave 4 | S16, S17 |

Vendoring-spike output lands in a sibling file, `docs/MILESTONE-6-0-VENDORING-DECISION.md` (owned by
S1, not by this document set).

---

## Risks

| Risk | Mitigation |
|---|---|
| ANGLE gives GLES, not desktop GL; `Silk.NET.OpenGL` desktop bindings miss an ES entry point | `GlCapabilities` probe lands before shader work; dual shader variants; `Unsupported` → text fallback is a *supported* outcome, not a failure |
| `MdlMeshBuilder` pulls in upstream types outside the delta review's coverage | S1 spike gates it; first-party is the default; S16 is last so NO-GO changes nothing else |
| `ComparisonPanelView.axaml` / `PreviewSlotViewModel.cs` merge conflicts | A3 makes S10 the only slice that touches them, and it is serial in wave 2; wave-3 slices own disjoint new files |
| Three 48 MiB scenes + a 128 MiB cache breach the 750 MiB idle target | Budgets chosen for this; S17 asserts working set after three previews + forced GC |
| A new file contains a disqualifying provenance keyword in a comment | The audit greps **all** of `src/` and `tests/`; S1 pre-greps candidates and S16 re-greps before commit. Do not paraphrase provenance history inside any `.cs` file |
| Someone adds a project and breaks `AuditDependencies.ps1`'s hard-coded `7` | A1 removes the need; called out explicitly in ADR 0003 |

---

## Verification

**Per slice:** `dotnet build -c Release` clean under `TreatWarningsAsErrors`, plus that slice's own
tests. Slices must not modify tests owned by another slice.

**Per wave:** `dotnet test SRN.CC.sln -c Release --filter 'Category!=Corpus&Category!=Performance'`
green with zero skips, before starting the next wave.

**Milestone gate — run the master verifier and record its run ID:**

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/VerifyBuild.ps1
```

That covers SDK pin `10.0.302` → gitignore probes → `dotnet restore --locked-mode` →
`AuditDependencies` + `AuditVendoredSources` → Release build → filtered test run → self-contained
`win-x64` publish → license copy → `AuditPublish` → 1.5 s launch smoke → immutability recheck of all
lock files and both `Vendored/` trees. It writes `artifacts/verification-runs/<runId>/summary.json`.

**Opt-in GPU/corpus pass** (not run in CI, per the existing pattern):

```bash
dotnet test tests/SRN.CC.CorpusTests -c Release --filter 'Category=Corpus' -p:SRNCCVerificationRuntimeIdentifier=win-x64
```

with `SRNCC_RUN_GPU=1` and `SRNCC_CORPUS_ROOT` set.

**Manual end-to-end** (the gate scenario, via `/run` or a direct launch): import a HAK containing
tileset and creature MDLs → select an MDL → switch a slot to **Model** → confirm geometry renders,
orbit/pan/dolly work, and the walkmesh toggle shows surface-id-coloured triangles excluded from the
artwork pass → fill all three slots with models and confirm three viewports exist → switch one slot
to Text and confirm its viewport is destroyed (registry count drops) → select an MDL whose textures
are missing and confirm it renders untextured with diagnostics rather than failing → force a
shader-unsupported path and confirm the slot degrades to the MDL text preview.

**Evidence:** `docs/evidence/MILESTONE-6-EVIDENCE.md` (owned by S17) maps every clause of the
`PLAN.md:210` gate to a named test, and records the `VerifyBuild.ps1` run ID, build warning/error
counts, and test pass/fail/skip counts — matching the `docs/evidence/MILESTONE-3-EVIDENCE.md` format.
