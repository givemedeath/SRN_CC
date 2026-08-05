using FluentAssertions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Records;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Cache;

namespace SRN.CC.Tests.Cache;

/// <summary>
/// Covers S12: the cache constructor must never throw. Before this slice a corrupt cache whose
/// quarantine rename failed propagated out of <c>App.OnFrameworkInitializationCompleted</c> as a
/// hard startup crash with no message; it must now degrade to a permanent miss that the startup
/// preflight can report.
/// </summary>
[TestFixture]
public class SqliteCacheQuarantineTests
{
    private string _directory = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SRNCC_CacheQuarantine_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "cache-v1.sqlite");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle must not fail an otherwise passing test run.
        }
    }

    [Test]
    public void Initialize_GarbageDatabaseFile_QuarantinesRebuildsAndEmitsDiagnostic()
    {
        File.WriteAllBytes(_dbPath, "this is not a SQLite database, not even slightly"u8.ToArray());
        RecordingLogger logger = new();

        SqliteCacheService? service = null;
        Action act = () => service = new SqliteCacheService(_dbPath, logger: logger);

        act.Should().NotThrow();
        using (service)
        {
            service!.IsAvailable.Should().BeTrue("a rebuilt cache is fully usable");
            service.LastQuarantineReason.Should().NotBeNullOrWhiteSpace(
                "the operator needs to know why their cache was discarded");
        }

        GetQuarantinedFiles().Should().ContainSingle()
            .Which.Should().MatchRegex(@"\.corrupt-\d{8}T\d{6}Z$");
        File.Exists(_dbPath).Should().BeTrue("a fresh database replaces the quarantined one");
        logger.Entries.Should().Contain(entry =>
            entry.Data != null &&
            entry.Data.ContainsKey("diagnosticCode") &&
            entry.Data["diagnosticCode"] == nameof(DiagnosticCode.CorruptedCacheQuarantined));
    }

    [Test]
    public async Task Initialize_TruncatedDatabaseFile_QuarantinesAndRebuildsAWorkingCache()
    {
        // Build a genuine database, then lop off its tail so the header survives but the pages do not.
        using (SqliteCacheService seed = new(_dbPath))
        {
            seed.IsAvailable.Should().BeTrue();
        }

        SqliteConnection.ClearAllPools();
        byte[] original = File.ReadAllBytes(_dbPath);
        File.WriteAllBytes(_dbPath, original.Take(original.Length / 3).ToArray());

        RecordingLogger logger = new();
        using SqliteCacheService service = new(_dbPath, logger: logger);

        service.IsAvailable.Should().BeTrue();
        service.LastQuarantineReason.Should().NotBeNullOrWhiteSpace();
        GetQuarantinedFiles().Should().ContainSingle();
        logger.Entries.Should().Contain(entry =>
            entry.Data != null &&
            entry.Data.ContainsKey("diagnosticCode") &&
            entry.Data["diagnosticCode"] == nameof(DiagnosticCode.CorruptedCacheQuarantined));

        // The rebuilt database must be a real, writable cache — not merely a file that exists.
        (AssetSource source, SourceFingerprint fingerprint, SourceIndexSnapshot snapshot) = CreateSnapshot(0x11);
        await service.SaveSnapshotAsync(snapshot);
        (await service.TryGetSnapshotAsync(source, fingerprint)).Should().NotBeNull();
    }

    [Test]
    public void Initialize_UnsupportedSchemaVersionZero_IsClassifiedUnsupportedAndQuarantined()
    {
        using (SqliteConnection connection = new($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_info (version INTEGER PRIMARY KEY); INSERT INTO schema_info VALUES (0);";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        using SqliteCacheService service = new(_dbPath);

        service.IsAvailable.Should().BeTrue();
        service.LastQuarantineReason.Should().Contain("Unsupported cache schema version 0");
        GetQuarantinedFiles().Should().ContainSingle();
    }

    [Test]
    public void Initialize_NewerSchemaVersion_IsDiscardedRatherThanOpenedReadOnly()
    {
        using (SqliteConnection connection = new($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_info (version INTEGER PRIMARY KEY); INSERT INTO schema_info VALUES (99);";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        using SqliteCacheService service = new(_dbPath);

        service.IsAvailable.Should().BeTrue("the cache is derived data, so it is rebuilt rather than opened read-only");
        service.LastQuarantineReason.Should().Contain("newer than supported version");
        GetQuarantinedFiles().Should().ContainSingle();
    }

    [Test]
    public void Initialize_QuarantineRenameFails_ReportsUnavailableWithoutThrowing()
    {
        File.WriteAllBytes(_dbPath, "corrupt"u8.ToArray());
        RecordingLogger logger = new();

        // An exclusive handle makes the rename genuinely impossible, reproducing the read-only
        // %LOCALAPPDATA% that used to crash startup.
        using FileStream exclusive = new(_dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        SqliteCacheService? service = null;
        Action act = () => service = new SqliteCacheService(_dbPath, logger: logger);

        act.Should().NotThrow("a failed quarantine must degrade, not crash startup");
        using (service)
        {
            service!.IsAvailable.Should().BeFalse();
            service.LastQuarantineReason.Should().Contain("Quarantine failed");
        }

        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error);
    }

    [Test]
    public async Task DegradedCache_EveryPublicMethod_BehavesLikeAColdCache()
    {
        File.WriteAllBytes(_dbPath, "corrupt"u8.ToArray());
        using FileStream exclusive = new(_dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using SqliteCacheService service = new(_dbPath);
        service.IsAvailable.Should().BeFalse();

        (AssetSource source, SourceFingerprint fingerprint, SourceIndexSnapshot snapshot) = CreateSnapshot(0x22);
        AssetOccurrence occurrence = snapshot.Records[0].Occurrence!;

        await service.SaveSnapshotAsync(snapshot);
        (await service.TryGetSnapshotAsync(source, fingerprint)).Should().BeNull();

        await service.SavePreviewAsync(fingerprint, occurrence, 8, 8, new byte[] { 1, 2, 3, 4 });
        (await service.TryGetPreviewAsync(fingerprint, occurrence)).Should().BeNull();

        Func<Task> clear = () => service.ClearCacheAsync();
        await clear.Should().NotThrowAsync();
    }

    [Test]
    public void Initialize_HealthyDatabase_IsAvailableWithNoQuarantineReason()
    {
        using SqliteCacheService service = new(_dbPath);

        service.IsAvailable.Should().BeTrue();
        service.LastQuarantineReason.Should().BeNull();
        GetQuarantinedFiles().Should().BeEmpty();
    }

    private string[] GetQuarantinedFiles() =>
        Directory.GetFiles(_directory, "cache-v1.sqlite.corrupt-*");

    private static (AssetSource Source, SourceFingerprint Fingerprint, SourceIndexSnapshot Snapshot) CreateSnapshot(byte seed)
    {
        byte[] digest = new byte[32];
        Array.Fill(digest, seed);
        SourceFingerprint fingerprint = new(AssetSourceKind.Folder, 1, digest);
        AssetSource source = AssetSource.CreateFolder(@"C:\Test\Degraded", id: Guid.NewGuid());

        AssetOccurrence occurrence = new(
            identity: new AssetIdentity("file", 2009),
            sourceId: source.Id,
            locator: new FolderFileLocator("file.nss"),
            originalName: "file.nss",
            size: 64);

        SourceIndexSnapshot snapshot = new(
            source: source with { Fingerprint = fingerprint },
            fingerprint: fingerprint,
            records: new[] { new IndexedAssetRecord(occurrence) },
            diagnostics: Array.Empty<AssetDiagnosticRecord>(),
            isCacheHit: false,
            scanStatistics: new SourceScanStatistics(1, 64, TimeSpan.Zero));

        return (source, fingerprint, snapshot);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, string>? Data);

    private sealed class RecordingLogger : IAppLogger
    {
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public void Log(
            LogLevel level,
            string category,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, string>? data = null)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(level, message, exception, data));
            }
        }
    }
}
