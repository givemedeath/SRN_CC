using Microsoft.Data.Sqlite;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Records;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;

namespace SRN.CC.Infrastructure.Cache;

public interface ISqliteCacheService
{
    Task<SourceIndexSnapshot?> TryGetSnapshotAsync(AssetSource source, SourceFingerprint fingerprint, CancellationToken cancellationToken = default);
    Task SaveSnapshotAsync(SourceIndexSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<PreviewCachePayload?> TryGetPreviewAsync(SourceFingerprint sourceFingerprint, AssetOccurrence occurrence, CancellationToken cancellationToken = default);
    Task SavePreviewAsync(SourceFingerprint sourceFingerprint, AssetOccurrence occurrence, int width, int height, byte[] pngBytes, CancellationToken cancellationToken = default);
    Task ClearCacheAsync(CancellationToken cancellationToken = default);
}

public sealed record PreviewCachePayload(int Width, int Height, byte[] PngBytes);

public sealed class SqliteCacheService : ISqliteCacheService, IDisposable
{
    public const long DefaultMaxLogicalBytes = 2L * 1024 * 1024 * 1024; // 2 GiB
    private const int EvictionBatchSize = 50;
    private readonly string _dbPath;
    private readonly long _maxLogicalBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _isDisposed;

    public string DatabasePath => _dbPath;

    public SqliteCacheService(string? dbPath = null, long maxLogicalBytes = DefaultMaxLogicalBytes)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string directory = Path.Combine(localAppData, "SRN.CC");
            Directory.CreateDirectory(directory);
            _dbPath = Path.Combine(directory, "cache-v1.sqlite");
        }
        else
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _dbPath = Path.GetFullPath(dbPath);
        }

        _maxLogicalBytes = maxLogicalBytes;
        EnsureDatabaseInitialized();
    }

    private string GetConnectionString()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            Cache = SqliteCacheMode.Private
        };
        return builder.ToString();
    }

    private SqliteConnection CreateConnection()
    {
        SqliteConnection conn = new(GetConnectionString());
        conn.Open();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=5000;
                PRAGMA auto_vacuum=INCREMENTAL;
            ";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    private void EnsureDatabaseInitialized()
    {
        string? quarantineReason = null;
        try
        {
            using SqliteConnection conn = CreateConnection();
            using SqliteCommand checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "PRAGMA quick_check;";
            string? checkResult = checkCmd.ExecuteScalar() as string;
            if (!string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase))
            {
                quarantineReason = "Quick check failed.";
            }
            else
            {
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS schema_info (
                        version INTEGER PRIMARY KEY
                    );
                ";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "SELECT version FROM schema_info LIMIT 1;";
                object? verObj = cmd.ExecuteScalar();
                if (verObj == null)
                {
                    using SqliteTransaction tx = conn.BeginTransaction();
                    cmd.Transaction = tx;
                    cmd.CommandText = GetSchemaCreationSql(includeSchemaInfo: true);
                    cmd.ExecuteNonQuery();
                    tx.Commit();
                }
                else
                {
                    int version = Convert.ToInt32(verObj);
                    if (version != 1)
                    {
                        quarantineReason = $"Unsupported cache schema version {verObj}.";
                    }
                    else if (!HasCompleteVersionOneBaseSchema(conn))
                    {
                        quarantineReason = "Cache schema version 1 is incomplete.";
                    }
                    else
                    {
                        EnsurePreviewCacheSchema(conn);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            quarantineReason = $"Database initialization error: {ex.Message}";
        }

        // The initialization connection must be disposed before a Windows rename.
        if (quarantineReason != null)
        {
            QuarantineCorruptedDatabase(quarantineReason);
        }
    }

    private static bool HasCompleteVersionOneBaseSchema(SqliteConnection conn)
    {
        return HasRequiredColumns(conn, "source_snapshots", "fingerprint", "source_kind", "timestamp_utc", "record_count", "logical_bytes", "last_access_utc") &&
               HasRequiredColumns(conn, "asset_records", "id", "fingerprint", "sequence_index", "locator_type", "entry_index", "relative_path", "original_name", "canonical_resref", "resource_type", "size", "validation_state", "diagnostic_code", "diagnostic_message");
    }

    private static bool HasTable(SqliteConnection conn, string tableName)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    private static bool HasRequiredColumns(SqliteConnection conn, string tableName, params string[] requiredColumns)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info([{tableName}]);";
        using SqliteDataReader reader = cmd.ExecuteReader();
        HashSet<string> actualColumns = new(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            actualColumns.Add(reader.GetString(1));
        }

        return requiredColumns.All(actualColumns.Contains);
    }

    private void EnsurePreviewCacheSchema(SqliteConnection conn)
    {
        if (!HasTable(conn, "preview_cache"))
        {
            using SqliteCommand createTableCmd = conn.CreateCommand();
            createTableCmd.CommandText = @"
                CREATE TABLE preview_cache (
                    source_fingerprint BLOB NOT NULL,
                    locator TEXT NOT NULL,
                    width INTEGER NOT NULL,
                    height INTEGER NOT NULL,
                    png_bytes BLOB NOT NULL,
                    last_access_utc TEXT NOT NULL
                );
            ";
            createTableCmd.ExecuteNonQuery();

            using SqliteCommand createIndexesCmd = conn.CreateCommand();
            createIndexesCmd.CommandText = @"
                CREATE UNIQUE INDEX idx_preview_cache_source_locator ON preview_cache(source_fingerprint, locator);
                CREATE INDEX idx_preview_cache_last_access ON preview_cache(last_access_utc ASC);
            ";
            createIndexesCmd.ExecuteNonQuery();
            return;
        }

        if (!HasRequiredColumns(conn, "preview_cache", "source_fingerprint", "locator", "width", "height", "png_bytes", "last_access_utc"))
        {
            throw new InvalidOperationException("Preview cache schema for preview_cache is incomplete.");
        }

        using SqliteCommand previewIndexesCmd = conn.CreateCommand();
        previewIndexesCmd.CommandText = @"
            CREATE UNIQUE INDEX IF NOT EXISTS idx_preview_cache_source_locator ON preview_cache(source_fingerprint, locator);
            CREATE INDEX IF NOT EXISTS idx_preview_cache_last_access ON preview_cache(last_access_utc ASC);
        ";
        previewIndexesCmd.ExecuteNonQuery();
    }

    private static string GetSchemaCreationSql(bool includeSchemaInfo) => $@"
        {(includeSchemaInfo ? "CREATE TABLE IF NOT EXISTS schema_info (version INTEGER PRIMARY KEY);" : string.Empty)}
        CREATE TABLE source_snapshots (
            fingerprint BLOB PRIMARY KEY,
            source_kind INTEGER NOT NULL,
            timestamp_utc TEXT NOT NULL,
            record_count INTEGER NOT NULL,
            logical_bytes INTEGER NOT NULL,
            last_access_utc TEXT NOT NULL
        );
        CREATE TABLE asset_records (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            fingerprint BLOB NOT NULL,
            sequence_index INTEGER NOT NULL,
            locator_type INTEGER NOT NULL,
            entry_index INTEGER,
            relative_path TEXT,
            original_name TEXT,
            canonical_resref TEXT,
            resource_type INTEGER,
            size INTEGER NOT NULL,
            validation_state INTEGER,
            diagnostic_code INTEGER,
            diagnostic_message TEXT,
            FOREIGN KEY(fingerprint) REFERENCES source_snapshots(fingerprint) ON DELETE CASCADE
        );
        CREATE TABLE preview_cache (
            source_fingerprint BLOB NOT NULL,
            locator TEXT NOT NULL,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL,
            png_bytes BLOB NOT NULL,
            last_access_utc TEXT NOT NULL
        );
        CREATE INDEX idx_asset_records_fingerprint ON asset_records(fingerprint);
        CREATE INDEX idx_source_snapshots_lru ON source_snapshots(last_access_utc ASC);
        CREATE UNIQUE INDEX idx_preview_cache_source_locator ON preview_cache(source_fingerprint, locator);
        CREATE INDEX idx_preview_cache_last_access ON preview_cache(last_access_utc ASC);
        {(includeSchemaInfo ? "INSERT INTO schema_info VALUES (1);" : string.Empty)}
    ";

    private static string GetCurrentTimestampUtc() => DateTime.UtcNow.ToString("o");

    private void QuarantineCorruptedDatabase(string reason)
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
        {
            string timeStamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string corruptSuffix = $".corrupt-{timeStamp}";

            MoveSidecarIfExists(_dbPath, _dbPath + corruptSuffix);
            MoveSidecarIfExists(_dbPath + "-wal", _dbPath + "-wal" + corruptSuffix);
            MoveSidecarIfExists(_dbPath + "-shm", _dbPath + "-shm" + corruptSuffix);
        }

        // Initialize clean database
        using SqliteConnection conn = CreateConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = GetSchemaCreationSql(includeSchemaInfo: true);
        cmd.ExecuteNonQuery();
    }

    private static void MoveSidecarIfExists(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }
        catch
        {
            // Best effort quarantine
        }
    }

    public async Task<SourceIndexSnapshot?> TryGetSnapshotAsync(AssetSource source, SourceFingerprint fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fingerprint);

        byte[] fpBytes = fingerprint.Digest.ToArray();
        using SqliteConnection conn = CreateConnection();
        using SqliteTransaction readTx = conn.BeginTransaction();

        using SqliteCommand checkCmd = conn.CreateCommand();
        checkCmd.Transaction = readTx;
        checkCmd.CommandText = "SELECT record_count, logical_bytes FROM source_snapshots WHERE fingerprint = @fp;";
        checkCmd.Parameters.AddWithValue("@fp", fpBytes);

        int recordCount;
        long totalLogicalBytes;
        using (SqliteDataReader reader = await checkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null; // Cache miss
            }

            recordCount = reader.GetInt32(0);
            totalLogicalBytes = reader.GetInt64(1);
        }

        List<IndexedAssetRecord> records = new();
        List<AssetDiagnosticRecord> diagnostics = new();
        using (SqliteCommand selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = readTx;
            selectCmd.CommandText = @"
                SELECT locator_type, entry_index, relative_path, original_name, canonical_resref, resource_type, size, validation_state, diagnostic_code, diagnostic_message
                FROM asset_records
                WHERE fingerprint = @fp
                ORDER BY sequence_index ASC;
            ";
            selectCmd.Parameters.AddWithValue("@fp", fpBytes);

            using (SqliteDataReader reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    int locatorType = reader.GetInt32(0);
                    int entryIndex = reader.IsDBNull(1) ? -1 : reader.GetInt32(1);
                    string relativePath = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string originalName = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    string canonicalResref = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    ushort resType = reader.IsDBNull(5) ? (ushort)0 : (ushort)reader.GetInt32(5);
                    long size = reader.GetInt64(6);
                    ValidationState vState = reader.IsDBNull(7) ? ValidationState.Valid : (ValidationState)reader.GetInt32(7);
                    int diagCodeInt = reader.IsDBNull(8) ? -1 : reader.GetInt32(8);
                    string diagMsg = reader.IsDBNull(9) ? "" : reader.GetString(9);

                    if (diagCodeInt >= 0)
                    {
                        DiagnosticCode code = (DiagnosticCode)diagCodeInt;
                        AssetDiagnosticRecord diag = new(code, diagMsg, targetPath: string.IsNullOrEmpty(relativePath) ? null : relativePath, entryIndex: entryIndex >= 0 ? entryIndex : null);
                        records.Add(new IndexedAssetRecord(diag));
                        diagnostics.Add(diag);
                    }
                    else
                    {
                        OccurrenceLocator locator = locatorType == 0
                            ? new HakEntryLocator(entryIndex)
                            : new FolderFileLocator(relativePath);

                        AssetIdentity identity = new(canonicalResref, resType);
                        AssetOccurrence occurrence = new(
                            identity: identity,
                            sourceId: source.Id,
                            locator: locator,
                            originalName: originalName,
                            size: size,
                            validationState: vState,
                            extensionMetadata: null,
                            sha256: null);

                        records.Add(new IndexedAssetRecord(occurrence));
                    }
                }
            }
        }

        readTx.Commit();

        // Touch LRU after releasing the consistent read snapshot.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand touchCmd = conn.CreateCommand();
            touchCmd.CommandText = "UPDATE source_snapshots SET last_access_utc = @now WHERE fingerprint = @fp;";
            touchCmd.Parameters.AddWithValue("@now", GetCurrentTimestampUtc());
            touchCmd.Parameters.AddWithValue("@fp", fpBytes);
            await touchCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        SourceScanStatistics stats = new(recordCount, totalLogicalBytes, TimeSpan.Zero);
        AssetSource sourceWithFingerprint = source with { Fingerprint = fingerprint, IsAvailable = true };

        return new SourceIndexSnapshot(
            source: sourceWithFingerprint,
            fingerprint: fingerprint,
            records: records,
            diagnostics: diagnostics,
            isCacheHit: true,
            scanStatistics: stats);
    }

    public async Task SaveSnapshotAsync(SourceIndexSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] fpBytes = snapshot.Fingerprint.Digest.ToArray();
            long logicalBytes = snapshot.ScanStatistics.TotalLogicalBytes;
            string nowUtc = GetCurrentTimestampUtc();

            using (SqliteConnection conn = CreateConnection())
            using (SqliteTransaction tx = conn.BeginTransaction())
            {
                using (SqliteCommand delCmd = conn.CreateCommand())
                {
                    delCmd.Transaction = tx;
                    delCmd.CommandText = "DELETE FROM source_snapshots WHERE fingerprint = @fp;";
                    delCmd.Parameters.AddWithValue("@fp", fpBytes);
                    await delCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                using (SqliteCommand delPreviewCmd = conn.CreateCommand())
                {
                    delPreviewCmd.Transaction = tx;
                    delPreviewCmd.CommandText = "DELETE FROM preview_cache WHERE source_fingerprint = @fp;";
                    delPreviewCmd.Parameters.AddWithValue("@fp", fpBytes);
                    await delPreviewCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                using (SqliteCommand insSnapCmd = conn.CreateCommand())
                {
                    insSnapCmd.Transaction = tx;
                    insSnapCmd.CommandText = @"
                        INSERT INTO source_snapshots (fingerprint, source_kind, timestamp_utc, record_count, logical_bytes, last_access_utc)
                        VALUES (@fp, @kind, @ts, @count, @bytes, @last_access);
                    ";
                    insSnapCmd.Parameters.AddWithValue("@fp", fpBytes);
                    insSnapCmd.Parameters.AddWithValue("@kind", (int)snapshot.Source.Kind);
                    insSnapCmd.Parameters.AddWithValue("@ts", nowUtc);
                    insSnapCmd.Parameters.AddWithValue("@count", snapshot.Records.Count);
                    insSnapCmd.Parameters.AddWithValue("@bytes", logicalBytes);
                    insSnapCmd.Parameters.AddWithValue("@last_access", nowUtc);
                    await insSnapCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                using (SqliteCommand insRecCmd = conn.CreateCommand())
                {
                    insRecCmd.Transaction = tx;
                    insRecCmd.CommandText = @"
                        INSERT INTO asset_records (fingerprint, sequence_index, locator_type, entry_index, relative_path, original_name, canonical_resref, resource_type, size, validation_state, diagnostic_code, diagnostic_message)
                        VALUES (@fp, @seq, @loc_type, @entry_idx, @rel_path, @orig_name, @canon_resref, @res_type, @size, @val_state, @diag_code, @diag_msg);
                    ";

                    var pFp = insRecCmd.Parameters.Add("@fp", SqliteType.Blob);
                    var pSeq = insRecCmd.Parameters.Add("@seq", SqliteType.Integer);
                    var pLocType = insRecCmd.Parameters.Add("@loc_type", SqliteType.Integer);
                    var pEntryIdx = insRecCmd.Parameters.Add("@entry_idx", SqliteType.Integer);
                    var pRelPath = insRecCmd.Parameters.Add("@rel_path", SqliteType.Text);
                    var pOrigName = insRecCmd.Parameters.Add("@orig_name", SqliteType.Text);
                    var pCanonResref = insRecCmd.Parameters.Add("@canon_resref", SqliteType.Text);
                    var pResType = insRecCmd.Parameters.Add("@res_type", SqliteType.Integer);
                    var pSize = insRecCmd.Parameters.Add("@size", SqliteType.Integer);
                    var pValState = insRecCmd.Parameters.Add("@val_state", SqliteType.Integer);
                    var pDiagCode = insRecCmd.Parameters.Add("@diag_code", SqliteType.Integer);
                    var pDiagMsg = insRecCmd.Parameters.Add("@diag_msg", SqliteType.Text);

                    for (int i = 0; i < snapshot.Records.Count; i++)
                    {
                        var rec = snapshot.Records[i];
                        pFp.Value = fpBytes;
                        pSeq.Value = i;

                        if (rec.IsValid && rec.Occurrence != null)
                        {
                            var occ = rec.Occurrence;
                            if (occ.Locator is HakEntryLocator hakLoc)
                            {
                                pLocType.Value = 0;
                                pEntryIdx.Value = hakLoc.EntryIndex;
                                pRelPath.Value = DBNull.Value;
                            }
                            else if (occ.Locator is FolderFileLocator folderLoc)
                            {
                                pLocType.Value = 1;
                                pEntryIdx.Value = DBNull.Value;
                                pRelPath.Value = folderLoc.NormalizedRelativePath;
                            }
                            else
                            {
                                pLocType.Value = 0;
                                pEntryIdx.Value = DBNull.Value;
                                pRelPath.Value = DBNull.Value;
                            }

                            pOrigName.Value = occ.OriginalName;
                            pCanonResref.Value = occ.Identity.OriginalName;
                            pResType.Value = occ.Identity.ResourceType;
                            pSize.Value = occ.Size;
                            pValState.Value = (int)occ.ValidationState;
                            pDiagCode.Value = DBNull.Value;
                            pDiagMsg.Value = DBNull.Value;
                        }
                        else if (rec.Diagnostic != null)
                        {
                            var diag = rec.Diagnostic;
                            pLocType.Value = snapshot.Source.Kind == AssetSourceKind.Hak ? 0 : 1;
                            pEntryIdx.Value = diag.EntryIndex.HasValue ? diag.EntryIndex.Value : DBNull.Value;
                            pRelPath.Value = diag.TargetPath ?? (object)DBNull.Value;
                            pOrigName.Value = DBNull.Value;
                            pCanonResref.Value = DBNull.Value;
                            pResType.Value = DBNull.Value;
                            pSize.Value = 0;
                            pValState.Value = DBNull.Value;
                            pDiagCode.Value = (int)diag.Code;
                            pDiagMsg.Value = diag.Message;
                        }

                        await insRecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            // Perform LRU eviction if logical bytes exceed threshold
            await EnforceLruEvictionAsync(fpBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<PreviewCachePayload?> TryGetPreviewAsync(SourceFingerprint sourceFingerprint, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(occurrence);

        if (!TryFormatPreviewLocator(occurrence, out string locator))
        {
            return null;
        }

        byte[] fpBytes = sourceFingerprint.Digest.ToArray();
        using SqliteConnection conn = CreateConnection();
        using SqliteCommand selectCmd = conn.CreateCommand();
        selectCmd.CommandText = @"
            SELECT width, height, png_bytes
            FROM preview_cache
            WHERE source_fingerprint = @fp AND locator = @locator;
        ";
        selectCmd.Parameters.AddWithValue("@fp", fpBytes);
        selectCmd.Parameters.AddWithValue("@locator", locator);

        using SqliteDataReader reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int width = reader.GetInt32(0);
        int height = reader.GetInt32(1);
        byte[] pngBytes = (byte[])reader["png_bytes"];

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand touchCmd = conn.CreateCommand();
            touchCmd.CommandText = "UPDATE preview_cache SET last_access_utc = @now WHERE source_fingerprint = @fp AND locator = @locator;";
            touchCmd.Parameters.AddWithValue("@now", GetCurrentTimestampUtc());
            touchCmd.Parameters.AddWithValue("@fp", fpBytes);
            touchCmd.Parameters.AddWithValue("@locator", locator);
            await touchCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        return new PreviewCachePayload(width, height, pngBytes);
    }

    public async Task SavePreviewAsync(SourceFingerprint sourceFingerprint, AssetOccurrence occurrence, int width, int height, byte[] pngBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
        {
            throw new ArgumentException("PNG payload cannot be empty.", nameof(pngBytes));
        }
        if (width < 1 || height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Preview dimensions must be positive.");
        }

        if (!TryFormatPreviewLocator(occurrence, out string locator))
        {
            throw new NotSupportedException($"Preview cache does not support locator type '{occurrence.Locator.GetType().Name}'.");
        }

        byte[] fpBytes = sourceFingerprint.Digest.ToArray();
        byte[] payload = pngBytes.ToArray();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (SqliteConnection conn = CreateConnection())
            using (SqliteTransaction tx = conn.BeginTransaction())
            {
                using (SqliteCommand upsertCmd = conn.CreateCommand())
                {
                    upsertCmd.Transaction = tx;
                    upsertCmd.CommandText = @"
                        INSERT INTO preview_cache (source_fingerprint, locator, width, height, png_bytes, last_access_utc)
                        VALUES (@fp, @locator, @width, @height, @png, @last_access)
                        ON CONFLICT(source_fingerprint, locator) DO UPDATE SET
                            width = excluded.width,
                            height = excluded.height,
                            png_bytes = excluded.png_bytes,
                            last_access_utc = excluded.last_access_utc;
                    ";
                    upsertCmd.Parameters.AddWithValue("@fp", fpBytes);
                    upsertCmd.Parameters.AddWithValue("@locator", locator);
                    upsertCmd.Parameters.AddWithValue("@width", width);
                    upsertCmd.Parameters.AddWithValue("@height", height);
                    upsertCmd.Parameters.Add("@png", SqliteType.Blob).Value = payload;
                    upsertCmd.Parameters.AddWithValue("@last_access", GetCurrentTimestampUtc());
                    await upsertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            // Keep cache bounded while preserving the source snapshot if any.
            await EnforceLruEvictionAsync(fpBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static bool TryFormatPreviewLocator(AssetOccurrence occurrence, out string locator)
    {
        switch (occurrence.Locator)
        {
            case HakEntryLocator hakEntry:
                locator = $"hak:{hakEntry.EntryIndex}";
                return true;
            case FolderFileLocator folderFile:
                locator = $"folder:{folderFile.NormalizedRelativePath}";
                return true;
            default:
                locator = string.Empty;
                return false;
        }
    }

    private async Task<long> GetTotalTrackedBytesAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        using SqliteCommand sumCmd = conn.CreateCommand();
        sumCmd.CommandText = @"
            SELECT
            COALESCE((SELECT TOTAL(logical_bytes) FROM source_snapshots), 0) +
            COALESCE((SELECT TOTAL(LENGTH(png_bytes)) FROM preview_cache), 0);
        ";
        object? sumObj = await sumCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return sumObj is null ? 0 : Convert.ToInt64(sumObj);
    }

    private async Task<List<(byte[] Fingerprint, long Bytes)>> GetCandidateSnapshotEvictionBatchAsync(
        SqliteConnection conn,
        byte[] protectedFingerprint,
        CancellationToken cancellationToken)
    {
        using SqliteCommand selectLruCmd = conn.CreateCommand();
        selectLruCmd.CommandText = @"
            SELECT s.fingerprint,
                   s.logical_bytes + COALESCE(pc.preview_bytes, 0) AS total_bytes
            FROM source_snapshots s
            LEFT JOIN (
                SELECT source_fingerprint,
                       SUM(LENGTH(png_bytes)) AS preview_bytes
                FROM preview_cache
                GROUP BY source_fingerprint
            ) pc ON pc.source_fingerprint = s.fingerprint
            WHERE s.fingerprint <> @protected_fp
            ORDER BY s.last_access_utc ASC
            LIMIT @batch_size;
        ";
        selectLruCmd.Parameters.AddWithValue("@protected_fp", protectedFingerprint);
        selectLruCmd.Parameters.AddWithValue("@batch_size", EvictionBatchSize);

        List<(byte[] Fingerprint, long Bytes)> lruEntries = new();
        using (SqliteDataReader reader = await selectLruCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    byte[] fp = (byte[])reader["fingerprint"];
                    long b = Convert.ToInt64(reader.GetValue(1));
                    lruEntries.Add((fp, b));
                }
        }

        return lruEntries;
    }

    private async Task EnforceLruEvictionAsync(byte[] protectedFingerprint, CancellationToken cancellationToken)
    {
        using SqliteConnection conn = CreateConnection();
        long currentTotal = await GetTotalTrackedBytesAsync(conn, cancellationToken).ConfigureAwait(false);
        if (currentTotal <= _maxLogicalBytes) return;

        while (currentTotal > _maxLogicalBytes)
        {
            List<(byte[] Fingerprint, long Bytes)> lruEntries = await GetCandidateSnapshotEvictionBatchAsync(
                conn,
                protectedFingerprint,
                cancellationToken).ConfigureAwait(false);

            if (lruEntries.Count == 0)
            {
                break;
            }

            foreach (var entry in lruEntries)
            {
                if (currentTotal <= _maxLogicalBytes)
                {
                    break;
                }

                using SqliteCommand delCmd = conn.CreateCommand();
                delCmd.CommandText = "DELETE FROM source_snapshots WHERE fingerprint = @fp;";
                delCmd.Parameters.AddWithValue("@fp", entry.Fingerprint);
                int removedRows = await delCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (removedRows > 0)
                {
                    currentTotal -= entry.Bytes;
                }

                    using SqliteCommand delPreviewCmd = conn.CreateCommand();
                    delPreviewCmd.CommandText = "DELETE FROM preview_cache WHERE source_fingerprint = @fp;";
                    delPreviewCmd.Parameters.AddWithValue("@fp", entry.Fingerprint);
                    await delPreviewCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        using SqliteCommand vacCmd = conn.CreateCommand();
        vacCmd.CommandText = @"
            PRAGMA incremental_vacuum;
            PRAGMA wal_checkpoint(PASSIVE);
        ";
        await vacCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteConnection conn = CreateConnection();
            using SqliteTransaction tx = conn.BeginTransaction();
            using (SqliteCommand previewCmd = conn.CreateCommand())
            {
                previewCmd.Transaction = tx;
                previewCmd.CommandText = "DELETE FROM preview_cache;";
                await previewCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (SqliteCommand sourceCmd = conn.CreateCommand())
            {
                sourceCmd.Transaction = tx;
                sourceCmd.CommandText = "DELETE FROM source_snapshots;";
                await sourceCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _writeLock.Dispose();
        }
    }
}
