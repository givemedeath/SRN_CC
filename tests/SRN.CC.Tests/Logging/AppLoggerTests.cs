using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Logging;

namespace SRN.CC.Tests.Logging;

/// <summary>
/// Fan-out, record construction and per-sink failure isolation for <see cref="AppLogger"/>.
/// The governing rule under test is that nothing here ever throws at the caller.
/// </summary>
[TestFixture]
public class AppLoggerTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 3, 4, 5, 6, 7, 89, TimeSpan.Zero);

    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "srncc-logger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A stray handle on a temp folder must never fail an otherwise green run.
        }
    }

    private static AppLogger Logger(params ILogSink[] sinks) =>
        new(sinks, new FixedTimeProvider(FixedNow));

    // --- Fan-out -----------------------------------------------------------------------------

    [Test]
    public void Log_DeliversTheSameRecordToEverySink()
    {
        var first = new RecordingSink();
        var second = new RecordingSink();
        using var logger = Logger(first, second);

        logger.Log(LogLevel.Warn, "Cache", "quarantined");

        logger.SinkCount.Should().Be(2);
        first.Records.Should().ContainSingle();
        second.Records.Should().ContainSingle();
        second.Records[0].Should().BeSameAs(first.Records[0], "one record is built, then shared");
    }

    [Test]
    public void Log_PopulatesEveryRecordFieldFromTheCallAndTheClock()
    {
        var sink = new RecordingSink();
        using var logger = Logger(sink);
        var data = new Dictionary<string, string> { ["path"] = "a.hak" };
        var exception = new InvalidOperationException("bad state");

        logger.Log(LogLevel.Error, "Publisher", "failed", exception, data);

        LogRecord record = sink.Records.Should().ContainSingle().Subject;
        record.TimestampUtc.Should().Be(FixedNow);
        record.Level.Should().Be(LogLevel.Error);
        record.Category.Should().Be("Publisher");
        record.Message.Should().Be("failed");
        record.Data.Should().BeSameAs(data);
        record.ExceptionType.Should().Be("System.InvalidOperationException");
        record.ExceptionMessage.Should().Be("bad state");
    }

    [Test]
    public void Log_WithoutAnException_LeavesTheExceptionFieldsNull()
    {
        var sink = new RecordingSink();
        using var logger = Logger(sink);

        logger.Log(LogLevel.Info, "Startup", "ready");

        LogRecord record = sink.Records.Should().ContainSingle().Subject;
        record.ExceptionType.Should().BeNull();
        record.ExceptionMessage.Should().BeNull();
        record.EventCode.Should().BeNull();
        record.Data.Should().BeNull();
    }

    [Test]
    public void Log_PromotesTheWellKnownDataKeyIntoTheEventCodeField()
    {
        var sink = new RecordingSink();
        using var logger = Logger(sink);
        var data = new Dictionary<string, string>
        {
            [AppLogger.EventCodeKey] = "CorruptedCacheQuarantined",
            ["path"] = "cache-v1.sqlite"
        };

        logger.Log(LogLevel.Warn, "Cache", "quarantined", data: data);

        LogRecord record = sink.Records.Should().ContainSingle().Subject;
        record.EventCode.Should().Be("CorruptedCacheQuarantined");
        record.Data!["eventCode"].Should().Be(
            "CorruptedCacheQuarantined", "the promotion is additive; the caller still sees what it passed");
    }

    [Test]
    public void Log_WithNoSinks_IsAHarmlessNoOp()
    {
        using var logger = new AppLogger([]);

        Action log = () => logger.Log(LogLevel.Error, "Nowhere", "message", new Exception("x"));

        log.Should().NotThrow();
        logger.SinkCount.Should().Be(0);
        logger.FailedSinkCount.Should().Be(0);
    }

    // --- Exit criterion: per-sink failures are isolated and counted ---------------------------

    [Test]
    public void Log_WhenASinkThrows_DoesNotThrowAndIncrementsFailedSinkCount()
    {
        var broken = new ThrowingSink();
        var healthy = new RecordingSink();
        using var logger = Logger(broken, healthy);

        Action log = () => logger.Log(LogLevel.Error, "Cache", "boom");

        log.Should().NotThrow("a logger must never turn a diagnostic into an outage");
        logger.FailedSinkCount.Should().Be(1);
        healthy.Records.Should().ContainSingle(
            "one broken destination must not silence the others");
    }

    [Test]
    public void Log_WhenTheFirstSinkThrows_StillReachesEverySinkBehindIt()
    {
        var broken = new ThrowingSink();
        var second = new RecordingSink();
        var third = new RecordingSink();
        using var logger = Logger(broken, second, third);

        logger.Log(LogLevel.Info, "A", "one");
        logger.Log(LogLevel.Info, "A", "two");

        second.Records.Should().HaveCount(2);
        third.Records.Should().HaveCount(2);
    }

    [Test]
    public void FailedSinkCount_CountsSinksNotFailures()
    {
        var broken = new ThrowingSink();
        using var logger = Logger(broken, new RecordingSink());

        for (int i = 0; i < 25; i++)
        {
            logger.Log(LogLevel.Trace, "A", "spam");
        }

        broken.WriteAttempts.Should().Be(25);
        logger.FailedSinkCount.Should().Be(
            1, "the number answers 'how many destinations are broken', not 'how many records were lost'");
    }

    [Test]
    public void FailedSinkCount_CountsEachBrokenSinkSeparately()
    {
        using var logger = Logger(new ThrowingSink(), new RecordingSink(), new ThrowingSink());

        logger.Log(LogLevel.Info, "A", "message");

        logger.FailedSinkCount.Should().Be(2);
    }

    [Test]
    public void Flush_WhenASinkThrows_DoesNotThrowAndIsCounted()
    {
        var broken = new ThrowingSink { ThrowOnWrite = false, ThrowOnFlush = true };
        var healthy = new RecordingSink();
        using var logger = Logger(broken, healthy);

        Action flush = logger.Flush;

        flush.Should().NotThrow();
        logger.FailedSinkCount.Should().Be(1);
        healthy.FlushCount.Should().Be(1);
    }

    // --- Lifetime ----------------------------------------------------------------------------

    [Test]
    public void Dispose_FlushesAndDisposesEverySink()
    {
        var first = new RecordingSink();
        var second = new RecordingSink();
        var logger = Logger(first, second);

        logger.Dispose();

        first.FlushCount.Should().Be(1);
        first.DisposeCount.Should().Be(1);
        second.FlushCount.Should().Be(1);
        second.DisposeCount.Should().Be(1);
    }

    [Test]
    public void Dispose_WhenASinkThrowsOnTheWayOut_DoesNotThrowAndStillDisposesTheRest()
    {
        var broken = new ThrowingSink { ThrowOnWrite = false, ThrowOnDispose = true };
        var healthy = new RecordingSink();
        var logger = Logger(broken, healthy);

        Action dispose = logger.Dispose;

        dispose.Should().NotThrow("a failing sink must not take application shutdown with it");
        healthy.DisposeCount.Should().Be(1);
        logger.FailedSinkCount.Should().Be(1);
    }

    [Test]
    public void Dispose_IsIdempotentAndSilencesLaterLogging()
    {
        var sink = new RecordingSink();
        var logger = Logger(sink);

        logger.Dispose();
        Action disposeAgain = logger.Dispose;
        disposeAgain.Should().NotThrow();
        logger.Log(LogLevel.Error, "A", "after shutdown");

        sink.DisposeCount.Should().Be(1);
        sink.Records.Should().BeEmpty();
    }

    [Test]
    public void Constructor_RejectsANullSinkCollection()
    {
        Action nullSinks = () => new AppLogger(null!);

        nullSinks.Should().Throw<ArgumentNullException>();
    }

    // --- Concurrency and the real sink --------------------------------------------------------

    [Test]
    public void Log_FromEightConcurrentCallers_LosesNothing()
    {
        const int callers = 8;
        const int perCaller = 200;
        var sink = new RecordingSink();
        using var logger = Logger(sink);
        using var start = new ManualResetEventSlim(false);

        var threads = new Thread[callers];
        for (int c = 0; c < callers; c++)
        {
            threads[c] = new Thread(() =>
            {
                start.Wait();
                for (int i = 0; i < perCaller; i++)
                {
                    logger.Log(LogLevel.Info, "Concurrent", "message");
                }
            });
            threads[c].Start();
        }

        start.Set();
        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        sink.Records.Should().HaveCount(callers * perCaller);
        logger.FailedSinkCount.Should().Be(0);
    }

    [Test]
    public void Log_ThroughARealSinkWhoseDirectoryIsUnusable_DoesNotThrow()
    {
        string logDirectory = new AppPaths(_root).LogDirectory;
        Directory.CreateDirectory(Path.Combine(logDirectory, LogFileSet.ActiveFileName));
        var file = new JsonLineLogSink(logDirectory);
        var healthy = new RecordingSink();
        using var logger = Logger(file, healthy);

        Action log = () => logger.Log(LogLevel.Error, "Publisher", "still running");

        log.Should().NotThrow();
        file.FailureCount.Should().Be(
            1, "the sink absorbs its own failure, so its counter is where the evidence lives");
        logger.FailedSinkCount.Should().Be(
            0, "a well-behaved sink never throws, so the logger has nothing to isolate");
        healthy.Records.Should().ContainSingle();
    }

    [Test]
    public void Log_ThroughARealSink_ProducesAJsonLine()
    {
        string logDirectory = new AppPaths(_root).LogDirectory;
        var file = new JsonLineLogSink(logDirectory);
        using (var logger = Logger(file))
        {
            logger.Log(LogLevel.Warn, "Startup", "degraded", new IOException("no disk"));
        }

        string[] lines = File.ReadAllLines(Path.Combine(logDirectory, LogFileSet.ActiveFileName));
        lines.Should().ContainSingle();
        lines[0].Should().Contain("\"level\":\"Warn\"")
            .And.Contain("\"category\":\"Startup\"")
            .And.Contain("\"exceptionType\":\"System.IO.IOException\"");
    }

    // --- Doubles -----------------------------------------------------------------------------

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingSink : ILogSink
    {
        private readonly List<LogRecord> _records = [];

        public IReadOnlyList<LogRecord> Records
        {
            get
            {
                lock (_records)
                {
                    return _records.ToArray();
                }
            }
        }

        public int FlushCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void Write(LogRecord record)
        {
            lock (_records)
            {
                _records.Add(record);
            }
        }

        public void Flush() => FlushCount++;

        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingSink : ILogSink
    {
        public bool ThrowOnWrite { get; init; } = true;

        public bool ThrowOnFlush { get; init; }

        public bool ThrowOnDispose { get; init; }

        public int WriteAttempts { get; private set; }

        public void Write(LogRecord record)
        {
            WriteAttempts++;
            if (ThrowOnWrite)
            {
                throw new IOException("sink is broken");
            }
        }

        public void Flush()
        {
            if (ThrowOnFlush)
            {
                throw new IOException("flush is broken");
            }
        }

        public void Dispose()
        {
            if (ThrowOnDispose)
            {
                throw new IOException("dispose is broken");
            }
        }
    }
}
