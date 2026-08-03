using System.Text.Json;
using System.Text.Json.Nodes;
using SRN.CC.Core.Services;
using SRN.CC.Core.Settings;

namespace SRN.CC.Infrastructure.Persistence;

public sealed class SettingsStore : ISettingsStore
{
    private const int CurrentSchemaVersion = 1;

    public static string DefaultSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRN.CC", "settings.json");

    public async Task<ApplicationSettings> LoadAsync(string? overrideFilePath = null, CancellationToken cancellationToken = default)
    {
        string filePath = ResolveFilePath(overrideFilePath);
        if (!File.Exists(filePath))
        {
            return new ApplicationSettings();
        }

        try
        {
            string jsonContent = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            JsonNode rootNode = JsonNode.Parse(jsonContent) ?? throw new JsonException("Settings JSON root was null.");
            JsonObject rootObj = rootNode.AsObject();

            int schemaVersion = rootObj["schemaVersion"]?.GetValue<int>() ?? 0;
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"Unsupported settings schema version {schemaVersion}.");
            }

            string? lastProjectPath = rootObj["lastProjectPath"]?.GetValue<string>();
            string? nwnInstallOverride = rootObj["nwnInstallOverride"]?.GetValue<string>();

            List<string> recentPaths = new();
            if (rootObj["recentProjectPaths"] is JsonArray recentArray)
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

            return new ApplicationSettings(lastProjectPath, recentPaths, nwnInstallOverride);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Quarantine corrupt settings file
            QuarantineCorruptSettingsFile(filePath);
            return new ApplicationSettings();
        }
    }

    public async Task SaveAsync(ApplicationSettings settings, string? overrideFilePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string filePath = ResolveFilePath(overrideFilePath);
        string? targetDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        JsonObject rootObj = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
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
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
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

    private static void QuarantineCorruptSettingsFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string corruptPath = $"{filePath}.corrupt.{timestamp}";
            File.Move(filePath, corruptPath, overwrite: true);
        }
        catch
        {
            // Ignore quarantine move failures
        }
    }
}
