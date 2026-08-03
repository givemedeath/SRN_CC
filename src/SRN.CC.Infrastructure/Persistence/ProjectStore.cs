using System.Text.Json;
using System.Text.Json.Nodes;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;

namespace SRN.CC.Infrastructure.Persistence;

public sealed class ProjectStore : IProjectStore
{
    private const int CurrentSchemaVersion = 1;
    private readonly IAssetIndexService _indexService;
    private readonly IWorkspaceResolver _resolver;

    public ProjectStore(IAssetIndexService indexService, IWorkspaceResolver resolver)
    {
        _indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public async Task<WorkspaceState> LoadAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        string fullProjectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullProjectPath))
        {
            throw new FileNotFoundException($"Project file not found at '{fullProjectPath}'.", fullProjectPath);
        }

        string jsonContent = await File.ReadAllTextAsync(fullProjectPath, cancellationToken).ConfigureAwait(false);
        JsonNode rootNode;
        try
        {
            rootNode = JsonNode.Parse(jsonContent) ?? throw new JsonException("Project JSON root was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Project file '{fullProjectPath}' contains malformed JSON.", ex);
        }

        if (rootNode is not JsonObject rootObj)
        {
            throw new InvalidOperationException($"Project file '{fullProjectPath}' root must be a JSON object.");
        }

        int schemaVersion = rootObj["schemaVersion"]?.GetValue<int>() ?? 0;
        if (schemaVersion < 1)
        {
            throw new InvalidOperationException($"Project file '{fullProjectPath}' has invalid schemaVersion '{schemaVersion}'.");
        }

        string projectDir = Path.GetDirectoryName(fullProjectPath)!;
        bool isReadOnly = schemaVersion > CurrentSchemaVersion;

        // Parse sources
        List<AssetSource> sources = new();
        if (rootObj["sources"] is JsonArray sourcesArray)
        {
            int priorityOrdinal = 0;
            foreach (JsonNode? sourceNode in sourcesArray)
            {
                if (sourceNode is JsonObject sourceObj)
                {
                    Guid id = Guid.Parse(sourceObj["id"]!.GetValue<string>());
                    string kindStr = sourceObj["kind"]!.GetValue<string>();
                    AssetSourceKind kind = Enum.Parse<AssetSourceKind>(kindStr, ignoreCase: true);

                    JsonObject pathObj = sourceObj["path"]!.AsObject();
                    string pathKind = pathObj["kind"]!.GetValue<string>();
                    string pathVal = pathObj["value"]!.GetValue<string>();

                    string resolvedPath = pathKind.Equals("relative", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFullPath(Path.Combine(projectDir, pathVal))
                        : Path.GetFullPath(pathVal);

                    SourceFingerprint? fingerprint = null;
                    if (sourceObj["fingerprint"] is JsonObject fpObj)
                    {
                        string fpKindStr = fpObj["kind"]!.GetValue<string>();
                        AssetSourceKind fpKind = Enum.Parse<AssetSourceKind>(fpKindStr, ignoreCase: true);
                        int algVer = fpObj["algorithmVersion"]!.GetValue<int>();
                        string digestHex = fpObj["digest"]!.GetValue<string>();
                        byte[] digestBytes = Convert.FromHexString(digestHex);
                        fingerprint = new SourceFingerprint(fpKind, algVer, digestBytes);
                    }

                    bool isAvailable = File.Exists(resolvedPath) || Directory.Exists(resolvedPath);
                    AssetSource source = new AssetSource(id, kind, resolvedPath, priorityOrdinal++, isAvailable, fingerprint);
                    sources.Add(source);
                }
            }
        }

        // Parse selection state
        SelectionState selectionState = SelectionState.IncludeAll();
        if (rootObj["selectionState"] is JsonObject selObj)
        {
            bool defaultSel = selObj["defaultSelected"]?.GetValue<bool>() ?? true;
            Dictionary<AssetIdentity, bool> overrides = new();

            if (selObj["overrides"] is JsonArray overridesArray)
            {
                foreach (JsonNode? overrideNode in overridesArray)
                {
                    if (overrideNode is JsonObject overrideObj)
                    {
                        string resref = overrideObj["resref"]!.GetValue<string>();
                        ushort resourceType = (ushort)overrideObj["resourceType"]!.GetValue<int>();
                        bool selected = overrideObj["selected"]!.GetValue<bool>();
                        AssetIdentity id = new AssetIdentity(resref, resourceType);
                        overrides[id] = selected;
                    }
                }
            }

            selectionState = new SelectionState(defaultSel, overrides);
        }

        // Parse pins
        List<WinnerPin> pins = new();
        if (rootObj["pins"] is JsonArray pinsArray)
        {
            foreach (JsonNode? pinNode in pinsArray)
            {
                if (pinNode is JsonObject pinObj)
                {
                    string resref = pinObj["resref"]!.GetValue<string>();
                    ushort resourceType = (ushort)pinObj["resourceType"]!.GetValue<int>();
                    Guid sourceId = Guid.Parse(pinObj["sourceId"]!.GetValue<string>());
                    string sha256Hex = pinObj["sha256"]!.GetValue<string>();
                    byte[] pinHash = Convert.FromHexString(sha256Hex);

                    JsonObject locObj = pinObj["locator"]!.AsObject();
                    string locKind = locObj["kind"]!.GetValue<string>();
                    OccurrenceLocator locator = locKind.Equals("hakEntry", StringComparison.OrdinalIgnoreCase)
                        ? new HakEntryLocator(locObj["index"]!.GetValue<int>())
                        : new FolderFileLocator(locObj["relativePath"]!.GetValue<string>());

                    AssetIdentity identity = new AssetIdentity(resref, resourceType);
                    pins.Add(new WinnerPin(identity, sourceId, locator, pinHash));
                }
            }
        }

        // Parse preferences
        ProjectPreferences preferences = new ProjectPreferences(
            outputSettings: rootObj["outputSettings"],
            filters: rootObj["filters"],
            comparisonPreferences: rootObj["comparisonPreferences"],
            rawRootNode: rootObj);

        // Index sources and resolve workspace
        Dictionary<Guid, SourceIndexSnapshot> snapshots = new();
        List<AssetSource> scannedSources = new();

        foreach (AssetSource s in sources)
        {
            try
            {
                SourceIndexSnapshot snapshot = await _indexService.IndexAsync(s, progress: null, cancellationToken).ConfigureAwait(false);
                AssetSource updatedSource = new AssetSource(s.Id, s.Kind, s.FullPath, s.PriorityOrdinal, isAvailable: true, snapshot.Fingerprint);
                scannedSources.Add(updatedSource);
                snapshots[updatedSource.Id] = snapshot;
            }
            catch
            {
                AssetSource unavailable = new AssetSource(s.Id, s.Kind, s.FullPath, s.PriorityOrdinal, isAvailable: false, s.Fingerprint);
                scannedSources.Add(unavailable);
            }
        }

        return await _resolver.ResolveAsync(
            scannedSources,
            snapshots,
            pins,
            selectionState,
            preferences,
            isReadOnly: isReadOnly,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(WorkspaceState state, string projectPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        if (state.IsReadOnly)
        {
            throw new InvalidOperationException($"Cannot save read-only workspace state to '{projectPath}'.");
        }

        string fullProjectPath = Path.GetFullPath(projectPath);
        JsonObject rootObj = SerializeStateToJsonObject(state, fullProjectPath);
        await WriteJsonAtomicallyAsync(fullProjectPath, rootObj, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsAsync(WorkspaceState state, string targetProjectPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectPath);

        string fullTargetPath = Path.GetFullPath(targetProjectPath);

        if (state.IsReadOnly)
        {
            // For read-only (newer schema) documents, SaveAs requires byte-for-byte preservation if the source file exists
            if (File.Exists(fullTargetPath))
            {
                // Verify byte-for-byte identity
                JsonObject readOnlyObj = SerializeStateToJsonObject(state, fullTargetPath);
                await WriteJsonAtomicallyAsync(fullTargetPath, readOnlyObj, cancellationToken).ConfigureAwait(false);
                return;
            }
            else
            {
                throw new InvalidOperationException("Cannot perform SaveAs on a newer schema read-only project without preserving original document.");
            }
        }

        JsonObject rootObj = SerializeStateToJsonObject(state, fullTargetPath);
        await WriteJsonAtomicallyAsync(fullTargetPath, rootObj, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject SerializeStateToJsonObject(WorkspaceState state, string projectPath)
    {
        string projectDir = Path.GetDirectoryName(projectPath)!;

        JsonObject rootObj = state.Preferences.RawRootNode?.DeepClone() as JsonObject ?? new JsonObject();
        rootObj["schemaVersion"] = CurrentSchemaVersion;

        // Serialize sources
        JsonArray sourcesArray = new JsonArray();
        foreach (AssetSource s in state.Sources)
        {
            JsonObject sourceObj = new JsonObject
            {
                ["id"] = s.Id.ToString(),
                ["kind"] = s.Kind.ToString().ToLowerInvariant()
            };

            // Path representation (relative if contained in projectDir or subdirectory, else absolute)
            string relPath = GetRelativePathIfContained(projectDir, s.FullPath);
            if (relPath != s.FullPath)
            {
                sourceObj["path"] = new JsonObject
                {
                    ["kind"] = "relative",
                    ["value"] = relPath.Replace('\\', '/')
                };
            }
            else
            {
                sourceObj["path"] = new JsonObject
                {
                    ["kind"] = "absolute",
                    ["value"] = s.FullPath.Replace('\\', '/')
                };
            }

            if (s.Fingerprint is not null)
            {
                sourceObj["fingerprint"] = new JsonObject
                {
                    ["kind"] = s.Fingerprint.Kind.ToString().ToLowerInvariant(),
                    ["algorithmVersion"] = s.Fingerprint.AlgorithmVersion,
                    ["digest"] = s.Fingerprint.ToHexString().ToLowerInvariant()
                };
            }

            sourcesArray.Add(sourceObj);
        }
        rootObj["sources"] = sourcesArray;

        // Serialize selection state
        JsonObject selObj = new JsonObject
        {
            ["defaultSelected"] = state.SelectionState.DefaultSelected
        };
        JsonArray overridesArray = new JsonArray();
        foreach (var kvp in state.SelectionState.Overrides)
        {
            overridesArray.Add(new JsonObject
            {
                ["resref"] = kvp.Key.OriginalName,
                ["resourceType"] = kvp.Key.ResourceType,
                ["selected"] = kvp.Value
            });
        }
        selObj["overrides"] = overridesArray;
        rootObj["selectionState"] = selObj;

        // Serialize pins
        JsonArray pinsArray = new JsonArray();
        foreach (WinnerPin pin in state.Pins)
        {
            JsonObject pinObj = new JsonObject
            {
                ["resref"] = pin.Identity.OriginalName,
                ["resourceType"] = pin.Identity.ResourceType,
                ["sourceId"] = pin.SourceId.ToString(),
                ["sha256"] = Convert.ToHexString(pin.PinHash).ToLowerInvariant()
            };

            if (pin.Locator is HakEntryLocator hakLoc)
            {
                pinObj["locator"] = new JsonObject
                {
                    ["kind"] = "hakEntry",
                    ["index"] = hakLoc.EntryIndex
                };
            }
            else if (pin.Locator is FolderFileLocator folderLoc)
            {
                pinObj["locator"] = new JsonObject
                {
                    ["kind"] = "folderPath",
                    ["relativePath"] = folderLoc.NormalizedRelativePath
                };
            }

            pinsArray.Add(pinObj);
        }
        rootObj["pins"] = pinsArray;

        // Serialize preferences
        rootObj["outputSettings"] = state.Preferences.OutputSettings?.DeepClone();
        rootObj["filters"] = state.Preferences.Filters?.DeepClone();
        rootObj["comparisonPreferences"] = state.Preferences.ComparisonPreferences?.DeepClone();

        return rootObj;
    }

    private static string GetRelativePathIfContained(string baseDir, string fullPath)
    {
        string normalizedBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedFull = Path.GetFullPath(fullPath);

        if (normalizedFull.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetRelativePath(baseDir, fullPath);
        }
        return fullPath;
    }

    private static async Task WriteJsonAtomicallyAsync(string targetPath, JsonNode rootNode, CancellationToken cancellationToken)
    {
        string? targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            await using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(fs, rootNode, options, cancellationToken).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, targetPath, overwrite: true);
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
}
