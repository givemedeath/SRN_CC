using System.Text.Json;
using System.Text.Json.Nodes;
using SRN.CC.App.ViewModels;
using SRN.CC.App.Views;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Settings;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Build;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Catalog;
using SRN.CC.Infrastructure.Install;
using SRN.CC.Infrastructure.Logging;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Infrastructure.Startup;
using SRN.CC.Preview;
using SRN.CC.Preview.Render;

namespace SRN.CC.App.Services;

/// <summary>
/// The application's composition root. Owns every long-lived service instance, runs the startup
/// preflight, and hands back a fully wired <see cref="MainWindowViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// This exists instead of a dependency-injection container: adding one would mean a new NuGet
/// package, a reviewed policy entry, and seven regenerated lock files, for a graph of roughly
/// fifteen hand-constructed singletons that has never needed lifetime management.
/// </para>
/// <para>
/// The point of moving the graph out of the framework-initialisation callback is that
/// <see cref="CreateAsync"/> can run headlessly against an <see cref="AppPaths"/> pointing at a
/// temporary directory. That is how the preflight is asserted without a window, and it is what the
/// <c>--srncc-preflight-only</c> smoke mode drives.
/// </para>
/// <para>
/// <see cref="CreateAsync"/> never throws for an environment reason. Every failure a fresh machine
/// can present — an unwritable data directory, a corrupt cache, a settings file from a newer build,
/// an interrupted publish, a missing game install — is a preflight result, not an exception.
/// </para>
/// <para>
/// <strong>Exactly one <see cref="SettingsStore"/> is constructed here and shared by everything.</strong>
/// The store's read-only-newer write guard is per-instance state keyed by path: it remembers which
/// paths were last loaded as newer-than-this-build and refuses to write them. A second store over
/// the same path would not have observed that load, so a caller handing it fresh defaults could
/// overwrite a future build's settings. There is one instance, and there must stay one.
/// </para>
/// </remarks>
public sealed class AppServices : IAsyncDisposable
{
    /// <summary>Log category for records the composition root itself emits.</summary>
    public const string LogCategory = nameof(AppServices);

    /// <summary>Version of the <c>--srncc-preflight-only</c> JSON document shape.</summary>
    public const int StartupReportJsonSchemaVersion = 1;

    private readonly AppLogger _logger;
    private readonly SqliteCacheService _cache;
    private readonly IWorkspaceService _workspaceService;
    private readonly IProjectStore _projectStore;
    private readonly IBuildOrchestrator _buildOrchestrator;
    private readonly IArtifactPublisher _artifactPublisher;
    private readonly PreviewEngine _previewEngine;
    private readonly IResourceTypeRegistry _registry;
    private readonly ISourceReaderDispatcher _dispatcher;
    private readonly TextureSourceHolder _textureSourceHolder;
    private readonly IBaseGameResourceCatalog? _baseGameCatalog;
    private readonly ModelSceneCache _modelSceneCache;

    private bool _disposed;

    private AppServices(
        AppPaths paths,
        AppLogger logger,
        ObservableLogSink observableLogSink,
        StartupReport startupReport,
        ApplicationSettings settings,
        ISettingsStore settingsStore,
        SqliteCacheService cache,
        IWorkspaceService workspaceService,
        IProjectStore projectStore,
        IBuildOrchestrator buildOrchestrator,
        IArtifactPublisher artifactPublisher,
        PreviewEngine previewEngine,
        IResourceTypeRegistry registry,
        ISourceReaderDispatcher dispatcher,
        TextureSourceHolder textureSourceHolder,
        IBaseGameResourceCatalog? baseGameCatalog,
        ModelSceneCache modelSceneCache)
    {
        Paths = paths;
        _logger = logger;
        ObservableLogSink = observableLogSink;
        StartupReport = startupReport;
        Settings = settings;
        SettingsStore = settingsStore;
        _cache = cache;
        _workspaceService = workspaceService;
        _projectStore = projectStore;
        _buildOrchestrator = buildOrchestrator;
        _artifactPublisher = artifactPublisher;
        _previewEngine = previewEngine;
        _registry = registry;
        _dispatcher = dispatcher;
        _textureSourceHolder = textureSourceHolder;
        _baseGameCatalog = baseGameCatalog;
        _modelSceneCache = modelSceneCache;
    }

    /// <summary>The per-user data layout this composition is rooted at.</summary>
    public AppPaths Paths { get; }

    /// <summary>The application logger every composed service was given.</summary>
    public IAppLogger Logger => _logger;

    /// <summary>The UI sink, attached to the operation log by <see cref="CreateMainWindowViewModel"/>.</summary>
    public ObservableLogSink ObservableLogSink { get; }

    /// <summary>The completed startup preflight. Always has one result per shipped check.</summary>
    public StartupReport StartupReport { get; }

    /// <summary>
    /// Settings as the preflight loaded them. Taken from <see cref="SettingsStartupCheck.LoadResult"/>
    /// rather than by loading the file a second time, so quarantine and read-only-newer are observed
    /// exactly once.
    /// </summary>
    public ApplicationSettings Settings { get; }

    /// <summary>The single settings store. See the type-level remarks for why there is only one.</summary>
    public ISettingsStore SettingsStore { get; }

    /// <summary>
    /// Whether a base-game KEY/BIF catalog was discovered and opened. Null when no valid install was
    /// found, which is a supported configuration, not a failure.
    /// </summary>
    public bool HasBaseGameCatalog => _baseGameCatalog is not null;

    /// <summary>
    /// Builds the whole service graph and runs the startup preflight.
    /// </summary>
    /// <param name="paths">Where per-user data lives. Pass a temporary root to run headlessly.</param>
    /// <param name="startupArgs">Process command-line arguments; used for journal recovery.</param>
    /// <param name="cancellationToken">Cancels the preflight, not the construction.</param>
    public static async Task<AppServices> CreateAsync(
        AppPaths paths,
        IReadOnlyList<string> startupArgs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        startupArgs ??= Array.Empty<string>();

        var observableLogSink = new ObservableLogSink();
        AppLogger logger = CreateLogger(paths, observableLogSink);

        var registry = new ResourceTypeRegistry();
        var cache = new SqliteCacheService(paths.CacheDatabasePath, SqliteCacheService.DefaultMaxLogicalBytes, logger);
        var indexService = new AssetIndexService(cache, registry);
        var dispatcher = new SourceReaderDispatcher(typeRegistry: registry);
        var hashService = new StreamingHashService(dispatcher);
        var resolver = new WorkspaceResolver(hashService);
        var workspaceService = new WorkspaceService(indexService, resolver);
        var projectStore = new ProjectStore(indexService, resolver, logger);

        // One instance, deliberately. See the type-level remarks.
        var settingsStore = new SettingsStore(logger);

        var packer = new AssetPacker(dispatcher);
        var verifier = new BuildVerifier();
        var manifestGenerator = new ProvenanceManifestGenerator(registry);
        var publisher = new ArtifactPublisher(logger);
        var orchestrator = new BuildOrchestrator(
            packer, verifier, manifestGenerator, publisher, registry, dispatcher, logger);

        var cacheCheck = new CacheStartupCheck(cache, paths, logger);
        var settingsCheck = new SettingsStartupCheck(settingsStore, paths, logger);

        // Order is a contract: the checks run sequentially, and the journal check's recent-project
        // lambda is evaluated when it runs, so it observes the list the settings check just loaded.
        var journalCheck = new PublicationJournalStartupCheck(
            publisher,
            projectStore,
            startupArgs,
            () => settingsCheck.RecentProjectPaths,
            currentDirectory: null,
            logger: logger);
        var toolCheck = new ToolCapabilityStartupCheck(paths, baseDirectory: null, logger: logger);

        StartupReport report = await StartupPreflight
            .RunAsync(new IStartupCheck?[] { cacheCheck, settingsCheck, journalCheck, toolCheck }, logger, cancellationToken)
            .ConfigureAwait(false);

        ApplicationSettings settings = settingsCheck.LoadResult?.Settings ?? new ApplicationSettings();

        IBaseGameResourceCatalog? baseGameCatalog = TryCreateBaseGameCatalog(settings, logger);

        // MdlSceneBuilder/ModelSceneCache are process-lifetime singletons. The texture-source
        // accessor is late-bound over textureSourceHolder.Current: this provider array is built
        // before MainWindowViewModel exists, so the holder is the shared mutable slot both sides
        // close over.
        var mdlSceneBuilder = new MdlSceneBuilder();
        var modelSceneCache = new ModelSceneCache();
        var textureSourceHolder = new TextureSourceHolder();

        // A cached RenderScene has whichever ITextureSource was current at build time baked into its
        // resolved textures, and the cache key deliberately does not include texture-source identity.
        textureSourceHolder.Changed += modelSceneCache.Clear;

        var previewEngine = new PreviewEngine(
            dispatcher,
            new IPreviewProvider[]
            {
                new MetadataPreviewProvider(registry),
                new ImagePreviewProvider(registry),
                new TextPreviewProvider(registry),
                new AudioPreviewProvider(registry),
                new TreePreviewProvider(registry),
                new MdlPreviewProvider(registry, mdlSceneBuilder, modelSceneCache, () => textureSourceHolder.Current),
                new BoundedHexPreviewProvider()
            },
            new SqlitePreviewThumbnailCache(cache));

        logger.Log(
            LogLevel.Info,
            LogCategory,
            "Application services composed.",
            exception: null,
            data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["root"] = paths.Root,
                ["worstStartupSeverity"] = report.Worst.ToString(),
                ["baseGameCatalog"] = baseGameCatalog is null ? "absent" : "loaded"
            });

        return new AppServices(
            paths,
            logger,
            observableLogSink,
            report,
            settings,
            settingsStore,
            cache,
            workspaceService,
            projectStore,
            orchestrator,
            publisher,
            previewEngine,
            registry,
            dispatcher,
            textureSourceHolder,
            baseGameCatalog,
            modelSceneCache);
    }

    /// <summary>
    /// Builds the main view model, attaches the UI log sink, and replays the startup report into the
    /// operation log so the user sees the preflight outcome on first launch.
    /// </summary>
    public MainWindowViewModel CreateMainWindowViewModel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var viewModel = new MainWindowViewModel(
            _workspaceService,
            _projectStore,
            SettingsStore,
            _buildOrchestrator,
            _artifactPublisher,
            _previewEngine,
            _registry,
            _dispatcher,
            _textureSourceHolder,
            _baseGameCatalog,
            _logger,
            Settings,
            Paths.SettingsPath);

        // Attach before wiring the logger, or the first entry the view model writes would be routed
        // to a logger whose sink cannot yet reach it.
        ObservableLogSink.Attach(viewModel.OperationLog);
        viewModel.OperationLog.Logger = _logger;

        // The 3D control is constructed by the XAML runtime and has no constructor seam.
        ModelViewportControl.Logger = _logger;

        viewModel.ReplayStartupReport(StartupReport);
        return viewModel;
    }

    /// <summary>
    /// Renders a startup report as the JSON document <c>--srncc-preflight-only</c> writes to stdout.
    /// </summary>
    /// <remarks>
    /// The shape is a contract consumed by the clean-machine smoke script, which parses it and
    /// asserts every check ran and nothing is blocking. Field names are camelCase; severities are
    /// their enum names; <c>details</c> is always an array, never null; <c>code</c> is the diagnostic
    /// code name or null.
    /// </remarks>
    public static string SerializeStartupReport(StartupReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var results = new JsonArray();
        foreach (StartupCheckResult result in report.Results)
        {
            var details = new JsonArray();
            foreach (string detail in result.Details ?? Array.Empty<string>())
            {
                details.Add(JsonValue.Create(detail));
            }

            results.Add(new JsonObject
            {
                ["checkId"] = result.CheckId,
                ["severity"] = result.Severity.ToString(),
                ["summary"] = result.Summary,
                ["details"] = details,
                ["code"] = result.Code is { } code ? code.ToString() : null
            });
        }

        var document = new JsonObject
        {
            ["schemaVersion"] = StartupReportJsonSchemaVersion,
            ["hasBlocking"] = report.HasBlocking,
            ["worst"] = report.Worst.ToString(),
            ["results"] = results
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        _textureSourceHolder.Changed -= _modelSceneCache.Clear;
        Dispose(ObservableLogSink);
        Dispose(_cache);
        Dispose(_baseGameCatalog as IDisposable);

        // The logger last: everything above may log while it shuts down.
        Dispose(_logger);

        return ValueTask.CompletedTask;
    }

    private static void Dispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception)
        {
            // Shutdown is best-effort; a failing dispose must not mask the reason we are shutting
            // down, and there is by definition nowhere left to report it.
        }
    }

    /// <summary>
    /// Builds the logger. A data directory that refuses the log folder degrades to the UI sink
    /// alone rather than failing startup — the file log is a diagnostic, not a dependency.
    /// </summary>
    private static AppLogger CreateLogger(AppPaths paths, ObservableLogSink observableLogSink)
    {
        var sinks = new List<ILogSink>();

        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            sinks.Add(new JsonLineLogSink(paths.LogDirectory));
        }
        catch (Exception)
        {
            // No file sink this session.
        }

        sinks.Add(observableLogSink);
        return new AppLogger(sinks);
    }

    /// <summary>
    /// Discovers and opens the base-game KEY/BIF catalog, honouring the configured install override.
    /// Absence is normal: the application curates HAK sources and only uses the catalog to resolve
    /// textures a workspace does not itself carry.
    /// </summary>
    private static IBaseGameResourceCatalog? TryCreateBaseGameCatalog(ApplicationSettings settings, IAppLogger logger)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            NwnInstallLocation location = new NwnInstallLocator().Locate(settings.NwnInstallOverride);
            if (!location.IsValid || string.IsNullOrWhiteSpace(location.InstallRoot))
            {
                logger.Log(
                    LogLevel.Info,
                    LogCategory,
                    "No valid game install was found; base-game texture fallback is unavailable this session.");
                return null;
            }

            var catalog = new BaseGameResourceCatalog(location.InstallRoot, location.NwnBaseKeyPath);
            logger.Log(
                LogLevel.Info,
                LogCategory,
                $"Base-game resource catalog loaded from '{location.InstallRoot}'.");
            return catalog;
        }
        catch (Exception ex)
        {
            logger.Log(
                LogLevel.Warn,
                LogCategory,
                "Could not load the base-game resource catalog; texture fallback is unavailable this session.",
                ex);
            return null;
        }
    }
}
