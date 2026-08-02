# Milestone 1 Evidence Record: Bootstrap and Compliance

* **Date:** 2026-08-02
* **Target OS/SDK:** Windows 11 / .NET SDK 10.0.302
* **Commit Target Pin:** SWLOR commit `8202faa203eddd6f4972104d22ea5740e23f20f7`

---

## 1. Acceptance Matrix Results

| Area | Status | Evidence Summary |
|---|---|---|
| Repository Bootstrap | **PASSED** | Git repository initialized on `main`; `.gitignore` verified to exclude `/content/`, `/tools/local_only/`, `/artifacts/`. |
| Toolchain & Packages | **PASSED** | `global.json` pinned to `10.0.302`, `Directory.Packages.props` pinned 10 direct packages via CPM. `packages.lock.json` lock files committed. |
| Architecture Boundaries | **PASSED** | Seven-project solution built. `SRN.CC.Tests.Architecture` verified strict project reference direction rules. |
| Vendored SWLOR Sources | **PASSED** | 40 format readers + 8 portable test files imported from `8202faa2...`. Provenance, attestation, and delta review documented. |
| CP1252 Identity | **PASSED** | `AssetIdentity` tests pass for 1-16 byte limits, ASCII folding, accents, invalid characters, and equality. |
| HAK Streaming Primitives | **PASSED** | `HakReader` and `HakWriter` pass semantic round-trip, normalized header output, entry sorting, and payload stream tests. |
| UI Virtualization Probe | **PASSED** | Headless Avalonia NUnit test verified 187,943 rows materialize fewer than 2,000 visual containers (virtualization confirmed). |
| Dependency Audit | **PASSED** | `AuditDependencies.ps1` verified all resolved packages against `eng/dependency-policy.json`. |
| Publish Audit | **PASSED** | Self-contained `win-x64` output audited by `AuditPublish.ps1`; `publish-inventory.json` emitted with zero forbidden files. |
| Automation Entry Point | **PASSED** | `tools/VerifyBuild.ps1` executes all steps locally and cleanly. |
