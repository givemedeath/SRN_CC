# Vendoring Policy and Provenance Policy

## Overview

This repository includes a selective snapshot of format readers and portable format tests imported from the upstream [`SWLOR_NWN`](https://github.com/givemedeath/SWLOR_NWN) repository.

* **Upstream Pin:** `8202faa203eddd6f4972104d22ea5740e23f20f7` (and corresponding commit object `c9610a895f13c53028f183085c39570452eee859` on `feature/swlor-toolset`).
* **License:** Upstream files are governed by the MIT License (see [`docs/compliance/THIRD-PARTY-LICENSES/SWLOR-LICENSE.txt`](file:///d:/source/repos/SRN_CC/docs/compliance/THIRD-PARTY-LICENSES/SWLOR-LICENSE.txt)).
* **Location:** Format readers reside under [`src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/`](file:///d:/source/repos/SRN_CC/src/SRN.CC.Formats/Vendored/SWLOR.NWN.Formats/). Portable tests reside under [`tests/SRN.CC.Tests/Vendored/SWLOR.NWN.Formats.Tests/`](file:///d:/source/repos/SRN_CC/tests/SRN.CC.Tests/Vendored/SWLOR.NWN.Formats.Tests/).

## Provenance Records and Historical Evidence

The historical upstream attestation files [`FORMAT-PROVENANCE.md`](file:///d:/source/repos/SRN_CC/docs/compliance/FORMAT-PROVENANCE.md) and [`FORMAT-REVIEW-ATTESTATION.md`](file:///d:/source/repos/SRN_CC/docs/compliance/FORMAT-REVIEW-ATTESTATION.md) are retained verbatim as historical baseline evidence.

**Important Legal Notice:** Retaining these historical attestation files does not constitute a new legal opinion, warranty, or certification by `SRN.CC` authors. All changes made between historical anchor commit `f79eff6e0f269573b98e2e68373f5fc9f99b785b` (2026-07-26) and exact pin `8202faa203eddd6f4972104d22ea5740e23f20f7` have been reviewed independently and recorded in [`docs/compliance/swlor-post-attestation-delta-review.md`](file:///d:/source/repos/SRN_CC/docs/compliance/swlor-post-attestation-delta-review.md).

## Audit Enforcement

All imported files are tracked in [`eng/vendored-sources-manifest.json`](file:///d:/source/repos/SRN_CC/eng/vendored-sources-manifest.json). The audit script [`tools/AuditVendoredSources.ps1`](file:///d:/source/repos/SRN_CC/tools/AuditVendoredSources.ps1) verifies:

1. Every destination file's SHA-256 matches the expected hash in `vendored-sources-manifest.json`.
2. Git blob SHA-1s for verbatim files match the pinned upstream Git tree.
3. No forbidden Radoub/GPL path, dependency, or binary exists.
