using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.Services;
using SRN.CC.Core.Startup;

namespace SRN.CC.Tests.App;

/// <summary>
/// Pins the wiring between the four startup checks as <see cref="AppServices"/> composes them —
/// as opposed to the checks' own behaviour, which their individual fixtures cover.
/// </summary>
/// <remarks>
/// The load-bearing property is ordering. The checks run sequentially in list order, and the
/// publication-journal check is handed a <em>lambda</em> over the settings check's recent-project
/// list rather than the list itself, so it observes settings that had not been read when it was
/// constructed. Composing them in the wrong order silently reduces journal recovery to whatever the
/// command line happened to name, which is exactly the failure this milestone set out to close.
/// </remarks>
[TestFixture]
public class StartupPreflightWiringTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_PreflightWiringTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private AppPaths Paths(string name = "root") => new(Path.Combine(_tempDir, name));

    private static int DirectoriesScanned(StartupCheckResult journalResult)
    {
        string detail = journalResult.Details.Single(d => d.StartsWith("directories-scanned: ", StringComparison.Ordinal));
        return int.Parse(detail["directories-scanned: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Test]
    public async Task JournalCheck_SeesRecentProjectsThatTheSettingsCheckLoadedInTheSameRun()
    {
        string recentA = Path.Combine(_tempDir, "projectA");
        string recentB = Path.Combine(_tempDir, "projectB");
        Directory.CreateDirectory(recentA);
        Directory.CreateDirectory(recentB);

        AppPaths paths = Paths();
        Directory.CreateDirectory(paths.Root);
        string settingsJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["recentProjectPaths"] = new[]
            {
                Path.Combine(recentA, "a.srnccproj"),
                Path.Combine(recentB, "b.srnccproj")
            }
        });
        await File.WriteAllTextAsync(paths.SettingsPath, settingsJson);

        await using AppServices withSettings = await AppServices.CreateAsync(paths, Array.Empty<string>());

        StartupCheckResult settingsResult = withSettings.StartupReport.Results.Single(r => r.CheckId == "settings");
        settingsResult.Severity.Should().Be(StartupCheckSeverity.Ok);
        withSettings.Settings.RecentProjectPaths.Should().HaveCount(2);

        StartupCheckResult journalResult =
            withSettings.StartupReport.Results.Single(r => r.CheckId == "publication-journal");

        // Working directory plus both recent-project directories. Had the journal check been
        // constructed with a snapshot of the list, or run before the settings check, this would be 1.
        DirectoriesScanned(journalResult).Should().Be(3);
    }

    [Test]
    public async Task JournalCheck_WithNoSettings_ScansOnlyTheWorkingDirectory()
    {
        await using AppServices services = await AppServices.CreateAsync(Paths(), Array.Empty<string>());

        StartupCheckResult journalResult =
            services.StartupReport.Results.Single(r => r.CheckId == "publication-journal");

        DirectoriesScanned(journalResult).Should().Be(1);
    }

    [Test]
    public async Task EveryShippedCheck_ProducesExactlyOneResultAndNoneIsBlocking()
    {
        await using AppServices services = await AppServices.CreateAsync(Paths(), Array.Empty<string>());

        services.StartupReport.Results.Should().HaveCount(4);
        services.StartupReport.Results.Select(r => r.CheckId).Should().OnlyHaveUniqueItems();
        services.StartupReport.Results.Should().OnlyContain(r => r.Severity != StartupCheckSeverity.Blocking);
        services.StartupReport.Results.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Summary));
    }

    [Test]
    public async Task ToolCapabilityCheck_ReportsGpuAsDeferredRatherThanProbingIt()
    {
        await using AppServices services = await AppServices.CreateAsync(Paths(), Array.Empty<string>());

        StartupCheckResult toolResult = services.StartupReport.Results.Single(r => r.CheckId == "tool-capability");

        toolResult.Details.Should().Contain(d => d.StartsWith("render-gpu:", StringComparison.Ordinal)
            && d.Contains("Deferred", StringComparison.Ordinal));
    }

    [Test]
    public async Task PreflightJson_CarriesEveryCheckTheSmokeRunnerAssertsOn()
    {
        await using AppServices services = await AppServices.CreateAsync(Paths(), Array.Empty<string>());

        using JsonDocument document = JsonDocument.Parse(AppServices.SerializeStartupReport(services.StartupReport));

        string[] checkIds = document.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("checkId").GetString()!)
            .ToArray();

        checkIds.Should().BeEquivalentTo(new[] { "cache", "settings", "publication-journal", "tool-capability" });
        document.RootElement.GetProperty("hasBlocking").GetBoolean().Should().BeFalse();
    }
}
