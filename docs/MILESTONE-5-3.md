# Milestone 5 - Phase 3: Dependency Analysis

## Scope
Implement cycle-safe dependency extraction and transitive closure for preview import workflows.

## Deliverables
- Functional dependency graph builder for supported families.
- Deterministic closure results with cycle protection.
- Import flow that resolves dependencies first from workspace, then base-game catalog.

## Detailed tasks

### Dependency Analyzer
- [ ] Add `src/SRN.CC.Preview/DependencyAnalyzer.cs`.
- [ ] Design parser dispatch table by file family/extension.
- [ ] Parse MDL geometries:
  - extract mesh texture and supermodel references.
- [ ] Parse MTR files:
  - extract direct texture / texture-family dependencies.
- [ ] Parse companion files:
  - `TXI`, `WOK`, `PWK`, `DWK` reference resolution via shared resref logic.
- [ ] Parse `SET` files:
  - deterministic tokenization
  - explicit inclusion/exclusion of non-asset tokens.
- [ ] Return direct dependency set as `IReadOnlySet<AssetIdentity>`.
- [ ] Build cycle-safe traversal:
  - maintain `visited` set by `AssetIdentity`.
  - maintain `inFlight` set to guard pathological graphs.
  - enforce recursion depth limit and hard fail on exceeded depth.
- [ ] Add duplicate suppression so repeated assets are resolved once.

### Integration with Closure Command
- [ ] Update `src/SRN.CC.App/ViewModels/MainWindowViewModel.cs` and related command bindings.
- [ ] Implement/extend `AddAvailableDependenciesCommand`:
  - input: selected asset occurrences.
  - output: closure result with `resolved`, `unresolved`, `bytes`, and `count`.
- [ ] Resolution strategy:
  - workspace-first lookup for immediate dependency candidates.
  - base-game catalog fallback when workspace miss.
- [ ] Keep resolution deterministic:
  - stable ordering for queue/dependency processing.
  - stable output ordering in prompt and confirmation dialog.
- [ ] Add unresolved grouping:
  - by family
  - by source dependency path and originating parent
  - with clear counts and missing reason strings.
- [ ] Ensure unresolved reporting is complete:
  - include every missing dependency discovered during full transitive traversal.
  - do not hide unresolved entries based on source depth or error severity.
- [ ] Add user confirmation step:
  - confirm list of additional imports.
  - cancel/continue behavior.
  - no unresolved filtering before user confirmation.
  - continue path is explicit and must preserve full unresolved traceability.

## Additional Delivery
- [ ] Verify both catalog discovery entry paths:
  - `NwnInstallOverride`
  - `NwnInstallLocator` auto discovery.
- [ ] Add helper abstraction for analyzer dependencies to allow unit testing the command.
- [ ] Keep command cancellation responsive under large dependency graphs.

## Exit Criteria
- [ ] Cycle-safe transitive closure completes on cyclic fixture graphs.
- [ ] Workspace and catalog resolution path are both covered by command-level tests.
- [ ] Unresolved dependencies are visible, grouped, and actionable.
- [ ] Imported list is deterministic across repeated runs.
