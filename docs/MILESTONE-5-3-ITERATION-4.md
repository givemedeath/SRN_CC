# Milestone 5 - Phase 3 Iteration 4: Resolution Strategy, Testability, and Responsiveness

## Scope
Finalize resolution behavior by proving workspace-first catalog fallback, adding command-level test seams, and ensuring cancellation responsiveness.

## Deliverables
- Verified resolution strategy for workspace-first then base-game catalog.
- Explicit helper seam for command testability.
- Cancellation-aware command execution under large graph workloads.
- Shared traversal result contract finalized across iterations.

## Detailed tasks
- [x] Verify both catalog discovery entry paths:
  - `NwnInstallOverride`
  - `NwnInstallLocator` auto-discovery
- [x] Confirm workspace-first fallback ordering:
  - immediate candidates resolve from workspace first
  - unresolved workspace candidates resolve from catalog when available
- [x] Add helper abstraction for command dependency lookup:
  - dependency locator + resolver adapter
  - explicit seam for unit tests and deterministic injection
- [x] Define shared traversal result contract used by command and analyzer layers:
  - resolved set
  - unresolved set with reasons
  - summary metrics: bytes, count, max depth, duplicates suppressed
- [x] Keep command cancellation responsive for large traversal runs.
- [x] Document behavior when traversal or resolution fails hard during execution.
- [x] Add completion checks for workspace/catalog parity coverage at the command layer.

### Definition of done
- [x] Command tests can inject fake locator/resolver via helper seam.
- [x] Resolution tests cover both discovery paths without integration side effects.
- [x] Cancellation tests verify command remains responsive under heavy traversal.

## Exit Criteria
- [x] Workspace and catalog resolution strategy is fully verified at phase level.
- [x] Command remains responsive under cancellation and large input graphs.
- [x] Final traversal contract is explicit and stable across all four iterations.

## Completion evidence (tests)
- [x] Catalog discovery tests include `NwnInstallOverride` and `NwnInstallLocator`.
- [x] End-to-end closure command smoke test covers failure and happy paths.

---

> Reconciled against delivered code during Milestone 5 completion pass; see `docs/MILESTONE-5-3.md` for the Phase 3 completion summary.
