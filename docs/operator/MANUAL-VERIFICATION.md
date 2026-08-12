# Manual verification runbook

Two acceptance clauses in `PLAN.md` cannot run on an arbitrary build machine because they need a real
HAK corpus and a real GPU. The automated gate (`tools/VerifyBuild.ps1`) deliberately does **not**
cover them, and the milestone-7 evidence records them as unproven rather than claimed. This runbook is
how you close them on a reference machine before signing off a release.

Run these on the recorded reference machine and capture the results next to the release evidence.

## 1. Corpus indexing and working-set budget (PLAN.md:229)

Requires the full 117-HAK corpus (187,943 entries) on local disk.

Set the corpus location and opt in with the *require* flag so a missing corpus fails loudly instead of
being skipped:

```bash
setx SRNCC_CORPUS_ROOT "D:\path\to\corpus"
setx SRNCC_REQUIRE_CORPUS 1
```

Then run the corpus test project in Release, in a new shell so the variables are picked up:

```bash
dotnet test tests/SRN.CC.CorpusTests/SRN.CC.CorpusTests.csproj -c Release
```

What to confirm:

- **Index acceptance**: all 117 HAKs and exactly 187,943 entries index without extraction, preserving
  the four duplicate file instances and all 212 type-2078 entries.
- **Cold-index time**: median of three cold-application-cache runs is under 10 seconds. Measure the
  application cache cold, not the OS disk cache — re-run after warming the OS cache so eviction is not
  what you are timing.
- **Idle working set**: after indexing and 30 seconds idle, the private working set stays below
  750 MiB. Opt in to that probe with `SRNCC_RUN_IDLE_PROBE=1`.

Record the three run times, the median, and the idle working-set figure with the machine baseline.

## 2. Real-GPU preview smoke pass (PLAN.md:230)

Requires a machine with a real OpenGL-capable GPU. The default test tier uses a fake GL device; this
pass exercises the real device.

```bash
setx SRNCC_RUN_GPU 1
dotnet test tests/SRN.CC.CorpusTests/SRN.CC.CorpusTests.csproj -c Release
```

Then launch the application (`docs/operator/INSTALL-AND-RUN.md`) and, on representative corpus content,
confirm one-to-three-slot model and walkmesh previews render, survive unsupported features and context
loss without leaking GPU objects or sharing handles across contexts, and dispose cleanly on slot
teardown. Walk the real-GPU operator checklist in `docs/evidence/MILESTONE-7-EVIDENCE.md` §13 and sign
its line.

## Where this fits in the release flow

`tools/VerifyBuild.ps1` is the automated gate and must be green first. This runbook is the **manual**
addition for a full 1.0 sign-off: the corpus and GPU clauses are environment-bound, so they are run by
hand on the reference machine and their results recorded in the release evidence, not asserted by CI.
