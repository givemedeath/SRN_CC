# 2. Core and Formats Independence Boundary

* Status: Accepted
* Date: 2026-08-02

## Context and Problem Statement

The solution architecture consists of five core production projects: `SRN.CC.Core`, `SRN.CC.Formats`, `SRN.CC.Infrastructure`, `SRN.CC.Preview`, and `SRN.CC.App`. We must prevent tight coupling between low-level binary format parsing (`Formats`) and business domain identity/curation rules (`Core`), while keeping both UI-free.

## Decision Drivers

* Clean separation of concerns between raw file format representations and canonical asset identity models.
* Independent testability of HAK/ERF streaming primitives without depending on domain asset structures.
* Strict architecture enforcement via unit tests.

## Decision Outcome

Chosen option: **`SRN.CC.Core` and `SRN.CC.Formats` remain completely independent with no project references between them**.

### Dependency Direction Rules

```text
SRN.CC.App            -> Core, Infrastructure, Preview
SRN.CC.Infrastructure -> Core, Formats
SRN.CC.Preview        -> Core, Formats
SRN.CC.Core           -> (No project references, No UI references)
SRN.CC.Formats        -> (No project references, No UI references)
```

### Consequences

* HAK primitives in `SRN.CC.Formats` accept format-level keys containing trimmed CP1252 bytes and `ushort` resource type (`HakFormatKey`).
* `SRN.CC.Infrastructure` provides the explicit bridge (`AssetIdentityBridge`) to convert `HakFormatKey` into `AssetIdentity`.
* An automated architecture test (`SRN.CC.Tests.Architecture`) parses `.csproj` files to ensure this rule is never violated.
