# Milestone 6 — S1 Upstream Fetch/Verify Spike: Vendoring Decision

## Summary

**Verdict: NO-GO.** Do not vendor `MdlMeshBuilder.cs` into
`src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/Mdl/` under the plan's Architecture Decision A5.
`MdlSceneBuilder` (S6b) stays first-party, per A5's stated default. S16 (Wave 4, "Vendoring
execution") should collapse into the NO-GO path A5 already anticipates: no manifest edit, no audit
count change, no new vendored file.

The blocking finding is structural, not textual: at the exact pinned commit
`8202faa203eddd6f4972104d22ea5740e23f20f7`, `MdlMeshBuilder.cs` **does not live under
`SWLOR.NWN.Formats/Mdl/`** as A5 assumed. It lives at `SWLOR.Toolset.Domain/Render/MdlMeshBuilder.cs`
— inside the Toolset codebase A5 already decided not to vendor — and it has a hard, uninspected
compile-time dependency on a second Toolset-domain file (`MdlAnimationPose.cs`) that our existing
compliance delta review never reviewed or dispositioned. The file's own text is clean of
`Radoub`/`GPL`/`Avalonia`/`Silk` by the exact word-boundary test `tools/AuditVendoredSources.ps1`
uses, so this is not a licensed-content-leak finding — it is a location/provenance/scope mismatch
against what A5 and the existing delta review actually reviewed and approved.

## Method (commands run)

All work was performed in a disposable scratch clone (never inside the SRN_CC repo), and fully
removed afterward.

```bash
# Initial full clone attempted into the session scratchpad; repeatedly hit stray-process file
# locks and, once past that, Windows MAX_PATH failures against this monorepo's deeply nested
# SWLOR.Game.Server/Module trees (unrelated to the files needed here).
git clone https://github.com/givemedeath/SWLOR_NWN.git <scratch>/s1clone

# Local-cloned the already-fetched objects into a short path (D:\tmp\s1c) to dodge MAX_PATH,
# then used a cone-mode sparse checkout scoped to only the trees this spike needs.
git clone --no-checkout --local --no-hardlinks <scratch>/s1clone/.git D:\tmp\s1c
cd D:\tmp\s1c
git config core.longpaths true
git sparse-checkout init --cone
git sparse-checkout set SWLOR.NWN.Formats SWLOR.Toolset SWLOR.Toolset.Domain SWLOR.Toolset.Tests toolset
git checkout 8202faa203eddd6f4972104d22ea5740e23f20f7

# Located the file (it is not at the assumed path)
git ls-tree -r --name-only HEAD | grep MdlMeshBuilder
# -> SWLOR.Toolset.Domain/Render/MdlMeshBuilder.cs
# -> SWLOR.Toolset.Tests/MdlMeshBuilderPlaceholderTests.cs

# Hashes, at the file's actual location
git rev-parse HEAD:SWLOR.Toolset.Domain/Render/MdlMeshBuilder.cs
Get-FileHash -Algorithm SHA256 SWLOR.Toolset.Domain\Render\MdlMeshBuilder.cs

# Toolset inventory
git ls-tree -r --name-only HEAD SWLOR.Toolset/
git ls-tree -r --name-only HEAD SWLOR.Toolset.Domain/ SWLOR.Toolset.Tests/ toolset/

# Word-boundary grep, replicating tools/AuditVendoredSources.ps1's exact method:
#   $text = Get-Content $file.FullName -Raw
#   foreach pattern in Radoub, GPL, Avalonia, Silk: $text -match "\b$pattern\b"
# (PowerShell -match is case-insensitive by default; the audit script does not opt into -cmatch,
# so this scan intentionally reproduces that same case-insensitive behavior rather than a
# stricter case-sensitive one.)
```

After recording results, the scratch clone (`D:\tmp\s1c`, the original `s1clone` under the session
scratchpad, and a stray timed-out-clone artifact left by an interrupted first attempt) were all
deleted. Nothing was copied into the SRN_CC repo.

### A note on how the clone went

The first `git clone` attempt into the session scratchpad timed out and left orphaned `git`
processes holding file locks; those had to be killed (`Stop-Process`) before the directory could be
removed (a plain `Remove-Item` failed on locked pack files — a `robocopy /MIR` against an empty
directory was used to force the clear). The second attempt cloned successfully but then failed to
`git checkout` the pin: this upstream repository is a large monorepo (`SWLOR.Game.Server`, `Module`,
etc., ~2.27 GB) with deeply nested paths that exceed Windows `MAX_PATH` once combined with this
machine's long scratch-directory prefix, even with `core.longpaths=true` set. Re-cloning locally
into a short path (`D:\tmp\s1c`) and scoping a cone-mode sparse checkout to only the trees this
spike needs (`SWLOR.NWN.Formats`, `SWLOR.Toolset`, `SWLOR.Toolset.Domain`, `SWLOR.Toolset.Tests`,
`toolset`) resolved it. This is noted only because it is a real repeatable obstacle for any future
slice (e.g. a hypothetical S16) that needs a full checkout of this upstream repository on Windows.

## MdlMeshBuilder.cs hashes

Computed at the file's **actual** location at the pin, `SWLOR.Toolset.Domain/Render/MdlMeshBuilder.cs`
(921 lines / 48,214 bytes) — not at the assumed `SWLOR.NWN.Formats/Mdl/MdlMeshBuilder.cs`, which does
not exist at this commit. Ready to paste into a manifest entry if a future review reopens this:

```json
{
    "status": "verbatim",
    "sha256": "f4fb0817dc2820d730746644c2d4308cf9a341feff33213f02231d11cd3865e3",
    "gitBlobSha1": "b6956e538446150c1c4fcb85d635b0e1bae6dfd9",
    "destinationPath": "src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/Mdl/MdlMeshBuilder.cs",
    "upstreamPath": "SWLOR.Toolset.Domain/Render/MdlMeshBuilder.cs"
}
```

The `upstreamPath` above is deliberately written as the file's true current location, not the
`SWLOR.NWN.Formats/Mdl/MdlMeshBuilder.cs` path recorded in
`docs/compliance/swlor-post-attestation-delta-review.md:19`. That review's disposition
("Accept (Verbatim)") was made against a diff at upstream commit `092a5ad9eb` under the
`SWLOR.NWN.Formats/Mdl/` path; by the time of the actual pin `8202faa2`, the file (along with its
four clean-room sibling replacements) had relocated into `SWLOR.Toolset.Domain/Render/`. A manifest
entry that kept the old `upstreamPath` to satisfy the existing disposition would misrepresent where
the file actually comes from.

## MdlMeshBuilder.cs transitive-dependency verdict: additional undisclosed dependency found

`MdlMeshBuilder.cs` imports only `System.Numerics` and `SWLOR.NWN.Formats.Mdl` at its `using`
declarations, and its `SWLOR.NWN.Formats.Mdl` usage is limited to types already vendored in this
repo's `src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/Mdl/` (compared file-for-file against the 11
files there: `AsciiMdlReader.cs`, `MdlAnimation.cs`, `MdlBoneIndices.cs`, `MdlEmitterNode.cs`,
`MdlFace.cs`, `MdlModel.cs`, `MdlNode.cs`, `MdlReader.cs`, `MdlSkinInfluence.cs`,
`MdlSkinmeshNode.cs`, `MdlTrimeshNode.cs`):

- `MdlModel`, `MdlNode`, `MdlTrimeshNode`, `MdlSkinmeshNode`, `MdlEmitterNode`, `MdlAnimation` —
  all already vendored, all verbatim. **No unvendored `SWLOR.NWN.Formats` type is referenced.**
- `ReferenceEqualityComparer` — BCL (`System.Runtime.CompilerServices`), not upstream.

However, `MdlMeshBuilder.Build`/`BuildAnimatedPreview`/`BuildPlaceablePreview` take parameters typed
`PosedNode` and `MdlAnimationPose.SampledAnimation`, and the internal builder calls
`MdlAnimationPose.PlaceableAnimations`, `MdlAnimationPose.SampleFrames`, and
`MdlAnimationPose.FindPlaceableDefault`. **Neither `PosedNode` nor `MdlAnimationPose` is defined in
`MdlMeshBuilder.cs`, in any already-vendored file, or anywhere under `SWLOR.NWN.Formats/`.** Both are
defined in a sibling file at the same pin:

- `SWLOR.Toolset.Domain/Render/MdlAnimationPose.cs` (511 lines) — a second Toolset-domain file, not
  part of `SWLOR.NWN.Formats`, never mentioned or dispositioned in
  `docs/compliance/swlor-post-attestation-delta-review.md`.

`MdlMeshBuilder.cs` **does not compile standalone**; vendoring it "verbatim" would require also
vendoring `MdlAnimationPose.cs` (itself clean of the four forbidden words — `MdlAnimationPose.cs`
only imports `System.Numerics` and `SWLOR.NWN.Formats.Mdl`, and its own upstream types, `MdlModel`,
`MdlNode`, `MdlAnimation`, are already vendored) and obtaining a fresh compliance disposition for it,
which is out of S1's scope and was never anticipated by A5's "vendor `MdlMeshBuilder.cs`" framing.

### Why the file moved: upstream's own Radoub/GPL replacement project

`git log --follow` on `SWLOR.Toolset.Domain/Render/MdlMeshBuilder.cs` traces directly through
`092a5ad9e Fix parity regressions found in the Radoub-removal review`,
`75c4252a2 Address Radoub replacement review feedback`, and
`f79eff6e0 Remove Radoub from the SWLOR Toolset` — the exact historical baseline anchor commit cited
in `docs/compliance/swlor-post-attestation-delta-review.md`. The `Render/` directory that now houses
`MdlMeshBuilder.cs` also contains three upstream-authored compliance records that make the picture
explicit:

- `SWLOR.Toolset.Domain/Render/REPLACEMENT-PROVENANCE.md` — records that `MdlMeshBuilder.cs`,
  `MdlPartComposer.cs`, `MdlPartBoneMap.cs`, `TextureLoader.cs`, and `MdlGeometryFlattener.cs` are
  **"the five render files replaced as part of the external format dependency removal"** — a
  GPL-3.0-licensed library named "Radoub" that the toolset formerly linked. Each replacement was
  independently clean-room re-authored (not derived from the old GPL code) and now carries a
  `// SPDX-License-Identifier: MIT` header.
- `SWLOR.Toolset.Domain/Render/CLEAN-AUTHOR-ATTESTATION.md` /
  `SWLOR.Toolset.Domain/Render/CLEAN-REVIEW-ATTESTATION.md` — the clean-room author/reviewer
  declarations for that replacement, dated 2026-07-26.
- `SWLOR.Toolset/LICENSE-NOTICE.md` — states the current dependency graph is
  `SWLOR.Toolset → SWLOR.Toolset.Domain → { SWLOR.NWN.Formats, SWLOR.Game.Server }`, that current
  first-party source (including `SWLOR.Toolset.Domain`) is MIT-licensed, and that
  `SWLOR.Toolset/LICENSE.GPL-3.0` is retained only "as a historical third-party license notice for
  those earlier revisions... not a declaration that the current toolset binary links GPL code."

This corroborates, almost verbatim, the disqualification note already vendored into this repo at
`docs/compliance/FORMAT-PROVENANCE.md:17-20` ("Historical render-file exposure... disqualifies the
same author from making the plan's independent-author declaration for the five replacement render
files") — `MdlMeshBuilder.cs` is one of those same five files. Its **current text** is clean (MIT
header, no forbidden words), but it is, by upstream's own architecture rule in `LICENSE-NOTICE.md`
("shared libraries... must not reference `SWLOR.Toolset` or `SWLOR.Toolset.Domain`"), classified by
upstream itself as **toolset-layer** code, not formats-layer code — the exact category A5 excludes.

## SWLOR.Toolset/ inventory

The plan's S1 description assumes a single `SWLOR.Toolset/` directory. At this pin, upstream has
split that into four top-level trees:

| Tree | File count (`git ls-tree -r --name-only`) | Role |
|---|---|---|
| `SWLOR.Toolset/` | 310 | Desktop Avalonia application (views, viewmodels, the GL viewport) |
| `SWLOR.Toolset.Domain/` | 332 | Editor/render/document domain logic, incl. `Render/MdlMeshBuilder.cs` |
| `SWLOR.Toolset.Tests/` | 245 | Toolset test suite |
| `toolset/` (lowercase) | 1 | Unrelated single file, not part of the C# toolset |

`SWLOR.Toolset.Domain.csproj` project-references both `SWLOR.NWN.Formats.csproj` **and**
`SWLOR.Game.Server.csproj` (the full NWN game-server codebase), plus `PackageReference Pfim`.
`SWLOR.Toolset.csproj` references `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`,
`Avalonia.Fonts.Inter`, `Avalonia.Diagnostics`, `Avalonia.Controls.DataGrid`, `Avalonia.AvaloniaEdit`,
`CommunityToolkit.Mvvm`, `NAudio`, `Microsoft.Extensions.DependencyInjection`, `Dock.Avalonia`,
`Dock.Avalonia.Themes.Fluent`, `Dock.Model.Mvvm`, and `Silk.NET.OpenGL` — confirming this is
precisely the "Avalonia/Silk viewport code" A5 already decided not to vendor.

## Grep results table

Word-boundary scan (`$text -match "\b$pattern\b"`, PowerShell's default case-insensitive `-match`,
exactly matching `tools/AuditVendoredSources.ps1`'s own method) across all `.cs`/`.csproj`/`.axaml`
files in `SWLOR.Toolset/`, `SWLOR.Toolset.Domain/`, `SWLOR.Toolset.Tests/`, and `toolset/`
(868 files scanned):

| Pattern | Files matched | Notes |
|---|---|---|
| `Radoub` | 1 | `SWLOR.Toolset.Tests/ToolsetLicenseBoundaryTests.cs` — a boundary-enforcement test whose own comments discuss the historical Radoub removal (e.g. guarding against a stale worktree reporting `SWLOR.Toolset.Domain -> Radoub.Formats`). Not a leaked dependency; not proposed for vendoring. |
| `GPL` | 1 | `SWLOR.Toolset.Domain/Script/ScriptLexicon.cs` — a comment about linking (not shipping) the GFDL-licensed community NWScript Lexicon rather than bundling it, referencing "this project's GPL-3.0" (a stale comment predating the toolset's move to MIT per `LICENSE-NOTICE.md`). Unrelated to `Render/`. |
| `Avalonia` | 146 | UI layer of `SWLOR.Toolset/` (views, viewmodels, `.axaml.cs` code-behind) plus its `.csproj` package references. |
| `Silk` | 3 | `SWLOR.Toolset/Viewport/DepthPrecisionFramebuffer.cs`, `SWLOR.Toolset/Viewport/GlAreaControl.cs`, `SWLOR.Toolset/SWLOR.Toolset.csproj`. |

**Explicit check on the two files that would actually need vendoring** (`MdlMeshBuilder.cs` and its
required dependency `MdlAnimationPose.cs`), same method: **zero matches for all four patterns in
both files.** Neither the file this spike was asked to vendor, nor the dependency it turned out to
need, contains the forbidden words. The blocking issue is where these files live and what compliance
review has (and has not) already covered them — not their text content.

## Final recommendation: NO-GO

**Do not vendor `MdlMeshBuilder.cs` under `src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/Mdl/` in
S16.** Reasoning:

1. **Location mismatch with A5's premise.** A5 states `MdlMeshBuilder.cs` "sits under
   `SWLOR.NWN.Formats/Mdl/`, is render-neutral by construction." At the actual pinned commit, it does
   not — it sits in `SWLOR.Toolset.Domain/Render/`, which upstream's own `LICENSE-NOTICE.md`
   architecture rule places on the toolset side of the formats/toolset boundary, alongside a project
   reference to the entire `SWLOR.Game.Server` codebase.
2. **An undisclosed second file is required.** `MdlMeshBuilder.cs` does not compile without
   `MdlAnimationPose.cs` (`PosedNode`, `MdlAnimationPose.SampledAnimation` and three of its static
   methods). That file was never reviewed or dispositioned by
   `docs/compliance/swlor-post-attestation-delta-review.md`. "Vendor `MdlMeshBuilder.cs`" as scoped by
   A5 is not actually a single-file, self-contained action.
3. **The existing "Accept (Verbatim)" disposition doesn't cover this artifact as it now exists.**
   That disposition was written against a diff under the `SWLOR.NWN.Formats/Mdl/` path at commit
   `092a5ad9eb`. The file at the pin has since relocated into a different project with a different
   dependency footprint. Recording the true `upstreamPath` in the manifest would not match what the
   delta review actually reviewed; recording the old path to match the review would misrepresent
   provenance. Either way, per A5's own text, this is "adaptation" that "demands `adaptationReason` +
   `reviewReference`, i.e. a new compliance review either way" — which is out of scope for S1 and for
   a mechanical S16 vendoring pass.
4. **Content itself is not the blocker.** Both `MdlMeshBuilder.cs` and `MdlAnimationPose.cs` are
   individually clean of `Radoub`/`GPL`/`Avalonia`/`Silk` by the exact test the audit tooling uses,
   and both carry `// SPDX-License-Identifier: MIT` headers consistent with upstream's completed
   Radoub-removal remediation. A future, narrower slice — one that updates the delta review with the
   correct upstream paths for both files and re-runs the independent-author/reviewer process A5
   describes — could reopen this. That is explicitly not what S1 or a following S16 is chartered to
   do.
5. **This is the risk the plan already priced in.** The plan's own risk table states: "`MdlMeshBuilder`
   pulls in upstream types outside the delta review's coverage | S1 spike gates it; first-party is
   the default; S16 is last so NO-GO changes nothing else." That is exactly what happened
   (`MdlAnimationPose`/`PosedNode` are the "upstream types outside the delta review's coverage").

**Consequence per A5:** `MdlSceneBuilder` (S6b) stays first-party, unaffected by this NO-GO. S16
(Wave 4) needs no manifest edit, no `tools/AuditVendoredSources.ps1` count change (the audit's
hard-coded 40/8 counts stay untouched), and no new file under
`src/SRN.CC.Formats/Vendored/`. `MdlPartComposer.cs` (deferred separately by A5, not gated on this
spike) is unaffected by this verdict either way.

## S16 (Wave 4, "Vendoring execution") — closed as a no-op per this NO-GO

Per the plan's own text: "If NO-GO: the slice becomes a two-paragraph rationale appended to
`docs/MILESTONE-6-0-VENDORING-DECISION.md` and nothing else changes." This section is that closure.

`MdlSceneBuilder` (`src/SRN.CC.Preview/Render/MdlSceneBuilder.cs`, landed in Wave 1 as slice S6b)
was built entirely first-party from the start against this spike's NO-GO expectation, and Wave 1's
own texture-pipeline slice (S7) independently converged on an identical `TextureResolver` contract
without needing any vendored mesh-building code — so no rework was required when this verdict
landed. Re-verified before closing: `tools/AuditVendoredSources.ps1` still reports exactly 40
vendored format sources and 8 portable test files (unchanged from before Milestone 6 began), and
`eng/vendored-sources-manifest.json` carries no new entry for `MdlMeshBuilder.cs` or
`MdlAnimationPose.cs`. Reopening this decision — vendoring either file — requires, at minimum: an
updated `docs/compliance/swlor-post-attestation-delta-review.md` entry recording the correct
`SWLOR.Toolset.Domain/Render/` upstream paths for both files, a fresh independent-author/reviewer
disposition per architecture decision A5 (since the existing "Accept (Verbatim)" disposition was
written against a different upstream path and does not cover `MdlAnimationPose.cs` at all), and only
then the mechanical manifest/audit-count changes this slice was originally scoped to make. None of
that is milestone-6 MVP scope; it is explicitly out of scope here and left for a future milestone to
pick up if desired.
