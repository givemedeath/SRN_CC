using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Settings;
using SRN.CC.Infrastructure.Persistence;

namespace SRN.CC.Tests.Persistence;

/// <summary>
/// Covers the schema-classification half of <see cref="SettingsStore"/>: a newer document must open
/// read-only and survive untouched, and only a genuinely corrupt document may be quarantined. The
/// store previously treated "newer" as "corrupt" and renamed away a future build's settings.
/// </summary>
[TestFixture]
public class SettingsStoreSchemaTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_SettingsSchemaTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task NewerSchemaVersion_ReturnsReadOnlyDefaults_AndLeavesFileByteIdentical()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, NewerSchemaJson);
        string hashBefore = HashFile(settingsPath);

        SettingsStore store = new SettingsStore();

        SettingsLoadResult result = await store.LoadAsync(settingsPath);

        result.Status.Should().Be(SettingsLoadStatus.ReadOnlyNewer);
        result.QuarantinedPath.Should().BeNull("a newer file is not corrupt and must not be quarantined");
        result.Settings.IsReadOnly.Should().BeTrue();
        result.Settings.LastProjectPath.Should().BeNull("a newer document is not interpreted, only left alone");
        result.Settings.RecentProjectPaths.Should().BeEmpty();
        result.Settings.NwnInstallOverride.Should().BeNull();

        File.Exists(settingsPath).Should().BeTrue();
        Directory.GetFiles(_tempDir, "*.corrupt.*").Should().BeEmpty();
        HashFile(settingsPath).Should().Be(hashBefore, "the newer file must be byte-identical after a load");
    }

    [Test]
    public async Task SaveAsync_WithReadOnlySettings_Throws()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, NewerSchemaJson);
        string hashBefore = HashFile(settingsPath);

        SettingsStore store = new SettingsStore();
        SettingsLoadResult result = await store.LoadAsync(settingsPath);

        Func<Task> save = () => store.SaveAsync(result.Settings, settingsPath);

        await save.Should().ThrowAsync<InvalidOperationException>();
        HashFile(settingsPath).Should().Be(hashBefore);
    }

    [Test]
    public async Task SaveAsync_ToAPathLoadedAsReadOnlyNewer_ThrowsEvenForFreshSettings()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, NewerSchemaJson);
        string hashBefore = HashFile(settingsPath);

        SettingsStore store = new SettingsStore();
        await store.LoadAsync(settingsPath);

        // Defaults carry no IsReadOnly flag, so only the store's memory of the path can stop this.
        Func<Task> save = () => store.SaveAsync(new ApplicationSettings(lastProjectPath: "c:/proj/x.srnccproj"), settingsPath);

        await save.Should().ThrowAsync<InvalidOperationException>();
        HashFile(settingsPath).Should().Be(hashBefore);
    }

    [Test]
    public async Task SaveAsync_ToAnUnrelatedPath_StillSucceedsAfterAReadOnlyLoad()
    {
        string newerPath = Path.Combine(_tempDir, "newer.json");
        string writablePath = Path.Combine(_tempDir, "writable.json");
        await File.WriteAllTextAsync(newerPath, NewerSchemaJson);

        SettingsStore store = new SettingsStore();
        await store.LoadAsync(newerPath);

        await store.SaveAsync(new ApplicationSettings(lastProjectPath: "c:/proj/x.srnccproj"), writablePath);

        SettingsLoadResult reloaded = await store.LoadAsync(writablePath);
        reloaded.Status.Should().Be(SettingsLoadStatus.Loaded);
        reloaded.Settings.LastProjectPath.Should().Be("c:/proj/x.srnccproj");
    }

    [Test]
    public async Task CurrentSchemaVersion_BecomesWritableAgainAfterAReadOnlyLoadOfTheSamePath()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, NewerSchemaJson);

        SettingsStore store = new SettingsStore();
        (await store.LoadAsync(settingsPath)).Status.Should().Be(SettingsLoadStatus.ReadOnlyNewer);

        // A future build downgrading its own file must not stay permanently locked out.
        File.Delete(settingsPath);
        (await store.LoadAsync(settingsPath)).Status.Should().Be(SettingsLoadStatus.Loaded);

        await store.SaveAsync(new ApplicationSettings(lastProjectPath: "c:/proj/x.srnccproj"), settingsPath);

        (await store.LoadAsync(settingsPath)).Settings.LastProjectPath.Should().Be("c:/proj/x.srnccproj");
    }

    [Test]
    public async Task UnsupportedSchemaVersion_IsQuarantinedAndRebuilt()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{ \"schemaVersion\": 0, \"lastProjectPath\": \"c:/proj/old.srnccproj\" }");

        SettingsStore store = new SettingsStore();

        SettingsLoadResult result = await store.LoadAsync(settingsPath);

        result.Status.Should().Be(SettingsLoadStatus.RebuiltAfterQuarantine);
        result.Settings.IsReadOnly.Should().BeFalse();
        result.Settings.LastProjectPath.Should().BeNull();

        File.Exists(settingsPath).Should().BeFalse();
        string[] quarantined = Directory.GetFiles(_tempDir, "settings.json.corrupt.*");
        quarantined.Should().HaveCount(1);
        result.QuarantinedPath.Should().Be(quarantined[0]);
    }

    [Test]
    public async Task MalformedJson_ReportsTheQuarantinePathAndLogsIt()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{ not json at all");

        RecordingLogger logger = new RecordingLogger();
        SettingsStore store = new SettingsStore(logger);

        SettingsLoadResult result = await store.LoadAsync(settingsPath);

        result.Status.Should().Be(SettingsLoadStatus.RebuiltAfterQuarantine);
        result.QuarantinedPath.Should().NotBeNull();
        Path.GetFileName(result.QuarantinedPath!).Should().StartWith("settings.json.corrupt.");
        File.Exists(result.QuarantinedPath!).Should().BeTrue();

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error, "the swallowed quarantine catch now logs");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warn && e.Data != null && e.Data.ContainsKey("quarantinedPath"));
    }

    [Test]
    public async Task NewerSchemaVersion_IsLogged()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, NewerSchemaJson);

        RecordingLogger logger = new RecordingLogger();
        SettingsStore store = new SettingsStore(logger);

        await store.LoadAsync(settingsPath);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Warn);
        logger.Entries[0].Data.Should().ContainKey("fileSchemaVersion");
    }

    [Test]
    public async Task CurrentSchemaVersion_LoadsThroughTheMigrationPipelineWithoutLogging()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        RecordingLogger logger = new RecordingLogger();
        SettingsStore store = new SettingsStore(logger);

        await store.SaveAsync(new ApplicationSettings(lastProjectPath: "c:/proj/x.srnccproj"), settingsPath);
        SettingsLoadResult result = await store.LoadAsync(settingsPath);

        result.Status.Should().Be(SettingsLoadStatus.Loaded);
        result.Settings.LastProjectPath.Should().Be("c:/proj/x.srnccproj");
        logger.Entries.Should().BeEmpty("the v1 pipeline is an identity pass with nothing to report");
    }

    private const string NewerSchemaJson =
        "{\n  \"schemaVersion\": 2,\n  \"lastProjectPath\": \"c:/proj/future.srnccproj\",\n"
        + "  \"aFieldThisBuildHasNeverHeardOf\": true\n}\n";

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, string>? Data);

    private sealed class RecordingLogger : IAppLogger
    {
        public List<LogEntry> Entries { get; } = new();

        public void Log(
            LogLevel level,
            string category,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, string>? data = null)
        {
            Entries.Add(new LogEntry(level, message, data));
        }
    }
}
