using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Schema;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Logging;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Infrastructure.Persistence;

public sealed class ProjectStore : IProjectStore
{
    private const string LogCategory = nameof(ProjectStore);

    /// <summary>
    /// No project migrations exist at schema version 1, so this pipeline is an identity pass. It is
    /// wired into the load path anyway — and its <em>output</em>, not the parsed input, is what the
    /// rest of the load reads — so the first real migration is a new class and a registration rather
    /// than a change to this store.
    /// </summary>
    private static readonly SchemaMigrationPipeline Migrations =
        new(SchemaKind.Project, Array.Empty<IJsonSchemaMigration>());

    private readonly IAssetIndexService _indexService;
    private readonly IWorkspaceResolver _resolver;
    private readonly IAppLogger _logger;

    /// <summary>Creates a store.</summary>
    /// <param name="indexService">Indexer used to scan each source during load.</param>
    /// <param name="resolver">Resolver that turns indexed sources into a workspace.</param>
    /// <param name="logger">
    /// Optional sink for the temp-file and schema events this store used to swallow. Trailing and
    /// optional so no existing call site changes.
    /// </param>
    public ProjectStore(IAssetIndexService indexService, IWorkspaceResolver resolver, IAppLogger? logger = null)
    {
        _indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? NullAppLogger.Instance;
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
        SchemaOpenMode openMode = SchemaVersions.Classify(SchemaKind.Project, schemaVersion);
        if (openMode == SchemaOpenMode.Unsupported)
        {
            // Projects are never quarantined (PLAN.md:96 scopes that to settings and cache): the
            // file is left exactly as found and the caller is told which path failed.
            _logger.Log(
                LogLevel.Error,
                LogCategory,
                "Project file declares an unsupported schema version; refusing to open and leaving the file untouched.",
                exception: null,
                data: new Dictionary<string, string>
                {
                    [AppLogger.EventCodeKey] = "project.schema.unsupported",
                    ["path"] = fullProjectPath,
                    ["fileSchemaVersion"] = schemaVersion.ToString(CultureInfo.InvariantCulture),
                    ["supportedSchemaVersion"] = SchemaVersions.Project.ToString(CultureInfo.InvariantCulture)
                });

            throw new InvalidOperationException($"Project file '{fullProjectPath}' has invalid schemaVersion '{schemaVersion}'.");
        }

        string projectDir = Path.GetDirectoryName(fullProjectPath)!;
        bool isReadOnly = openMode == SchemaOpenMode.ReadOnlyNewer;

        // Bring the document forward before a single field is read, so a future migration is seen by
        // validation and projection alike. A newer-than-current document is not upgradeable and is
        // read best-effort from exactly what was on disk.
        JsonObject documentObj = rootObj;
        if (!isReadOnly)
        {
            if (!Migrations.TryUpgrade(
                    rootObj,
                    schemaVersion,
                    out JsonObject upgraded,
                    out IReadOnlyList<string> applied,
                    out string? migrationError))
            {
                _logger.Log(
                    LogLevel.Error,
                    LogCategory,
                    "Project file could not be brought forward to the current schema version.",
                    exception: null,
                    data: new Dictionary<string, string>
                    {
                        [AppLogger.EventCodeKey] = "project.schema.migrationFailed",
                        ["path"] = fullProjectPath,
                        ["fileSchemaVersion"] = schemaVersion.ToString(CultureInfo.InvariantCulture),
                        ["error"] = migrationError ?? string.Empty
                    });

                throw new InvalidOperationException(
                    migrationError
                        ?? $"Project file '{fullProjectPath}' declares schemaVersion '{schemaVersion}' and could not be upgraded.");
            }

            documentObj = upgraded;
            documentObj["schemaVersion"] = SchemaVersions.Project;

            if (applied.Count > 0)
            {
                _logger.Log(
                    LogLevel.Info,
                    LogCategory,
                    "Applied project schema migrations.",
                    exception: null,
                    data: new Dictionary<string, string>
                    {
                        [AppLogger.EventCodeKey] = "project.schema.migrated",
                        ["path"] = fullProjectPath,
                        ["migrations"] = string.Join(", ", applied)
                    });
            }
        }

        // Parse sources
        if (!isReadOnly && documentObj["sources"] is not JsonArray)
        {
            throw new InvalidOperationException($"Project file '{fullProjectPath}' is missing the required 'sources' JSON array.");
        }

        List<AssetSource> sources = new();
        if (documentObj["sources"] is JsonArray sourcesArray)
        {
            int priorityOrdinal = 0;
            foreach (JsonNode? sourceNode in sourcesArray)
            {
                if (sourceNode is not JsonObject sourceObj)
                {
                    if (!isReadOnly)
                    {
                        throw new InvalidOperationException($"Project file '{fullProjectPath}' source array item must be a JSON object.");
                    }
                    continue;
                }

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
                if (!isReadOnly)
                {
                    if (pathObj is null)
                    {
                        throw new InvalidOperationException($"Project file '{fullProjectPath}' source is missing required 'path' object.");
                    }
                    string? pathKindStr = TryGetString(pathObj["kind"]);
                    string? pathValStr = TryGetString(pathObj["value"]);
                    if (string.IsNullOrEmpty(pathKindStr) || (!pathKindStr.Equals("absolute", StringComparison.OrdinalIgnoreCase) && !pathKindStr.Equals("relative", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException($"Project file '{fullProjectPath}' source path has invalid or missing kind '{pathKindStr}'.");
                    }
                    if (string.IsNullOrWhiteSpace(pathValStr))
                    {
                        throw new InvalidOperationException($"Project file '{fullProjectPath}' source path has missing or empty value.");
                    }
                }

                string pathKind = TryGetString(pathObj?["kind"]) ?? "absolute";
                string pathVal = TryGetString(pathObj?["value"]) ?? string.Empty;

                string resolvedPath = pathKind.Equals("relative", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFullPath(Path.Combine(projectDir, pathVal))
                    : (string.IsNullOrWhiteSpace(pathVal) ? fullProjectPath : Path.GetFullPath(pathVal));

                SourceFingerprint? fingerprint = null;
                if (sourceObj["fingerprint"] is JsonObject fpObj)
                {
                    string? fpKindStr = TryGetString(fpObj["kind"]);
                    int? algVerVal = TryGetInt(fpObj["algorithmVersion"]);
                    string? digestHex = TryGetString(fpObj["digest"]);

                    if (!isReadOnly)
                    {
                        if (string.IsNullOrEmpty(fpKindStr) || !Enum.TryParse<AssetSourceKind>(fpKindStr, ignoreCase: true, out _))
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' fingerprint contains invalid kind '{fpKindStr}'.");
                        }
                        if (!algVerVal.HasValue || algVerVal.Value < 1)
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' fingerprint contains invalid algorithmVersion '{algVerVal}'.");
                        }
                        if (string.IsNullOrEmpty(digestHex) || digestHex.Length % 2 != 0 || !TryFromHexString(digestHex, out _))
                        {
                            throw new InvalidOperationException($"Project file '{fullProjectPath}' fingerprint contains invalid digest hex string.");
                        }
                    }

                    try
                    {
                        AssetSourceKind fpKind = Enum.TryParse<AssetSourceKind>(fpKindStr, ignoreCase: true, out AssetSourceKind parsedFpKind) ? parsedFpKind : kind;
                        int algVer = algVerVal ?? 1;
                        byte[] digestBytes = !string.IsNullOrEmpty(digestHex) && TryFromHexString(digestHex, out byte[] parsedDigest) ? parsedDigest : Array.Empty<byte>();
                        fingerprint = new SourceFingerprint(fpKind, algVer, digestBytes);
                    }
                    catch
                    {
                        if (!isReadOnly)
                        {
                            throw;
                        }
                        fingerprint = null;
                    }
                }
                else if (!isReadOnly && sourceObj["fingerprint"] is not null)
                {
                    throw new InvalidOperationException($"Project file '{fullProjectPath}' fingerprint must be a JSON object.");
                }

                // Per-source mode is an additive, optional field (default Full). Absent -> Full;
                // present-but-unparseable throws on the strict path, tolerated as Full read-only.
                SourceMode mode = SourceMode.Full;
                string? modeStr = TryGetString(sourceObj["mode"]);
                if (!string.IsNullOrEmpty(modeStr))
                {
                    if (Enum.TryParse<SourceMode>(modeStr, ignoreCase: true, out SourceMode parsedMode))
                    {
                        mode = parsedMode;
                    }
                    else if (!isReadOnly)
                    {
                        throw new InvalidOperationException($"Project file '{fullProjectPath}' contains invalid source mode '{modeStr}'.");
                    }
                }

                bool isAvailable = File.Exists(resolvedPath) || Directory.Exists(resolvedPath);
                AssetSource source = new AssetSource(id, kind, resolvedPath, priorityOrdinal++, isAvailable, fingerprint, mode);
                sources.Add(source);
            }
        }

        // Parse selection state
        if (!isReadOnly && documentObj["selectionState"] is not JsonObject)
        {
            throw new InvalidOperationException($"Project file '{fullProjectPath}' is missing required 'selectionState' object.");
        }

        SelectionState selectionState = SelectionState.IncludeAll();
        if (documentObj["selectionState"] is JsonObject selObj)
        {
            if (!isReadOnly)
            {
                if (selObj["defaultSelected"] is null || TryGetBool(selObj["defaultSelected"]) is null)
                {
                    throw new InvalidOperationException($"Project file '{fullProjectPath}' selectionState missing valid defaultSelected boolean.");
                }
                if (selObj["overrides"] is not JsonArray)
                {
                    throw new InvalidOperationException($"Project file '{fullProjectPath}' selectionState missing required 'overrides' array.");
                }
            }

            bool defaultSel = TryGetBool(selObj["defaultSelected"]) ?? true;
            Dictionary<AssetIdentity, bool> overrides = new();

            if (selObj["overrides"] is JsonArray overridesArray)
            {
                foreach (JsonNode? overrideNode in overridesArray)
                {
                    if (overrideNode is JsonObject overrideObj)
                    {
                        string? resref = TryGetString(overrideObj["resref"]);
                        int? rawType = TryGetInt(overrideObj["resourceType"]);
                        bool? selected = TryGetBool(overrideObj["selected"]);

                        if (!isReadOnly)
                        {
                            if (string.IsNullOrEmpty(resref) || !rawType.HasValue || rawType.Value is < 0 or > ushort.MaxValue || !selected.HasValue)
                            {
                                throw new InvalidOperationException($"Project file '{fullProjectPath}' contains invalid selection override entry.");
                            }
                        }

                        if (!string.IsNullOrEmpty(resref) && rawType is >= 0 and <= ushort.MaxValue && selected.HasValue)
                        {
                            AssetIdentity id = new AssetIdentity(resref, (ushort)rawType.Value);
                            overrides[id] = selected.Value;
                        }
                    }
                    else if (!isReadOnly)
                    {
                        throw new InvalidOperationException($"Project file '{fullProjectPath}' selection override entry must be a JSON object.");
                    }
                }
            }

            selectionState = new SelectionState(defaultSel, overrides);
        }

        // Parse pins
        if (!isReadOnly && documentObj["pins"] is not JsonArray)
        {
            throw new InvalidOperationException($"Project file '{fullProjectPath}' is missing required 'pins' JSON array.");
        }

        List<WinnerPin> pins = new();
        if (documentObj["pins"] is JsonArray pinsArray)
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
                    byte[]? pinHash = null;
                    if (!string.IsNullOrEmpty(sha256Hex) && sha256Hex.Length == 64 && TryFromHexString(sha256Hex, out byte[] parsedHash))
                    {
                        pinHash = parsedHash;
                    }

                    if (!isReadOnly && pinHash is null)
                    {
                        throw new InvalidOperationException($"Project file '{fullProjectPath}' contains invalid pin sha256 hex string.");
                    }

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
                    OccurrenceLocator? locator = null;
                    if (locKind.Equals("hakEntry", StringComparison.OrdinalIgnoreCase))
                    {
                        int? idx = TryGetInt(locObj?["index"]);
                        if (idx.HasValue && idx.Value >= 0)
                        {
                            locator = new HakEntryLocator(idx.Value);
                        }
                    }
                    else if (locKind.Equals("folderPath", StringComparison.OrdinalIgnoreCase))
                    {
                        string? relPath = TryGetString(locObj?["relativePath"]);
                        if (relPath is not null)
                        {
                            locator = new FolderFileLocator(relPath);
                        }
                    }

                    if (pinHash is not null && locator is not null && !string.IsNullOrEmpty(resref) && rawType is >= 0 and <= ushort.MaxValue)
                    {
                        AssetIdentity identity = new AssetIdentity(resref, (ushort)rawType);
                        pins.Add(new WinnerPin(identity, sourceId, locator, pinHash));
                    }
                }
                else if (!isReadOnly)
                {
                    throw new InvalidOperationException($"Project file '{fullProjectPath}' pin array item must be a JSON object.");
                }
            }
        }

        // Parse preferences
        ProjectPreferences preferences = new ProjectPreferences(
            outputSettings: documentObj["outputSettings"],
            filters: documentObj["filters"],
            comparisonPreferences: documentObj["comparisonPreferences"],
            rawRootNode: documentObj,
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
                    AssetSource unavailable = s with { IsAvailable = false };
                    scannedSources.Add(unavailable);
                }
                else
                {
                    AssetSource updatedSource = s with { IsAvailable = true, Fingerprint = snapshot.Fingerprint };
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
                AssetSource unavailable = s with { IsAvailable = false };
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
        rootObj["schemaVersion"] = SchemaVersions.Project;

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
            sourceObj["mode"] = s.Mode.ToString().ToLowerInvariant();

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
        Dictionary<AssetIdentity, JsonObject> rawOverrideItems = new();
        if (rootObj["selectionState"] is JsonObject existingSelObj && existingSelObj["overrides"] is JsonArray existingOverridesArray)
        {
            foreach (JsonNode? item in existingOverridesArray)
            {
                if (item is JsonObject obj && TryGetString(obj["resref"]) is string resref && TryGetInt(obj["resourceType"]) is int resType && resType is >= 0 and <= ushort.MaxValue)
                {
                    AssetIdentity id = new AssetIdentity(resref, (ushort)resType);
                    rawOverrideItems[id] = obj.DeepClone().AsObject();
                }
            }
        }

        JsonObject selObj = rootObj["selectionState"]?.AsObject() ?? new JsonObject();
        selObj["defaultSelected"] = state.SelectionState.DefaultSelected;
        JsonArray overridesArray = new JsonArray();
        foreach (var kvp in state.SelectionState.Overrides)
        {
            JsonObject overrideObj = rawOverrideItems.TryGetValue(kvp.Key, out JsonObject? existingObj)
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
        Dictionary<AssetIdentity, JsonObject> rawPinItems = new();
        if (rootObj["pins"] is JsonArray existingPinsArray)
        {
            foreach (JsonNode? item in existingPinsArray)
            {
                if (item is JsonObject obj && TryGetString(obj["resref"]) is string resref && TryGetInt(obj["resourceType"]) is int resType && resType is >= 0 and <= ushort.MaxValue)
                {
                    AssetIdentity id = new AssetIdentity(resref, (ushort)resType);
                    rawPinItems[id] = obj.DeepClone().AsObject();
                }
            }
        }

        JsonArray pinsArray = new JsonArray();
        foreach (WinnerPin pin in state.Pins)
        {
            JsonObject pinObj = rawPinItems.TryGetValue(pin.Identity, out JsonObject? existingObj)
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

    private async Task WriteJsonAtomicallyAsync(string targetPath, JsonNode rootNode, CancellationToken cancellationToken)
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
        catch (Exception ex)
        {
            LogAtomicWriteFailure("json", targetPath, tempPath, ex);
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception deleteEx)
                {
                    LogTempFileCleanupFailure("json", targetPath, tempPath, deleteEx);
                }
            }
            throw;
        }
    }

    private async Task WriteTextAtomicallyAsync(string targetPath, string text, CancellationToken cancellationToken)
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
        catch (Exception ex)
        {
            LogAtomicWriteFailure("text", targetPath, tempPath, ex);
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception deleteEx)
                {
                    LogTempFileCleanupFailure("text", targetPath, tempPath, deleteEx);
                }
            }
            throw;
        }
    }

    private async Task WriteBytesAtomicallyAsync(string targetPath, byte[] bytes, CancellationToken cancellationToken)
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
        catch (Exception ex)
        {
            LogAtomicWriteFailure("bytes", targetPath, tempPath, ex);
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception deleteEx)
                {
                    LogTempFileCleanupFailure("bytes", targetPath, tempPath, deleteEx);
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Records an atomic-write failure. The exception is rethrown by the caller; this only makes the
    /// failure visible after the fact, including the temp path that may have been left behind.
    /// </summary>
    private void LogAtomicWriteFailure(string writer, string targetPath, string tempPath, Exception ex)
    {
        _logger.Log(
            LogLevel.Error,
            LogCategory,
            "Atomic project write failed; the target file is unchanged.",
            ex,
            new Dictionary<string, string>
            {
                [AppLogger.EventCodeKey] = "project.write.failed",
                ["writer"] = writer,
                ["targetPath"] = targetPath,
                ["tempPath"] = tempPath
            });
    }

    /// <summary>
    /// Records a failed temp-file cleanup. Swallowed by design — the original write failure is the
    /// one worth propagating — but a stranded <c>.tmp.*</c> sibling is exactly the kind of leak that
    /// used to be invisible.
    /// </summary>
    private void LogTempFileCleanupFailure(string writer, string targetPath, string tempPath, Exception ex)
    {
        _logger.Log(
            LogLevel.Warn,
            LogCategory,
            "Could not delete the temporary file left by a failed project write.",
            ex,
            new Dictionary<string, string>
            {
                [AppLogger.EventCodeKey] = "project.tempFile.cleanupFailed",
                ["writer"] = writer,
                ["targetPath"] = targetPath,
                ["tempPath"] = tempPath
            });
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

    private bool TryFromHexString(string? hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
        {
            return false;
        }
        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (Exception ex)
        {
            // A malformed digest is a validation outcome, not a crash: the caller decides whether to
            // reject the document (schema 1) or degrade gracefully (newer, read-only).
            _logger.Log(
                LogLevel.Debug,
                LogCategory,
                "Project file contains a value that is not a valid hex string.",
                ex,
                new Dictionary<string, string>
                {
                    [AppLogger.EventCodeKey] = "project.parse.invalidHex",
                    ["length"] = hex.Length.ToString(CultureInfo.InvariantCulture)
                });

            return false;
        }
    }
}
