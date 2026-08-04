using System.Text.Json.Nodes;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Services;
using SRN.CC.Core.Startup;
using SRN.CC.Core.Workspace;

namespace SRN.CC.Infrastructure.Startup;

/// <summary>
/// Recovers publication journals left behind by an interrupted publish, and reports what it did.
/// </summary>
/// <remarks>
/// <para>
/// This is the startup-check form of the recovery pass that used to live inline in the desktop
/// application's framework-initialisation callback. The discovery rules and the ordering are
/// unchanged: the working directory is always a candidate; every startup argument that names an
/// existing file contributes its own directory; and a startup argument that names a project file
/// additionally contributes the directory of that project's configured <c>targetHak</c>.
/// </para>
/// <para>
/// Two things changed deliberately.
/// </para>
/// <para>
/// First, the project-file test now matches <c>.srnccproj</c>. The original compared against
/// <c>".srncc"</c>, which no project file has ever been named, so the <c>targetHak</c> branch was
/// unreachable and a publish interrupted while writing to a configured output directory outside the
/// project folder was never recovered.
/// </para>
/// <para>
/// Second, the directories behind <c>RecentProjectPaths</c> are added as candidates. Recovery
/// previously depended entirely on how the application happened to be launched, so a journal in a
/// project the user opens from the recent list was invisible.
/// </para>
/// <para>
/// The three silent <c>catch</c> blocks the original used are now per-item results: every recovered
/// journal and every failed directory is named in the details and logged. No outcome is ever
/// <see cref="StartupCheckSeverity.Blocking"/> — an unrecoverable journal leaves the on-disk
/// artefacts exactly as they were and the user can retry from the build screen.
/// </para>
/// </remarks>
public sealed class PublicationJournalStartupCheck : IStartupCheck
{
    /// <summary>Stable identifier for this check.</summary>
    public const string Id = "publication-journal";

    /// <summary>Log category used by this check.</summary>
    public const string LogCategory = nameof(PublicationJournalStartupCheck);

    /// <summary>
    /// Extension of a project file. The ported original compared against <c>.srncc</c>, which
    /// never matches; see the type-level remarks.
    /// </summary>
    public const string ProjectFileExtension = ".srnccproj";

    private readonly IArtifactPublisher _publisher;
    private readonly IProjectStore _projectStore;
    private readonly IReadOnlyList<string> _startupArgs;
    private readonly Func<IReadOnlyList<string>> _recentProjectPaths;
    private readonly Func<string> _currentDirectory;
    private readonly IAppLogger _logger;

    /// <param name="publisher">Performs the actual journal recovery.</param>
    /// <param name="projectStore">Reads project files named on the command line.</param>
    /// <param name="startupArgs">Command-line arguments, or <see langword="null"/> when there are none.</param>
    /// <param name="recentProjectPaths">
    /// Supplies the recent-project list. Evaluated when the check runs, not when it is constructed,
    /// so it can be wired to a <see cref="SettingsStartupCheck"/> that has not loaded yet.
    /// </param>
    /// <param name="currentDirectory">
    /// Supplies the working directory. Overridable so the check is assertable headlessly without
    /// mutating process-wide state.
    /// </param>
    /// <param name="logger">Optional logger; defaults to the no-op logger.</param>
    public PublicationJournalStartupCheck(
        IArtifactPublisher publisher,
        IProjectStore projectStore,
        IEnumerable<string>? startupArgs = null,
        Func<IReadOnlyList<string>>? recentProjectPaths = null,
        Func<string>? currentDirectory = null,
        IAppLogger? logger = null)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _startupArgs = startupArgs?.ToArray() ?? Array.Empty<string>();
        _recentProjectPaths = recentProjectPaths ?? (static () => Array.Empty<string>());
        _currentDirectory = currentDirectory ?? Directory.GetCurrentDirectory;
        _logger = logger ?? NullAppLogger.Instance;
    }

    /// <inheritdoc />
    public string CheckId => Id;

    /// <inheritdoc />
    public async Task<StartupCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        HashSet<string> recoveryDirectories = new(StringComparer.OrdinalIgnoreCase);
        List<string> details = new();

        AddCurrentDirectory(recoveryDirectories, details);
        await AddStartupArgumentDirectoriesAsync(recoveryDirectories, details, cancellationToken).ConfigureAwait(false);
        AddRecentProjectDirectories(recoveryDirectories, details);

        List<string> recovered = new();
        List<string> failed = new();

        foreach (string journalDirectory in recoveryDirectories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                bool didRecover = await _publisher
                    .RecoverPendingJournalAsync(journalDirectory, cancellationToken)
                    .ConfigureAwait(false);

                if (didRecover)
                {
                    recovered.Add(journalDirectory);
                    details.Add($"recovered: {journalDirectory}");
                    _logger.Log(
                        LogLevel.Warn,
                        LogCategory,
                        $"Recovered an interrupted publication journal in '{journalDirectory}'.",
                        exception: null,
                        data: new Dictionary<string, string>(StringComparer.Ordinal) { ["directory"] = journalDirectory });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add(journalDirectory);
                details.Add($"recovery-failed: {journalDirectory} — {ex.GetType().Name}: {ex.Message}");
                _logger.Log(
                    LogLevel.Error,
                    LogCategory,
                    $"Could not recover the publication journal in '{journalDirectory}'.",
                    ex,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["directory"] = journalDirectory });
            }
        }

        details.Insert(0, $"directories-scanned: {recoveryDirectories.Count}");

        if (failed.Count > 0)
        {
            return new StartupCheckResult(
                Id,
                StartupCheckSeverity.Degraded,
                $"{failed.Count} publication journal(s) could not be recovered. Retry the publish from the build screen.",
                details);
        }

        if (recovered.Count > 0)
        {
            return new StartupCheckResult(
                Id,
                StartupCheckSeverity.Degraded,
                $"Recovered {recovered.Count} interrupted publication journal(s).",
                details);
        }

        return new StartupCheckResult(
            Id,
            StartupCheckSeverity.Ok,
            "No pending publication journals.",
            details);
    }

    private void AddCurrentDirectory(HashSet<string> recoveryDirectories, List<string> details)
    {
        try
        {
            recoveryDirectories.Add(_currentDirectory());
        }
        catch (Exception ex)
        {
            details.Add($"working-directory-unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task AddStartupArgumentDirectoriesAsync(
        HashSet<string> recoveryDirectories,
        List<string> details,
        CancellationToken cancellationToken)
    {
        foreach (string arg in _startupArgs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(arg) || !File.Exists(arg))
            {
                continue;
            }

            try
            {
                string? projectDirectory = Path.GetDirectoryName(arg);
                if (!string.IsNullOrWhiteSpace(projectDirectory))
                {
                    recoveryDirectories.Add(Path.GetFullPath(projectDirectory));
                }
            }
            catch (Exception ex)
            {
                details.Add($"invalid-startup-path: {arg} — {ex.GetType().Name}: {ex.Message}");
            }

            if (!string.Equals(Path.GetExtension(arg), ProjectFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                WorkspaceState project = await _projectStore.LoadAsync(arg, cancellationToken).ConfigureAwait(false);
                AddConfiguredOutputDirectory(project, arg, recoveryDirectories);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                details.Add($"unreadable-project: {arg} — {ex.GetType().Name}: {ex.Message}");
                _logger.Log(
                    LogLevel.Warn,
                    LogCategory,
                    $"Could not read the project '{arg}' while discovering journal directories.",
                    ex,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["projectPath"] = arg });
            }
        }
    }

    private static void AddConfiguredOutputDirectory(
        WorkspaceState project,
        string projectPath,
        HashSet<string> recoveryDirectories)
    {
        if (project.Preferences.OutputSettings is not JsonObject outputSettings)
        {
            return;
        }

        if (outputSettings["targetHak"] is not JsonValue targetHakNode
            || !targetHakNode.TryGetValue(out string? targetHak)
            || string.IsNullOrWhiteSpace(targetHak))
        {
            return;
        }

        string resolvedTargetHak = Path.IsPathFullyQualified(targetHak)
            ? Path.GetFullPath(targetHak)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath) ?? ".", targetHak));

        string? configuredOutputDirectory = Path.GetDirectoryName(resolvedTargetHak);
        if (!string.IsNullOrWhiteSpace(configuredOutputDirectory))
        {
            recoveryDirectories.Add(Path.GetFullPath(configuredOutputDirectory));
        }
    }

    private void AddRecentProjectDirectories(HashSet<string> recoveryDirectories, List<string> details)
    {
        IReadOnlyList<string> recent;
        try
        {
            recent = _recentProjectPaths() ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            details.Add($"recent-projects-unavailable: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        foreach (string recentPath in recent)
        {
            if (string.IsNullOrWhiteSpace(recentPath))
            {
                continue;
            }

            try
            {
                string? recentDirectory = Directory.Exists(recentPath)
                    ? recentPath
                    : Path.GetDirectoryName(recentPath);

                if (!string.IsNullOrWhiteSpace(recentDirectory))
                {
                    recoveryDirectories.Add(Path.GetFullPath(recentDirectory));
                }
            }
            catch (Exception ex)
            {
                details.Add($"invalid-recent-path: {recentPath} — {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
