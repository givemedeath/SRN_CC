using System.Text.Json;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.Services;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Startup;

namespace SRN.CC.Tests.App;

/// <summary>
/// Covers the composition root introduced by milestone 7: the whole service graph, the logging
/// pipeline, and the startup preflight now live behind <see cref="AppServices"/>, which is
/// constructible against a temporary <see cref="AppPaths"/> and therefore assertable without ever
/// creating a window.
/// </summary>
[TestFixture]
public class AppServicesTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_AppServicesTests_" + Guid.NewGuid().ToString("N"));
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

    [Test]
    public async Task CreateAsync_AgainstATempRoot_CreatesTheLogDirectoryAndWritesPreflightRecords()
    {
        AppPaths paths = Paths();

        await using AppServices services = await AppServices.CreateAsync(paths, Array.Empty<string>());

        Directory.Exists(paths.LogDirectory).Should().BeTrue("the composition root owns log-directory creation");

        string logFile = Path.Combine(paths.LogDirectory, "srncc.log");
        File.Exists(logFile).Should().BeTrue("the preflight logs one record per check");

        string[] lines = File.ReadAllLines(logFile).Where(l => l.Length > 0).ToArray();
        lines.Length.Should().BeGreaterThanOrEqualTo(services.StartupReport.Results.Count);

        foreach (string line in lines)
        {
            Action parse = () => JsonDocument.Parse(line).Dispose();
            parse.Should().NotThrow("every log line is a standalone JSON document");
        }
    }

    [Test]
    public async Task CreateAsync_RunsAllFourChecksInTheOrderTheHandoffRequires()
    {
        await using AppServices services = await AppServices.CreateAsync(Paths(), Array.Empty<string>());

        services.StartupReport.Results.Select(r => r.CheckId).Should().Equal(
            "cache", "settings", "publication-journal", "tool-capability");
    }

    [Test]
    public async Task CreateAsync_OnAFreshMachine_ProducesNoBlockingResult()
    {
        await using AppServices services = await AppServices.CreateAsync(Paths(), Array.Empty<string>());

        services.StartupReport.HasBlocking.Should().BeFalse(
            "every degradation this application can detect today is survivable");
    }

    [Test]
    public async Task CreateAsync_WithAnUnwritableRoot_NeverThrowsAndStillReportsEveryCheck()
    {
        // A file where the root directory should be: the log directory cannot be created and the
        // cache database cannot be opened. Both must degrade, not throw.
        string blocked = Path.Combine(_tempDir, "blocked");
        await File.WriteAllTextAsync(blocked, "this is a file, not a directory");
        var paths = new AppPaths(blocked);

        AppServices? services = null;
        Func<Task> act = async () => services = await AppServices.CreateAsync(paths, Array.Empty<string>());

        await act.Should().NotThrowAsync("a hostile data directory is a preflight result, not a crash");

        try
        {
            services.Should().NotBeNull();
            services!.StartupReport.Results.Should().HaveCount(4);
            services.StartupReport.HasBlocking.Should().BeFalse();
            Directory.Exists(paths.LogDirectory).Should().BeFalse("the log directory could not be created");
        }
        finally
        {
            if (services is not null)
            {
                await services.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task CreateAsync_ReusesTheSettingsCheckResultRatherThanLoadingTwice()
    {
        AppPaths paths = Paths();
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(
            paths.SettingsPath,
            """{"schemaVersion":1,"lastProjectPath":"C:\\projects\\demo.srnccproj","recentProjectPaths":["C:\\projects\\demo.srnccproj"]}""");

        await using AppServices services = await AppServices.CreateAsync(paths, Array.Empty<string>());

        services.Settings.LastProjectPath.Should().Be(@"C:\projects\demo.srnccproj");
        services.Settings.RecentProjectPaths.Should().ContainSingle();
        services.StartupReport.Results
            .Single(r => r.CheckId == "settings").Severity.Should().Be(StartupCheckSeverity.Ok);
    }

    [Test]
    public async Task SettingsStore_IsASingleSharedInstance()
    {
        // The store's read-only-newer write guard is per-instance state keyed by path. Two stores
        // over one path would let a caller holding fresh defaults overwrite a newer build's file.
        await using AppServices services = await AppServices.CreateAsync(Paths(), Array.Empty<string>());

        MainWindowViewModel first = services.CreateMainWindowViewModel();
        MainWindowViewModel second = services.CreateMainWindowViewModel();

        first.Should().NotBeSameAs(second);
        services.SettingsStore.Should().BeSameAs(services.SettingsStore);
        services.SettingsStore.Should().BeOfType<SRN.CC.Infrastructure.Persistence.SettingsStore>();
    }

    [AvaloniaTest]
    public async Task CreateMainWindowViewModel_AttachesTheUiSinkAndReplaysTheStartupReport()
    {
        await using AppServices services = await AppServices.CreateAsync(Paths(), Array.Empty<string>());

        MainWindowViewModel viewModel = services.CreateMainWindowViewModel();
        Dispatcher.UIThread.RunJobs();

        services.ObservableLogSink.Target.Should().BeSameAs(viewModel.OperationLog);
        viewModel.OperationLog.Logger.Should().BeSameAs(services.Logger);

        foreach (StartupCheckResult result in services.StartupReport.Results)
        {
            viewModel.OperationLog.Entries
                .Should().Contain(e => e.Message.Contains($"Startup check '{result.CheckId}'", StringComparison.Ordinal),
                    "the startup report is visible in the operation log on first launch");
        }
    }

    [AvaloniaTest]
    public async Task ViewModelLogEntries_RoundTripThroughTheSharedLoggerIntoTheLogFile()
    {
        AppPaths paths = Paths();
        await using (AppServices services = await AppServices.CreateAsync(paths, Array.Empty<string>()))
        {
            MainWindowViewModel viewModel = services.CreateMainWindowViewModel();
            viewModel.OperationLog.AddEntry("WARN", "a message from the drawer");
            Dispatcher.UIThread.RunJobs();

            viewModel.OperationLog.Entries.Should().Contain(e => e.Message == "a message from the drawer");
            viewModel.OperationLog.WarningCount.Should().BeGreaterThan(0);
        }

        string log = await File.ReadAllTextAsync(Path.Combine(paths.LogDirectory, "srncc.log"));
        log.Should().Contain("a message from the drawer", "the drawer is a view over the one log, not a second one");
    }

    [Test]
    public async Task SerializeStartupReport_EmitsTheDocumentShapeTheSmokeRunnerParses()
    {
        await using AppServices services = await AppServices.CreateAsync(Paths(), Array.Empty<string>());

        string json = AppServices.SerializeStartupReport(services.StartupReport);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("schemaVersion").GetInt32().Should().Be(AppServices.StartupReportJsonSchemaVersion);
        root.GetProperty("hasBlocking").GetBoolean().Should().BeFalse();
        root.GetProperty("worst").GetString().Should().BeOneOf("Ok", "Degraded", "Blocking");

        JsonElement results = root.GetProperty("results");
        results.ValueKind.Should().Be(JsonValueKind.Array);
        results.GetArrayLength().Should().Be(services.StartupReport.Results.Count);

        foreach (JsonElement result in results.EnumerateArray())
        {
            result.GetProperty("checkId").GetString().Should().NotBeNullOrWhiteSpace();
            result.GetProperty("severity").GetString().Should().BeOneOf("Ok", "Degraded", "Blocking");
            result.GetProperty("summary").GetString().Should().NotBeNull();
            result.GetProperty("details").ValueKind.Should().Be(JsonValueKind.Array);
            result.TryGetProperty("code", out _).Should().BeTrue();
        }

        results.EnumerateArray().Select(r => r.GetProperty("checkId").GetString())
            .Should().Equal("cache", "settings", "publication-journal", "tool-capability");
    }

    [Test]
    public async Task DisposeAsync_IsIdempotent()
    {
        AppServices services = await AppServices.CreateAsync(Paths(), Array.Empty<string>());

        await services.DisposeAsync();
        Func<Task> second = async () => await services.DisposeAsync();

        await second.Should().NotThrowAsync();
        Action afterDispose = () => services.CreateMainWindowViewModel();
        afterDispose.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void ObservableLogSink_DropsRecordsBeforeAttachAndNeverThrows()
    {
        var sink = new ObservableLogSink(dispatch: action => action());

        Action write = () => sink.Write(new LogRecord(
            DateTimeOffset.UtcNow, LogLevel.Info, "cat", "before attach", null, null, null, null));
        write.Should().NotThrow();
        sink.DroppedCount.Should().Be(1);

        var log = new OperationLogViewModel();
        sink.Attach(log);

        sink.Write(new LogRecord(DateTimeOffset.UtcNow, LogLevel.Debug, "cat", "too quiet", null, null, null, null));
        log.Entries.Should().BeEmpty("the drawer filters below Info");

        sink.Write(new LogRecord(DateTimeOffset.UtcNow, LogLevel.Error, "cat", "boom", null, null, "System.IOException", "disk"));
        log.Entries.Should().ContainSingle();
        log.Entries[0].Level.Should().Be("ERROR");
        log.Entries[0].Message.Should().Be("[cat] boom (System.IOException: disk)");
        log.ErrorCount.Should().Be(1);
        sink.FailureCount.Should().Be(0);
    }

    [Test]
    public void ObservableLogSink_SwallowsAFailingAppend()
    {
        var sink = new ObservableLogSink(dispatch: _ => throw new InvalidOperationException("no dispatcher"));
        sink.Attach(new OperationLogViewModel());

        Action write = () => sink.Write(new LogRecord(
            DateTimeOffset.UtcNow, LogLevel.Info, "cat", "message", null, null, null, null));

        write.Should().NotThrow("a logger must never turn a diagnostic into an outage");
        sink.FailureCount.Should().Be(1);
    }

    [Test]
    public void OperationLogViewModel_CapsEntriesAtTwoThousandAndKeepsSessionCounts()
    {
        var log = new OperationLogViewModel();

        for (int i = 0; i < OperationLogViewModel.MaxEntries + 500; i++)
        {
            log.AddEntry("WARN", $"entry {i}");
        }

        log.Entries.Should().HaveCount(OperationLogViewModel.MaxEntries);
        log.Entries[0].Message.Should().Be("entry 500", "the oldest entries are evicted first");
        log.WarningCount.Should().Be(OperationLogViewModel.MaxEntries + 500,
            "counts are session totals and must not decrease when entries are evicted");
    }

    [Test]
    public void OperationLogViewModel_WithALoggerAttached_DelegatesInsteadOfAppendingDirectly()
    {
        var recording = new RecordingLogger();
        var log = new OperationLogViewModel { Logger = recording };

        log.AddEntry("ERROR", "delegated");

        recording.Records.Should().ContainSingle();
        recording.Records[0].Level.Should().Be(LogLevel.Error);
        recording.Records[0].Category.Should().Be(OperationLogViewModel.LogCategory);
        recording.Records[0].Message.Should().Be("delegated");
        log.Entries.Should().BeEmpty("the entry arrives back through the sink, not by a parallel path");
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<LogRecord> Records { get; } = new();

        public void Log(
            LogLevel level,
            string category,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, string>? data = null)
            => Records.Add(new LogRecord(
                DateTimeOffset.UtcNow, level, category, message, null, data,
                exception?.GetType().FullName, exception?.Message));
    }
}
