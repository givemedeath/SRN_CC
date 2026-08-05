namespace SRN.CC.Core.Startup;

/// <summary>
/// A single startup preflight probe. Implementations report their outcome as a
/// <see cref="StartupCheckResult"/>; the runner that hosts them treats a thrown
/// exception as a degradation rather than letting it reach startup.
/// </summary>
public interface IStartupCheck
{
    string CheckId { get; }

    Task<StartupCheckResult> RunAsync(CancellationToken cancellationToken = default);
}
