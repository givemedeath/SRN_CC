# Milestone 5 - Phase 3 Iteration 4: Resolution Strategy, Testability, and Responsiveness

## Scope
Finalize resolution behavior by proving workspace-first catalog fallback, adding command-level test seams, and ensuring cancellation responsiveness.

## Deliverables
- Verified resolution strategy for workspace-first then base-game catalog.
- Explicit helper seam for command testability.
- Cancellation-aware command execution under large graph workloads.
- Shared traversal result contract finalized across iterations.

## Detailed tasks
- [ ] Verify both catalog discovery entry paths:
  - `NwnInstallOverride`
  - `NwnInstallLocator` auto-discovery
- [ ] Confirm workspace-first fallback ordering:
  - immediate candidates resolve from workspace first
  - unresolved workspace candidates resolve from catalog when available
- [ ] Add helper abstraction for command dependency lookup:
  - dependency locator + resolver adapter
  - explicit seam for unit tests and deterministic injection
- [ ] Define shared traversal result contract used by command and analyzer layers:
  - resolved set
  - unresolved set with reasons
  - summary metrics: bytes, count, max depth, duplicates suppressed
- [ ] Keep command cancellation responsive for large traversal runs.
- [ ] Document behavior when traversal or resolution fails hard during execution.
- [ ] Add completion checks for workspace/catalog parity coverage at the command layer.

### Definition of done
- [ ] Command tests can inject fake locator/resolver via helper seam.
- [ ] Resolution tests cover both discovery paths without integration side effects.
- [ ] Cancellation tests verify command remains responsive under heavy traversal.

## Exit Criteria
- [ ] Workspace and catalog resolution strategy is fully verified at phase level.
- [ ] Command remains responsive under cancellation and large input graphs.
- [ ] Final traversal contract is explicit and stable across all four iterations.

## Completion evidence (tests)
- [ ] Catalog discovery tests include `NwnInstallOverride` and `NwnInstallLocator`.
- [ ] End-to-end closure command smoke test covers failure and happy paths.
