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
using SRN.CC.Infrastructure.Services;

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

        byte[] rawDocumentBytes = await File.ReadAllBytesAsync(fullProjectPath, cancellationToken).ConfigureAwait(false);
        string jsonContent;
        using (StreamReader reader = new StreamReader(new MemoryStream(rawDocumentBytes), detectEncodingFromByteOrderMarks: true))
        {
            jsonContent = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
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
        if (!isReadOnly && rootObj["sources"] is not JsonArray)
        {
            throw new InvalidOperationException($"Project file '{fullProjectPath}' is missing the required 'sources' JSON array.");
        }

        List<AssetSource> sources = new();
        if (rootObj["sources"] is JsonArray sourcesArray)
        {
            int priorityOrdinal = 0;
            foreach (JsonNode? sourceNode in sourcesArray)
            {
                if (sourceNode is JsonObject sourceObj)
                {
                    Guid id;
                    AssetSourceKind kind;
                    if (!isReadOnly)
                    {
                        // Strict validation for schema 1
                        string? idStr = TryGetString(sourceObj["id"]);
                        if (string.IsNullOrEmpty(idStr) || !Guid.TryParse(idStr, out id))
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' contains invalid source id '{idStr}'.");
                        }
                        string? kindStr = TryGetString(sourceObj["kind"]);
                        if (string.IsNullOrEmpty(kindStr) || !Enum.TryParse<AssetSourceKind>(kindStr, ignoreCase: true, out kind))
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' contains invalid source kind '{kindStr}'.");
                        }
                    }
                    else
                    {
                        // Shape-tolerant extraction for newer schema versions
                        string? idStr = TryGetString(sourceObj["id"]);
                        id = Guid.TryParse(idStr, out Guid parsedId) ? parsedId : Guid.NewGuid();
                        string? kindStr = TryGetString(sourceObj["kind"]);
                        kind = Enum.TryParse<AssetSourceKind>(kindStr, ignoreCase: true, out AssetSourceKind parsedKind)
                            ? parsedKind
                            : AssetSourceKind.Folder;
                    }

                    JsonObject? pathObj = sourceObj["path"] as JsonObject;
                    string pathKind = TryGetString(pathObj?["kind"]) ?? "absolute";
                    string pathVal = TryGetString(pathObj?["value"]) ?? string.Empty;

                    string resolvedPath = pathKind.Equals("relative", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFullPath(Path.Combine(projectDir, pathVal))
                        : (string.IsNullOrWhiteSpace(pathVal) ? fullProjectPath : Path.GetFullPath(pathVal));

                    SourceFingerprint? fingerprint = null;
                    if (sourceObj["fingerprint"] is JsonObject fpObj)
                    {
                        string? fpKindStr = TryGetString(fpObj["kind"]);
                        AssetSourceKind fpKind = Enum.TryParse<AssetSourceKind>(fpKindStr, ignoreCase: true, out AssetSourceKind parsedFpKind) ? parsedFpKind : kind;
                        int algVer = fpObj["algorithmVersion"]?.GetValue<int>() ?? 1;
                        string? digestHex = TryGetString(fpObj["digest"]);
                        byte[] digestBytes = !string.IsNullOrEmpty(digestHex) ? Convert.FromHexString(digestHex) : Array.Empty<byte>();
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
            bool defaultSel = TryGetBool(selObj["defaultSelected"]) ?? true;
            Dictionary<AssetIdentity, bool> overrides = new();

            if (selObj["overrides"] is JsonArray overridesArray)
            {
                foreach (JsonNode? overrideNode in overridesArray)
                {
                    if (overrideNode is JsonObject overrideObj)
                    {
                        string? resref = TryGetString(overrideObj["resref"]);
                        int rawType = TryGetInt(overrideObj["resourceType"]) ?? -1;
                        bool selected = TryGetBool(overrideObj["selected"]) ?? true;
                        if (!string.IsNullOrEmpty(resref) && rawType is >= 0 and <= ushort.MaxValue)
                        {
                            AssetIdentity id = new AssetIdentity(resref, (ushort)rawType);
                            overrides[id] = selected;
                        }
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
                    string? resref = TryGetString(pinObj["resref"]);
                    int rawType = TryGetInt(pinObj["resourceType"]) ?? -1;
                    string? sourceIdStr = TryGetString(pinObj["sourceId"]);
                    string? sha256Hex = TryGetString(pinObj["sha256"]);

                    if (!isReadOnly)
                    {
                        if (string.IsNullOrEmpty(resref) || rawType is < 0 or > ushort.MaxValue)
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' contains invalid pin resref or resourceType.");
                        }
                        if (string.IsNullOrEmpty(sourceIdStr) || !Guid.TryParse(sourceIdStr, out _))
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' contains invalid pin sourceId '{sourceIdStr}'.");
                        }
                        if (string.IsNullOrEmpty(sha256Hex) || sha256Hex.Length != 64)
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' contains invalid pin sha256 hex string.");
                        }
                    }

                    Guid sourceId = Guid.TryParse(sourceIdStr, out Guid parsedSrcId) ? parsedSrcId : Guid.Empty;
                    byte[] pinHash = !string.IsNullOrEmpty(sha256Hex) && sha256Hex.Length == 64 ? Convert.FromHexString(sha256Hex) : new byte[32];

                    JsonObject? locObj = pinObj["locator"] as JsonObject;
                    if (!isReadOnly)
                    {
                        if (locObj is null)
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' pin is missing locator object.");
                        }
                        string? locKindStr = TryGetString(locObj["kind"]);
                        if (string.IsNullOrEmpty(locKindStr))
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' pin locator is missing kind property.");
                        }
                        if (locKindStr.Equals("hakEntry", StringComparison.OrdinalIgnoreCase))
                        {
                            int? indexVal = TryGetInt(locObj["index"]);
                            if (!indexVal.HasValue || indexVal.Value < 0)
                            {
                                throw new InvalidOperationException($"Project file '{fullProjectPath}' hakEntry pin locator missing or invalid index.");
                            }
                        }
                        else if (locKindStr.Equals("folderPath", StringComparison.OrdinalIgnoreCase))
                        {
                            string? relPathStr = TryGetString(locObj["relativePath"]);
                            if (relPathStr is null)
                            {
                                throw new InvalidOperationException($"Project file '{fullProjectPath}' folderPath pin locator missing relativePath.");
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' pin locator has unrecognized kind '{locKindStr}'.");
                        }
                    }

                    string locKind = TryGetString(locObj?["kind"]) ?? "folderPath";
                    OccurrenceLocator locator = locKind.Equals("hakEntry", StringComparison.OrdinalIgnoreCase)
                        ? new HakEntryLocator(TryGetInt(locObj?["index"]) ?? 0)
                        : new FolderFileLocator(TryGetString(locObj?["relativePath"]) ?? string.Empty);

                    if (!string.IsNullOrEmpty(resref) && rawType is >= 0 and <= ushort.MaxValue)
                    {
                        AssetIdentity identity = new AssetIdentity(resref, (ushort)rawType);
                        pins.Add(new WinnerPin(identity, sourceId, locator, pinHash));
                    }
                }
            }
        }

        // Parse preferences
        ProjectPreferences preferences = new ProjectPreferences(
            outputSettings: rootObj["outputSettings"],
            filters: rootObj["filters"],
            comparisonPreferences: rootObj["comparisonPreferences"],
            rawRootNode: rootObj,
            rawDocumentText: jsonContent,
            rawDocumentBytes: rawDocumentBytes);

        // Index sources and resolve workspace
        Dictionary<Guid, SourceIndexSnapshot> snapshots = new();
        List<AssetSource> scannedSources = new();

        foreach (AssetSource s in sources)
        {
            try
            {
                SourceIndexSnapshot snapshot = await _indexService.IndexAsync(s, progress: null, cancellationToken).ConfigureAwait(false);
                if (!snapshot.Source.IsAvailable)
                {
                    AssetSource unavailable = new AssetSource(s.Id, s.Kind, s.FullPath, s.PriorityOrdinal, isAvailable: false, s.Fingerprint);
                    scannedSources.Add(unavailable);
                }
                else
                {
                    AssetSource updatedSource = new AssetSource(s.Id, s.Kind, s.FullPath, s.PriorityOrdinal, isAvailable: true, snapshot.Fingerprint);
                    scannedSources.Add(updatedSource);
                    snapshots[updatedSource.Id] = snapshot;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                AssetSource unavailable = new AssetSource(s.Id, s.Kind, s.FullPath, s.PriorityOrdinal, isAvailable: false, s.Fingerprint);
                scannedSources.Add(unavailable);
            }
        }

        if (_resolver is WorkspaceResolver resolverWithCache)
        {
            foreach (AssetSource s in scannedSources)
            {
                resolverWithCache.HashCache.InvalidateSource(s.Id);
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
            if (state.Preferences.RawDocumentBytes is not null)
            {
                await WriteBytesAtomicallyAsync(fullTargetPath, state.Preferences.RawDocumentBytes, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!string.IsNullOrEmpty(state.Preferences.RawDocumentText))
            {
                await WriteTextAtomicallyAsync(fullTargetPath, state.Preferences.RawDocumentText, cancellationToken).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException("Cannot perform SaveAs on a newer schema read-only project without original document bytes.");
        }

        JsonObject rootObj = SerializeStateToJsonObject(state, fullTargetPath);
        await WriteJsonAtomicallyAsync(fullTargetPath, rootObj, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject SerializeStateToJsonObject(WorkspaceState state, string projectPath)
    {
        string projectDir = Path.GetDirectoryName(projectPath)!;

        JsonObject rootObj = state.Preferences.RawRootNode?.DeepClone() as JsonObject ?? new JsonObject();
        rootObj["schemaVersion"] = CurrentSchemaVersion;

        // Serialize sources overlaying on raw array item nodes
        Dictionary<Guid, JsonObject> rawSourceItems = new();
        if (rootObj["sources"] is JsonArray existingSourcesArray)
        {
            foreach (JsonNode? item in existingSourcesArray)
            {
                if (item is JsonObject obj && TryGetString(obj["id"]) is string idStr && Guid.TryParse(idStr, out Guid parsedId))
                {
                    rawSourceItems[parsedId] = obj.DeepClone().AsObject();
                }
            }
        }

        JsonArray sourcesArray = new JsonArray();
        foreach (AssetSource s in state.Sources)
        {
            JsonObject sourceObj = rawSourceItems.TryGetValue(s.Id, out JsonObject? existingObj)
                ? existingObj
                : new JsonObject();

            sourceObj["id"] = s.Id.ToString();
            sourceObj["kind"] = s.Kind.ToString().ToLowerInvariant();

            JsonObject pathObj = sourceObj["path"]?.AsObject().DeepClone().AsObject() ?? new JsonObject();
            string relPath = GetRelativePathIfContained(projectDir, s.FullPath);
            if (relPath != s.FullPath)
            {
                pathObj["kind"] = "relative";
                pathObj["value"] = relPath.Replace('\\', '/');
            }
            else
            {
                pathObj["kind"] = "absolute";
                pathObj["value"] = s.FullPath.Replace('\\', '/');
            }
            sourceObj["path"] = pathObj;

            if (s.Fingerprint is not null)
            {
                JsonObject fpObj = sourceObj["fingerprint"]?.AsObject().DeepClone().AsObject() ?? new JsonObject();
                fpObj["kind"] = s.Fingerprint.Kind.ToString().ToLowerInvariant();
                fpObj["algorithmVersion"] = s.Fingerprint.AlgorithmVersion;
                fpObj["digest"] = s.Fingerprint.ToHexString().ToLowerInvariant();
                sourceObj["fingerprint"] = fpObj;
            }

            sourcesArray.Add(sourceObj);
        }
        rootObj["sources"] = sourcesArray;

        // Serialize selection state overlaying on raw override nodes
        Dictionary<(string Resref, ushort ResourceType), JsonObject> rawOverrideItems = new();
        if (rootObj["selectionState"] is JsonObject existingSelObj && existingSelObj["overrides"] is JsonArray existingOverridesArray)
        {
            foreach (JsonNode? item in existingOverridesArray)
            {
                if (item is JsonObject obj && obj["resref"]?.GetValue<string>() is string resref && obj["resourceType"]?.GetValue<int>() is int resType)
                {
                    rawOverrideItems[(resref.ToLowerInvariant(), (ushort)resType)] = obj.DeepClone().AsObject();
                }
            }
        }

        JsonObject selObj = rootObj["selectionState"]?.AsObject() ?? new JsonObject();
        selObj["defaultSelected"] = state.SelectionState.DefaultSelected;
        JsonArray overridesArray = new JsonArray();
        foreach (var kvp in state.SelectionState.Overrides)
        {
            JsonObject overrideObj = rawOverrideItems.TryGetValue((kvp.Key.OriginalName.ToLowerInvariant(), kvp.Key.ResourceType), out JsonObject? existingObj)
                ? existingObj
                : new JsonObject();

            overrideObj["resref"] = kvp.Key.OriginalName;
            overrideObj["resourceType"] = kvp.Key.ResourceType;
            overrideObj["selected"] = kvp.Value;
            overridesArray.Add(overrideObj);
        }
        selObj["overrides"] = overridesArray;
        rootObj["selectionState"] = selObj;

        // Serialize pins overlaying on raw pin nodes
        Dictionary<(string Resref, ushort ResourceType), JsonObject> rawPinItems = new();
        if (rootObj["pins"] is JsonArray existingPinsArray)
        {
            foreach (JsonNode? item in existingPinsArray)
            {
                if (item is JsonObject obj && obj["resref"]?.GetValue<string>() is string resref && obj["resourceType"]?.GetValue<int>() is int resType)
                {
                    rawPinItems[(resref.ToLowerInvariant(), (ushort)resType)] = obj.DeepClone().AsObject();
                }
            }
        }

        JsonArray pinsArray = new JsonArray();
        foreach (WinnerPin pin in state.Pins)
        {
            JsonObject pinObj = rawPinItems.TryGetValue((pin.Identity.OriginalName.ToLowerInvariant(), pin.Identity.ResourceType), out JsonObject? existingObj)
                ? existingObj
                : new JsonObject();

            pinObj["resref"] = pin.Identity.OriginalName;
            pinObj["resourceType"] = pin.Identity.ResourceType;
            pinObj["sourceId"] = pin.SourceId.ToString();
            pinObj["sha256"] = Convert.ToHexString(pin.PinHash).ToLowerInvariant();

            JsonObject locObj = pinObj["locator"]?.AsObject().DeepClone().AsObject() ?? new JsonObject();
            if (pin.Locator is HakEntryLocator hakLoc)
            {
                locObj["kind"] = "hakEntry";
                locObj["index"] = hakLoc.EntryIndex;
            }
            else if (pin.Locator is FolderFileLocator folderLoc)
            {
                locObj["kind"] = "folderPath";
                locObj["relativePath"] = folderLoc.NormalizedRelativePath;
            }
            pinObj["locator"] = locObj;

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

    private static async Task WriteTextAtomicallyAsync(string targetPath, string text, CancellationToken cancellationToken)
    {
        string? targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            await using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (StreamWriter writer = new StreamWriter(fs, System.Text.Encoding.UTF8))
            {
                await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task WriteBytesAtomicallyAsync(string targetPath, byte[] bytes, CancellationToken cancellationToken)
    {
        string? targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            await using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await fs.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
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

    private static string? TryGetString(JsonNode? node)
    {
        if (node is JsonValue val && val.TryGetValue(out string? s))
        {
            return s;
        }
        return null;
    }

    private static bool? TryGetBool(JsonNode? node)
    {
        if (node is JsonValue val && val.TryGetValue(out bool b))
        {
            return b;
        }
        return null;
    }

    private static int? TryGetInt(JsonNode? node)
    {
        if (node is JsonValue val && val.TryGetValue(out int i))
        {
            return i;
        }
        return null;
    }
}
