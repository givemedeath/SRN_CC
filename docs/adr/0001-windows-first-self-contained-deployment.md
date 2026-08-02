# 1. Windows-First Self-Contained Deployment

* Status: Accepted
* Date: 2026-08-02

## Context and Problem Statement

`SRN.CC` requires a predictable execution environment on Windows without requiring a pre-installed .NET shared framework on target machines. The application relies on specific Avalonia 12.1 visual and virtualization features as well as high-performance HAK binary streaming.

## Decision Drivers

* Reproducible runtime environment on Windows `win-x64`.
* Zero requirement for end users to pre-install runtime SDKs or shared frameworks.
* Precise servicing control over runtime assemblies shipped with the app.

## Considered Options

1. Framework-dependent deployment (`win-x64` or portable).
2. Self-contained deployment (`win-x64`), non-single-file.
3. Single-file self-contained deployment.

## Decision Outcome

Chosen option: **Self-contained deployment (`win-x64`), non-single-file**.

### Consequences

* Positive: Shipped runtime assemblies are fully audited and locked to SDK `10.0.302`.
* Positive: Avoids single-file extraction overhead and path inspection issues during preview/render operations.
* Negative: Larger publish directory size (~70–100 MB), which is managed and validated by `AuditPublish.ps1`.
