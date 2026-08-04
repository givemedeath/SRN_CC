# Milestone 5 - Phase 3 Iteration 3: AddAvailableDependenciesCommand and Output Flow

## Scope
Wire dependency traversal into the app command surface and normalize the user-facing closure/unresolved output.

## Deliverables
- `AddAvailableDependenciesCommand` has explicit input and output contracts.
- Deterministic closure output and unresolved grouping for user confirmation.
- Completion/interrupt flow that preserves unresolved traceability.

## Detailed tasks
- [x] Update `src/SRN.CC.App/ViewModels/MainWindowViewModel.cs` and related bindings.
- [x] Implement/extend `AddAvailableDependenciesCommand`:
  - input: selected asset occurrences
  - output: closure result containing:
    - `resolved`
    - `unresolved`
    - `bytes`
    - `count`
- [x] Define and record shared traversal contract:
  - `resolved` set
  - `unresolved` set with reason
  - metrics summary (bytes, count, max-depth, duplicate-suppressed count)
- [x] Keep resolution deterministic:
  - stable ordering for queue/dependency processing
  - stable output ordering in prompt and confirmation dialog
- [x] Add unresolved grouping:
  - by family
  - by source dependency path
  - by originating parent
  - with counts and missing reason strings
- [x] Ensure unresolved reporting includes every missing dependency discovered during full transitive traversal.
- [x] Preserve unresolved entries regardless of source depth or error severity.
- [x] Add user confirmation step:
  - prompt with additional imports list
  - explicit cancel/continue behavior
  - continue path preserves full unresolved traceability

### Definition of done
- [x] Command returns deterministic closure summaries on repeated selections.
- [x] Unresolved reporting includes complete grouped parent/source context.
- [x] Confirm flow can cancel or continue without losing dependency summary state.

## Exit Criteria
- [x] User flow: selection -> closure preview -> confirm/cancel is stable.
- [x] Closure output contains deterministic, grouped, and complete unresolved data.
- [x] No hidden unresolved entries before confirmation.

## Completion evidence (tests)
- [x] Unit tests validate deterministic ordering and grouping behavior.
- [x] Command tests validate cancel and continue branches and output shapes.


---

> Reconciled against delivered code during Milestone 5 completion pass; see `docs/MILESTONE-5-3.md` for the Phase 3 completion summary.
