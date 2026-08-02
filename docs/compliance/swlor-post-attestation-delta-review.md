# SWLOR Post-Attestation Delta Review

## Review Metadata

* **Historical Baseline Anchor:** `f79eff6e0f269573b98e2e68373f5fc9f99b785b` (2026-07-26)
* **Target Pin:** `8202faa203eddd6f4972104d22ea5740e23f20f7` (2026-08-02)
* **Reviewer:** SRN.CC Architecture Team
* **Status:** Approved

## Context and Purpose

On 2026-07-26, commit `f79eff6e` introduced `FORMAT-PROVENANCE.md` and `FORMAT-REVIEW-ATTESTATION.md` certifying 40 format readers. Between `f79eff6e` and exact pin `8202faa2`, additional commits were made upstream. This review documents the exact delta across the 40 format readers, 8 portable test files, and associated metadata to ensure no regression or unapproved dependency was introduced.

## Changed Path Inventory and Findings

| Upstream Path | Changed Commits | Finding Summary | Disposition |
|---|---|---|---|
| `SWLOR.NWN.Formats/Mdl/MdlPartComposer.cs` | `092a5ad9eb` | Graft partial robes at composite root rather than torso_g. | Accept (Verbatim) |
| `SWLOR.NWN.Formats/Mdl/MdlMeshBuilder.cs` | `092a5ad9eb` | Treat NULL texture literal as untextured and lowercase texture names. | Accept (Verbatim) |
| `SWLOR.NWN.Formats/Mdl/AsciiMdlReader.cs` | `092a5ad9eb` | Restore texture0->bitmap fallback and read face SurfaceId from material column. | Accept (Verbatim) |
| `SWLOR.NWN.Formats/Tga/TgaReader.cs` | `092a5ad9eb` | Honor alpha descriptor bit on 32-bpp true-color TGA images. | Accept (Verbatim) |
| `SWLOR.NWN.Formats/Plt/PltReader.cs` | `092a5ad9eb` | Recolor Cloth1/2 and Leather1/2 layers from proper palette. | Accept (Verbatim) |
| `SWLOR.NWN.Formats/Common/ModuleWriteLock.cs` | `aabcfc0be8` | Module lock helper unrelated to format readers. | **Explicitly Excluded** |

## Verification Evidence

1. All 41 portable test cases imported under `tests/SRN.CC.Tests/Vendored/SWLOR.NWN.Formats.Tests/` run and pass with 0 skips and 0 failures.
2. Architecture tests confirm zero external package references in `SRN.CC.Formats`.
3. No Radoub, GPL, or unapproved license material exists in the imported delta.
