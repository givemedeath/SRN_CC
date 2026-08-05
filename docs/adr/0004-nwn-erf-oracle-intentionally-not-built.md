# 4. NWN_ERF Oracle Intentionally Not Built

* Status: Accepted
* Date: 2026-08-04

## Context and Problem Statement

`PLAN.md:170-176` specifies an opt-in downloader that fetches `nwn_erf 2.1.2` into ignored local
storage (`/tools/local_only/`, reserved by `.gitignore:8`), verifying three pinned SHA-256 values —
the Windows x64 archive, `nwn_erf.exe`, and the required `sqlite3_64.dll`. `PLAN.md:228` then
specifies opt-in "Oracle tests: opt-in `nwn_erf -t` compatibility checks for ASCII fixtures/output
only".

Six milestones later, none of it exists. A repository-wide grep for `nwn_erf`, `sqlite3_64`, and
`neverwinter.nim` returns hits only in `PLAN.md` and in milestone documentation — no downloader
script under `tools/` (which holds exactly four scripts: `AuditDependencies.ps1`, `AuditPublish.ps1`,
`AuditVendoredSources.ps1`, `VerifyBuild.ps1`), no test fixture, no pinned hash in any source or
policy file. `tools/VerifyBuild.ps1:85,89` asserts `tools/local_only/` is ignored and untracked, but
nothing in the repository has ever written there.

Milestone 7 is release hardening — the last milestone before 1.0 acceptance, and the point at which
`PLAN.md`'s test plan is mapped row by row to real coverage (`docs/MILESTONE-7-5.md`). The oracle row
must resolve to something. This ADR decides what.

## Decision Drivers

* `PLAN.md:228` itself forbids the oracle from being the authoritative duplicate or CP1252 verifier
  ("never use its listing as the authoritative duplicate or CP1252 verifier"). Whatever the oracle
  would have been, the plan never intended it to be the source of truth.
* `PLAN.md:16` records why the writer is first-party in the first place: `nwn_erf 2.1.2` encodes
  non-ASCII staging filenames as UTF-8 rather than CP1252, buffers each resource while packing, and
  can skip invalid directory entries while still returning exit code zero. Those are precisely the
  three behaviours the first-party HAK path exists to avoid.
* The first-party reader/writer tests already discharge the verification role, across cases an
  ASCII-only listing structurally cannot express (CP1252-accented resref bytes, case-equivalent
  duplicate keys, `ushort.MaxValue` resource types, exactly-shared payload ranges).
* Milestone 7's release audit adds `nwn_erf` and `sqlite3_64.dll` to `forbiddenPathFragments`
  (`docs/MILESTONE-7.md:357-358`, slice S7). A tool the repository can download is a tool whose name
  can end up in a release artifact, and the audit that catches that is the same audit the oracle
  would put at risk.
* Nothing about a downloaded third-party Windows executable runs in CI: it requires network access,
  a hash-verified fetch, a tool-discovery path with its own failure modes, and a machine that is not
  the build agent.
* The plan's own pin data is not usable as written: the `nwn_erf.exe` SHA-256 at `PLAN.md:173` is 63
  hexadecimal characters, not 64. A verifying downloader could not have been implemented from that
  line without first re-deriving the hash from the upstream release.

## Considered Options

1. Build the oracle as specified: an opt-in, hash-verifying downloader into `/tools/local_only/`
   plus opt-in `nwn_erf -t` compatibility tests over ASCII-only fixtures.
2. Do not build it. Close the item as intentionally not built, and record the first-party tests that
   already discharge its intended role.
3. Add a second, independent in-repo HAK reader implementation as a differential cross-check,
   replacing the external binary with a first-party one.

## Decision Outcome

Chosen option: **Option 2 — the `nwn_erf` oracle is closed as intentionally not built.**

This mirrors how Milestone 6 closed slice S16: `docs/MILESTONE-6-0-VENDORING-DECISION.md` returned a
**NO-GO** verdict on vendoring `MdlMeshBuilder.cs` and `docs/MILESTONE-6-5.md` recorded the slice as
closed on that evidence rather than abandoned. The same disposition applies here. `PLAN.md:170-176`
and `PLAN.md:228` are not deleted or contradicted; they are answered.

### The tests that discharge the oracle's intended role

All in `tests/SRN.CC.Tests/`, namespace `SRN.CC.Tests.Formats`, and all in the default
(non-corpus, non-performance) run:

* **CP1252 verification** — `HakRoundTripTests.HAK_CP1252UnknownTypeAndZeroLengthPayload_ShouldSurvive`
  (`Formats/HakRoundTripTests.cs:76`) writes a resref whose second byte is `0xE9`, with resource type
  `ushort.MaxValue` and a zero-byte payload, and asserts both the raw resref bytes and the canonical
  (lowercased) bytes survive a full write/read cycle at the byte level. `nwn_erf`'s listing cannot
  express this input, let alone verify it — per `PLAN.md:16`, it would re-encode the name as UTF-8.
* **Duplicate-key verification** — `HakRoundTripTests.HAK_CaseEquivalentDuplicateKeys_ShouldThrow`
  (`:120`) asserts that `Duplicate`/`duplicate` at the same resource type is rejected by the writer
  rather than silently packed. `HakReaderTests.Read_ExactSharedPayloadRanges_Succeeds`
  (`Formats/HakReaderTests.cs:93`) covers the legitimate opposite case: two distinct keys pointing at
  one identical payload range, which must be accepted.
* **Deterministic output** — `HakRoundTripTests.HAK_Writes_ShouldBeIndependentOfInputOrder` (`:91`)
  asserts byte-identical output from two differently-ordered input sets, and
  `HAK_MetadataRichInput_ShouldNormalizeOnRewrite` (`:100`) asserts that build ID, timestamp, and
  description fields are zeroed on rewrite so output does not carry the clock. Slice S16 extends this
  in wave 1 with `tests/SRN.CC.Tests/Build/HakDeterminismTests.cs`, packing the same `BuildPlan` twice
  through the production `BuildOrchestrator` and asserting byte equality, plus CP1252 resrefs, paths
  containing spaces, opaque unknown types, and mid-build source mutation.
* **Structural round trip** — `HakRoundTripTests.HAK_WriteAndRead_ShouldRoundTripSemantically`
  (`:12`), `HAK_NonSequentialResourceIds_ShouldMapKeysThroughResourceTable` (`:35`), and
  `HakReaderTests.WriteAndRead_RoundTrip_PreservesSortingAndPayloads`
  (`Formats/HakReaderTests.cs:123`).
* **Malformed-input rejection** — the cases an oracle that "can skip invalid directory entries while
  returning exit code zero" is structurally unfit to judge:
  `HakReaderTests.Read_TableOverlapHeader_ThrowsInvalidDataException` (`:40`),
  `Read_PartialPayloadOverlap_ThrowsInvalidDataException` (`:57`),
  `Read_InconsistentLocalizedStringMetadata_Throws` (`:165`, three cases),
  `Read_EntryMetadataExceedsAllocationBudget_ThrowsBeforeAllocating` (`:184`),
  `Read_LargeLocalizedStringRecord_SkipsWithoutIntSizedAllocation` (`:200`),
  `HakRoundTripTests.HAK_TruncatedAndOutOfRangePayloads_ShouldThrowSafely` (`:138`),
  `HAK_InvalidResourceId_ShouldThrow` (`:54`), `HAK_WrongFileType_ShouldThrow` (`:65`), and
  `HAK_InvalidWriterKeys_ShouldThrow` (`:128`).

### Consequences

* Positive: no tool-discovery path, no hash-pinned network fetch, no third-party executable name that
  a release-audit forbidden-fragment check must then police in output it did not produce.
* Positive: the verification story stays entirely inside the default `dotnet test` run. There is no
  opt-in tier whose green status depends on a machine having downloaded a binary, and therefore no
  tier that quietly stops running.
* Positive: `docs/MILESTONE-7-5.md`'s test-plan mapping can cite this ADR for the `PLAN.md:228` row as
  a contract-supported rejection rather than leaving it as an open gap.
* **Negative — the real cost.** An independent third-party implementation reading our output is a
  genuinely different kind of evidence from our own tests reading our own output. Our reader and our
  writer share a codebase, an author, and a set of assumptions about the ERF layout; a shared
  misreading of the format would round-trip cleanly and pass every test listed above. `nwn_erf -t`
  would not share that blind spot. This decision forgoes that cross-check, and the ASCII-only,
  never-authoritative scope `PLAN.md:228` permits does not make the loss zero — it only makes it
  small enough to accept.
* Negative: the residual risk lands where it always was — on real-toolset acceptance. Nothing in this
  repository proves a Neverwinter Nights toolset will load a HAK the first-party writer produced. The
  clean-machine smoke test in `PLAN.md:215` is the closest substitute and does not cover it.
* Negative: `PLAN.md:170-176`'s hash pins are now dead text. They are left in place as a record of
  what was considered, not as an instruction.

### Rejected options

**Option 1 (build the oracle as specified)** was rejected because it costs a downloader, a
tool-discovery path, a fixture tier, and a permanent CI-exempt surface to buy a check that
`PLAN.md:228` had already declared non-authoritative, over an ASCII-only subset of the input space
that the first-party tests cover more thoroughly. `PLAN.md:16` documents three concrete respects in
which `nwn_erf 2.1.2` is a weaker implementation than the one it would be checking: UTF-8 rather than
CP1252 for non-ASCII names, per-resource buffering, and skipping invalid directory entries while
returning exit code zero. An oracle less strict than its subject can confirm agreement but cannot
establish correctness, and the one case where it would disagree loudly — a non-ASCII resref — is the
case `PLAN.md:228` explicitly excludes from its remit.

**Option 3 (a second in-repo reader)** was rejected on cost and on independence. It would be a
second implementation by the same author against the same understanding of the format, which is most
of what makes a differential oracle worth having and exactly the property a first-party clone cannot
supply. It would also add production or test source subject to `tools/AuditVendoredSources.ps1`'s
hard-coded `40`/`8` file counts at three sites, which Milestone 7 forbids changing.

### Compliance gate to reopen this decision in a future milestone

Any one of the following makes building the oracle worthwhile, and none of them is speculative:

1. **A reported field defect** — a real Neverwinter Nights toolset, or any third-party ERF consumer,
   rejects or misreads a HAK that the first-party verifier
   (`src/SRN.CC.Infrastructure/Build/BuildVerifier.cs` plus the tests above) accepts. This is the case the first-party tests structurally cannot catch, and it is the reason
   the gate exists.
2. **A change to the HAK write path's on-disk layout** — anything beyond entry content: header field
   semantics, key/resource table ordering or offsets, the size-limit boundary, or the introduction of
   a second output format. A differential check is worth most at exactly the moment the format
   handling changes.
3. **A supported non-ASCII path in a third-party tool** — an `nwn_erf` release (or an equivalent) that
   documents CP1252 resref handling, at which point the tool stops being weaker than its subject on
   the axis that matters and the `PLAN.md:228` restriction could be lifted rather than worked around.

If reopened, the mechanical work is: re-derive all three SHA-256 values from the upstream release
(the `nwn_erf.exe` pin at `PLAN.md:173` is 63 hex characters and cannot be used as transcribed); add
the opt-in downloader writing only into `/tools/local_only/`; gate the tests behind an explicit
environment variable in the style of `SRNCC_RUN_CORPUS`, defaulting to skipped; and confirm
`tools/AuditRelease.ps1`'s forbidden-fragment check still passes with the tool present on the
build machine.
