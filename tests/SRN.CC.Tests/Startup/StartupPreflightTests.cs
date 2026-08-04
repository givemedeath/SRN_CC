using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Settings;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Logging;
using SRN.CC.Infrastructure.Startup;

namespace SRN.CC.Tests.Startup;

/// <summary>
/// A minimal in-memory <see cref="IAppLogger"/>. Deliberately local to this slice rather than
/// shared: a startup-check assertion should not be able to break because another suite changed a
/// logging helper.
/// </summary>
internal sealed class RecordingAppLogger : IAppLogger
{
    private readonly List<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Log(
        LogLevel level,
        string category,
        string message,
        Exception? exception = null,
        IReadOnlyDictionary<string, string>? data = null)
    {
        lock (_entries)
        {
            _entries.Add(new Entry(
                level,
                category,
                message,
                exception?.GetType().FullName,
                data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(data)));
        }
    }

    internal sealed record Entry(
        LogLevel Level,
        string Category,
        string Message,
        string? ExceptionType,
        IReadOnlyDictionary<string, string> Data);
}

/// <summary>A check whose behaviour is supplied per test.</summary>
internal sealed class DelegateStartupCheck : IStartupCheck
{
    private readonly Func<CancellationToken, Task<StartupCheckResult>> _body;

    public DelegateStartupCheck(string checkId, Func<CancellationToken, Task<StartupCheckResult>> body)
    {
        CheckId = checkId;
        _body = body;
    }

    public string CheckId { get; }

    public Task<StartupCheckResult> RunAsync(CancellationToken cancellationToken = default) => _body(cancellationToken);

    public static DelegateStartupCheck Ok(string id) => new(
        id,
        _ => Task.FromResult(new StartupCheckResult(id, StartupCheckSeverity.Ok, id + " ok", Array.Empty<string>())));

    public static DelegateStartupCheck Throws(string id, Exception ex) => new(id, _ => throw ex);

    public static DelegateStartupCheck ReturnsNullResult(string id) => new(
        id,
        _ => Task.FromResult<StartupCheckResult>(null!));

    public static DelegateStartupCheck ReturnsNullTask(string id) => new(id, _ => null!);
}

[TestFixture]
public class StartupPreflightTests
{
    [Test]
    public async Task RunAsync_ReturnsOneResultPerCheck_InOrder()
    {
        StartupReport report = await StartupPreflight.RunAsync(new IStartupCheck?[]
        {
            DelegateStartupCheck.Ok("a"),
            DelegateStartupCheck.Ok("b"),
            DelegateStartupCheck.Ok("c")
        });

        report.Results.Select(r => r.CheckId).Should().Equal("a", "b", "c");
        report.Worst.Should().Be(StartupCheckSeverity.Ok);
        report.HasBlocking.Should().BeFalse();
    }

    [Test]
    public async Task RunAsync_ThrowingCheck_BecomesDegradedAndOtherChecksStillReport()
    {
        StartupReport report = await StartupPreflight.RunAsync(new IStartupCheck?[]
        {
            DelegateStartupCheck.Ok("first"),
            DelegateStartupCheck.Throws("boom", new InvalidOperationException("exploded during preflight")),
            DelegateStartupCheck.Ok("last")
        });

        report.Results.Should().HaveCount(3);
        report.Results.Select(r => r.CheckId).Should().Equal("first", "boom", "last");

        StartupCheckResult failed = report.Results[1];
        failed.Severity.Should().Be(StartupCheckSeverity.Degraded);
        failed.Summary.Should().Contain(nameof(InvalidOperationException));
        failed.Summary.Should().Contain("exploded during preflight");
        failed.Details.Should().Contain(d => d.Contains("System.InvalidOperationException", StringComparison.Ordinal));
        failed.Details.Should().Contain(d => d.Contains("exploded during preflight", StringComparison.Ordinal));

        report.Results[0].Severity.Should().Be(StartupCheckSeverity.Ok);
        report.Results[2].Severity.Should().Be(StartupCheckSeverity.Ok);
        report.HasBlocking.Should().BeFalse();
    }

    [Test]
    public async Task RunAsync_CheckReturningNullResult_BecomesDegraded()
    {
        StartupReport report = await StartupPreflight.RunAsync(new IStartupCheck?[]
        {
            DelegateStartupCheck.ReturnsNullResult("nullresult"),
            DelegateStartupCheck.Ok("after")
        });

        report.Results.Should().HaveCount(2);
        report.Results[0].CheckId.Should().Be("nullresult");
        report.Results[0].Severity.Should().Be(StartupCheckSeverity.Degraded);
        report.Results[0].Summary.Should().Contain("returned no result");
        report.Results[1].Severity.Should().Be(StartupCheckSeverity.Ok);
    }

    [Test]
    public async Task RunAsync_CheckReturningNullTask_BecomesDegraded()
    {
        StartupReport report = await StartupPreflight.RunAsync(new IStartupCheck?[]
        {
            DelegateStartupCheck.ReturnsNullTask("nulltask")
        });

        report.Results.Should().ContainSingle();
        report.Results[0].Severity.Should().Be(StartupCheckSeverity.Degraded);
        report.Results[0].Details.Should().Contain(d => d.Contains("null Task", StringComparison.Ordinal));
    }

    [Test]
    public async Task RunAsync_NullCheckEntry_BecomesDegradedWithoutThrowing()
    {
        StartupReport report = await StartupPreflight.RunAsync(new IStartupCheck?[]
        {
            null,
            DelegateStartupCheck.Ok("survivor")
        });

        report.Results.Should().HaveCount(2);
        report.Results[0].CheckId.Should().Be(StartupPreflight.UnknownCheckId);
        report.Results[0].Severity.Should().Be(StartupCheckSeverity.Degraded);
        report.Results[1].CheckId.Should().Be("survivor");
    }

    [Test]
    public async Task RunAsync_CheckThatHangsUntilCancelled_BecomesDegradedAndRunCompletes()
    {
        using CancellationTokenSource cts = new();

        DelegateStartupCheck hangs = new("hangs", async ct =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new StartupCheckResult("hangs", StartupCheckSeverity.Ok, "never", Array.Empty<string>());
        });

        StartupReport report = await StartupPreflight.RunAsync(
            new IStartupCheck?[] { hangs, DelegateStartupCheck.Ok("after") },
            logger: null,
            cancellationToken: cts.Token);

        report.Results.Should().HaveCount(2);
        report.Results[0].CheckId.Should().Be("hangs");
        report.Results[0].Severity.Should().Be(StartupCheckSeverity.Degraded);
        report.Results[1].CheckId.Should().Be("after");
        report.HasBlocking.Should().BeFalse();
    }

    [Test]
    public async Task RunAsync_LogsOneRecordPerResult_WithSeverityMappedToLevel()
    {
        RecordingAppLogger logger = new();

        await StartupPreflight.RunAsync(
            new IStartupCheck?[]
            {
                DelegateStartupCheck.Ok("fine"),
                DelegateStartupCheck.Throws("bad", new IOException("disk gone"))
            },
            logger);

        IReadOnlyList<RecordingAppLogger.Entry> entries = logger.Entries;
        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.Category == StartupPreflight.LogCategory);
        entries[0].Level.Should().Be(LogLevel.Info);
        entries[0].Data["checkId"].Should().Be("fine");
        entries[1].Level.Should().Be(LogLevel.Warn);
        entries[1].Data["checkId"].Should().Be("bad");
    }

    [Test]
    public async Task RunAsync_CopiesDiagnosticCodeIntoTheEventCodeKey()
    {
        RecordingAppLogger logger = new();
        DelegateStartupCheck coded = new("coded", _ => Task.FromResult(new StartupCheckResult(
            "coded",
            StartupCheckSeverity.Degraded,
            "cache was quarantined",
            new[] { "detail-one" },
            DiagnosticCode.CorruptedCacheQuarantined)));

        await StartupPreflight.RunAsync(new IStartupCheck?[] { coded }, logger);

        RecordingAppLogger.Entry entry = logger.Entries.Single();
        entry.Data[AppLogger.EventCodeKey].Should().Be(nameof(DiagnosticCode.CorruptedCacheQuarantined));
        entry.Data["detail0"].Should().Be("detail-one");
    }

    [Test]
    public async Task RunAsync_BlockingResultIsSurfacedByTheReport()
    {
        // The runner must not swallow Blocking: no shipped check produces it, but the aggregation
        // has to be honest if one ever does.
        DelegateStartupCheck blocking = new("blocker", _ => Task.FromResult(new StartupCheckResult(
            "blocker", StartupCheckSeverity.Blocking, "stop", Array.Empty<string>())));

        StartupReport report = await StartupPreflight.RunAsync(new IStartupCheck?[] { blocking });

        report.HasBlocking.Should().BeTrue();
        report.Worst.Should().Be(StartupCheckSeverity.Blocking);
    }

    [Test]
    public void RunAsync_NullCheckList_Throws()
    {
        Func<Task> act = () => StartupPreflight.RunAsync(null!);

        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task RunAsync_EmptyCheckList_ReportsOk()
    {
        StartupReport report = await StartupPreflight.RunAsync(Array.Empty<IStartupCheck?>());

        report.Results.Should().BeEmpty();
        report.Worst.Should().Be(StartupCheckSeverity.Ok);
        report.HasBlocking.Should().BeFalse();
    }

    [Test]
    public async Task NoShippedCheckEverReturnsBlocking_AssertedOverTheRealFour()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_ShippedChecks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            AppPaths paths = new(tempDir);

            // Every check is driven into its worst plausible state: the cache failed to rebuild,
            // settings were quarantined, journal recovery throws, and no native resolves.
            IStartupCheck?[] shipped =
            {
                new CacheStartupCheck(
                    new FakeSqliteCacheService { IsAvailable = false, LastQuarantineReason = "disk image malformed" },
                    paths),
                new SettingsStartupCheck(
                    new FakeSettingsStore(new SettingsLoadResult(
                        new ApplicationSettings(), SettingsLoadStatus.RebuiltAfterQuarantine, null)),
                    paths),
                new PublicationJournalStartupCheck(
                    new FakeArtifactPublisher(throwing: new[] { tempDir }),
                    new FakeProjectStore(),
                    currentDirectory: () => tempDir),
                new ToolCapabilityStartupCheck(paths, Path.Combine(tempDir, "empty-base"))
            };

            StartupReport report = await StartupPreflight.RunAsync(shipped, new RecordingAppLogger());

            report.Results.Should().HaveCount(4);
            report.Results.Should().OnlyContain(r => r.Severity != StartupCheckSeverity.Blocking);
            report.HasBlocking.Should().BeFalse();
            report.Worst.Should().Be(StartupCheckSeverity.Degraded);
            report.Results.Select(r => r.CheckId).Should().Equal(
                CacheStartupCheck.Id,
                SettingsStartupCheck.Id,
                PublicationJournalStartupCheck.Id,
                ToolCapabilityStartupCheck.Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Test]
    public async Task ShippedChecks_AreAllOkOnAHealthyMachine_ExceptWhatTheEnvironmentDecides()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_ShippedChecksHealthy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            AppPaths paths = new(tempDir);
            SettingsStartupCheck settingsCheck = new(
                new FakeSettingsStore(new SettingsLoadResult(
                    new ApplicationSettings(recentProjectPaths: new[] { Path.Combine(tempDir, "recent.srnccproj") }),
                    SettingsLoadStatus.Loaded,
                    null)),
                paths);
            FakeArtifactPublisher publisher = new();

            IStartupCheck?[] shipped =
            {
                new CacheStartupCheck(new FakeSqliteCacheService(), paths),
                settingsCheck,
                new PublicationJournalStartupCheck(
                    publisher,
                    new FakeProjectStore(),
                    startupArgs: null,
                    recentProjectPaths: () => settingsCheck.RecentProjectPaths,
                    currentDirectory: () => tempDir)
            };

            StartupReport report = await StartupPreflight.RunAsync(shipped);

            report.Results.Should().OnlyContain(r => r.Severity == StartupCheckSeverity.Ok);

            // The journal check consumed the recent-project list the settings check loaded — the
            // ordering contract the runner's sequential execution exists to provide.
            publisher.ScannedDirectories.Should().Contain(Path.GetFullPath(tempDir));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
