using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Logging;

namespace SRN.CC.Tests.Logging;

/// <summary>
/// Line-shape, durability and failure-isolation coverage for <see cref="JsonLineLogSink"/>.
/// Rotation and retention live in <c>LogRotationTests</c>.
/// </summary>
[TestFixture]
public class JsonLineLogSinkTests
{
    private static readonly JsonSerializerOptions RecordOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] RequiredProperties =
    [
        "timestampUtc", "level", "category", "message",
        "eventCode", "data", "exceptionType", "exceptionMessage"
    ];

    private string _root = string.Empty;
    private string _logDirectory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "srncc-sink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _logDirectory = new AppPaths(_root).LogDirectory;
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

    private static LogRecord Record(
        string message = "message",
        LogLevel level = LogLevel.Info,
        string category = "Category",
        string? eventCode = null,
        IReadOnlyDictionary<string, string>? data = null,
        string? exceptionType = null,
        string? exceptionMessage = null) =>
        new(new DateTimeOffset(2026, 3, 4, 5, 6, 7, 890, TimeSpan.Zero),
            level, category, message, eventCode, data, exceptionType, exceptionMessage);

    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // --- Layout ------------------------------------------------------------------------------

    [Test]
    public void Constructor_DoesNotTouchTheFileSystem()
    {
        using var sink = new JsonLineLogSink(_logDirectory);

        Directory.Exists(_logDirectory).Should().BeFalse(
            "describing a log layout must not create it; the directory appears on the first record");
        sink.ActivePath.Should().Be(Path.Combine(_logDirectory, LogFileSet.ActiveFileName));
        sink.MaxFileBytes.Should().Be(JsonLineLogSink.DefaultMaxFileBytes);
    }

    [Test]
    public void DefaultMaxFileBytes_IsTenMebibytes()
    {
        JsonLineLogSink.DefaultMaxFileBytes.Should().Be(10L * 1024 * 1024);
    }

    [Test]
    public void Write_CreatesTheLogDirectoryOnDemand()
    {
        using var sink = new JsonLineLogSink(_logDirectory);

        sink.Write(Record());

        File.Exists(sink.ActivePath).Should().BeTrue();
        sink.FailureCount.Should().Be(0);
    }

    // --- Exit criterion: every emitted line is valid JSON with all required fields ------------

    [Test]
    public void Write_SingleRecord_EmitsOneLineCarryingEveryContractField()
    {
        using var sink = new JsonLineLogSink(_logDirectory);
        var data = new Dictionary<string, string> { ["path"] = @"C:\temp\a.hak", ["count"] = "3" };

        sink.Write(Record(
            message: "wrote the file",
            level: LogLevel.Warn,
            category: "ArtifactPublisher",
            eventCode: "SRN0042",
            data: data,
            exceptionType: "System.IO.IOException",
            exceptionMessage: "disk full"));

        string[] lines = ReadLines(sink.ActivePath);
        lines.Should().HaveCount(1);

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        foreach (string property in RequiredProperties)
        {
            root.TryGetProperty(property, out _).Should().BeTrue($"'{property}' is part of the line contract");
        }

        root.GetProperty("level").GetString().Should().Be("Warn", "levels are greppable names, not ordinals");
        root.GetProperty("category").GetString().Should().Be("ArtifactPublisher");
        root.GetProperty("message").GetString().Should().Be("wrote the file");
        root.GetProperty("eventCode").GetString().Should().Be("SRN0042");
        root.GetProperty("exceptionType").GetString().Should().Be("System.IO.IOException");
        root.GetProperty("exceptionMessage").GetString().Should().Be("disk full");
        root.GetProperty("data").GetProperty("path").GetString().Should().Be(@"C:\temp\a.hak");
        root.GetProperty("data").GetProperty("count").GetString().Should().Be("3");
    }

    [Test]
    public void Write_SingleRecord_RoundTripsBackIntoALogRecord()
    {
        var original = Record(
            message: "round trip",
            level: LogLevel.Error,
            category: "Cache",
            eventCode: "SRN0001",
            data: new Dictionary<string, string> { ["k"] = "v" },
            exceptionType: "System.InvalidOperationException",
            exceptionMessage: "boom");

        using (var sink = new JsonLineLogSink(_logDirectory))
        {
            sink.Write(original);
        }

        var restored = JsonSerializer.Deserialize<LogRecord>(
            ReadLines(Path.Combine(_logDirectory, LogFileSet.ActiveFileName))[0], RecordOptions);

        restored.Should().NotBeNull();
        restored!.TimestampUtc.Should().Be(original.TimestampUtc);
        restored.Level.Should().Be(original.Level);
        restored.Category.Should().Be(original.Category);
        restored.Message.Should().Be(original.Message);
        restored.EventCode.Should().Be(original.EventCode);
        restored.ExceptionType.Should().Be(original.ExceptionType);
        restored.ExceptionMessage.Should().Be(original.ExceptionMessage);
        restored.Data.Should().NotBeNull();
        restored.Data!["k"].Should().Be("v");
    }

    [Test]
    public void Write_OptionalFieldsAbsent_StillEmitsThemAsNull()
    {
        using var sink = new JsonLineLogSink(_logDirectory);

        sink.Write(Record());

        using JsonDocument document = JsonDocument.Parse(ReadLines(sink.ActivePath)[0]);
        foreach (string property in new[] { "eventCode", "data", "exceptionType", "exceptionMessage" })
        {
            document.RootElement.GetProperty(property).ValueKind.Should().Be(
                JsonValueKind.Null,
                "a consumer must be able to rely on the key existing rather than probing for it");
        }
    }

    [Test]
    public void Write_TimestampIsNormalisedToUtc()
    {
        using var sink = new JsonLineLogSink(_logDirectory);
        var local = new DateTimeOffset(2026, 3, 4, 15, 0, 0, TimeSpan.FromHours(5));

        sink.Write(new LogRecord(local, LogLevel.Info, "C", "m", null, null, null, null));

        var restored = JsonSerializer.Deserialize<LogRecord>(ReadLines(sink.ActivePath)[0], RecordOptions);
        restored!.TimestampUtc.Offset.Should().Be(TimeSpan.Zero);
        restored.TimestampUtc.Should().Be(local.ToUniversalTime());
    }

    [Test]
    public void Write_MessageContainingNewlines_StaysOnASinglePhysicalLine()
    {
        using var sink = new JsonLineLogSink(_logDirectory);
        const string message = "first\nsecond\r\nthird";

        sink.Write(Record(message: message));
        sink.Write(Record(message: "after"));

        string[] lines = ReadLines(sink.ActivePath);
        lines.Should().HaveCount(2, "escaping, not splitting, is how a multi-line message is stored");
        JsonSerializer.Deserialize<LogRecord>(lines[0], RecordOptions)!.Message.Should().Be(message);
        JsonSerializer.Deserialize<LogRecord>(lines[1], RecordOptions)!.Message.Should().Be("after");
    }

    [Test]
    public void Write_ManyRecords_AppendsOneLineEachInOrder()
    {
        using var sink = new JsonLineLogSink(_logDirectory);

        for (int i = 0; i < 50; i++)
        {
            sink.Write(Record(message: "record-" + i.ToString("D2")));
        }

        string[] lines = ReadLines(sink.ActivePath);
        lines.Should().HaveCount(50);
        for (int i = 0; i < 50; i++)
        {
            JsonSerializer.Deserialize<LogRecord>(lines[i], RecordOptions)!
                .Message.Should().Be("record-" + i.ToString("D2"));
        }
    }

    [Test]
    public void Write_SecondSinkInstance_AppendsRatherThanTruncating()
    {
        using (var first = new JsonLineLogSink(_logDirectory))
        {
            first.Write(Record(message: "before restart"));
        }

        using (var second = new JsonLineLogSink(_logDirectory))
        {
            second.Write(Record(message: "after restart"));
        }

        ReadLines(Path.Combine(_logDirectory, LogFileSet.ActiveFileName)).Should().HaveCount(
            2, "a restart must not discard the previous session's log");
    }

    // --- Tailing -----------------------------------------------------------------------------

    [Test]
    public void Write_WhileAnOperatorHoldsTheFileOpenForReading_KeepsWorking()
    {
        using var sink = new JsonLineLogSink(_logDirectory);
        sink.Write(Record(message: "one"));

        // The share mode Get-Content -Wait uses.
        using var tail = new FileStream(sink.ActivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(tail, Encoding.UTF8);
        reader.ReadToEnd();

        sink.Write(Record(message: "two"));
        sink.Write(Record(message: "three"));

        sink.FailureCount.Should().Be(0, "a tailing operator must never break the writer");
        string appended = reader.ReadToEnd();
        appended.Should().Contain("two").And.Contain("three");
    }

    // --- Exit criterion: eight concurrent writers, N well-formed lines, no interleaving --------

    [Test]
    public void Write_FromEightConcurrentWriters_ProducesExactlyNWellFormedLines()
    {
        const int writers = 8;
        const int perWriter = 250;

        using var sink = new JsonLineLogSink(_logDirectory);
        using var start = new ManualResetEventSlim(false);

        var threads = new Thread[writers];
        for (int w = 0; w < writers; w++)
        {
            int writerId = w;
            threads[w] = new Thread(() =>
            {
                start.Wait();
                for (int i = 0; i < perWriter; i++)
                {
                    sink.Write(Record(
                        message: $"writer {writerId} record {i}\nwith an embedded newline",
                        category: "Writer" + writerId,
                        data: new Dictionary<string, string> { ["writer"] = writerId.ToString(), ["seq"] = i.ToString() }));
                }
            });
            threads[w].Start();
        }

        start.Set();
        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        sink.FailureCount.Should().Be(0);

        string[] lines = ReadLines(sink.ActivePath);
        lines.Should().HaveCount(writers * perWriter);

        var perWriterCounts = new int[writers];
        foreach (string line in lines)
        {
            var restored = JsonSerializer.Deserialize<LogRecord>(line, RecordOptions);
            restored.Should().NotBeNull("every physical line must be a complete JSON object");
            restored!.Data.Should().NotBeNull();
            perWriterCounts[int.Parse(restored.Data!["writer"])]++;
        }

        perWriterCounts.Should().AllSatisfy(count => count.Should().Be(perWriter));
    }

    // --- Exit criterion: a broken destination never throws out of the sink --------------------

    [Test]
    public void Write_WhenTheActiveFilePathIsUnusable_DoesNotThrowAndCountsTheFailure()
    {
        // The most faithful reproduction of "the log destination vanished under us" that Windows
        // permits: the sink releases its handle between records, so a genuinely deleted directory is
        // simply recreated (covered below). Blocking the active file name with a directory produces
        // the unrecoverable variant — an open that can never succeed.
        Directory.CreateDirectory(Path.Combine(_logDirectory, LogFileSet.ActiveFileName));
        using var sink = new JsonLineLogSink(_logDirectory);

        Action write = () => sink.Write(Record(message: "into the void"));

        write.Should().NotThrow("a logger must never turn a diagnostic into an outage");
        write.Should().NotThrow();
        sink.FailureCount.Should().Be(2, "the caller cannot see the failure, so the sink must count it");
    }

    [Test]
    public void Write_AfterAFailureIsRepaired_ResumesWriting()
    {
        string blocker = Path.Combine(_logDirectory, LogFileSet.ActiveFileName);
        Directory.CreateDirectory(blocker);
        using var sink = new JsonLineLogSink(_logDirectory);
        sink.Write(Record(message: "lost"));

        Directory.Delete(blocker);
        sink.Write(Record(message: "recovered"));

        sink.FailureCount.Should().Be(1);
        string[] lines = ReadLines(sink.ActivePath);
        lines.Should().HaveCount(1);
        JsonSerializer.Deserialize<LogRecord>(lines[0], RecordOptions)!.Message.Should().Be("recovered");
    }

    [Test]
    public void Write_WhenTheLogDirectoryIsDeletedMidRun_DoesNotThrowAndRecreatesIt()
    {
        using var sink = new JsonLineLogSink(_logDirectory);
        sink.Write(Record(message: "before"));

        // Only possible because the sink does not hold the handle between records; that is exactly
        // why it does not hold it.
        Directory.Delete(_logDirectory, recursive: true);
        Directory.Exists(_logDirectory).Should().BeFalse();

        Action write = () => sink.Write(Record(message: "after"));

        write.Should().NotThrow();
        sink.FailureCount.Should().Be(0);
        string[] lines = ReadLines(sink.ActivePath);
        lines.Should().HaveCount(1);
        JsonSerializer.Deserialize<LogRecord>(lines[0], RecordOptions)!.Message.Should().Be("after");
    }

    // --- Lifetime ----------------------------------------------------------------------------

    [Test]
    public void Flush_IsSafeBeforeAnyRecordAndAfterDispose()
    {
        var sink = new JsonLineLogSink(_logDirectory);

        Action flushEmpty = sink.Flush;
        flushEmpty.Should().NotThrow();

        sink.Write(Record());
        sink.Dispose();

        flushEmpty.Should().NotThrow();
    }

    [Test]
    public void Dispose_IsIdempotentAndMakesLaterWritesNoOps()
    {
        var sink = new JsonLineLogSink(_logDirectory);
        sink.Write(Record(message: "kept"));

        sink.Dispose();
        Action disposeAgain = sink.Dispose;
        disposeAgain.Should().NotThrow();

        Action writeAfterDispose = () => sink.Write(Record(message: "dropped"));
        writeAfterDispose.Should().NotThrow();

        string[] lines = ReadLines(Path.Combine(_logDirectory, LogFileSet.ActiveFileName));
        lines.Should().HaveCount(1);
        lines[0].Should().Contain("kept").And.NotContain("dropped");
    }

    [Test]
    public void Constructor_RejectsANonPositiveSizeCap()
    {
        Action zero = () => new JsonLineLogSink(_logDirectory, maxFileBytes: 0);

        zero.Should().Throw<ArgumentOutOfRangeException>(
            "construction is a programming-time mistake, unlike a write, which must stay silent");
    }
}
