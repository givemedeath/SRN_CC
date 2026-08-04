# Milestone 5 - Phase 3 Iteration 1: Dependency Analyzer Core and Direct Extractors

## Scope
Implement the direct dependency extraction layer for supported preview asset families and lock parser behavior needed by transitive closure.

## Deliverables
- Dependency analyzer implementation exists for direct extraction.
- Parser dispatch is deterministic by family/extension.
- Direct dependencies are returned as canonical `IReadOnlySet<AssetIdentity>`.
- Coverage exists for core family extraction use cases.

## Detailed tasks

### Parser and dispatch
- [x] Add `src/SRN.CC.Preview/DependencyAnalyzer.cs`.
- [x] Add a deterministic parser dispatch map by file family and extension.
- [x] Define and enforce parsing behavior for unsupported/unknown extensions with explicit skip/failure semantics.
- [x] Document stream ownership expectations and return contract in docs/comments for `AnalyzeDependenciesAsync`.

### Family-specific extractors
- [x] Parse MDL geometries and extract:
  - mesh texture references
  - supermodel references
- [x] Parse MTR files for direct texture / texture-family dependencies.
- [x] Resolve companion-family references through shared resref resolution:
  - `TXI`
  - `WOK`
  - `PWK`
  - `DWK`
- [x] Parse `SET` files:
  - deterministic tokenization
  - explicit inclusion/exclusion rules for non-asset tokens.

### Contracts and validation
- [x] Return direct dependency set as `IReadOnlySet<AssetIdentity>`.
- [x] Document dedupe expectations for direct extraction.
- [x] Add a shared contract note for later stages:
  - output shape includes `AssetIdentity` identity only, without recursive resolution.
- [x] Add deterministic output ordering tests for parser dispatch and family routing.
- [x] Ensure parser paths are exercised for all supported families before moving to traversal.

### Definition of done
- [x] Parser selection is reproducible on repeated runs.
- [x] Each supported family above produces complete expected direct dependencies for fixture coverage.
- [x] No direct extractor path can emit duplicate direct entries for the same source+identity pair.
- [x] Error handling for malformed family inputs is deterministic and testable.

## Exit Criteria
- [x] `DependencyAnalyzer` can parse all targeted family types in Phase 3 scope.
- [x] Direct dependency set is stable, deduplicated, and typed as `IReadOnlySet<AssetIdentity>`.
- [x] Unit fixtures exist for MDL, MTR, TXI/WOK/PWK/DWK, and SET parser coverage.

## Completion evidence (tests)
- [x] Fixture-based test matrix validates each family extractor path.
- [x] Parser dispatch tests verify consistent behavior for ambiguous/edge extensions.


---

> Reconciled against delivered code during Milestone 5 completion pass; see `docs/MILESTONE-5-3.md` for the Phase 3 completion summary.
