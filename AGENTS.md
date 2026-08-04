## Review closure policy (machine-readable)

```yaml
reviewClosurePolicy:
  schemaVersion: 1
  appliesTo: pull-request.review-threads
  resolvedThread:
    requiredEvidence: exactly-one
    acceptedEvidence:
      - type: fixing-commit
        requiredFields: [commit, changedPaths, verification]
      - type: contract-supported-rejection
        requiredFields: [decision, contractRefs, rationale, verification]
    uiResolvedAlone: forbidden
    unresolvedEvidenceAction: block-merge
```

Every resolved thread has either a traceable fixing commit or a documented,
contract-supported rejection. A resolved UI state alone is never closure evidence and
must not be used to justify merge, package commitment, or acceptance.