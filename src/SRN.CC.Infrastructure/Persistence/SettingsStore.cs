using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Schema;
using SRN.CC.Core.Services;
using SRN.CC.Core.Settings;
using SRN.CC.Core.Startup;

namespace SRN.CC.Infrastructure.Persistence;

/// <summary>
/// Reads and writes the per-user settings document, following the same schema rule as the project
/// file: a newer document opens read-only and is never written back, and only a genuinely corrupt
/// document is quarantined. Treating "newer" as "corrupt" — as this store used to — silently renamed
/// away a future build's settings.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private const string LogCategory = nameof(SettingsStore);

    /// <summary>
    /// No settings migrations exist at schema version 1, so this pipeline is an identity pass. It is
    /// wired into the load path anyway so the seam is exercised by every load rather than only
    /// existing on paper, which is how migration hooks rot before their first real migration.
    /// </summary>
    private static readonly SchemaMigrationPipeline Migrations =
        new(SchemaKind.Settings, Array.Empty<IJsonSchemaMigration>());

    private readonly IAppLogger _logger;

    /// <summary>
    /// Paths whose most recent load classified as <see cref="SchemaOpenMode.ReadOnlyNewer"/>. Keyed
    /// by path rather than being a single flag so that reading a newer file does not block writes to
    /// an unrelated one.
    /// </summary>
    private readonly HashSet<string> _readOnlyNewerPaths = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _readOnlyNewerGate = new();

    /// <summary>Creates a store.</summary>
    /// <param name="logger">
    /// Optional sink for the quarantine and write-failure events this store used to swallow.
    /// </param>
    public SettingsStore(IAppLogger? logger = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
    }

    /// <summary>
    /// The shipped settings location. Delegates to <see cref="AppPaths"/> rather than re-deriving
    /// <c>%LOCALAPPDATA%\SRN.CC\settings.json</c>, so the layout has one owner.
    /// </summary>
    public static string DefaultSettingsPath => AppPaths.Default.SettingsPath;

    /// <inheritdoc />
    public async Task<SettingsLoadResult> LoadAsync(string? overrideFilePath = null, CancellationToken cancellationToken = default)
    {
        string filePath = ResolveFilePath(overrideFilePath);
        if (!File.Exists(filePath))
        {
            ClearReadOnlyNewer(filePath);
            return new SettingsLoadResult(new ApplicationSettings(), SettingsLoadStatus.Loaded, QuarantinedPath: null);
        }

        try
        {
            string jsonContent = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            JsonNode rootNode = JsonNode.Parse(jsonContent) ?? throw new JsonException("Settings JSON root was null.");
            JsonObject rootObj = rootNode.AsObject();

            int schemaVersion = rootObj["schemaVersion"]?.GetValue<int>() ?? 0;
            SchemaOpenMode openMode = SchemaVersions.Classify(SchemaKind.Settings, schemaVersion);

            if (openMode == SchemaOpenMode.ReadOnlyNewer)
            {
                // Return before touching anything: the file must be byte-identical afterwards.
                MarkReadOnlyNewer(filePath);
                _logger.Log(
                    LogLevel.Warn,
                    LogCategory,
                    "Settings file declares a newer schema version; opening read-only and leaving the file untouched.",
                    exception: null,
                    data: new Dictionary<string, string>
                    {
                        ["path"] = filePath,
                        ["fileSchemaVersion"] = schemaVersion.ToString(CultureInfo.InvariantCulture),
                        ["supportedSchemaVersion"] = SchemaVersions.Settings.ToString(CultureInfo.InvariantCulture)
                    });

                return new SettingsLoadResult(
                    new ApplicationSettings(isReadOnly: true),
                    SettingsLoadStatus.ReadOnlyNewer,
                    QuarantinedPath: null);
            }

            if (openMode != SchemaOpenMode.Current)
            {
                throw new InvalidOperationException($"Unsupported settings schema version {schemaVersion}.");
            }

            if (!Migrations.TryUpgrade(
                    rootObj,
                    schemaVersion,
                    out JsonObject upgraded,
                    out IReadOnlyList<string> applied,
                    out string? migrationError))
            {
                throw new InvalidOperationException(
                    migrationError ?? $"Settings schema version {schemaVersion} could not be upgraded.");
            }

            if (applied.Count > 0)
            {
                _logger.Log(
                    LogLevel.Info,
                    LogCategory,
                    "Applied settings schema migrations.",
                    exception: null,
                    data: new Dictionary<string, string>
                    {
                        ["path"] = filePath,
                        ["migrations"] = string.Join(", ", applied)
                    });
            }

            string? lastProjectPath = upgraded["lastProjectPath"]?.GetValue<string>();
            string? nwnInstallOverride = upgraded["nwnInstallOverride"]?.GetValue<string>();

            List<string> recentPaths = new();
            if (upgraded["recentProjectPaths"] is JsonArray recentArray)
            {
                foreach (JsonNode? node in recentArray)
                {
                    if (node is not null)
                    {
                        string val = node.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            recentPaths.Add(val);
                        }
                    }
                }
            }

            ClearReadOnlyNewer(filePath);
            return new SettingsLoadResult(
                new ApplicationSettings(lastProjectPath, recentPaths, nwnInstallOverride),
                SettingsLoadStatus.Loaded,
                QuarantinedPath: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log(
                LogLevel.Error,
                LogCategory,
                "Settings file is unreadable; quarantining it and rebuilding defaults.",
                ex,
                new Dictionary<string, string> { ["path"] = filePath });

            string? quarantinedPath = QuarantineCorruptSettingsFile(filePath);
            ClearReadOnlyNewer(filePath);
            return new SettingsLoadResult(
                new ApplicationSettings(),
                SettingsLoadStatus.RebuiltAfterQuarantine,
                quarantinedPath);
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ApplicationSettings settings, string? overrideFilePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string filePath = ResolveFilePath(overrideFilePath);

        if (settings.IsReadOnly)
        {
            throw new InvalidOperationException(
                "These settings were loaded read-only from a newer schema version and cannot be saved.");
        }

        if (IsReadOnlyNewer(filePath))
        {
            throw new InvalidOperationException(
                $"Settings file '{filePath}' declares a newer schema version and must not be overwritten.");
        }

        string? targetDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        JsonObject rootObj = new JsonObject
        {
            ["schemaVersion"] = SchemaVersions.Settings,
            ["lastProjectPath"] = settings.LastProjectPath,
            ["nwnInstallOverride"] = settings.NwnInstallOverride
        };

        JsonArray recentArray = new JsonArray();
        foreach (string path in settings.RecentProjectPaths)
        {
            recentArray.Add(path);
        }
        rootObj["recentProjectPaths"] = recentArray;

        string tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };

            await using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(fs, rootObj, options, cancellationToken).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Log(
                LogLevel.Error,
                LogCategory,
                "Failed to write settings file; the previous file is left in place.",
                ex,
                new Dictionary<string, string> { ["path"] = filePath, ["tempPath"] = tempPath });

            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception deleteEx)
                {
                    // The write already failed; a stray temp file must not mask the real cause.
                    _logger.Log(
                        LogLevel.Warn,
                        LogCategory,
                        "Failed to delete the settings temp file after a failed write.",
                        deleteEx,
                        new Dictionary<string, string> { ["tempPath"] = tempPath });
                }
            }
            throw;
        }
    }

    private static string ResolveFilePath(string? overrideFilePath)
    {
        return !string.IsNullOrWhiteSpace(overrideFilePath)
            ? Path.GetFullPath(overrideFilePath)
            : DefaultSettingsPath;
    }

    /// <summary>Moves a corrupt settings file aside.</summary>
    /// <param name="filePath">The file to quarantine.</param>
    /// <returns>The quarantine path, or <see langword="null"/> when the move did not happen.</returns>
    private string? QuarantineCorruptSettingsFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            string corruptPath = $"{filePath}.corrupt.{timestamp}";
            File.Move(filePath, corruptPath, overwrite: true);

            _logger.Log(
                LogLevel.Warn,
                LogCategory,
                "Quarantined a corrupt settings file.",
                exception: null,
                data: new Dictionary<string, string> { ["path"] = filePath, ["quarantinedPath"] = corruptPath });

            return corruptPath;
        }
        catch (Exception ex)
        {
            // A failed quarantine must not fail the load: the caller still gets usable defaults.
            _logger.Log(
                LogLevel.Error,
                LogCategory,
                "Failed to quarantine a corrupt settings file; defaults are in use and the file is unchanged.",
                ex,
                new Dictionary<string, string> { ["path"] = filePath });
            return null;
        }
    }

    /// <summary>
    /// Canonicalizes a path for use as a read-only-newer key. Two spellings of the same file — a
    /// relative path and its absolute form, or one containing <c>..</c> — must not disagree about
    /// whether that file may be written, or the guard is bypassed by the way the caller happened to
    /// spell the path.
    /// </summary>
    private static string ReadOnlyNewerKey(string filePath)
    {
        try
        {
            return Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Unresolvable spelling: fall back to the literal so the guard still works for callers
            // that use that same spelling consistently.
            return filePath;
        }
    }

    private void MarkReadOnlyNewer(string filePath)
    {
        string key = ReadOnlyNewerKey(filePath);
        lock (_readOnlyNewerGate)
        {
            _readOnlyNewerPaths.Add(key);
        }
    }

    private void ClearReadOnlyNewer(string filePath)
    {
        string key = ReadOnlyNewerKey(filePath);
        lock (_readOnlyNewerGate)
        {
            _readOnlyNewerPaths.Remove(key);
        }
    }

    private bool IsReadOnlyNewer(string filePath)
    {
        string key = ReadOnlyNewerKey(filePath);
        lock (_readOnlyNewerGate)
        {
            return _readOnlyNewerPaths.Contains(key);
        }
    }
}
