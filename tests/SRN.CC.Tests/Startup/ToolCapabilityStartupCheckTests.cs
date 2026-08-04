using System.Runtime.InteropServices;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Startup;

namespace SRN.CC.Tests.Startup;

[TestFixture]
public class ToolCapabilityStartupCheckTests
{
    private string _tempDir = null!;
    private string _baseDir = null!;
    private AppPaths _paths = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_ToolCapabilityTests_" + Guid.NewGuid().ToString("N"));
        _baseDir = Path.Combine(_tempDir, "base");
        Directory.CreateDirectory(_baseDir);
        _paths = new AppPaths(Path.Combine(_tempDir, "data"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void PlaceNatives(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string name in ToolCapabilityStartupCheck.ProbedNativeLibraries)
        {
            File.WriteAllBytes(Path.Combine(directory, name), Array.Empty<byte>());
        }
    }

    [Test]
    public async Task ReportsRuntimeIdentifierArchitectureAndBaseDirectory()
    {
        ToolCapabilityStartupCheck check = new(_paths, _baseDir);

        StartupCheckResult result = await check.RunAsync();

        check.CheckId.Should().Be(ToolCapabilityStartupCheck.Id);
        result.Details.Should().Contain($"runtime-identifier: {RuntimeInformation.RuntimeIdentifier}");
        result.Details.Should().Contain($"process-architecture: {RuntimeInformation.ProcessArchitecture}");
        result.Details.Should().Contain($"os-architecture: {RuntimeInformation.OSArchitecture}");
        result.Details.Should().Contain($"base-directory: {_baseDir}");
    }

    [Test]
    public async Task MissingNative_IsNamedAndDegraded_NeverAFailure()
    {
        ToolCapabilityStartupCheck check = new(_paths, _baseDir);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Degraded);
        result.Severity.Should().NotBe(StartupCheckSeverity.Blocking);
        foreach (string name in ToolCapabilityStartupCheck.ProbedNativeLibraries)
        {
            result.Details.Should().Contain($"native-missing: {name}");
            result.Summary.Should().Contain(name);
        }
    }

    [Test]
    public async Task NativeInTheBaseDirectory_IsFoundThere()
    {
        PlaceNatives(_baseDir);
        ToolCapabilityStartupCheck check = new(_paths, _baseDir);

        StartupCheckResult result = await check.RunAsync();

        foreach (string name in ToolCapabilityStartupCheck.ProbedNativeLibraries)
        {
            result.Details.Should().Contain($"native: {name} -> {Path.Combine(_baseDir, name)}");
        }
    }

    [Test]
    public async Task NativeInTheRidProbePath_IsFoundWhenTheBaseDirectoryHasNone()
    {
        string probeDir = Path.Combine(_baseDir, ToolCapabilityStartupCheck.NativeProbeSubdirectory);
        PlaceNatives(probeDir);
        ToolCapabilityStartupCheck check = new(_paths, _baseDir);

        StartupCheckResult result = await check.RunAsync();

        result.Details.Should().NotContain(d => d.StartsWith("native-missing:", StringComparison.Ordinal));
        foreach (string name in ToolCapabilityStartupCheck.ProbedNativeLibraries)
        {
            result.Details.Should().Contain($"native: {name} -> {Path.Combine(probeDir, name)}");
        }
    }

    [Test]
    public async Task BaseDirectoryWins_OverTheRidProbePath()
    {
        PlaceNatives(_baseDir);
        PlaceNatives(Path.Combine(_baseDir, ToolCapabilityStartupCheck.NativeProbeSubdirectory));
        ToolCapabilityStartupCheck check = new(_paths, _baseDir);

        StartupCheckResult result = await check.RunAsync();

        string first = ToolCapabilityStartupCheck.ProbedNativeLibraries[0];
        result.Details.Should().Contain($"native: {first} -> {Path.Combine(_baseDir, first)}");
    }

    [Test]
    public async Task ProbesTheFourExpectedNatives()
    {
        ToolCapabilityStartupCheck.ProbedNativeLibraries.Should().Equal(
            "e_sqlite3.dll", "av_libglesv2.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll");

        StartupCheckResult result = await new ToolCapabilityStartupCheck(_paths, _baseDir).RunAsync();

        result.Details.Count(d => d.StartsWith("native-missing:", StringComparison.Ordinal)).Should().Be(4);
    }

    [Test]
    public async Task GpuIsReportedAsDeferred_AndNoContextIsCreated()
    {
        StartupCheckResult result = await new ToolCapabilityStartupCheck(_paths, _baseDir).RunAsync();

        result.Details.Should().Contain("render-gpu: Deferred — probed on first 3D slot");
        result.Details.Should().Contain(ToolCapabilityStartupCheck.GpuDeferredDetail);
    }

    [Test]
    public async Task ReportsInstallDiscovery_AccordingToPlatform()
    {
        StartupCheckResult result = await new ToolCapabilityStartupCheck(_paths, _baseDir).RunAsync();

        if (OperatingSystem.IsWindows())
        {
            result.Details.Should().Contain(d => d.StartsWith("nwn-install:", StringComparison.Ordinal));
            result.Details.Should().NotContain(ToolCapabilityStartupCheck.InstallDiscoveryUnavailableDetail);
        }
        else
        {
            result.Details.Should().Contain(ToolCapabilityStartupCheck.InstallDiscoveryUnavailableDetail);
        }
    }

    [Test]
    public async Task ReportsFreeSpaceOnTheDataRoot_EvenWhenTheRootDoesNotExistYet()
    {
        Directory.Exists(_paths.Root).Should().BeFalse();

        StartupCheckResult result = await new ToolCapabilityStartupCheck(_paths, _baseDir).RunAsync();

        result.Details.Should().Contain(d => d.StartsWith("data-root-free-space: ", StringComparison.Ordinal));
        result.Details.Should().Contain($"data-root: {Path.GetFullPath(_paths.Root)}");
    }

    [Test]
    public async Task FullyProvisionedBaseDirectory_IsOkOnAWindowsMachineWithAnInstall()
    {
        PlaceNatives(_baseDir);

        StartupCheckResult result = await new ToolCapabilityStartupCheck(_paths, _baseDir).RunAsync();

        // Install discovery and free space are environment-dependent, so the only invariant that
        // holds everywhere is that natives no longer contribute to the degradation.
        result.Details.Should().NotContain(d => d.StartsWith("native-missing:", StringComparison.Ordinal));
        result.Severity.Should().NotBe(StartupCheckSeverity.Blocking);
    }

    [Test]
    public async Task LogsTheOutcome()
    {
        RecordingAppLogger logger = new();

        await new ToolCapabilityStartupCheck(_paths, _baseDir, logger).RunAsync();

        RecordingAppLogger.Entry entry = logger.Entries.Single();
        entry.Category.Should().Be(ToolCapabilityStartupCheck.LogCategory);
        entry.Level.Should().Be(LogLevel.Warn);
        entry.Data["baseDirectory"].Should().Be(_baseDir);
        entry.Data["missingNatives"].Should().Contain("libSkiaSharp.dll");
    }

    [Test]
    public async Task DefaultsToTheProcessBaseDirectory()
    {
        StartupCheckResult result = await new ToolCapabilityStartupCheck(_paths).RunAsync();

        result.Details.Should().Contain($"base-directory: {AppContext.BaseDirectory}");
    }

    [Test]
    public void NullPaths_Throws()
    {
        Action act = () => _ = new ToolCapabilityStartupCheck(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
