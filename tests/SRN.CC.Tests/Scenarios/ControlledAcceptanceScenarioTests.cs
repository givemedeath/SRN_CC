using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Records;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Tests.Scenarios;

[TestFixture]
public class ControlledAcceptanceScenarioTests
{
    private sealed class DynamicIndexService : IAssetIndexService
    {
        public Dictionary<Guid, List<AssetOccurrence>> DynamicOccurrences { get; } = new();

        public Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            List<AssetOccurrence> list = DynamicOccurrences.TryGetValue(source.Id, out var occs) ? occs : new List<AssetOccurrence>();
            List<IndexedAssetRecord> records = list.Select(o => new IndexedAssetRecord(o)).ToList();

            byte[] fpBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.FullPath + ":" + list.Count));
            SourceFingerprint fp = new SourceFingerprint(source.Kind, 1, fpBytes);
            SourceScanStatistics stats = new SourceScanStatistics(list.Count, 100, TimeSpan.FromMilliseconds(5));

            return Task.FromResult(new SourceIndexSnapshot(source, fp, records, Array.Empty<AssetDiagnosticRecord>(), isCacheHit: false, scanStatistics: stats));
        }
    }

    private sealed class DynamicHashService : IStreamingHashService
    {
        public Dictionary<(Guid SourceId, OccurrenceLocator Locator), byte[]> PayloadMap { get; } = new();

        public Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            if (PayloadMap.TryGetValue((source.Id, occurrence.Locator), out byte[]? payload))
            {
                return Task.FromResult(payload);
            }
            byte[] defaultHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{source.Id}:{occurrence.Locator}"));
            return Task.FromResult(defaultHash);
        }
    }

    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_AcceptanceScenario_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Test]
    public async Task CompleteControlledAcceptanceScenario_Steps1Through6()
    {
        // ----------------------------------------------------
        // Step 1: Index at least two fixtures with priority and duplicate conflicts.
        // ----------------------------------------------------
        string source1Path = Path.Combine(_tempDir, "source1.hak");
        string source2Path = Path.Combine(_tempDir, "source2.hak");
        await File.WriteAllTextAsync(source1Path, "hak1 dummy");
        await File.WriteAllTextAsync(source2Path, "hak2 dummy");

        Guid sourceId1 = Guid.NewGuid();
        Guid sourceId2 = Guid.NewGuid();

        AssetSource source1 = AssetSource.CreateHak(source1Path, priorityOrdinal: 0, id: sourceId1);
        AssetSource source2 = AssetSource.CreateHak(source2Path, priorityOrdinal: 1, id: sourceId2);

        AssetIdentity identity1 = new AssetIdentity("shared_asset", 2000);
        AssetIdentity identity2 = new AssetIdentity("unique_asset", 2000);

        AssetOccurrence s1Occ = new AssetOccurrence(identity1, sourceId1, new HakEntryLocator(0), "shared_asset.tga", 100);
        AssetOccurrence s2Occ = new AssetOccurrence(identity1, sourceId2, new HakEntryLocator(5), "shared_asset.tga", 100);
        AssetOccurrence s2UniqueOcc = new AssetOccurrence(identity2, sourceId2, new HakEntryLocator(6), "unique_asset.tga", 100);

        DynamicIndexService indexService = new DynamicIndexService();
        indexService.DynamicOccurrences[sourceId1] = new List<AssetOccurrence> { s1Occ };
        indexService.DynamicOccurrences[sourceId2] = new List<AssetOccurrence> { s2Occ, s2UniqueOcc };

        DynamicHashService hashService = new DynamicHashService();
        byte[] s1Payload = new byte[32]; s1Payload[0] = 0x11;
        byte[] s2Payload = new byte[32]; s2Payload[0] = 0x22;
        hashService.PayloadMap[(sourceId1, s1Occ.Locator)] = s1Payload;
        hashService.PayloadMap[(sourceId2, s2Occ.Locator)] = s2Payload;

        AssetHashCache hashCache = new AssetHashCache();
        WorkspaceResolver resolver = new WorkspaceResolver(hashService, hashCache);
        WorkspaceService workspaceService = new WorkspaceService(indexService, resolver, hashCache);

        var (stateStep1, _) = await workspaceService.InitializeAsync(new[] { source1, source2 });

        stateStep1.CuratedAssets.Should().HaveCount(2);
        CuratedAsset sharedAssetStep1 = stateStep1.CuratedAssets.First(a => a.Identity.Equals(identity1));
        sharedAssetStep1.ResolvedOccurrence!.SourceId.Should().Be(sourceId1, "Priority 0 source should win by default");

        // ----------------------------------------------------
        // Step 2: Resolve, pin a non-default occurrence, change selections, and save.
        // ----------------------------------------------------
        WinnerPin pinS2 = new WinnerPin(identity1, sourceId2, s2Occ.Locator, s2Payload);
        WorkspaceState statePinned = await workspaceService.PinAsync(pinS2);

        SelectionState customSelection = statePinned.SelectionState.SetOverride(identity2, false);
        WorkspaceState stateSelected = await workspaceService.UpdateSelectionAsync(customSelection);

        CuratedAsset sharedAssetStep2 = stateSelected.CuratedAssets.First(a => a.Identity.Equals(identity1));
        sharedAssetStep2.ResolvedOccurrence!.SourceId.Should().Be(sourceId2, "Pin should override source priority");

        ProjectStore projectStore = new ProjectStore(indexService, resolver);
        string projectPath = Path.Combine(_tempDir, "acceptance_project.srnccproj");
        await projectStore.SaveAsync(stateSelected, projectPath);

        // ----------------------------------------------------
        // Step 3: Restart from persisted files and reproduce source order, filters, selections, winners, and pin state.
        // ----------------------------------------------------
        WorkspaceState reloadedState = await projectStore.LoadAsync(projectPath);
        WorkspaceState workspaceStep3 = await workspaceService.LoadProjectStateAsync(reloadedState);

        workspaceStep3.Sources.Should().HaveCount(2);
        workspaceStep3.Sources[0].Id.Should().Be(sourceId1);
        workspaceStep3.Sources[1].Id.Should().Be(sourceId2);

        CuratedAsset reloadedShared = workspaceStep3.CuratedAssets.First(a => a.Identity.Equals(identity1));
        reloadedShared.ResolvedOccurrence!.SourceId.Should().Be(sourceId2, "Reloaded state must preserve pinned winner");
        reloadedShared.Pin.Should().NotBeNull();
        reloadedShared.Pin!.PinHash.Should().Equal(s2Payload);

        CuratedAsset reloadedUnique = workspaceStep3.CuratedAssets.First(a => a.Identity.Equals(identity2));
        reloadedUnique.IsSelected.Should().BeFalse("Reloaded state must preserve custom selection override");

        // ----------------------------------------------------
        // Step 4: Move one source and relocate it while preserving its source ID and valid pin.
        // ----------------------------------------------------
        string relocatedSource2Path = Path.Combine(_tempDir, "relocated_source2.hak");
        File.Move(source2Path, relocatedSource2Path);

        var (stateStep4, reportStep4) = await workspaceService.RelocateSourceAsync(sourceId2, relocatedSource2Path);

        stateStep4.Sources.First(s => s.Id == sourceId2).FullPath.Should().Be(relocatedSource2Path);
        CuratedAsset relocatedShared = stateStep4.CuratedAssets.First(a => a.Identity.Equals(identity1));
        relocatedShared.ResolvedOccurrence!.SourceId.Should().Be(sourceId2, "Pin remains valid after relocation");
        relocatedShared.Status.Should().Be(ResolutionStatus.Resolved);

        // ----------------------------------------------------
        // Step 5: Change a pinned payload and rescan; confirm changed-input report and invalid blocking pin.
        // ----------------------------------------------------
        byte[] alteredS2Payload = new byte[32]; alteredS2Payload[0] = 0x99; // Payload changed!
        hashService.PayloadMap[(sourceId2, s2Occ.Locator)] = alteredS2Payload;

        var (stateStep5, reportStep5) = await workspaceService.RescanAsync(new[] { sourceId2 });

        CuratedAsset alteredShared = stateStep5.CuratedAssets.First(a => a.Identity.Equals(identity1));
        alteredShared.Status.Should().Be(ResolutionStatus.InvalidPin);
        alteredShared.HasInvalidPin.Should().BeTrue();
        alteredShared.ResolvedOccurrence.Should().BeNull("Invalid pin must block auto-resolution");
        reportStep5.PinInvalidations.Should().Contain(identity1);

        // ----------------------------------------------------
        // Step 6: Restore matching payload under a unique new locator; confirm deterministic pin reattachment.
        // ----------------------------------------------------
        // Move occurrence to a new locator (index 99) with original s2Payload
        AssetOccurrence s2NewLocatorOcc = new AssetOccurrence(identity1, sourceId2, new HakEntryLocator(99), "shared_asset.tga", 100);
        indexService.DynamicOccurrences[sourceId2] = new List<AssetOccurrence> { s2NewLocatorOcc, s2UniqueOcc };
        hashService.PayloadMap[(sourceId2, s2NewLocatorOcc.Locator)] = s2Payload;

        var (stateStep6, reportStep6) = await workspaceService.RescanAsync(new[] { sourceId2 });

        CuratedAsset reattachedShared = stateStep6.CuratedAssets.First(a => a.Identity.Equals(identity1));
        reattachedShared.Status.Should().Be(ResolutionStatus.Resolved);
        reattachedShared.ResolvedOccurrence!.Locator.Should().Be(new HakEntryLocator(99));
        reattachedShared.Pin!.Locator.Should().Be(new HakEntryLocator(99), "Pin should deterministically reattach to unique matching payload locator");
        reportStep6.PinReattachments.Should().Contain(identity1);
    }
}
