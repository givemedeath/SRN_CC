using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Records;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Schema;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Logging;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Tests.Persistence;

/// <summary>
/// Covers the schema seam <c>ProjectStore</c> gained in Milestone 7: the centralized
/// <see cref="SchemaVersions"/> registry, the <see cref="SchemaMigrationPipeline"/> pass that runs
/// before validation, and the logger on the paths that used to fail silently.
/// </summary>
/// <remarks>
/// The pre-existing <c>ProjectStoreTests</c> file is deliberately untouched: every behaviour it
/// asserts must survive this change unmodified, so it is the regression proof and this file is the
/// new-behaviour proof.
/// </remarks>
[TestFixture]
public class ProjectStoreSchemaTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_ProjectStoreSchemaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- registry

    [Test]
    public void SchemaVersions_IsTheOnlyProjectVersionSource_AndProjectStoreHasNoPrivateConstant()
    {
        SchemaVersions.Current(SchemaKind.Project).Should().Be(SchemaVersions.Project);

        FieldInfo[] privateConstants = typeof(ProjectStore)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToArray();

        privateConstants.Should().BeEmpty(
            "the private CurrentSchemaVersion constant must be replaced by SchemaVersions.Project");
    }

    [Test]
    public void SchemaVersions_Classify_DrivesTheThreeProjectOpenModes()
    {
        SchemaVersions.Classify(SchemaKind.Project, 0).Should().Be(SchemaOpenMode.Unsupported);
        SchemaVersions.Classify(SchemaKind.Project, SchemaVersions.Project).Should().Be(SchemaOpenMode.Current);
        SchemaVersions.Classify(SchemaKind.Project, SchemaVersions.Project + 1).Should().Be(SchemaOpenMode.ReadOnlyNewer);
    }

    // ---------------------------------------------------------------- the seam

    [Test]
    public void MigrationPipeline_IsWiredForProject_AndTargetsTheCurrentVersion()
    {
        FieldInfo? field = typeof(ProjectStore).GetField("Migrations", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("ProjectStore must hold a SchemaMigrationPipeline, not just call Classify");

        SchemaMigrationPipeline pipeline = (SchemaMigrationPipeline)field!.GetValue(null)!;
        pipeline.Kind.Should().Be(SchemaKind.Project);
        pipeline.TargetVersion.Should().Be(SchemaVersions.Project);
        pipeline.MigrationCount.Should().Be(0, "v1 is an identity pass; the deliverable is the seam");
    }

    [Test]
    public async Task Load_ProjectsEveryPreferenceFieldFromTheSameDocumentAsRawRootNode()
    {
        // The seam is only load-bearing if the field projection reads the pipeline's OUTPUT rather
        // than the parsed input. ProjectPreferences deep-clones everything it is handed, so the
        // observable form of that is content agreement: every projected preference must match the
        // corresponding node under RawRootNode. If a future change left projection reading the
        // pre-migration parse while RawRootNode came from the pipeline, a registered migration would
        // show up on one side only, and this is what catches it.
        string projectPath = Path.Combine(_tempDir, "projection.srnccproj");
        await File.WriteAllTextAsync(projectPath, """
        {
          "schemaVersion": 1,
          "sources": [],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": [],
          "outputSettings": { "destination": "out" },
          "filters": { "customFlag": true },
          "comparisonPreferences": { "mode": "side-by-side" }
        }
        """);

        WorkspaceState loaded = await CreateStore().LoadAsync(projectPath);
        ProjectPreferences preferences = loaded.Preferences;

        JsonObject raw = preferences.RawRootNode.Should().BeOfType<JsonObject>().Subject;

        JsonNode.DeepEquals(preferences.OutputSettings, raw["outputSettings"]).Should().BeTrue();
        JsonNode.DeepEquals(preferences.Filters, raw["filters"]).Should().BeTrue();
        JsonNode.DeepEquals(preferences.ComparisonPreferences, raw["comparisonPreferences"]).Should().BeTrue();

        // ...and the document that survived the pass is still at the current version.
        raw["schemaVersion"]!.GetValue<int>().Should().Be(SchemaVersions.Project);
    }

    [Test]
    public void MigrationPipeline_WhenAMigrationIsRegistered_TransformsTheDocumentWithoutMutatingTheInput()
    {
        // Documents what the seam buys: because ProjectStore projects from TryUpgrade's output, a
        // registered step reaches the projected fields. The three-argument overload is used because
        // every shipped SchemaVersions constant is still 1, so no chain can run against the default
        // target yet.
        JsonObject input = new()
        {
            ["schemaVersion"] = 1,
            ["legacyName"] = "before",
            ["unknownRootProp"] = "preservedValue"
        };
        string inputBefore = input.ToJsonString();

        SchemaMigrationPipeline pipeline = new(SchemaKind.Project, new IJsonSchemaMigration[] { new RenameLegacyName() });

        pipeline.TryUpgrade(input, 1, 2, out JsonObject upgraded, out IReadOnlyList<string> applied, out string? error)
            .Should().BeTrue();

        error.Should().BeNull();
        applied.Should().ContainSingle().Which.Should().Be("Project 1->2");
        upgraded["modernName"]!.GetValue<string>().Should().Be("before");
        upgraded["unknownRootProp"]!.GetValue<string>().Should().Be("preservedValue");

        input.ToJsonString().Should().Be(inputBefore, "the caller's parsed document must never be mutated");
    }

    [Test]
    public async Task Load_UnreachableTargetVersion_SurfacesAsAnError_NotASilentSkip()
    {
        // A gap in the chain must fail loudly. There is no such gap at v1, so this asserts the
        // pipeline contract ProjectStore relies on: TryUpgrade returns false with an error rather
        // than handing back a half-migrated or unmigrated document.
        JsonObject document = new() { ["schemaVersion"] = 1 };
        SchemaMigrationPipeline pipeline = new(SchemaKind.Project, Array.Empty<IJsonSchemaMigration>());

        pipeline.TryUpgrade(document, 1, 3, out JsonObject upgraded, out IReadOnlyList<string> applied, out string? error)
            .Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
        applied.Should().BeEmpty();
        ReferenceEquals(upgraded, document).Should().BeTrue();

        // And the store still loads a normal v1 document, so the failure path above is not the
        // everyday path.
        string projectPath = Path.Combine(_tempDir, "normal.srnccproj");
        await File.WriteAllTextAsync(projectPath, MinimalProjectJson);
        WorkspaceState loaded = await CreateStore().LoadAsync(projectPath);
        loaded.IsReadOnly.Should().BeFalse();
    }

    // ------------------------------------------------ unknown-field preservation

    [Test]
    public async Task RoundTrip_PreservesUnknownFieldsByteForByte()
    {
        string projectPath = Path.Combine(_tempDir, "unknowns.srnccproj");
        await File.WriteAllTextAsync(projectPath, """
        {
          "schemaVersion": 1,
          "sources": [],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": [],
          "unknownScalar": "preservedValue",
          "unknownNumber": 42,
          "unknownNull": null,
          "unknownArray": [1, "two", { "three": 3 }, [4, 5]],
          "futureFeatureObject": {
            "nested": { "deep": { "deeper": [true, false, null] } },
            "unicode": "café — éèê",
            "bigNumber": 12345678901234,
            "precise": 0.1234567890123
          }
        }
        """);

        JsonObject original = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!;

        ProjectStore store = CreateStore();
        WorkspaceState loaded = await store.LoadAsync(projectPath);

        string savePath = Path.Combine(_tempDir, "unknowns_saved.srnccproj");
        await store.SaveAsync(loaded, savePath);

        JsonObject saved = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(savePath))!;

        foreach (string key in new[]
        {
            "unknownScalar", "unknownNumber", "unknownNull", "unknownArray", "futureFeatureObject"
        })
        {
            string before = original[key]?.ToJsonString() ?? "null";
            string after = saved[key]?.ToJsonString() ?? "null";
            after.Should().Be(before, $"unknown field '{key}' must survive load-save byte-for-byte");
        }

        // Nothing the document carried was dropped on the way through. (The store additionally
        // materializes the three known preference keys, which is pre-existing behaviour and not
        // what this test is about.)
        saved.Select(kvp => kvp.Key).Should().Contain(original.Select(kvp => kvp.Key));
    }

    [Test]
    public async Task RoundTrip_PreservesUnknownFieldsNestedInsideKnownArrayItems()
    {
        Guid sourceId = Guid.NewGuid();
        string hakPath = Path.Combine(_tempDir, "nested.hak");
        await File.WriteAllTextAsync(hakPath, "dummy");

        string projectPath = Path.Combine(_tempDir, "nested_unknowns.srnccproj");
        await File.WriteAllTextAsync(projectPath, $$"""
        {
          "schemaVersion": 1,
          "sources": [
            {
              "id": "{{sourceId}}",
              "kind": "Hak",
              "path": { "kind": "absolute", "value": {{System.Text.Json.JsonSerializer.Serialize(hakPath)}} },
              "sourceUnknown": { "keep": ["me", 1, null] }
            }
          ],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": []
        }
        """);

        JsonObject original = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!;
        string beforeJson = original["sources"]![0]!["sourceUnknown"]!.ToJsonString();

        ProjectStore store = CreateStore();
        WorkspaceState loaded = await store.LoadAsync(projectPath);

        string savePath = Path.Combine(_tempDir, "nested_unknowns_saved.srnccproj");
        await store.SaveAsync(loaded, savePath);

        JsonObject saved = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(savePath))!;
        saved["sources"]![0]!["sourceUnknown"]!.ToJsonString().Should().Be(beforeJson);
    }

    // ------------------------------------------------------------ open modes

    [Test]
    public async Task SchemaVersion2_OpensReadOnly_AndLeavesTheFileUntouched()
    {
        string projectPath = Path.Combine(_tempDir, "v2.srnccproj");
        string json = """
        {
          "schemaVersion": 2,
          "sources": [],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": [],
          "futureOnlyField": "from a newer build"
        }
        """;
        await File.WriteAllTextAsync(projectPath, json);
        byte[] before = await File.ReadAllBytesAsync(projectPath);

        WorkspaceState loaded = await CreateStore().LoadAsync(projectPath);

        loaded.IsReadOnly.Should().BeTrue("schemaVersion 2 is newer than SchemaVersions.Project");
        (await File.ReadAllBytesAsync(projectPath)).Should().Equal(before);

        // The newer document is read from exactly what was on disk; the pipeline cannot and must not
        // have run backwards over it.
        loaded.Preferences.RawRootNode!["futureOnlyField"]!.GetValue<string>().Should().Be("from a newer build");
        loaded.Preferences.RawRootNode!["schemaVersion"]!.GetValue<int>().Should().Be(2);
    }

    [Test]
    public async Task SchemaVersion0_Throws_LeavesTheFileUntouched_AndIsNeverQuarantined()
    {
        string projectPath = Path.Combine(_tempDir, "v0.srnccproj");
        string json = """
        {
          "schemaVersion": 0,
          "sources": [],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": []
        }
        """;
        await File.WriteAllTextAsync(projectPath, json);
        byte[] before = await File.ReadAllBytesAsync(projectPath);

        RecordingLogger logger = new();
        ProjectStore store = CreateStore(logger);

        Func<Task> act = async () => await store.LoadAsync(projectPath);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(projectPath);

        File.Exists(projectPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(projectPath)).Should().Equal(before);

        // PLAN.md:96 scopes quarantine to settings and cache — no sibling file may appear.
        Directory.GetFiles(_tempDir).Should().ContainSingle().Which.Should().Be(projectPath);

        logger.Records.Should().ContainSingle(r => r.EventCode == "project.schema.unsupported");
    }

    [Test]
    public async Task MissingSchemaVersion_IsStillUnsupported_AndThrowsNamingThePath()
    {
        string projectPath = Path.Combine(_tempDir, "noversion.srnccproj");
        await File.WriteAllTextAsync(projectPath, """{ "sources": [], "pins": [] }""");

        Func<Task> act = async () => await CreateStore().LoadAsync(projectPath);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(projectPath);

        File.Exists(projectPath).Should().BeTrue();
    }

    // --------------------------------------------------------------- logging

    [Test]
    public void Constructor_WithoutLogger_UsesTheNoOpLogger()
    {
        // The logger parameter is trailing and optional, so no existing call site changed.
        ProjectStore store = new(new StubIndexService(), new WorkspaceResolver(new StubHashService(), new AssetHashCache()));
        store.Should().NotBeNull();

        ConstructorInfo ctor = typeof(ProjectStore).GetConstructors().Single();
        ParameterInfo[] parameters = ctor.GetParameters();
        parameters.Should().HaveCount(3);
        parameters[0].ParameterType.Should().Be<IAssetIndexService>();
        parameters[1].ParameterType.Should().Be<IWorkspaceResolver>();
        parameters[2].ParameterType.Should().Be<IAppLogger>();
        parameters[2].IsOptional.Should().BeTrue();
        parameters[2].DefaultValue.Should().BeNull();
    }

    [Test]
    public async Task FailedAtomicWrite_IsLogged_AndLeavesNoTempFileBehind()
    {
        // A directory standing where the project file should go makes File.Move fail after the temp
        // file has been written, which is exactly the path whose catch blocks used to be silent.
        string blockedPath = Path.Combine(_tempDir, "blocked.srnccproj");
        Directory.CreateDirectory(blockedPath);

        RecordingLogger logger = new();
        ProjectStore store = CreateStore(logger);

        WorkspaceState state = new(
            sources: Array.Empty<AssetSource>(),
            snapshots: new Dictionary<Guid, SourceIndexSnapshot>(),
            curatedAssets: Array.Empty<CuratedAsset>(),
            selectionState: SelectionState.IncludeAll(),
            pins: Array.Empty<WinnerPin>());

        Func<Task> act = async () => await store.SaveAsync(state, blockedPath);
        await act.Should().ThrowAsync<Exception>();

        LoggedRecord failure = logger.Records.Should()
            .ContainSingle(r => r.EventCode == "project.write.failed").Subject;
        failure.Level.Should().Be(LogLevel.Error);
        failure.Category.Should().Be(nameof(ProjectStore));
        failure.Data!["writer"].Should().Be("json");
        failure.Data!["targetPath"].Should().Be(blockedPath);
        failure.ExceptionType.Should().NotBeNull();

        // Cleanup succeeded, so no cleanup-failure record and no stranded sibling.
        logger.Records.Should().NotContain(r => r.EventCode == "project.tempFile.cleanupFailed");
        Directory.GetFiles(_tempDir, "*.tmp.*").Should().BeEmpty();
    }

    [Test]
    public async Task MalformedFingerprintDigest_IsLoggedAsAParseEvent_OnTheReadOnlyPath()
    {
        // A newer-schema document degrades gracefully instead of throwing, which routes an invalid
        // hex digest through the seventh formerly-silent catch.
        Guid sourceId = Guid.NewGuid();
        string hakPath = Path.Combine(_tempDir, "fp.hak");
        await File.WriteAllTextAsync(hakPath, "dummy");

        string projectPath = Path.Combine(_tempDir, "badhex.srnccproj");
        await File.WriteAllTextAsync(projectPath, $$"""
        {
          "schemaVersion": 2,
          "sources": [
            {
              "id": "{{sourceId}}",
              "kind": "Hak",
              "path": { "kind": "absolute", "value": {{System.Text.Json.JsonSerializer.Serialize(hakPath)}} },
              "fingerprint": { "kind": "Hak", "algorithmVersion": 1, "digest": "zzzz" }
            }
          ],
          "selectionState": { "defaultSelected": true, "overrides": [] },
          "pins": []
        }
        """);

        RecordingLogger logger = new();
        WorkspaceState loaded = await CreateStore(logger).LoadAsync(projectPath);

        loaded.IsReadOnly.Should().BeTrue();
        logger.Records.Should().Contain(r => r.EventCode == "project.parse.invalidHex");
    }

    // ----------------------------------------------------------------- helpers

    private const string MinimalProjectJson = """
    {
      "schemaVersion": 1,
      "sources": [],
      "selectionState": { "defaultSelected": true, "overrides": [] },
      "pins": []
    }
    """;

    private static ProjectStore CreateStore(IAppLogger? logger = null)
    {
        return new ProjectStore(
            new StubIndexService(),
            new WorkspaceResolver(new StubHashService(), new AssetHashCache()),
            logger);
    }

    private sealed class RenameLegacyName : IJsonSchemaMigration
    {
        public SchemaKind Kind => SchemaKind.Project;

        public int FromVersion => 1;

        public int ToVersion => 2;

        public JsonObject Apply(JsonObject document)
        {
            JsonNode? legacy = document["legacyName"];
            document.Remove("legacyName");
            document["modernName"] = legacy?.DeepClone();
            document["schemaVersion"] = 2;
            return document;
        }
    }

    private sealed record LoggedRecord(
        LogLevel Level,
        string Category,
        string Message,
        string? EventCode,
        string? ExceptionType,
        IReadOnlyDictionary<string, string>? Data);

    /// <summary>
    /// A local recording logger. Deliberately self-contained rather than shared with another slice's
    /// test helper, so this file compiles on its own.
    /// </summary>
    private sealed class RecordingLogger : IAppLogger
    {
        private readonly List<LoggedRecord> _records = new();

        public IReadOnlyList<LoggedRecord> Records => _records;

        public void Log(
            LogLevel level,
            string category,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, string>? data = null)
        {
            string? eventCode = null;
            data?.TryGetValue(AppLogger.EventCodeKey, out eventCode);

            _records.Add(new LoggedRecord(
                level,
                category,
                message,
                eventCode,
                exception?.GetType().FullName,
                data));
        }
    }

    private sealed class StubIndexService : IAssetIndexService
    {
        public Task<SourceIndexSnapshot> IndexAsync(
            AssetSource source,
            IProgress<IndexProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            SourceFingerprint fingerprint = new(source.Kind, 1, new byte[32]);
            SourceScanStatistics stats = new(0, 0, TimeSpan.Zero);
            return Task.FromResult(new SourceIndexSnapshot(
                source,
                fingerprint,
                Array.Empty<IndexedAssetRecord>(),
                Array.Empty<AssetDiagnosticRecord>(),
                isCacheHit: false,
                scanStatistics: stats));
        }
    }

    private sealed class StubHashService : IStreamingHashService
    {
        public Task<byte[]> ComputeSha256Async(
            AssetSource source,
            AssetOccurrence occurrence,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(occurrence.Locator.ToString())));
        }
    }
}
