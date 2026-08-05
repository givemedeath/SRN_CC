using SRN.CC.Core.Logging;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Logging;

namespace SRN.CC.Infrastructure.Startup;

/// <summary>
/// Runs a list of <see cref="IStartupCheck"/> instances and aggregates their outcomes into a
/// <see cref="StartupReport"/>.
/// </summary>
/// <remarks>
/// <para>
/// The runner is the reason a startup check is safe to write casually: every check is invoked
/// inside its own <c>try</c>, so a check that throws, returns <see langword="null"/>, or observes
/// cancellation degrades to a <see cref="StartupCheckSeverity.Degraded"/> result instead of
/// reaching the application entry point. A run therefore always produces exactly one result per
/// supplied check, in the order the checks were supplied.
/// </para>
/// <para>
/// Checks run sequentially rather than concurrently. Ordering is a contract, not an accident:
/// a later check may read state an earlier one produced — the publication-journal check consumes
/// the recent-project list that the settings check loaded.
/// </para>
/// </remarks>
public static class StartupPreflight
{
    /// <summary>Log category used for every record the runner itself emits.</summary>
    public const string LogCategory = "StartupPreflight";

    /// <summary>
    /// <see cref="StartupCheckResult.CheckId"/> substituted when a supplied check is
    /// <see langword="null"/> or its <see cref="IStartupCheck.CheckId"/> could not be read.
    /// </summary>
    public const string UnknownCheckId = "unknown-check";

    /// <summary>
    /// Runs every supplied check, never propagating a failure out of any of them.
    /// </summary>
    /// <param name="checks">The checks to run, in order. A <see langword="null"/> entry yields a degraded result.</param>
    /// <param name="logger">Receives one record per produced result.</param>
    /// <param name="cancellationToken">
    /// Passed to each check. Cancellation degrades the individual check rather than aborting the
    /// run, so the report still describes every check that was asked for.
    /// </param>
    public static async Task<StartupReport> RunAsync(
        IEnumerable<IStartupCheck?> checks,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        IAppLogger log = logger ?? NullAppLogger.Instance;

        List<StartupCheckResult> results = new();

        foreach (IStartupCheck? check in checks)
        {
            StartupCheckResult result = await RunOneAsync(check, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            try
            {
                LogResult(log, result);
            }
            catch (Exception)
            {
                // The whole point of this type is that nothing here can stop the application from
                // starting. A logger is contractually forbidden from throwing, but honouring that
                // contract is not something the preflight can verify about an injected instance, and
                // reporting a result must never be more dangerous than producing it.
            }
        }

        return new StartupReport(results);
    }

    private static async Task<StartupCheckResult> RunOneAsync(
        IStartupCheck? check,
        CancellationToken cancellationToken)
    {
        if (check is null)
        {
            return new StartupCheckResult(
                UnknownCheckId,
                StartupCheckSeverity.Degraded,
                "A null startup check was supplied and was skipped.",
                new[] { "The preflight check list contained a null entry." });
        }

        string checkId = UnknownCheckId;
        try
        {
            string? rawId = check.CheckId;
            if (!string.IsNullOrWhiteSpace(rawId))
            {
                checkId = rawId;
            }

            Task<StartupCheckResult>? pending = check.RunAsync(cancellationToken);
            if (pending is null)
            {
                return NoResult(checkId, "The check returned a null Task.");
            }

            StartupCheckResult? result = await pending.ConfigureAwait(false);
            if (result is null)
            {
                return NoResult(checkId, "The check completed but produced a null StartupCheckResult.");
            }

            return result;
        }
        catch (Exception ex)
        {
            return new StartupCheckResult(
                checkId,
                StartupCheckSeverity.Degraded,
                $"Startup check '{checkId}' failed: {ex.GetType().Name}: {ex.Message}",
                new[]
                {
                    $"exception-type: {ex.GetType().FullName}",
                    $"exception-message: {ex.Message}"
                });
        }
    }

    private static StartupCheckResult NoResult(string checkId, string detail) =>
        new(
            checkId,
            StartupCheckSeverity.Degraded,
            $"Startup check '{checkId}' returned no result.",
            new[] { detail });

    private static void LogResult(IAppLogger logger, StartupCheckResult result)
    {
        Dictionary<string, string> data = new(StringComparer.Ordinal)
        {
            ["checkId"] = result.CheckId,
            ["severity"] = result.Severity.ToString()
        };

        if (result.Code.HasValue)
        {
            data[AppLogger.EventCodeKey] = result.Code.Value.ToString();
        }

        IReadOnlyList<string>? details = result.Details;
        if (details is not null)
        {
            for (int i = 0; i < details.Count; i++)
            {
                data[$"detail{i}"] = details[i] ?? string.Empty;
            }
        }

        logger.Log(SeverityToLevel(result.Severity), LogCategory, result.Summary, exception: null, data: data);
    }

    private static LogLevel SeverityToLevel(StartupCheckSeverity severity) => severity switch
    {
        StartupCheckSeverity.Ok => LogLevel.Info,
        StartupCheckSeverity.Degraded => LogLevel.Warn,
        _ => LogLevel.Error
    };
}
