using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Services;
using SRN.CC.Core.Settings;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Startup;

namespace SRN.CC.Tests.Startup;

/// <summary>An <see cref="ISettingsStore"/> returning a canned load result.</summary>
internal sealed class FakeSettingsStore : ISettingsStore
{
    private readonly SettingsLoadResult _result;

    public FakeSettingsStore(SettingsLoadResult result) => _result = result;

    public string? LastRequestedPath { get; private set; }

    public int LoadCount { get; private set; }

    public Task<SettingsLoadResult> LoadAsync(string? overrideFilePath = null, CancellationToken cancellationToken = default)
    {
        LastRequestedPath = overrideFilePath;
        LoadCount++;
        return Task.FromResult(_result);
    }

    public Task SaveAsync(ApplicationSettings settings, string? overrideFilePath = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The startup check must not write settings.");
}

[TestFixture]
public class SettingsStartupCheckTests
{
    private string _tempDir = null!;
    private AppPaths _paths = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_SettingsStartupCheckTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new AppPaths(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static SettingsLoadResult Loaded(params string[] recent) =>
        new(new ApplicationSettings(recentProjectPaths: recent), SettingsLoadStatus.Loaded, null);

    [Test]
    public async Task Loaded_IsOk()
    {
        SettingsStartupCheck check = new(new FakeSettingsStore(Loaded()), _paths);

        StartupCheckResult result = await check.RunAsync();

        check.CheckId.Should().Be(SettingsStartupCheck.Id);
        result.Severity.Should().Be(StartupCheckSeverity.Ok);
        result.Details.Should().Contain(d => d.Contains(_paths.SettingsPath, StringComparison.Ordinal));
    }

    [Test]
    public async Task NoFileYet_IsOkBecauseFreshDefaultsAreNotADegradation()
    {
        // SettingsLoadStatus.Loaded covers "no file existed".
        SettingsStartupCheck check = new(new FakeSettingsStore(Loaded()), _paths);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Ok);
    }

    [Test]
    public async Task ReadOnlyNewer_IsDegradedAndSaysTheFileIsUntouched()
    {
        SettingsLoadResult loadResult = new(
            new ApplicationSettings(isReadOnly: true),
            SettingsLoadStatus.ReadOnlyNewer,
            null);
        SettingsStartupCheck check = new(new FakeSettingsStore(loadResult), _paths);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Degraded);
        result.Summary.Should().Contain("newer build");
        result.Summary.Should().Contain("untouched");
    }

    [Test]
    public async Task RebuiltAfterQuarantine_IsDegradedAndNamesTheQuarantinedFile()
    {
        string quarantined = Path.Combine(_tempDir, "settings.json.corrupt.20260804T000000Z");
        SettingsLoadResult loadResult = new(
            new ApplicationSettings(),
            SettingsLoadStatus.RebuiltAfterQuarantine,
            quarantined);
        SettingsStartupCheck check = new(new FakeSettingsStore(loadResult), _paths);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Degraded);
        result.Details.Should().Contain(d => d.Contains(quarantined, StringComparison.Ordinal));
    }

    [Test]
    public async Task RebuiltAfterQuarantine_WithNullPath_DoesNotThrow()
    {
        // The move itself can fail, leaving the status set and the path null.
        SettingsLoadResult loadResult = new(
            new ApplicationSettings(),
            SettingsLoadStatus.RebuiltAfterQuarantine,
            null);
        SettingsStartupCheck check = new(new FakeSettingsStore(loadResult), _paths);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Degraded);
        result.Details.Should().Contain(d => d.Contains("could not be moved aside", StringComparison.Ordinal));
    }

    [Test]
    public async Task ThreeStatuses_ProduceThreeDistinctSummaries()
    {
        string[] summaries =
        {
            (await new SettingsStartupCheck(new FakeSettingsStore(Loaded()), _paths).RunAsync()).Summary,
            (await new SettingsStartupCheck(new FakeSettingsStore(
                new SettingsLoadResult(new ApplicationSettings(isReadOnly: true), SettingsLoadStatus.ReadOnlyNewer, null)), _paths).RunAsync()).Summary,
            (await new SettingsStartupCheck(new FakeSettingsStore(
                new SettingsLoadResult(new ApplicationSettings(), SettingsLoadStatus.RebuiltAfterQuarantine, "x")), _paths).RunAsync()).Summary
        };

        summaries.Distinct(StringComparer.Ordinal).Should().HaveCount(3);
    }

    [Test]
    public async Task ReadsFromTheAppPathsSettingsLocation()
    {
        FakeSettingsStore store = new(Loaded());

        await new SettingsStartupCheck(store, _paths).RunAsync();

        store.LastRequestedPath.Should().Be(_paths.SettingsPath);
    }

    [Test]
    public async Task PublishesTheLoadResultSoStartupDoesNotReadTheFileTwice()
    {
        FakeSettingsStore store = new(Loaded(@"C:\projects\alpha.srnccproj", @"C:\projects\beta.srnccproj"));
        SettingsStartupCheck check = new(store, _paths);

        check.LoadResult.Should().BeNull();
        check.RecentProjectPaths.Should().BeEmpty();

        await check.RunAsync();

        store.LoadCount.Should().Be(1);
        check.LoadResult.Should().NotBeNull();
        check.RecentProjectPaths.Should().Equal(@"C:\projects\alpha.srnccproj", @"C:\projects\beta.srnccproj");
    }

    [Test]
    public async Task LogsTheStatus()
    {
        RecordingAppLogger logger = new();
        SettingsStartupCheck check = new(
            new FakeSettingsStore(new SettingsLoadResult(new ApplicationSettings(isReadOnly: true), SettingsLoadStatus.ReadOnlyNewer, null)),
            _paths,
            logger);

        await check.RunAsync();

        RecordingAppLogger.Entry entry = logger.Entries.Single();
        entry.Level.Should().Be(LogLevel.Warn);
        entry.Category.Should().Be(SettingsStartupCheck.LogCategory);
        entry.Data["status"].Should().Be(nameof(SettingsLoadStatus.ReadOnlyNewer));
    }

    [Test]
    public void NullArguments_Throw()
    {
        Action nullStore = () => _ = new SettingsStartupCheck(null!, _paths);
        Action nullPaths = () => _ = new SettingsStartupCheck(new FakeSettingsStore(Loaded()), null!);

        nullStore.Should().Throw<ArgumentNullException>();
        nullPaths.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task NeverReturnsBlocking()
    {
        foreach (SettingsLoadStatus status in Enum.GetValues<SettingsLoadStatus>())
        {
            SettingsStartupCheck check = new(
                new FakeSettingsStore(new SettingsLoadResult(new ApplicationSettings(), status, null)),
                _paths);

            StartupCheckResult result = await check.RunAsync();

            result.Severity.Should().NotBe(StartupCheckSeverity.Blocking);
        }
    }
}
