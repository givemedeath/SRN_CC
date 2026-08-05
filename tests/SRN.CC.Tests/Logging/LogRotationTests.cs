using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Logging;

namespace SRN.CC.Tests.Logging;

/// <summary>
/// Naming, ordering and retention coverage for <see cref="LogFileSet"/>, plus the rotation rule
/// <see cref="JsonLineLogSink"/> applies on top of it.
/// </summary>
[TestFixture]
public class LogRotationTests
{
    private const int TestCapBytes = 4 * 1024;

    private static readonly JsonSerializerOptions RecordOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private string _root = string.Empty;
    private string _logDirectory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "srncc-rot-" + Guid.NewGuid().ToString("N"));
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

    private static LogRecord Record(string message) =>
        new(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
            LogLevel.Info, "Rotation", message, null, null, null, null);

    private string TouchRotated(DateTimeOffset stamp)
    {
        Directory.CreateDirectory(_logDirectory);
        string path = Path.Combine(_logDirectory, LogFileSet.BuildRotatedFileName(stamp));
        File.WriteAllText(path, stamp.ToString("O", CultureInfo.InvariantCulture));
        return path;
    }

    private static string[] FileNames(IEnumerable<string> paths) =>
        paths.Select(Path.GetFileName).Select(name => name!).ToArray();

    // --- Naming ------------------------------------------------------------------------------

    [Test]
    public void BuildRotatedFileName_UsesTheDocumentedStamp()
    {
        var stamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 89, TimeSpan.Zero);

        LogFileSet.BuildRotatedFileName(stamp).Should().Be("srncc.20260304T050607089Z.log");
    }

    [Test]
    public void BuildRotatedFileName_NormalisesANonUtcInstantBeforeStamping()
    {
        var local = new DateTimeOffset(2026, 3, 4, 10, 6, 7, 89, TimeSpan.FromHours(5));

        LogFileSet.BuildRotatedFileName(local).Should().Be("srncc.20260304T050607089Z.log");
    }

    [Test]
    public void BuildRotatedFileName_OrdinalOrderMatchesChronologicalOrder()
    {
        var earlier = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 89, TimeSpan.Zero);
        var later = earlier.AddMilliseconds(1);
        var muchLater = earlier.AddYears(1);

        string a = LogFileSet.BuildRotatedFileName(earlier);
        string b = LogFileSet.BuildRotatedFileName(later);
        string c = LogFileSet.BuildRotatedFileName(muchLater);

        string.CompareOrdinal(a, b).Should().BeNegative("pruning sorts by name, never by file metadata");
        string.CompareOrdinal(b, c).Should().BeNegative();
    }

    [Test]
    public void TryParseRotatedFileName_AcceptsAStampedNameAndRecoversTheInstant()
    {
        var stamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 89, TimeSpan.Zero);

        LogFileSet.TryParseRotatedFileName(LogFileSet.BuildRotatedFileName(stamp), out DateTimeOffset parsed)
            .Should().BeTrue();

        parsed.Should().Be(stamp);
        parsed.Offset.Should().Be(TimeSpan.Zero);
    }

    [Test]
    [TestCase("srncc.log")]
    [TestCase("srncc..log")]
    [TestCase("srncc.notatimestamp.log")]
    [TestCase("srncc.20260304T050607089Z.txt")]
    [TestCase("other.20260304T050607089Z.log")]
    [TestCase("srncc.20261304T050607089Z.log")]
    [TestCase("")]
    public void TryParseRotatedFileName_RejectsAnythingThatIsNotARotatedFile(string fileName)
    {
        LogFileSet.TryParseRotatedFileName(fileName, out _).Should().BeFalse();
    }

    [Test]
    public void ActivePath_LivesUnderTheAppPathsLogDirectory()
    {
        var files = new LogFileSet(_logDirectory);

        files.ActivePath.Should().Be(Path.Combine(_logDirectory, "srncc.log"));
        Path.GetFileName(files.DirectoryPath).Should().Be(AppPaths.LogDirectoryName);
        files.MaxFileCount.Should().Be(10);
    }

    // --- Ordering ----------------------------------------------------------------------------

    [Test]
    public void ListRotatedPaths_OnAMissingDirectory_IsEmptyRatherThanThrowing()
    {
        new LogFileSet(_logDirectory).ListRotatedPaths().Should().BeEmpty();
    }

    [Test]
    public void ListRotatedPaths_ReturnsOldestFirst()
    {
        var baseStamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        TouchRotated(baseStamp.AddMinutes(5));
        TouchRotated(baseStamp);
        TouchRotated(baseStamp.AddMinutes(1));

        FileNames(new LogFileSet(_logDirectory).ListRotatedPaths()).Should().ContainInOrder(
            LogFileSet.BuildRotatedFileName(baseStamp),
            LogFileSet.BuildRotatedFileName(baseStamp.AddMinutes(1)),
            LogFileSet.BuildRotatedFileName(baseStamp.AddMinutes(5)));
    }

    [Test]
    public void ListRotatedPaths_IgnoresTheActiveFileAndForeignFiles()
    {
        var stamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        TouchRotated(stamp);
        File.WriteAllText(Path.Combine(_logDirectory, LogFileSet.ActiveFileName), "active");
        File.WriteAllText(Path.Combine(_logDirectory, "srncc.operator-notes.log"), "keep me");
        File.WriteAllText(Path.Combine(_logDirectory, "readme.txt"), "keep me too");

        FileNames(new LogFileSet(_logDirectory).ListRotatedPaths()).Should().Equal(
            LogFileSet.BuildRotatedFileName(stamp));
    }

    [Test]
    public void ReserveRotatedPath_AdvancesTheStampWhenTheNameIsAlreadyTaken()
    {
        var stamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 89, TimeSpan.Zero);
        TouchRotated(stamp);
        TouchRotated(stamp.AddMilliseconds(1));
        var files = new LogFileSet(_logDirectory);

        string reserved = files.ReserveRotatedPath(stamp);

        Path.GetFileName(reserved).Should().Be(LogFileSet.BuildRotatedFileName(stamp.AddMilliseconds(2)));
        string.CompareOrdinal(
            LogFileSet.BuildRotatedFileName(stamp.AddMilliseconds(1)),
            Path.GetFileName(reserved)).Should().BeNegative("collision handling must preserve the sort order");
    }

    // --- Retention ---------------------------------------------------------------------------

    [Test]
    public void Prune_UnderBudget_DeletesNothing()
    {
        var baseStamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        TouchRotated(baseStamp);
        TouchRotated(baseStamp.AddMinutes(1));

        new LogFileSet(_logDirectory, maxFileCount: 4).Prune().Should().Be(0);
        Directory.GetFiles(_logDirectory).Should().HaveCount(2);
    }

    [Test]
    public void Prune_ReservesASlotForTheActiveFileEvenWhileItIsAbsent()
    {
        var baseStamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        for (int i = 0; i < 3; i++)
        {
            TouchRotated(baseStamp.AddMinutes(i));
        }

        // No active file on disk: this is the window right after a rename, and the budget must still
        // account for the file the sink is about to create.
        new LogFileSet(_logDirectory, maxFileCount: 3).Prune().Should().Be(1);

        FileNames(Directory.GetFiles(_logDirectory)).Should().Equal(
            LogFileSet.BuildRotatedFileName(baseStamp.AddMinutes(1)),
            LogFileSet.BuildRotatedFileName(baseStamp.AddMinutes(2)));
    }

    [Test]
    public void Prune_DeletesTheOldestFirst()
    {
        var baseStamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        for (int i = 0; i < 6; i++)
        {
            TouchRotated(baseStamp.AddMinutes(i));
        }

        File.WriteAllText(Path.Combine(_logDirectory, LogFileSet.ActiveFileName), "active");

        new LogFileSet(_logDirectory, maxFileCount: 3).Prune().Should().Be(4);

        FileNames(Directory.GetFiles(_logDirectory)).Should().BeEquivalentTo(new[]
        {
            LogFileSet.ActiveFileName,
            LogFileSet.BuildRotatedFileName(baseStamp.AddMinutes(4)),
            LogFileSet.BuildRotatedFileName(baseStamp.AddMinutes(5))
        });
    }

    [Test]
    public void Prune_NeverDeletesAFileItDoesNotOwn()
    {
        var baseStamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        for (int i = 0; i < 5; i++)
        {
            TouchRotated(baseStamp.AddMinutes(i));
        }

        File.WriteAllText(Path.Combine(_logDirectory, "crash-dump.txt"), "operator evidence");

        new LogFileSet(_logDirectory, maxFileCount: 2).Prune();

        File.Exists(Path.Combine(_logDirectory, "crash-dump.txt")).Should().BeTrue(
            "retention must not be able to eat something a human put there");
    }

    [Test]
    public void Constructor_RejectsANonPositiveFileCount()
    {
        Action zero = () => new LogFileSet(_logDirectory, maxFileCount: 0);

        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    // --- Exit criterion: 4 KiB cap, never more than ten files, oldest deleted first -----------

    [Test]
    public void Write_UnderAFourKibibyteCap_KeepsAtMostTenFilesAndDeletesTheOldestFirst()
    {
        using var sink = new JsonLineLogSink(_logDirectory, maxFileBytes: TestCapBytes);
        var observedRotations = new List<string>();

        // Every record below renders to the same number of bytes — the counter is zero-padded — so
        // the file the first one lands in measures exactly one record.
        long recordBytes = 0;

        for (int i = 0; i < 400; i++)
        {
            var before = new HashSet<string>(FileNames(sink.Files.ListRotatedPaths()), StringComparer.Ordinal);
            sink.Write(Record($"record {i:D4} " + new string('x', 100)));

            if (i == 0)
            {
                recordBytes = new FileInfo(sink.ActivePath).Length;
                recordBytes.Should().BeGreaterThan(0);
            }

            foreach (string name in FileNames(sink.Files.ListRotatedPaths()))
            {
                if (before.Add(name))
                {
                    observedRotations.Add(name);
                }
            }

            Directory.GetFiles(_logDirectory).Length.Should().BeLessThanOrEqualTo(
                LogFileSet.DefaultMaxFileCount, "retention is enforced on every rotation, not at the end");
        }

        sink.FailureCount.Should().Be(0);
        observedRotations.Should().HaveCountGreaterThan(
            LogFileSet.DefaultMaxFileCount, "the run must actually outlive the retention window");

        string[] survivors = FileNames(sink.Files.ListRotatedPaths());
        survivors.Should().HaveCount(LogFileSet.DefaultMaxFileCount - 1);
        survivors.Should().Equal(
            observedRotations.TakeLast(LogFileSet.DefaultMaxFileCount - 1),
            "the survivors are the newest rotations; the oldest went first");

        foreach (string path in Directory.GetFiles(_logDirectory))
        {
            new FileInfo(path).Length.Should().BeLessThanOrEqualTo(
                TestCapBytes + recordBytes,
                "the cap is a pre-check, so a file may overshoot by at most the record that closed it");
        }
    }

    [Test]
    public void Write_AcrossARotation_MovesThePreviousContentIntoTheRotatedFileIntact()
    {
        // Retention is raised out of the way: this test is about rotation preserving records, and
        // pruning deliberately destroying the oldest of them is covered separately.
        using var sink = new JsonLineLogSink(_logDirectory, maxFileBytes: 512, maxFileCount: 50);

        for (int i = 0; i < 30; i++)
        {
            sink.Write(Record($"record {i:D2}"));
        }

        IReadOnlyList<string> rotated = sink.Files.ListRotatedPaths();
        rotated.Should().NotBeEmpty();

        var messages = new List<string>();
        foreach (string path in rotated.Append(sink.ActivePath))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                messages.Add(JsonSerializer.Deserialize<LogRecord>(line, RecordOptions)!.Message);
            }
        }

        messages.Should().HaveCount(30, "rotation moves records, it does not drop them");
        messages.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    // --- Exit criterion: an oversized record is written whole -------------------------------

    [Test]
    public void Write_ARecordLargerThanTheWholeCapIntoAnEmptyFile_WritesItWholeWithoutRotating()
    {
        using var sink = new JsonLineLogSink(_logDirectory, maxFileBytes: 512);
        string message = new('y', 4000);

        sink.Write(Record(message));

        sink.Files.ListRotatedPaths().Should().BeEmpty("there was nothing to preserve");
        string[] lines = File.ReadAllLines(sink.ActivePath);
        lines.Should().HaveCount(1);
        JsonSerializer.Deserialize<LogRecord>(lines[0], RecordOptions)!.Message.Should().Be(
            message, "an oversized record is never truncated — it is the one worth keeping");
        new FileInfo(sink.ActivePath).Length.Should().BeGreaterThan(512);
    }

    [Test]
    public void Write_ARecordLargerThanTheWholeCapAfterOtherRecords_RotatesFirstThenWritesItWhole()
    {
        using var sink = new JsonLineLogSink(_logDirectory, maxFileBytes: 512);
        sink.Write(Record("small"));
        string message = new('y', 4000);

        sink.Write(Record(message));

        IReadOnlyList<string> rotated = sink.Files.ListRotatedPaths();
        rotated.Should().HaveCount(1);
        File.ReadAllLines(rotated[0]).Should().ContainSingle().Which.Should().Contain("small");

        string[] lines = File.ReadAllLines(sink.ActivePath);
        lines.Should().HaveCount(1, "the oversized record lands alone, bounding the overshoot at one record");
        JsonSerializer.Deserialize<LogRecord>(lines[0], RecordOptions)!.Message.Should().Be(message);
    }

    [Test]
    public void Write_WithAnInjectedClock_StampsTheRotatedFileWithTheRotationInstant()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 3, 4, 5, 6, 7, 89, TimeSpan.Zero));
        using var sink = new JsonLineLogSink(_logDirectory, maxFileBytes: 256, timeProvider: clock);

        sink.Write(Record("first"));
        clock.UtcNow = clock.UtcNow.AddHours(2);
        sink.Write(Record(new string('z', 300)));

        FileNames(sink.Files.ListRotatedPaths()).Should().Equal(
            LogFileSet.BuildRotatedFileName(new DateTimeOffset(2026, 3, 4, 7, 6, 7, 89, TimeSpan.Zero)));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
