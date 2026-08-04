# Milestone 5 — Basic Previews and Dependencies (Curation MVP)

Implement basic preview providers for images, text/tables, audio, GFF/ITP, SSF, and MDL files. Enforce safety budgets, slot isolation, CPU-cache sharing, and linked zoom/pan and audio play navigation. Provide cycle-safe dependency analysis with base-game catalog lookup and a UI mechanism to select imported dependencies.

## User Review Required

> [!NOTE]
> - **Default Dyes**: Since PLT files contain 10 recolorable layers but no default palettes inside the PLT format itself, we define 10 default dye colors (Skin, Hair, Metal1, Metal2, Cloth1, Cloth2, Leather1, Leather2, Tattoo1, Tattoo2) to render PLT previews out-of-the-box.
> - **Linked Navigation**: By default, zooming or panning in one image preview slot will apply to other image preview slots to allow visual comparison. Toggling link synchronization will be supported in the UI.

## Open Questions

None. The requirements in `PLAN.md` are fully specified and matching our baseline architecture.

---

## Proposed Changes

### Central Package Management

#### [MODIFY] [Directory.Packages.props](file:///d:/source/repos/SRN_CC_milestone-5/Directory.Packages.props)
Add central package versions for `Pfim` (DDS decoding) and `NAudio` (WAV/BMU audio playing).
```xml
    <!-- Previews / Media -->
    <PackageVersion Include="Pfim" Version="0.11.4" />
    <PackageVersion Include="NAudio" Version="2.3.0" />
    <PackageVersion Include="Silk.NET.OpenGL" Version="2.23.0" />
```

### Core Interfaces and Registrations

#### [NEW] [IDependencyAnalyzer.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.Core/Services/IDependencyAnalyzer.cs)
Define the core dependency analyzer interface.
```csharp
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;

namespace SRN.CC.Core.Services;

public interface IDependencyAnalyzer
{
    Task<IReadOnlySet<AssetIdentity>> AnalyzeDependenciesAsync(
        AssetOccurrence occurrence,
        Stream stream,
        CancellationToken cancellationToken = default);
}
```

#### [MODIFY] [SqliteCacheService.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.Infrastructure/Cache/SqliteCacheService.cs)
Add support for caching PNG thumbnails in `cache-v1.sqlite` with a composite key of `(source_fingerprint, locator)`.
Implement LRU cleanup that considers the preview cache table size.
- New Table: `preview_cache` (columns: `source_fingerprint` BLOB, `locator` TEXT, `width` INTEGER, `height` INTEGER, `png_bytes` BLOB, `last_access_utc` TEXT).
- New methods: `TryGetPreviewAsync(...)`, `SavePreviewAsync(...)`.

### Preview Providers

#### [NEW] [ImagePreviewProvider.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.Preview/ImagePreviewProvider.cs)
Add decoding for TGA, DDS (using Pfim), and PLT (applying default dyes). Enforce safety budgets (4096x4096 / 64 MiB RGBA).
Output raw BGRA bytes in `RawPayload` and dimensions as string `{width}:{height}` in `FormattedContent`.

#### [NEW] [TextPreviewProvider.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.Preview/TextPreviewProvider.cs)
Add UTF-8/CP1252 auto-detecting reader for 2DA, MTR, TXI, SET, INI, GUI, txt, and shader files. Enforce 8 MiB safety budget.

#### [NEW] [AudioPreviewProvider.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.Preview/AudioPreviewProvider.cs)
Validate and skip the 8-byte BMU header (providing a skipped stream wrapping). Enforce streaming behavior.

#### [NEW] [TreePreviewProvider.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.Preview/TreePreviewProvider.cs)
Dump GFF-family/ITP files and SSF (Sound Set File) binary files into highly readable indented text outline dumps.
SSF parses `Magic`, `Version`, `Entry Count` and loops offsets to read 16-byte ResRefs and 4-byte StrRefs.

#### [NEW] [MdlPreviewProvider.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.Preview/MdlPreviewProvider.cs)
Read MDL files (binary/ASCII) and produce a metadata summary text including model name, supermodel, and mesh texture listings.

### Dependency Analysis

#### [NEW] [DependencyAnalyzer.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.Preview/DependencyAnalyzer.cs)
Parse MDL geometries, MTR files, same-resref TXI/WOK/PWK/DWK companions, and SET files. Returns sets of direct `AssetIdentity` dependencies.

### Application Shell and UI

#### [MODIFY] [PreviewSlotViewModel.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.App/ViewModels/PreviewSlotViewModel.cs)
- Connect to `ISqliteCacheService` for PNG thumbnail cache lookups and saves.
- Add zoom/pan properties (`ZoomScale`, `PanX`, `PanY`) and update parent when changed.
- Add audio playback properties (`PlayCommand`, `PauseCommand`, `StopCommand`, `PositionProgress`) and bind NAudio wave player.
- Ensure strict slot isolation and handle disposal of the NAudio player and streams.

#### [MODIFY] [ComparisonPanelViewModel.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.App/ViewModels/ComparisonPanelViewModel.cs)
- Expose shared zoom/pan properties and navigation linkage status.
- Implement propagation of zoom/pan state when linked navigation is enabled.
- Implement propagation of audio position/play/pause when every active slot is an Audio preview.

#### [MODIFY] [ComparisonPanelView.axaml](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.App/Views/ComparisonPanelView.axaml)
- Show conditional templates based on active `Family` (Text scroll box, Image Canvas with render transforms, or Audio player panel with play/pause/stop and progress slider).
- Add support for links synchronization toggle and manual slot family selection.

#### [MODIFY] [MainWindowViewModel.cs](file:///d:/source/repos/SRN_CC_milestone-5/src/SRN.CC.App/ViewModels/MainWindowViewModel.cs)
- Discover the base-game catalog (via `NwnInstallOverride` or auto-discovery `NwnInstallLocator`).
- Expose `AddAvailableDependenciesCommand`:
  - Traces the cycle-safe transitive closure of selected assets.
  - Resolves dependencies in the workspace first, then the base-game catalog.
  - Previews count/bytes and lists unresolved dependencies.
  - Prompts the user via delegate to confirm selection.

---

## Verification Plan

### Automated Tests
- Run `dotnet test` to execute unit tests.
- Add unit tests in `SRN.CC.Tests` for:
  - `ImagePreviewProvider` with mock streams.
  - `TextPreviewProvider` with UTF-8 / CP1252 test inputs.
  - `TreePreviewProvider` validating SSF binary reading and GFF structure parsing.
  - `DependencyAnalyzer` tracing cycles and extracting MDL/MTR dependencies.

### Manual Verification
- Launch `SRN.CC.App` and import HAK/folder files.
- Select a TGA/DDS image to verify zoom, pan, and linked compares.
- Select WAV/BMU files to verify audio play/pause, position scrubbing, and linked play.
- Trigger "Add available dependencies" to verify workspace closure selection and unresolved list reporting.
