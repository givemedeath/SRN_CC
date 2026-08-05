using System.Text.Json;
using NUnit.Framework;
using FluentAssertions;
using SRN.CC.Core.Logging;

namespace SRN.CC.Tests.Core.Logging;

[TestFixture]
public class LogContractTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static LogRecord MakeRecord(
        string message = "message",
        LogLevel level = LogLevel.Info,
        string category = "Category",
        string? eventCode = null,
        IReadOnlyDictionary<string, string>? data = null,
        string? exceptionType = null,
        string? exceptionMessage = null) =>
        new(FixedTimestamp, level, category, message, eventCode, data, exceptionType, exceptionMessage);

    private static string[] PhysicalLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.None);

    // --- Exit Criterion 1: one record is always exactly one physical line ---------------------

    [Test]
    public void Serialize_MessageContainingNewline_ProducesExactlyOnePhysicalLine()
    {
        var record = MakeRecord(message: "first line\nsecond line\r\nthird line");

        string json = JsonSerializer.Serialize(record);

        json.Should().NotContain("\n");
        json.Should().NotContain("\r");
        PhysicalLines(json).Should().HaveCount(1);
    }

    [Test]
    public void Serialize_MultilineMessage_RoundTripsToTheOriginalText()
    {
        const string message = "first line\nsecond line\r\nthird line";
        var record = MakeRecord(message: message);

        string json = JsonSerializer.Serialize(record);
        var restored = JsonSerializer.Deserialize<LogRecord>(json);

        restored.Should().NotBeNull();
        restored!.Message.Should().Be(message);
        restored.Should().Be(record);
    }

    [Test]
    public void Serialize_NewlinesInEveryStringField_ProducesExactlyOnePhysicalLine()
    {
        var record = MakeRecord(
            message: "msg\nwith\rbreaks",
            category: "Cat\negory",
            eventCode: "EVT\n001",
            data: new Dictionary<string, string>
            {
                ["key\nwith break"] = "value\r\nwith break",
                ["tab\tkey"] = "quote \" and backslash \\ and null \0"
            },
            exceptionType: "System.Exception\n",
            exceptionMessage: "boom\r\nboom");

        string json = JsonSerializer.Serialize(record);

        PhysicalLines(json).Should().HaveCount(1);
    }

    [Test]
    public void Serialize_AllOptionalFieldsNull_ProducesExactlyOnePhysicalLine()
    {
        var record = MakeRecord(message: string.Empty, category: string.Empty);

        string json = JsonSerializer.Serialize(record);

        PhysicalLines(json).Should().HaveCount(1);
        json.Should().Contain("\"EventCode\":null");
        json.Should().Contain("\"Data\":null");
    }

    [Test]
    public void Serialize_ManyRecordsJoinedByNewline_SplitsBackIntoOneRecordPerLine()
    {
        LogRecord[] records =
        [
            MakeRecord(message: "one\ntwo"),
            MakeRecord(message: "three", level: LogLevel.Error),
            MakeRecord(message: "four\r\nfive", category: "Other")
        ];

        string document = string.Join('\n', records.Select(r => JsonSerializer.Serialize(r)));
        string[] lines = document.Split('\n');

        lines.Should().HaveCount(records.Length);
        LogRecord[] restored = [.. lines.Select(l => JsonSerializer.Deserialize<LogRecord>(l)!)];
        restored.Should().Equal(records);
    }

    // --- Exit Criterion 2: the null logger never throws ----------------------------------------

    [Test]
    public void NullAppLogger_Instance_IsAStableNonNullSingleton()
    {
        NullAppLogger.Instance.Should().NotBeNull();
        NullAppLogger.Instance.Should().BeSameAs(NullAppLogger.Instance);
    }

    [Test]
    public void Log_WithNullExceptionEmptyCategoryAndNullData_DoesNotThrow()
    {
        Action act = () => NullAppLogger.Instance.Log(LogLevel.Error, string.Empty, string.Empty);

        act.Should().NotThrow();
    }

    [Test]
    public void Log_WithNullCategoryAndNullMessage_DoesNotThrow()
    {
        Action act = () => NullAppLogger.Instance.Log(LogLevel.Info, null!, null!, null, null);

        act.Should().NotThrow();
    }

    [Test]
    public void Log_WithExceptionAndData_DoesNotThrow()
    {
        var data = new Dictionary<string, string> { ["path"] = "C:\\temp\\x.hak", ["count"] = "3" };

        Action act = () => NullAppLogger.Instance.Log(
            LogLevel.Warn, "Category", "message\nwith break", new InvalidOperationException("boom"), data);

        act.Should().NotThrow();
    }

    [Test]
    public void Log_ForEveryDeclaredLevelAndForAnUndeclaredOne_DoesNotThrow()
    {
        Action act = () =>
        {
            foreach (LogLevel level in Enum.GetValues<LogLevel>())
            {
                NullAppLogger.Instance.Log(level, "Category", "message");
            }

            NullAppLogger.Instance.Log((LogLevel)999, "Category", "message");
        };

        act.Should().NotThrow();
    }

    [Test]
    public void Log_ConcurrentlyFromManyThreads_DoesNotThrow()
    {
        Action act = () => Parallel.For(0, 2_000, i =>
            NullAppLogger.Instance.Log(LogLevel.Trace, "Category", i.ToString()));

        act.Should().NotThrow();
    }

    // --- Contract shape ------------------------------------------------------------------------

    [Test]
    public void LogLevel_Ordering_AllowsGreaterThanOrEqualFiltering()
    {
        ((int)LogLevel.Trace).Should().BeLessThan((int)LogLevel.Debug);
        ((int)LogLevel.Debug).Should().BeLessThan((int)LogLevel.Info);
        ((int)LogLevel.Info).Should().BeLessThan((int)LogLevel.Warn);
        ((int)LogLevel.Warn).Should().BeLessThan((int)LogLevel.Error);

        Enum.GetValues<LogLevel>().Should().Equal(
            LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error);
    }

    [Test]
    public void LogRecord_WithExpression_ProducesAnIndependentValueEqualCopy()
    {
        var original = MakeRecord(message: "original");
        var changed = original with { Message = "changed" };

        changed.Should().NotBe(original);
        changed.Category.Should().Be(original.Category);
        (original with { Message = "original" }).Should().Be(original);
    }

    [Test]
    public void ILogSink_IsImplementableAndIsDisposable()
    {
        var sink = new RecordingSink();
        var record = MakeRecord(message: "written");

        using (sink)
        {
            ILogSink asSink = sink;
            asSink.Write(record);
            asSink.Write(record);
            asSink.Flush();
        }

        sink.Written.Should().Equal(record, record);
        sink.FlushCount.Should().Be(1);
        sink.Disposed.Should().BeTrue();
        sink.Should().BeAssignableTo<IDisposable>();
    }

    [Test]
    public void IAppLogger_IsImplementableAndUsableAsATrailingOptionalDefault()
    {
        var recording = new RecordingLogger();

        ServiceTakingAnOptionalLogger();
        ServiceTakingAnOptionalLogger(recording);

        recording.Entries.Should().ContainSingle();
        recording.Entries[0].Level.Should().Be(LogLevel.Info);
        recording.Entries[0].Category.Should().Be("Service");
    }

    private static void ServiceTakingAnOptionalLogger(IAppLogger? logger = null)
    {
        logger ??= NullAppLogger.Instance;
        logger.Log(LogLevel.Info, "Service", "did work");
    }

    private sealed class RecordingSink : ILogSink
    {
        public List<LogRecord> Written { get; } = [];
        public int FlushCount { get; private set; }
        public bool Disposed { get; private set; }

        public void Write(LogRecord record) => Written.Add(record);

        public void Flush() => FlushCount++;

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<LogRecord> Entries { get; } = [];

        public void Log(
            LogLevel level,
            string category,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, string>? data = null) =>
            Entries.Add(new LogRecord(
                DateTimeOffset.UtcNow, level, category, message, null, data,
                exception?.GetType().FullName, exception?.Message));
    }
}
