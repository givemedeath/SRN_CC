using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Logging;

namespace SRN.CC.Infrastructure.Startup;

/// <summary>
/// Reports whether the SQLite index/preview cache opened cleanly.
/// </summary>
/// <remarks>
/// The cache is a rebuildable derived artefact, so no outcome here is ever
/// <see cref="StartupCheckSeverity.Blocking"/>. There are three distinguishable states and the
/// middle one is the easy one to get wrong: a <em>successful</em> quarantine leaves
/// <see cref="ISqliteCacheService.IsAvailable"/> true while
/// <see cref="ISqliteCacheService.LastQuarantineReason"/> is non-null. The application recovered,
/// but the user lost their index and their next open will re-scan from scratch — that is a
/// degradation they are entitled to be told about, not a silent success.
/// </remarks>
public sealed class CacheStartupCheck : IStartupCheck
{
    /// <summary>Stable identifier for this check.</summary>
    public const string Id = "cache";

    /// <summary>Log category used by this check.</summary>
    public const string LogCategory = nameof(CacheStartupCheck);

    private readonly ISqliteCacheService _cache;
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;

    /// <param name="cache">The cache service whose open state is being reported.</param>
    /// <param name="paths">Supplies the database path named in the details.</param>
    /// <param name="logger">Optional logger; defaults to the no-op logger.</param>
    public CacheStartupCheck(ISqliteCacheService cache, AppPaths paths, IAppLogger? logger = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullAppLogger.Instance;
    }

    /// <inheritdoc />
    public string CheckId => Id;

    /// <inheritdoc />
    public Task<StartupCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool available = _cache.IsAvailable;
        string? reason = _cache.LastQuarantineReason;
        string databasePath = _paths.CacheDatabasePath;

        StartupCheckResult result;

        if (!available)
        {
            result = new StartupCheckResult(
                Id,
                StartupCheckSeverity.Degraded,
                "The asset cache could not be opened or rebuilt. Indexing and previews run without a cache this session.",
                new[]
                {
                    $"database: {databasePath}",
                    $"quarantine-reason: {reason ?? "(none reported)"}",
                    "effect: every index and preview lookup is a miss; nothing is persisted."
                },
                DiagnosticCode.CorruptedCacheQuarantined);
        }
        else if (!string.IsNullOrWhiteSpace(reason))
        {
            result = new StartupCheckResult(
                Id,
                StartupCheckSeverity.Degraded,
                "The asset cache was corrupt, has been moved aside, and was rebuilt empty. The first index of each source will be slow.",
                new[]
                {
                    $"database: {databasePath}",
                    $"quarantine-reason: {reason}",
                    "effect: the cache is usable again but starts empty."
                },
                DiagnosticCode.CorruptedCacheQuarantined);
        }
        else
        {
            result = new StartupCheckResult(
                Id,
                StartupCheckSeverity.Ok,
                "Asset cache opened.",
                new[] { $"database: {databasePath}" });
        }

        _logger.Log(
            result.Severity == StartupCheckSeverity.Ok ? LogLevel.Info : LogLevel.Warn,
            LogCategory,
            result.Summary,
            exception: null,
            data: result.Code.HasValue
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AppLogger.EventCodeKey] = result.Code.Value.ToString(),
                    ["database"] = databasePath
                }
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["database"] = databasePath });

        return Task.FromResult(result);
    }
}
