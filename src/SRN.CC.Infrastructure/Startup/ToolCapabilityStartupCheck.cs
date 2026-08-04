using System.Runtime.InteropServices;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Install;

namespace SRN.CC.Infrastructure.Startup;

/// <summary>
/// Reports what this process can actually do: which runtime it is, which native libraries resolved,
/// whether a game install was discovered, and how much room is left for the cache.
/// </summary>
/// <remarks>
/// <para>
/// This check answers the first question every support conversation asks, and it answers it from a
/// log line rather than from a guess. Everything it reports is cheap and passive.
/// </para>
/// <para>
/// GPU capability is deliberately <em>not</em> probed. Creating a GL context at startup would
/// contradict the rendering design's deferred-initialisation rule and would turn a driver bug into
/// a launch failure, so the check reports the deferral explicitly and lets the first 3D preview
/// slot discover the truth.
/// </para>
/// <para>
/// A missing native library is a named <see cref="StartupCheckSeverity.Degraded"/> detail, never a
/// failure: the feature that needs it will fail loudly at the point of use, and reporting it here
/// turns "the app does nothing when I click preview" into "libSkiaSharp.dll is not next to the
/// executable".
/// </para>
/// </remarks>
public sealed class ToolCapabilityStartupCheck : IStartupCheck
{
    /// <summary>Stable identifier for this check.</summary>
    public const string Id = "tool-capability";

    /// <summary>Log category used by this check.</summary>
    public const string LogCategory = nameof(ToolCapabilityStartupCheck);

    /// <summary>The fixed GPU detail line. See the type-level remarks for why nothing is probed.</summary>
    public const string GpuDeferredDetail = "render-gpu: Deferred — probed on first 3D slot";

    /// <summary>Detail emitted in place of install discovery when not running on Windows.</summary>
    public const string InstallDiscoveryUnavailableDetail =
        "nwn-install: install discovery unavailable on this platform";

    /// <summary>Relative probe directory searched after the base directory.</summary>
    public const string NativeProbeSubdirectory = @"runtimes\win-x64\native";

    /// <summary>Native libraries whose presence is reported.</summary>
    public static IReadOnlyList<string> ProbedNativeLibraries { get; } = new[]
    {
        "e_sqlite3.dll",
        "av_libglesv2.dll",
        "libSkiaSharp.dll",
        "libHarfBuzzSharp.dll"
    };

    private readonly AppPaths _paths;
    private readonly string _baseDirectory;
    private readonly IAppLogger _logger;

    /// <param name="paths">Supplies the root whose free space is reported.</param>
    /// <param name="baseDirectory">
    /// Directory probed for native libraries. Defaults to <see cref="AppContext.BaseDirectory"/>;
    /// overridable so the check is assertable headlessly against a temp directory.
    /// </param>
    /// <param name="logger">Optional logger; defaults to the no-op logger.</param>
    public ToolCapabilityStartupCheck(AppPaths paths, string? baseDirectory = null, IAppLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        _logger = logger ?? NullAppLogger.Instance;
    }

    /// <inheritdoc />
    public string CheckId => Id;

    /// <inheritdoc />
    public Task<StartupCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<string> details = new()
        {
            $"runtime-identifier: {RuntimeInformation.RuntimeIdentifier}",
            $"process-architecture: {RuntimeInformation.ProcessArchitecture}",
            $"os-architecture: {RuntimeInformation.OSArchitecture}",
            $"os-description: {RuntimeInformation.OSDescription}",
            $"framework: {RuntimeInformation.FrameworkDescription}",
            $"base-directory: {_baseDirectory}"
        };

        bool degraded = false;

        List<string> missingNatives = new();
        foreach (string nativeName in ProbedNativeLibraries)
        {
            string? found = ProbeNative(nativeName);
            if (found is null)
            {
                missingNatives.Add(nativeName);
                details.Add($"native-missing: {nativeName}");
                degraded = true;
            }
            else
            {
                details.Add($"native: {nativeName} -> {found}");
            }
        }

        degraded |= AddInstallDiscovery(details);
        degraded |= AddFreeSpace(details);

        details.Add(GpuDeferredDetail);

        string summary = degraded
            ? BuildDegradedSummary(missingNatives)
            : "Runtime capabilities verified.";

        StartupCheckResult result = new(
            Id,
            degraded ? StartupCheckSeverity.Degraded : StartupCheckSeverity.Ok,
            summary,
            details);

        _logger.Log(
            degraded ? LogLevel.Warn : LogLevel.Info,
            LogCategory,
            summary,
            exception: null,
            data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runtimeIdentifier"] = RuntimeInformation.RuntimeIdentifier,
                ["baseDirectory"] = _baseDirectory,
                ["missingNatives"] = string.Join(";", missingNatives)
            });

        return Task.FromResult(result);
    }

    private static string BuildDegradedSummary(IReadOnlyList<string> missingNatives) =>
        missingNatives.Count > 0
            ? $"Reduced capability: {missingNatives.Count} native library file(s) not found ({string.Join(", ", missingNatives)})."
            : "Reduced capability: see details.";

    private string? ProbeNative(string nativeName)
    {
        // Base directory first — a self-contained publish flattens the runtime assets — then the
        // RID-specific probe path a framework-dependent build leaves them in.
        foreach (string directory in new[] { _baseDirectory, Path.Combine(_baseDirectory, NativeProbeSubdirectory) })
        {
            try
            {
                string candidate = Path.Combine(directory, nativeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // An unusable probe directory is indistinguishable from an empty one here.
            }
        }

        return null;
    }

    private static bool AddInstallDiscovery(List<string> details)
    {
        if (!OperatingSystem.IsWindows())
        {
            // NwnInstallLocator is [SupportedOSPlatform("windows")]; calling it unguarded is a
            // hard build break under TreatWarningsAsErrors, and a registry probe is meaningless
            // off Windows anyway.
            details.Add(InstallDiscoveryUnavailableDetail);
            return true;
        }

        try
        {
            NwnInstallLocation location = new NwnInstallLocator().Locate();
            if (location.IsValid)
            {
                details.Add($"nwn-install: {location.InstallRoot}");
                details.Add($"nwn-base-key: {location.NwnBaseKeyPath}");
                return false;
            }

            details.Add("nwn-install: not found");
            details.Add($"nwn-install-candidates: {location.AutoDiscoveredCandidates.Count}");
            return true;
        }
        catch (Exception ex)
        {
            details.Add($"nwn-install: discovery failed — {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private bool AddFreeSpace(List<string> details)
    {
        try
        {
            string fullRoot = Path.GetFullPath(_paths.Root);
            string? driveRoot = Path.GetPathRoot(fullRoot);
            if (string.IsNullOrWhiteSpace(driveRoot))
            {
                details.Add($"data-root-free-space: unavailable (no drive root for '{fullRoot}')");
                return true;
            }

            DriveInfo drive = new(driveRoot);
            long freeBytes = drive.AvailableFreeSpace;
            details.Add($"data-root: {fullRoot}");
            details.Add($"data-root-free-space: {freeBytes} bytes ({freeBytes / (1024d * 1024d * 1024d):F2} GiB) on {driveRoot}");
            return false;
        }
        catch (Exception ex)
        {
            details.Add($"data-root-free-space: unavailable — {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }
}
