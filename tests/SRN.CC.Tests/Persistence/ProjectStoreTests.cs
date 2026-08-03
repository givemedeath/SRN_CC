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

namespace SRN.CC.Tests.Persistence;

[TestFixture]
public class ProjectStoreTests
{
    private sealed class MockIndexService : IAssetIndexService
    {
        public Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            byte[] fpBytes = new byte[32];
            SourceFingerprint fp = new SourceFingerprint(source.Kind, 1, fpBytes);
            SourceScanStatistics stats = new SourceScanStatistics(0, 0, TimeSpan.Zero);
            SourceIndexSnapshot emptySnap = new SourceIndexSnapshot(source, fp, Array.Empty<IndexedAssetRecord>(), Array.Empty<AssetDiagnosticRecord>(), isCacheHit: false, scanStatistics: stats);
            return Task.FromResult(emptySnap);
        }
    }

    private sealed class DummyHashService : IStreamingHashService
    {
        public Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(occurrence.Locator.ToString())));
        }
    }

    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_ProjectStoreTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task Schema1_RoundTrip_ShouldPreserveSourcesSelectionsPinsPreferences()
    {
        string hakPath = Path.Combine(_tempDir, "test.hak");
        await File.WriteAllTextAsync(hakPath, "dummy content");

        Guid sourceId = Guid.NewGuid();
        AssetSource source = AssetSource.CreateHak(hakPath, priorityOrdinal: 0, id: sourceId);

        AssetIdentity identity = new AssetIdentity("testres", 2000);
        byte[] pinHash = new byte[32]; pinHash[0] = 0xAB;
        WinnerPin pin = new WinnerPin(identity, sourceId, new HakEntryLocator(3), pinHash);

        SelectionState selection = SelectionState.IncludeAll().SetOverride(identity, false);

        JsonObject customFilter = new JsonObject { ["customFlag"] = true };
        ProjectPreferences preferences = new ProjectPreferences(filters: customFilter);

        WorkspaceState state = new WorkspaceState(
            sources: new[] { source },
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: Array.Empty<CuratedAsset>(),
            selectionState: selection,
            pins: new[] { pin },
            preferences: preferences);

        MockIndexService indexService = new();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), new AssetHashCache());
        ProjectStore store = new ProjectStore(indexService, resolver);

        string projectPath = Path.Combine(_tempDir, "myproject.srnccproj");
        await store.SaveAsync(state, projectPath);

        File.Exists(projectPath).Should().BeTrue();

        WorkspaceState loaded = await store.LoadAsync(projectPath);

        loaded.Sources.Should().HaveCount(1);
        loaded.Sources[0].Id.Should().Be(sourceId);
        loaded.SelectionState.DefaultSelected.Should().BeTrue();
        loaded.SelectionState.Overrides.Should().HaveCount(1);
        loaded.SelectionState.IsSelected(identity).Should().BeFalse();

        loaded.Pins.Should().HaveCount(1);
        loaded.Pins[0].Identity.Should().Be(identity);
        loaded.Pins[0].PinHash.Should().Equal(pinHash);

        loaded.Preferences.Filters.Should().NotBeNull();
        loaded.Preferences.Filters!["customFlag"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public async Task NewerSchemaVersion_OpensReadOnly()
    {
        string projectPath = Path.Combine(_tempDir, "future.srnccproj");
        string json = """
        {
          "schemaVersion": 99,
          "sources": [],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": []
        }
        """;
        await File.WriteAllTextAsync(projectPath, json);

        MockIndexService indexService = new();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), new AssetHashCache());
        ProjectStore store = new ProjectStore(indexService, resolver);

        WorkspaceState loaded = await store.LoadAsync(projectPath);

        loaded.IsReadOnly.Should().BeTrue("Schema version 99 > 1 must open read-only");
    }

    [Test]
    public async Task Save_ReadOnlyWorkspace_ShouldThrowInvalidOperationException()
    {
        WorkspaceState readOnlyState = new WorkspaceState(
            sources: Array.Empty<AssetSource>(),
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: Array.Empty<CuratedAsset>(),
            selectionState: SelectionState.IncludeAll(),
            pins: Array.Empty<WinnerPin>(),
            isReadOnly: true);

        MockIndexService indexService = new();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), new AssetHashCache());
        ProjectStore store = new ProjectStore(indexService, resolver);

        string projectPath = Path.Combine(_tempDir, "readonly.srnccproj");
        Func<Task> act = async () => await store.SaveAsync(readOnlyState, projectPath);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task PreserveUnknownProperties_OnSave_ShouldRetainUnknownNodes()
    {
        string projectPath = Path.Combine(_tempDir, "unknown_nodes.srnccproj");
        string jsonWithUnknowns = """
        {
          "schemaVersion": 1,
          "sources": [],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": [],
          "unknownRootProp": "preservedValue",
          "futureFeatureObject": { "nested": 42 }
        }
        """;
        await File.WriteAllTextAsync(projectPath, jsonWithUnknowns);

        MockIndexService indexService = new();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), new AssetHashCache());
        ProjectStore store = new ProjectStore(indexService, resolver);

        WorkspaceState state = await store.LoadAsync(projectPath);

        string savePath = Path.Combine(_tempDir, "saved_unknown_nodes.srnccproj");
        await store.SaveAsync(state, savePath);

        string savedJson = await File.ReadAllTextAsync(savePath);
        JsonNode savedNode = JsonNode.Parse(savedJson)!;

        savedNode["unknownRootProp"]?.GetValue<string>().Should().Be("preservedValue");
        savedNode["futureFeatureObject"]?["nested"]?.GetValue<int>().Should().Be(42);
    }

    [Test]
    public async Task PreserveArrayItemUnknownProperties_OnSave_ShouldRetainNestedUnknowns()
    {
        Guid sourceId = Guid.NewGuid();
        string projectPath = Path.Combine(_tempDir, "unknown_array_items.srnccproj");
        string json = $$"""
        {
          "schemaVersion": 1,
          "sources": [
            {
              "id": "{{sourceId}}",
              "kind": "hak",
              "path": { "kind": "absolute", "value": "c:/test.hak" },
              "unknownItemProp": "preservedInSourceItem"
            }
          ],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": []
        }
        """;
        await File.WriteAllTextAsync(projectPath, json);

        MockIndexService indexService = new();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), new AssetHashCache());
        ProjectStore store = new ProjectStore(indexService, resolver);

        WorkspaceState state = await store.LoadAsync(projectPath);

        string savePath = Path.Combine(_tempDir, "saved_array_unknowns.srnccproj");
        await store.SaveAsync(state, savePath);

        string savedJson = await File.ReadAllTextAsync(savePath);
        JsonNode savedNode = JsonNode.Parse(savedJson)!;

        savedNode["sources"]?[0]?["unknownItemProp"]?.GetValue<string>().Should().Be("preservedInSourceItem");
    }

    [Test]
    public async Task SaveAs_NewerSchema_ShouldPreserveOriginalDocumentByteForByte()
    {
        string projectPath = Path.Combine(_tempDir, "future_schema.srnccproj");
        string originalJson = """
        {
          "schemaVersion": 99,
          "sources": [],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": [],
          "futureSchemaProperty": "importantData"
        }
        """;
        await File.WriteAllTextAsync(projectPath, originalJson);

        MockIndexService indexService = new();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), new AssetHashCache());
        ProjectStore store = new ProjectStore(indexService, resolver);

        WorkspaceState state = await store.LoadAsync(projectPath);

        string saveAsPath = Path.Combine(_tempDir, "copy_future_schema.srnccproj");
        await store.SaveAsAsync(state, saveAsPath);

        string savedJson = await File.ReadAllTextAsync(saveAsPath);
        savedJson.Should().Be(originalJson, "SaveAs on read-only newer schema must preserve original document byte-for-byte");
    }

    [Test]
    public async Task NewerSchema_WithUnknownEnumKind_ShouldLoadAsReadOnlyWorkspace()
    {
        string projectPath = Path.Combine(_tempDir, "unknown_kind.srnccproj");
        string json = """
        {
          "schemaVersion": 2,
          "sources": [
            {
              "id": "11111111-2222-3333-4444-555555555555",
              "kind": "futureCloudKind",
              "path": { "kind": "absolute", "value": "c:/test.hak" }
            }
          ],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": []
        }
        """;
        await File.WriteAllTextAsync(projectPath, json);

        MockIndexService indexService = new();
        WorkspaceResolver resolver = new WorkspaceResolver(new DummyHashService(), new AssetHashCache());
        ProjectStore store = new ProjectStore(indexService, resolver);

        WorkspaceState state = await store.LoadAsync(projectPath);

        state.IsReadOnly.Should().BeTrue();
        state.Sources.Should().HaveCount(1);
    }
}
