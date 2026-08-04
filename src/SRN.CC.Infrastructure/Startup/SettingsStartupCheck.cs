using SRN.CC.Core.Logging;
using SRN.CC.Core.Services;
using SRN.CC.Core.Settings;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Logging;

namespace SRN.CC.Infrastructure.Startup;

/// <summary>
/// Loads the settings document during preflight and reports how it was obtained.
/// </summary>
/// <remarks>
/// <para>
/// The load happens here, once, and the outcome is published on <see cref="LoadResult"/> so the
/// rest of startup can consume the same settings instance instead of re-reading the file. This is
/// what makes settings observable at all: before Milestone 7 the three distinct outcomes below
/// were erased into an indistinguishable <c>ApplicationSettings</c>.
/// </para>
/// <para>
/// No outcome is ever <see cref="StartupCheckSeverity.Blocking"/> — defaults are always usable.
/// </para>
/// </remarks>
public sealed class SettingsStartupCheck : IStartupCheck
{
    /// <summary>Stable identifier for this check.</summary>
    public const string Id = "settings";

    /// <summary>Log category used by this check.</summary>
    public const string LogCategory = nameof(SettingsStartupCheck);

    private readonly ISettingsStore _store;
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;

    /// <param name="store">The settings store to read from.</param>
    /// <param name="paths">Supplies the settings path named in the details.</param>
    /// <param name="logger">Optional logger; defaults to the no-op logger.</param>
    public SettingsStartupCheck(ISettingsStore store, AppPaths paths, IAppLogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullAppLogger.Instance;
    }

    /// <inheritdoc />
    public string CheckId => Id;

    /// <summary>
    /// The load outcome, or <see langword="null"/> before <see cref="RunAsync"/> has completed.
    /// Composition roots read this instead of calling <c>LoadAsync</c> a second time.
    /// </summary>
    public SettingsLoadResult? LoadResult { get; private set; }

    /// <summary>
    /// The recent-project paths from <see cref="LoadResult"/>, or an empty list before the check
    /// has run. Shaped for direct use as
    /// <see cref="PublicationJournalStartupCheck"/>'s recent-path provider.
    /// </summary>
    public IReadOnlyList<string> RecentProjectPaths =>
        LoadResult?.Settings.RecentProjectPaths ?? Array.Empty<string>();

    /// <inheritdoc />
    public async Task<StartupCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        string settingsPath = _paths.SettingsPath;

        SettingsLoadResult loaded = await _store.LoadAsync(settingsPath, cancellationToken).ConfigureAwait(false);
        LoadResult = loaded;

        StartupCheckResult result = loaded.Status switch
        {
            SettingsLoadStatus.ReadOnlyNewer => new StartupCheckResult(
                Id,
                StartupCheckSeverity.Degraded,
                "The settings file was written by a newer build. Defaults are in use and settings are read-only this session; the file on disk is untouched.",
                new[]
                {
                    $"settings: {settingsPath}",
                    "effect: preference changes will not be saved.",
                    "remedy: use the newer build, or move the file aside yourself if you want this build to own it."
                }),

            SettingsLoadStatus.RebuiltAfterQuarantine => new StartupCheckResult(
                Id,
                StartupCheckSeverity.Degraded,
                "The settings file was unreadable. It has been moved aside and defaults were rebuilt.",
                new[]
                {
                    $"settings: {settingsPath}",
                    loaded.QuarantinedPath is { Length: > 0 } quarantined
                        ? $"quarantined-to: {quarantined}"
                        : "quarantined-to: (the file could not be moved aside; it may be overwritten on the next save)",
                    "effect: previous preferences and the recent-project list are gone."
                }),

            _ => new StartupCheckResult(
                Id,
                StartupCheckSeverity.Ok,
                "Settings loaded.",
                new[]
                {
                    $"settings: {settingsPath}",
                    $"recent-projects: {loaded.Settings.RecentProjectPaths.Count}"
                })
        };

        _logger.Log(
            result.Severity == StartupCheckSeverity.Ok ? LogLevel.Info : LogLevel.Warn,
            LogCategory,
            result.Summary,
            exception: null,
            data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["settings"] = settingsPath,
                ["status"] = loaded.Status.ToString(),
                ["quarantinedPath"] = loaded.QuarantinedPath ?? string.Empty
            });

        return result;
    }
}
