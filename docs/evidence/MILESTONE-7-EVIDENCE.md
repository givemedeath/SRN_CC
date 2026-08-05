# Milestone 7 Verification Evidence: Release Hardening

## Summary

Milestone 7 adds the startup preflight, the schema-migration seam, first-party logging, operator
documentation, and the deterministic release pipeline, then proves the whole application against
`PLAN.md:212-215` and the acceptance plan at `PLAN.md:217-230`.

Every row in sections 9 and 10 names a test that exists and passes. Each was verified by running its
filter, not by reading the source. Rows that cannot name a test say so and explain what stands in its
place; the `nwn_erf` oracle row is a contract-supported rejection citing ADR 0004, per the closure
policy in `AGENTS.md`.

Two clauses are **not verified on this machine** and are recorded as such rather than implied:
the corpus and performance measurements at `PLAN.md:229`, because `SRNCC_CORPUS_ROOT` is unset and no
corpus is present, and the manual real-GPU operator checklist in section 13, which is carried in full
and marked unrun with its reason. An honest gap is auditable; an implied pass is not.

Authoritative record: `tools/VerifyBuild.ps1` run `20260805T060309Z-16300`, status SUCCESS, on a
clean tree.

---

## 1. Startup Contracts (`SRN.CC.Core/Startup/`)

`AppPaths` centralises every `%LOCALAPPDATA%\SRN.CC` location so no component derives its own.
`IStartupCheck` is the single seam every preflight check implements, returning a
`StartupCheckResult` carrying an id, a `StartupCheckSeverity`, and a human-readable summary.
`StartupReport` aggregates the results and is the document the clean-machine smoke runner parses.

No shipped check may return `Blocking`. A first launch on a machine with nothing under
`%LOCALAPPDATA%` must reach a usable window; a check exists to report and to recover, never to refuse
to start. `StartupPreflightTests.NoShippedCheckEverReturnsBlocking_AssertedOverTheRealFour` asserts
this over the real four rather than over a stub.

## 2. The Four Startup Checks

`cache`, `settings`, `publication-journal`, `tool-capability` — asserted as an exact ordered sequence,
not as a set, by `App.AppServicesTests.CreateAsync_RunsAllFourChecksInTheOrderTheHandoffRequires`.
Asserting the order is what makes "the handoff requires it" checkable: a check reordered or dropped
from the wiring fails the test rather than passing a count-only assertion.

Note that `tools/SmokeCleanMachine.ps1` lists the same four ids in a different order in
`$expectedCheckIds`. That is deliberate and not a contradiction — the smoke runner asserts the *set*
of checks present in the report, while the ordering contract is asserted in the test above.

Each degrades rather than blocks. A corrupt cache is quarantined and rebuilt; corrupt settings are
quarantined and defaults returned; a journal left mid-publish is rolled back at startup, and a
recovery failure is reported as degraded and names the directory rather than being swallowed.

## 3. Schema Registry and Migration Seam (`SRN.CC.Core/Schema/`)

`SchemaVersions` is the single source of schema truth for the project file, the settings file, and
the preview cache. `SchemaMigrationPipeline` is the upgrade seam, and ships with **zero** migrations
— every persisted schema is at v1 — while proving the mechanism works via a synthetic two-step chain.
Shipping the seam empty is the point: the first real schema change is a registration, not an
architecture change.

The newer-schema rule is uniform. A file whose schema version exceeds the current one opens
**read-only** rather than being upgraded, downgraded, or refused.

## 4. Logging (`SRN.CC.Infrastructure/Logging/`)

`AppLogger` over `JsonLineLogSink`, writing one JSON object per line to `Logs\srncc.log`, rolling
across ten files of ten mebibytes each via `LogFileSet`. Synchronous and first-party by design; see
ADR 0005.

Components take `IAppLogger? logger = null` as a **trailing optional** parameter. That convention is
what let logging reach into existing constructors across four projects without a single breaking
signature change.

## 5. Composition Root (`SRN.CC.App/Services/AppServices.cs`)

One explicit composition root, no DI container. `AppServices.CreateAsync` builds the object graph,
runs the four checks in order, and hands the report to the shell, which surfaces it through
`ObservableLogSink` in the operation log. `Program.cs` recognises `--srncc-preflight-only`, which
serialises the `StartupReport` to stdout and exits 0 without creating a window — the contract the
clean-machine smoke runner depends on.

## 6. Release Pipeline (`tools/`)

`VerifyBuild.ps1` is an eleven-step ladder: SDK pin, locked restore, dependency and vendored-source
audits, Release/win-x64 build, tests, self-contained publish, licenses and notices, publish audit,
release audit, pack-twice-and-compare, extract-outside-the-repo-and-re-audit, and a final check that
locked and vendored inputs did not move.

`PackRelease.ps1` is deterministic: entries are ordinal-sorted and every ZIP timestamp is pinned to
`1980-01-01T00:00:00Z`, so the same publish tree always produces the same archive bytes. Step 9 packs
twice into separate directories and compares SHA-256 rather than trusting the property.

`AuditRelease.ps1` scans the shipped tree for anything that must not ship. `SmokeCleanMachine.ps1`
runs the release the way a user receives it: an expanded ZIP, a preflight-only run whose report is
parsed and asserted, then a windowed run that must survive and leave a log and a cache behind.

Version comes solely from `eng/Versions.props`; no project restates it.

## 7. Operator Documentation (`docs/operator/`)

Seven documents: `INSTALL-AND-RUN.md`, `FILE-LOCATIONS.md`, `GPU-REQUIREMENTS.md`,
`MANIFEST-FORMAT.md`, `PROJECT-FILE-FORMAT.md`, `RECOVERY.md`, `TROUBLESHOOTING.md`.

These are the one gate clause with **no automated test**, and the mapping table says so rather than
naming a proxy.

## 8. Corrections Landed Alongside

Wave 3 recorded two gaps it could not close in-slice. Both were closed in wave 4, in commit
`9082dc8`.

**Closure budgets.** `DependencyTraversalEngine` enforced only `maxDepth`, so the "closure
size/count" case at `PLAN.md:225` had nothing to assert — unimplemented scope, not a coverage gap.
It now takes `maxCount` and `maxBytes` after `maxDepth`, both defaulting to effectively unlimited so
no existing call or test changed behaviour. The count budget is tested at the point of admission; the
size budget once the payload size is known, so a rejected node contributes neither bytes nor
descendants and `TotalBytes` can never exceed the budget. `TraversalLimit` records which budget fired
(first-writer wins) and surfaces as `LimitHit` / `IsTruncated` on `TraversalResult` and
`ClosureSummary`.

No `DiagnosticCode` value was added. That enum is entirely source, index, and cache faults; the
traversal's diagnostic channel is the `Unresolved` reason map, which `UnresolvedDependencyGrouper`
already buckets by exact reason, so truncation reaches `ConfirmDependenciesDialog` through the
existing path. A banner bound to `Summary.IsTruncated` keeps a bounded closure from reading as
complete.

The banner is asserted by rendering the real view headlessly, not by inspecting the view model:
`UI.ConfirmDependenciesDialogTests.TruncatedClosure_ShowsTheTruncationBannerNamingTheBudgetThatFired`,
`UI.ConfirmDependenciesDialogTests.CompleteClosure_ShowsNoTruncationBanner`, and
`UI.ConfirmDependenciesDialogTests.NoClosureAtAll_ShowsNoTruncationBanner`. That last case is the one
worth having: this project does **not** set `AvaloniaUseCompiledBindingsByDefault`, so `x:DataType` is
declarative only and an unresolvable binding path fails silently at runtime while still compiling — a
green build is no evidence that a binding works. Since `ConfirmDependenciesDialogViewModel.Summary` is
nullable and `IsVisible` defaults to `true`, a banner bound naively could have announced a truncated
closure on a dialog that had no closure at all. It does not, and now cannot regress silently.

**Pin correctness.** `DependencyLocator` built its cache from `AllOccurrences` first-wins and never
read `ResolvedOccurrence`, so a pin that moved the winner to a lower-priority source was invisible to
traversal and the closure analyzed the wrong payload; assets in `InvalidPin`, `UnresolvedDuplicate`,
`Unavailable`, or `Unpackageable` resolved to an arbitrary occurrence instead of reporting
unresolved. The cache now holds `ResolvedOccurrence` and omits anything not `Resolved`. Wave 3 logged
this as an undecided precedence question; it was not undecided — `PLAN.md:82` states a valid pin
overrides source priority, and `PLAN.md:86` states missing, changed, or ambiguous pins remain invalid
and never fall back silently. It was a bug.

---

## 9. Milestone-7 Gate Clause Mapping

Every clause of `PLAN.md:212-215`.

| Milestone-7 clause | Status | Reproducible evidence |
|---|---|---|
| Startup **cache** check | **PASSED** | `Startup.CacheStartupCheckTests.CleanCache_IsOk`; `Startup.CacheStartupCheckTests.Quarantine_IsLoggedWithTheDiagnosticEventCode`; `Cache.SqliteCacheQuarantineTests.Initialize_GarbageDatabaseFile_QuarantinesRebuildsAndEmitsDiagnostic` |
| Startup **settings** check | **PASSED** | `Startup.SettingsStartupCheckTests.ThreeStatuses_ProduceThreeDistinctSummaries`; `Persistence.SettingsStoreTests.CorruptFile_QuarantinesFile_ReturnsDefaults` |
| Startup **journal** check | **PASSED** | `Startup.PublicationJournalStartupCheckTests.RecoveryFailure_IsDegradedAndNamesTheDirectory_RatherThanBeingSwallowed`; `Build.ArtifactPublisherRecoveryTests.RecoverPendingJournalAsync_WhilePublicationLockIsHeld_ReportsContentionAndLeavesDestinationAlone` |
| Startup **tool-capability** check | **PASSED** | `Startup.ToolCapabilityStartupCheckTests.ProbesTheFourExpectedNatives` |
| All four checks run, in order, none blocking | **PASSED** | `App.AppServicesTests.CreateAsync_RunsAllFourChecksInTheOrderTheHandoffRequires`; `Startup.StartupPreflightTests.NoShippedCheckEverReturnsBlocking_AssertedOverTheRealFour`; `App.StartupPreflightWiringTests.EveryShippedCheck_ProducesExactlyOneResultAndNoneIsBlocking` |
| **Schema migration hooks** | **PASSED** | `Core.Schema.SchemaMigrationPipelineTests.TryUpgrade_ChainOfTwoSteps_AppliesBothInAscendingOrder`; `Core.Schema.SchemaMigrationPipelineTests.TryUpgrade_ShippedDefaultPipeline_UpgradesVersionOneToCurrent`; `Core.Schema.SchemaMigrationPipelineTests.MigrationCount_AsShipped_IsZero`; `Persistence.ProjectStoreSchemaTests.MigrationPipeline_IsWiredForProject_AndTargetsTheCurrentVersion` |
| **Operator documentation** | **PASSED (by inspection — no test)** | The seven files in `docs/operator/` listed in section 7. This clause has **no automated assertion**; it is the one gate clause carried on documentation alone, stated here rather than mapped to a proxy test |
| **Release audit** | **PASSED** | `Architecture.ReleasePipelineTests.VerifyBuild_InvokesTheReleaseAuditAgainstThePublishedTreeAndTheExtractedArchive`; `Architecture.ReleasePipelineTests.VerifyBuild_PacksTheReleaseTwiceAndComparesTheHashes`; `Architecture.ReleasePipelineTests.VerifyBuild_WritesTheReleaseFieldsIntoTheSummary`; plus the live run in section 12 (`AuditRelease.ps1` PASSED over 252 files, twice) |
| Publish **self-contained, non-single-file** `win-x64` | **PASSED** | `Architecture.ReleasePipelineTests.NoBuildFile_DeclaresPublishSingleFile`; live run step 6 published a 252-file folder, not an executable |
| Folder **and ZIP** | **PASSED** | Live run steps 6 and 9; `release-manifest.json` records `SRN.CC-1.0.0-win-x64.zip`, `fileCount` 252 |
| Containing **only application files, licenses, and notices** | **PASSED** | `Architecture.ReleasePipelineTests.PublishPolicy_ForbidsLogsDebugSymbolsAndProjectFilesFromTheReleaseTree`; `Architecture.ReleasePipelineTests.PublishPolicy_NeverTreatsTheProjectExtensionAsAForbiddenPathFragment`; `Architecture.NoticesCompletenessTests.NoticesMd_HasARowForEveryRuntimeScopedPackageInTheDependencyPolicy`; `Architecture.NoticesCompletenessTests.NoticesMd_RowVersionsMatchTheResolvedRuntimePackageVersions` |
| **Gate: final acceptance flow passes** | **PASSED** | `Scenarios.FullAcceptanceScenarioTests.FullAcceptanceFlow_ImportResolvePinSaveRescanBuildVerifyPublishRecover`; `Scenarios.ControlledAcceptanceScenarioTests` |
| **Gate: clean-machine smoke test passes** | **PASSED (with a recorded caveat)** | `Architecture.ReleasePipelineTests.SmokeCleanMachine_AssertsTheCleanMachinePreconditionAndTheFirstRunArtifacts`; `Architecture.ReleasePipelineTests.VerifyWorkflow_HasACleanMachineSmokeJobThatDependsOnTheVerifyJob`; `App.AppServicesTests.SerializeStartupReport_EmitsTheDocumentShapeTheSmokeRunnerParses`; `App.StartupPreflightWiringTests.PreflightJson_CarriesEveryCheckTheSmokeRunnerAssertsOn`; live run in section 12b. **Caveat: run with `-AllowExistingAppData`** — see 12b |
| **Gate: no corpus, oracle, GPL/Radoub artifact, cache, project, or absolute source path in release output** | **PASSED (script-asserted)** | `AuditRelease.ps1` performs the forbidden-content scan and PASSED over all 252 files in both the published tree and the tree extracted outside the repository (section 12). The C# side asserts the policy that drives it: `Architecture.ReleasePipelineTests.PublishPolicy_ForbidsLogsDebugSymbolsAndProjectFilesFromTheReleaseTree`. **No unit test greps the published tree directly**; the assertion lives in the release-gate script, which is what CI runs |
| Version stamped from one source | **PASSED** | `Architecture.VersionStampingTests.InformationalVersion_ForEveryProductionAssembly_EqualsVersionsPropsVersionPrefix` |

---

## 10. Acceptance Plan Mapping

Every bullet of `PLAN.md:217-230`.

| `PLAN.md` bullet | Status | Reproducible evidence |
|---|---|---|
| **Identity** (219) — folding, CP1252, 16/17-byte boundaries, NUL/separators, raw-byte round trip | **PASSED** | `Core.AssetIdentityTests.ASCIIUppercase_ShouldFoldToLowercase`; `Core.AssetIdentityTests.CP1252Accents_ShouldBePreservedWithoutUnicodeFolding`; `Core.AssetIdentityTests.ResrefLength_OneAndSixteenBytes_ShouldSucceed`; `Core.AssetIdentityTests.ResrefLength_Outside1To16Bytes_ShouldThrow`; `Core.AssetIdentityTests.ByteConstructor_ShouldRejectPaddedOrEmbeddedNul`; `Build.HakDeterminismTests.Build_AccentedResref_RoundTripsAsRawBytes` |
| **HAK** (220) — duplicate keys, nonsequential IDs, shared ranges, allocation bombs, size limit | **PASSED** | `Formats.HakRoundTripTests.HAK_CaseEquivalentDuplicateKeys_ShouldThrow`; `Formats.HakRoundTripTests.HAK_NonSequentialResourceIds_ShouldMapKeysThroughResourceTable`; `Formats.HakReaderTests.Read_ExactSharedPayloadRanges_Succeeds`; `Formats.HakReaderTests.Read_EntryMetadataExceedsAllocationBudget_ThrowsBeforeAllocating`; `Formats.HakReaderTests.HakWriter_ExceedsSizeLimit_ThrowsInvalidOperationException` |
| **Resolution** (221) — priority, pins, changed pins, reattachment, case-only flags, override compaction, rescans | **PASSED** | `Core.ResolutionTests.PriorityOrdering_HighestPriorityAvailableSource_ShouldWin`; `Core.PinReattachmentTests.Reattach_Rung2_ExactLocatorGoneEntirely_ButUniqueSameSourceHashMatch_MovesThePin`; `Core.ResolutionRescanTests.Rescan_WhenCaseOnlyNamingDifferenceAppears_SetsTheFlagWithoutBlockingResolution`; `Core.SelectionOverrideCompactionTests.OverrideCompaction_SetDefaultKeepsOnlyOverridesThatStillDifferFromTheNewDefault`; pin-versus-priority precedence in the dependency path: `Preview.DependencyClosureTests.Closure_WithPinOverridingSourcePriority_UsesThePinnedOccurrenceAsTheDependency`; `Preview.DependencyClosureTests.Closure_WithUnresolvedCuratedAsset_ReportsItUnresolvedRatherThanSubstitutingAnOccurrence` |
| **Persistence** (222) — relative/absolute paths, unknown-field preservation, newer schema, atomic failure, corrupt cache/settings, relocation | **PASSED (one deferral)** | `Persistence.ProjectStoreTests.PreserveNestedObjectUnknownProperties_OnSave_ShouldRetainPropertiesInPathAndLocator`; `Persistence.ProjectStoreTests.NewerSchemaVersion_OpensReadOnly`; `Persistence.ProjectStoreSchemaTests.FailedAtomicWrite_IsLogged_AndLeavesNoTempFileBehind`; `Persistence.SettingsStoreTests.CorruptFile_QuarantinesFile_ReturnsDefaults`; `Cache.SqliteCacheQuarantineTests.Initialize_GarbageDatabaseFile_QuarantinesRebuildsAndEmitsDiagnostic`; `Core.ResolutionRescanTests.Relocate_ToANewPathWithTheSameContent_RepointsTheSourceAndKeepsIdentitiesResolved`. **Recursive unknown-field preservation is project-only**; `PLAN.md:94` requires it for projects, and settings are deferred — see section 11 |
| **UI** (223) — sorting/filtering, 188k rows, bulk selection, slots, promotion, mode switching, linked state | **PASSED** | `UI.TableViewLargeDatasetTests.TableView_With187943Rows_SortFilterAndBulkSelectionAllStayVirtualized`; `UI.ComparisonPanelSlotLifecycleTests.OccurrenceMode_WhenALeadingOccurrenceDisappears_ThePreviouslyOverflowOccurrencePromotesIntoASlot`; `UI.ComparisonPanelSlotLifecycleTests.ToggleMode_FromOccurrenceToResolved_RepopulatesTheSlotsFromTheSameSelection`; `UI.AssetTypeFilterTests.SelectingAType_ComposesWithTheSearchBox` |
| **Preview** (224) — valid/truncated/malformed/canceled/oversized for every provider; one success, one failure, one cancel concurrently | **PASSED** | `Preview.PreviewProviderMatrixTests.Image_TruncatedTga_FailsInsteadOfDecodingPartialPixels`; `Preview.PreviewProviderMatrixTests.Audio_OversizedPayload_StopsAtTheSixtyFourMebibyteBudget`; `Preview.PreviewProviderMatrixTests.Mdl_CanceledToken_ThrowsOperationCanceled`; `Preview.PreviewProviderMatrixTests.ConcurrentSlots_OneSuccessOneFailureOneCancellation_EachResolvesIndependently` |
| **Dependency** (225) — curated precedence, base fallback, missing references, cycles, closure size/count, unresolved, no automatic base-game packaging | **PASSED** | `Preview.DependencyClosureTests.Closure_WithIdentityInTwoSources_UsesCuratedPrecedenceWinnerAsTheDependency`; `Preview.DependencyClosureTests.Closure_WithDependencyOnlyInBaseGame_ReportsItUnresolvedAndPackagesNothingAutomatically`; `Preview.DependencyClosureTests.Closure_WithTwoNodeCycle_TerminatesAndResolvesEachIdentityExactlyOnce`; `Preview.DependencyClosureTests.Closure_CountLimit_StopsAtMaxCountAndReportsRemainderUnresolved`; `Preview.DependencyClosureTests.Closure_SizeLimit_StopsWhenAccumulatedBytesWouldExceedBudget`; `Preview.DependencyClosureTests.Closure_SizeLimit_NeverReportsTotalBytesAboveTheBudget`; `Preview.DependencyClosureTests.Closure_WithMissingReference_ReportsItUnresolvedWithoutFailingTheClosure` |
| **Build** (226) — source mutation, locked files, spaces, CP1252 resrefs, unknown types, size limit, byte-identical output, exact manifest hashing | **PASSED** | `Build.HakDeterminismTests.Build_SourceMutatedMidBuild_FailsVerification`; `Build.HakDeterminismTests.Build_PathsContainingSpaces_Succeed`; `Build.HakDeterminismTests.Build_TypesTheRegistryCannotName_ArePackagedOpaquely`; `Build.HakDeterminismTests.Packer_SamePlanPackedTwice_ProducesByteIdenticalHak`; `Build.BuildOrchestratorTests.Preflight_EstimateExceedsSingleHakLimit_ReportsBothNumbers`; `Build.ProvenanceManifestGeneratorTests.GenerateManifestAsync_ItemWithoutExpectedHash_ComputesPayloadHashFromHak` |
| **Publication** (227) — failure at every journal state, pair rollback, startup recovery, lock handling, previous destination preserved | **PASSED** | `Build.ArtifactPublisherJournalStateTests.Recovery_FromEveryState_LeavesDestinationPairByteForByte`; `Build.ArtifactPublisherJournalStateTests.PublishAsync_FailureReplacingManifest_RollsBackTheAlreadyReplacedHakByteForByte`; `Build.ArtifactPublisherRecoveryTests.RecoverPendingJournalAsync_WhilePublicationLockIsHeld_ReportsContentionAndLeavesDestinationAlone`; `Build.ArtifactPublisherTests.PublishAsync_FailureDuringReplacement_RestoresOriginalFilesFromBackup` |
| **Oracle** (228) — opt-in `nwn_erf -t` compatibility checks | **NOT BUILT — contract-supported rejection** | `docs/adr/0004-nwn-erf-oracle-intentionally-not-built.md`. **No test exists and none is claimed.** `PLAN.md:228` makes the oracle opt-in and forbids it from being the authoritative duplicate or CP1252 verifier, so nothing in the gate depends on it. Supporting detail: the `nwn_erf.exe` SHA-256 pin at `PLAN.md:173` is 63 hex characters, not 64, so the specified downloader was never implementable as transcribed — this strengthens the rejection but is not its basis |
| **Corpus / performance** (229) — cold index median of three under 10s; private working set under 750 MiB after 30s idle | **NOT VERIFIED ON THIS MACHINE** | Tests exist and are correct: `SRN.CC.CorpusTests.CorpusIndexAcceptanceTests.VerifyCorpusIndexing_ExactCountsAndPerformanceAcceptance`; `SRN.CC.CorpusTests.IdleWorkingSetTests.FullCorpusIndex_ThenThirtySecondsIdle_KeepsPrivateAndWorkingSetBelow750MiB`. Both **skipped** — `SRNCC_CORPUS_ROOT` is unset and no corpus is present. See section 12c |
| **Rendering / context disposal** (230) — semantic geometry/material assertions and adapter-dependent smoke tests, not cross-GPU pixel equality | **PASSED (fake-device tier); real-GPU tier unrun** | `Preview.Render.Gl.ModelRendererTests.DisposeAfterUpload_LeavesLiveResourcesEmpty`; `Preview.Render.Gl.ModelRendererTests.RendererBuiltOnDeviceA_UsedAgainstDeviceB_IssuesZeroCallsOnBAndReportsStale`; `Preview.Render.MdlSceneBuilderTests.BuildAsync_PartitionsWalkmeshAndArtworkNodesIntoSeparateLists`; `Scenarios.ModelPreviewScenarioTests.ThreeConcurrentModelPreviews_UploadRenderDisposeLifecycle_LeavesEveryDeviceLiveResourcesEmpty`. The real-GPU gap is itself asserted as a documented gap by `SRN.CC.CorpusTests.Render.ModelPreviewCorpusTests.RealGpuContext_CannotBeConstructedInThisRepositorysTestInfrastructure_DocumentedGap`, and covered by the manual checklist in section 13 |

---

## 11. Deferred, Not Gate-Blocking

Recorded verbatim from `docs/MILESTONE-7.md:450-465`'s "Explicitly deferred" list, so the two cannot
drift:

> **Explicitly deferred:** the `PLAN.md:143` build-start fingerprint recheck and any emission of
> `DiagnosticCode.SourceDriftDetected` — a pre-existing Milestone 4 gap, deferred here because the
> build is already protected by held `FileShare.Read` handles, the per-entry identity and size recheck
> at `HakAssetSourceReader.cs:254-263`, and the independent verifier pass, and because closing it means
> new production behaviour in `BuildOrchestrator` beyond what any Milestone 7 slice owns. S16's
> `HakDeterminismTests` covers the observable requirement — a source mutated mid-build fails
> verification. S23 must record this as a named plan-versus-code gap rather than letting the mapping
> table imply coverage; the `nwn_erf` oracle downloader and its compatibility tests (ADR 0004);
> recursive unknown-field preservation for `settings.json` (`PLAN.md:94` requires it for projects
> only); a real-GPU automated smoke test — no windowing package is approved in
> `eng/dependency-policy.json` and `Avalonia.Headless` has no GL backend, so the real-GPU path is
> covered by the signed-off manual operator checklist; startup GL probing (deferred by A2 to preserve
> Milestone 6's A4); and everything Milestone 6 deferred at `docs/MILESTONE-6.md:364-370` that is not
> listed above — environment maps as a rendered reflection term, skinmesh, emitters, animation
> playback, danglymesh, `MdlPartComposer`, supermodel inheritance, a standalone BWM reader, per-slot
> PLT dye editing, and MDL light-node lighting.

Carried additionally from wave 3, recorded verbatim from `docs/MILESTONE-7-4.md`:

> **S19 step 1, "the differing pair reported as conflicted."** Not assertable as written. When two
> sources hold the same identity with *different* payloads, `WorkspaceResolver` reports
> `Status = Resolved` with `HasDifferingPayloads = false` and raises only `HasCrossSourceCollision`;
> divergence is computed *within* the winning source, never across sources. The test asserts the
> behaviour that exists. Whether this is the intended semantics of "conflicted" is a question for
> S23, not a test defect.

Milestone 7's answer: this is a `PLAN.md` wording question, not a code defect. Nothing in
`PLAN.md:212-215` depends on the word "conflicted", and cross-source collision **is** reported —
under a different name. It is recorded here and left for a future milestone to settle deliberately
rather than settled implicitly by an evidence document.

### 11a. Plan-versus-code divergences

Recorded so the mapping tables cannot imply coverage that does not exist.

**1. `PLAN.md:143` requires a build-start fingerprint recheck that does not happen.** The line reads:

> Acquire read-sharing source handles that prevent writes/deletes during the build, then recheck
> fingerprints.

`DiagnosticCode.SourceDriftDetected` exists at `DiagnosticCode.cs:10` and is **emitted nowhere and
consumed nowhere** — the declaration is its only code reference. `BuildOrchestrator` performs no
fingerprint comparison at build start.

What protects the build instead: held `FileShare.Read` handles that prevent writes and deletes for
the build's duration; a per-entry recheck at `HakAssetSourceReader.cs:254-263` that re-reads the HAK
header and rejects any entry whose resource type, canonical resref bytes, or size no longer match the
indexed occurrence; and the independent verifier pass. The observable requirement — a source mutated
mid-build fails verification — is covered by
`Build.HakDeterminismTests.Build_SourceMutatedMidBuild_FailsVerification`. Note the per-entry recheck
compares identity and size, **not a content hash**, so it is narrower than a fingerprint recheck.

**2. `PLAN.md:159` implies camelCase manifest fields; the generator emits PascalCase.** The line
names the field `generatedUtc`. `ProvenanceManifestGenerator` sets `WriteIndented = true` but no
`PropertyNamingPolicy`, so `ManifestData` and `ManifestResourceEntry` serialize their record
properties as PascalCase — `GeneratedUtc`, `SchemaVersion`, `Sha256Hex`, and so on.

The tests and `docs/operator/MANIFEST-FORMAT.md` follow the code. **`PLAN.md`, not the code, is the
inaccurate artifact here**, and it is recorded rather than silently reconciled because changing the
serialization would break every manifest already written.

**3. The `nwn_erf.exe` SHA-256 pin at `PLAN.md:173` is 63 hex characters, not 64.** Supporting
detail for the ADR 0004 row above, not its primary basis.

### 11b. Wave-3 findings carried forward

`docs/MILESTONE-7-4.md` recorded six findings in code no slice owned. Finding 1
(`DependencyLocator` ignores pins) was fixed in wave 4 — see section 8. The remaining five are
recorded, non-gate-blocking observations:

- The "Cyclic dependency detected" branch in `DependencyTraversalEngine` is unreachable; cycles
  terminate via the visited set. The diagnostic string can never reach a user.
- `DependencyLocator`'s XML doc promises a catalog fallback that does not exist. That absence is
  precisely what makes "no automatic base-game packaging" true, so the doc invites someone to
  "finish" it and silently break a `PLAN.md` guarantee.
- "Add available dependencies" never completes: `MainWindowViewModel` awaits
  `ConfirmDependenciesDialogViewModel.ShowDialogAsync` but nothing shows the dialog, so the
  `TaskCompletionSource` is never completed.
- `MdlReader` accepts an unterminated ASCII model as a clean parse, so a truncated ASCII MDL renders
  as a normal preview with no parse-failure signal.
- `tools/AuditDependencies.ps1` never opens `NOTICES.md`.
  `Architecture.NoticesCompletenessTests` now covers the file, but the release-gate script itself
  remains blind to it.

One further observation, found in wave 4 while verifying this document rather than inherited from
wave 3:

- **XAML bindings are not compile-checked.** No project sets
  `AvaloniaUseCompiledBindingsByDefault`, so every `x:DataType` in the codebase is declarative only.
  A binding path that does not resolve fails silently at runtime and still compiles cleanly, which
  means a green build — including the 0-warning Release build recorded in section 12b — is not
  evidence that any given binding works. Anything asserted about UI behaviour must render the view.
  This is not gate-blocking and is not a defect, but it is the reason
  `UI.ConfirmDependenciesDialogTests` renders the real dialog instead of testing its view model, and
  a future milestone considering `AvaloniaUseCompiledBindingsByDefault` should know the current
  bindings have never been checked by the compiler.

---

## 12. Verification Pass Results

### 12a. Ad-hoc build and filtered test run (not the master verifier)

```
dotnet build SRN.CC.sln -c Debug
dotnet test SRN.CC.sln
```

- **Build**: 0 Warnings, 0 Errors.
- **`SRN.CC.Tests`**: 1022 passed, 0 failed, 0 skipped.
- **`SRN.CC.CorpusTests`**: 0 passed, 0 failed, 6 skipped — no corpus present, the gate behaving as
  designed.

Every test name cited in sections 9 and 10 was additionally verified by running its filter against
the Release build, in five batches:

- Identity, HAK, resolution, persistence classes: 135 passed, 0 failed.
- UI, preview, dependency, build classes: 152 passed, 0 failed.
- Publication, render, startup classes: 143 passed, 0 failed.
- Schema, release-pipeline, notices, version-stamping, acceptance classes: 45 passed, 0 failed.
- `DependencyClosureTests` and `DependencyTraversalEngineTests`: 55 passed, 0 failed.
- `UI.ConfirmDependenciesDialogTests`: 3 passed, 0 failed.

No row in this document names a test that was not observed to pass.

### 12b. Master verifier (`tools/VerifyBuild.ps1`) — authoritative, CI-gating record

```
powershell -NoProfile -File tools/VerifyBuild.ps1
```

**Run ID `20260805T060309Z-16300` — SUCCESS**, on a clean tree.

```json
{
    "status":  "SUCCESS",
    "timestampUtc":  "2026-08-05T06:04:47.4256638Z",
    "runId":  "20260805T060309Z-16300",
    "sdkVersion":  "10.0.302",
    "runtimeFrameworkVersion":  "10.0.10",
    "configuration":  "Release",
    "targetRID":  "win-x64",
    "version":  "1.0.0",
    "releaseZipSha256":  "db162257a83647aa0a903b1b8a1c326adfebffd5950b7312128514e6579ddb03",
    "gitStatus":  "",
    "error":  ""
}
```

`gitStatus` is empty, so this is a genuine clean-tree run. The last pre-milestone run,
`20260804T212248Z-1592`, was made on a dirty tree and is explicitly **not** the record cited here.

All eleven steps:

1. SDK 10.0.302 confirmed.
2. Locked restore of the `win-x64` graph across all seven projects.
3. **Dependency audit PASSED** — 66 direct/transitive package versions, metadata, hashes,
   signatures, sources, audit results, and project references.
   **Vendored sources audit PASSED** — 47 verbatim blobs, 40 source files, 8 portable test files.
4. **Build Release/win-x64: 0 Warnings, 0 Errors.**
5. **Tests: 1022 passed, 0 failed, 0 skipped** (`SRN.CC.Tests`). `SRN.CC.CorpusTests` matched no test
   under `Category!=Corpus&Category!=Performance`, as expected.
6. Self-contained `win-x64` publish, as a folder.
7. Reviewed licenses and notices added.
8. **Publish audit PASSED** — 252 files explained and hashed into `publish-inventory.json`.
   **Release audit PASSED** — 252 shipped files.
9. **Packed twice, hashes identical**:
   `db162257a83647aa0a903b1b8a1c326adfebffd5950b7312128514e6579ddb03` from both the release directory
   and the independent determinism probe.
10. Extracted outside the repository; all 252 manifest entries matched, and the **release audit
    PASSED again** against the extracted tree.
11. Locked and vendored inputs confirmed unchanged.

**Release archive.** `SRN.CC-1.0.0-win-x64.zip`, 49,299,540 bytes, **252 files**, SHA-256
`db162257a83647aa0a903b1b8a1c326adfebffd5950b7312128514e6579ddb03`. Cross-checked against
`release-manifest.json`, whose `archiveSha256` and `fileCount` match exactly, and which records a
`sha256` per file. Every ZIP entry timestamp is pinned to `1980-01-01T00:00:00Z`, which is what makes
the repack byte-identical.

**Determinism across independent runs.** Three full verifier passes on clean trees —
`20260805T052015Z-28984`, `20260805T053710Z-3956`, and the authoritative
`20260805T060309Z-16300` — all produced the **same archive SHA-256**
`db162257a83647aa0a903b1b8a1c326adfebffd5950b7312128514e6579ddb03` from separate builds, publishes,
and packs. That is a stronger result than step 9's within-run probe: the archive is reproducible
across independent compilations, not merely across two packs of one publish tree.

The third run also confirms a property worth stating explicitly: it carried three more tests than the
first (1022 against 1019) and produced a byte-identical archive, because test assemblies are not
shipped. Test changes cannot move the release artifact.

**Clean-machine smoke** (`tools/SmokeCleanMachine.ps1`), run against the extracted archive:
**PASSED**. The smoke was run against the archive produced by run `20260805T052015Z-28984`, which is
byte-identical to the authoritative run's archive — same SHA-256, as recorded above — so the result
transfers exactly rather than approximately. The release-tree audit passed over 252 files; `--srncc-preflight-only` reported all four
checks — `cache`, `settings`, `publication-journal`, `tool-capability` — as `Ok`; the application
launched and survived its ten-second window; and the log and cache were present with the first log
line parsing as JSON.

> **Caveat, recorded rather than glossed.** This run used `-AllowExistingAppData`, because
> `%LOCALAPPDATA%\SRN.CC` already exists on this development machine. That downgrades the
> clean-machine precondition to a warning and changes the log and cache assertions from "were
> created" to "were written during this run". The genuinely-clean first-launch path is covered by the
> `clean-machine-smoke` CI job on a fresh `windows-2025` runner, which never passes the switch, and
> by checklist item 1 in section 13.

### 12c. Corpus baseline — recorded as unmeasured

`docs/evidence/corpus-index-baseline.json` has `"recorded": false`. `SRNCC_CORPUS_ROOT` is unset and
no corpus is present on this machine, so every measured field is null by design.

Structural constants (from the baseline file, not measured here):

| Quantity | Value |
|---|---|
| HAK archives | 117 |
| Occurrences | 187,943 |
| Type-2078 entries | 212 |
| Duplicate archives | 4 |
| Cold-index threshold | 10.0 s, median of three runs |
| Idle working-set threshold | 786,432,000 bytes (750 MiB) after 30 s idle |
| Configuration | Release |

**Not measured on this machine:** the cold-index median against the ten-second threshold, and the
private and total working set after 30 seconds idle against the 750 MiB threshold. No reference
machine environment is recorded because no measurement was taken.

`PLAN.md:229`'s clauses are therefore **unverified**, not passed. The baseline file carries a
`populate` block with the exact commands to fill it on a machine that has the corpus; running those
and pasting the three numbers is all that is required to close this.

---

## 13. Manual Real-GPU Operator Checklist

`PLAN.md:230` requires adapter-dependent smoke tests rather than cross-GPU pixel equality precisely
because this path is environment-bound. No windowing package is approved in
`eng/dependency-policy.json` and `Avalonia.Headless` has no GL backend, so the real-GPU path is
covered here rather than by an automated test.

### Status: **NOT RUN**

**Reason:** this milestone was completed on a development machine with no NWN:EE corpus
(`SRNCC_CORPUS_ROOT` unset), no NWN:EE toolset installation to load a built HAK into, and no
verified discrete GPU adapter available to the session. Items 1 and 3 are partially evidenced by the
automated clean-machine smoke run in section 12b; every other item is unexercised.

**Sign-off line — to be completed by the operator who runs it:**

```
Run by: ____________________   Date: ____________   Machine/GPU: ____________________
Result: [ ] all items passed   [ ] items failed (list): ______________________________
```

**1. First launch on a clean machine.** With no `%LOCALAPPDATA%\SRN.CC`, launch the application.
Confirm the preflight report reaches the operation log, that all four checks report, and that
`Logs\srncc.log` is created and every line parses as JSON.
*Partial automated evidence:* section 12b confirmed the four checks, the log, and JSON validity —
but with pre-existing app data, so the "created on first launch" half is unproven here.

**2. The full curation round trip.** Open a real HAK. Resolve a conflict. Pin a winner. Save as
`.srnccproj`. Rescan. Build. Confirm the resulting HAK loads in the NWN:EE toolset.
*Unrun: no corpus and no toolset.*

**3. Cache corruption and recovery.** Corrupt `cache-v1.sqlite` and relaunch. Confirm the file is
quarantined, the cache is rebuilt, and the application reaches a usable window.
*Unrun as a manual check. Automated analogue:
`Cache.SqliteCacheQuarantineTests.Initialize_GarbageDatabaseFile_QuarantinesRebuildsAndEmitsDiagnostic`.*

**4. Journal rollback after a kill.** Kill the process mid-publish and relaunch. Confirm the journal
is rolled back and the previous valid destination pair is intact.
*Unrun as a manual check. Automated analogue:
`Build.ArtifactPublisherJournalStateTests.Recovery_FromEveryState_LeavesDestinationPairByteForByte`.*

**5. The 3D path on a real adapter.** Select an MDL. Orbit, pan, and dolly the camera. Toggle the
walkmesh. Fill three concurrent viewports. Confirm a model with a missing texture degrades rather
than failing the preview. Force a shader-unsupported path and confirm the text fallback appears.
*Unrun: no verified GPU adapter. This is the item with the least automated substitute — the
fake-device tier in section 10 proves the lifecycle and disposal contracts, not that a real driver
accepts the shaders.*

---

## 14. What Would Close the Open Items

Neither is a code change.

1. **Corpus and performance** (`PLAN.md:229`): set `SRNCC_CORPUS_ROOT`, run the commands in
   `docs/evidence/corpus-index-baseline.json`'s `populate` block, paste the three measured numbers
   and the reference-machine environment into that file, and flip `recorded` to `true`.
2. **The manual checklist** (section 13): run it on a machine with the corpus, the toolset, and a
   real GPU, and complete the sign-off line.

Both are environment-bound, which is why `PLAN.md:230` specifies adapter-dependent smoke tests rather
than something a hosted runner could assert.
