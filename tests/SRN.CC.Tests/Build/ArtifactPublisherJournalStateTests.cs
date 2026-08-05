using System.Text;
using System.Text.Json;
using NUnit.Framework;
using SRN.CC.Core.Build;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Build;
using SRN.CC.Infrastructure.Logging;

namespace SRN.CC.Tests.Build;

/// <summary>
/// Failure injected at every <see cref="PublicationState"/>, driven through the real file system.
/// </summary>
/// <remarks>
/// <para>
/// The suite previously exercised two of the five states, both by pointing the publisher at a missing
/// temp file. That left the interesting half of the transaction untested: the states a crash leaves
/// behind, where the destination has already been half replaced and the only thing standing between
/// the user and a corrupt pair is the rollback.
/// </para>
/// <para>
/// Every failure here is a genuine one — an exclusive handle a rename cannot get past, a file that is
/// really absent — rather than a fake that reports an error. The distinction matters: a rollback that
/// fails because Windows refused a rename behaves differently from one told to fail.
/// </para>
/// </remarks>
[TestFixture]
public class ArtifactPublisherJournalStateTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "srncc_pub_state_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestCase(PublicationState.Prepared)]
    [TestCase(PublicationState.BackedUp)]
    [TestCase(PublicationState.HakReplaced)]
    [TestCase(PublicationState.ManifestReplaced)]
    [TestCase(PublicationState.Committed)]
    public async Task Recovery_FromEveryState_LeavesDestinationPairByteForByte(PublicationState state)
    {
        var scene = PublicationCrashScene.Create(_tempDir, state, destinationsExistedBefore: true);
        var logger = new RecordingAppLogger();

        bool recovered = await new ArtifactPublisher(logger).RecoverPendingJournalAsync(_tempDir);

        // Below Committed the previous valid destination is the pre-transaction pair; at Committed it
        // is the newly published pair, which recovery must finish rather than undo.
        bool committed = state == PublicationState.Committed;
        byte[] expectedHak = committed ? scene.NewHakBytes : scene.OldHakBytes;
        byte[] expectedManifest = committed ? scene.NewManifestBytes : scene.OldManifestBytes;

        Assert.That(recovered, Is.True, $"A pending journal in state {state} must be recovered.");
        Assert.That(File.ReadAllBytes(scene.DestinationHakPath), Is.EqualTo(expectedHak));
        Assert.That(File.ReadAllBytes(scene.DestinationManifestPath), Is.EqualTo(expectedManifest));
        Assert.That(logger.WithCode(ArtifactPublisher.RollbackIncompleteEventCode), Is.Empty);
        AssertNoTransactionLeftovers(_tempDir);
    }

    [TestCase(PublicationState.Prepared)]
    [TestCase(PublicationState.BackedUp)]
    [TestCase(PublicationState.HakReplaced)]
    [TestCase(PublicationState.ManifestReplaced)]
    [TestCase(PublicationState.Committed)]
    public async Task Recovery_FromEveryState_WithNoPriorDestination_RemovesOrKeepsTheNewPair(PublicationState state)
    {
        var scene = PublicationCrashScene.Create(_tempDir, state, destinationsExistedBefore: false);

        bool recovered = await new ArtifactPublisher().RecoverPendingJournalAsync(_tempDir);

        Assert.That(recovered, Is.True);
        if (state == PublicationState.Committed)
        {
            Assert.That(File.ReadAllBytes(scene.DestinationHakPath), Is.EqualTo(scene.NewHakBytes));
            Assert.That(File.ReadAllBytes(scene.DestinationManifestPath), Is.EqualTo(scene.NewManifestBytes));
        }
        else
        {
            Assert.That(File.Exists(scene.DestinationHakPath), Is.False,
                "A HAK this transaction created must not survive its rollback.");
            Assert.That(File.Exists(scene.DestinationManifestPath), Is.False,
                "A manifest this transaction created must not survive its rollback.");
        }

        AssertNoTransactionLeftovers(_tempDir);
    }

    [Test]
    public async Task PublishAsync_FailureWhileBackingUp_RollsBackFromPreparedByteForByte()
    {
        var pair = DestinationPair.Create(_tempDir);
        byte[] originalHak = File.ReadAllBytes(pair.HakPath);
        var logger = new RecordingAppLogger();

        // An exclusive handle on the destination makes the very first backup copy fail, which is the
        // only way to reach the catch handler while the journal still says Prepared.
        using (new FileStream(pair.HakPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await new ArtifactPublisher(logger).PublishAsync(pair.Plan, pair.TempHakPath, pair.TempManifestPath);
            Assert.That(result.IsSuccess, Is.False);
        }

        Assert.That(File.ReadAllBytes(pair.HakPath), Is.EqualTo(originalHak));
        Assert.That(File.ReadAllBytes(pair.ManifestPath), Is.EqualTo(pair.OriginalManifestBytes));
        Assert.That(logger.WithCode(ArtifactPublisher.RollbackIncompleteEventCode), Is.Empty,
            "Nothing had been replaced yet, so the rollback must be reported as complete.");
        AssertNoTransactionLeftovers(_tempDir);
    }

    [Test]
    public async Task PublishAsync_FailureReplacingHak_RollsBackFromHakReplacedByteForByte()
    {
        var pair = DestinationPair.Create(_tempDir);
        File.Delete(pair.TempHakPath);
        var logger = new RecordingAppLogger();

        var result = await new ArtifactPublisher(logger).PublishAsync(pair.Plan, pair.TempHakPath, pair.TempManifestPath);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(File.ReadAllBytes(pair.HakPath), Is.EqualTo(pair.OriginalHakBytes));
        Assert.That(File.ReadAllBytes(pair.ManifestPath), Is.EqualTo(pair.OriginalManifestBytes));
        Assert.That(logger.WithCode(ArtifactPublisher.RollbackIncompleteEventCode), Is.Empty);
        AssertNoTransactionLeftovers(_tempDir);
    }

    [Test]
    public async Task PublishAsync_FailureReplacingManifest_RollsBackTheAlreadyReplacedHakByteForByte()
    {
        var pair = DestinationPair.Create(_tempDir);
        File.Delete(pair.TempManifestPath);
        var logger = new RecordingAppLogger();

        var result = await new ArtifactPublisher(logger).PublishAsync(pair.Plan, pair.TempHakPath, pair.TempManifestPath);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(File.ReadAllBytes(pair.HakPath), Is.EqualTo(pair.OriginalHakBytes),
            "The HAK was already replaced when the manifest failed; rollback must put the old one back.");
        Assert.That(File.ReadAllBytes(pair.ManifestPath), Is.EqualTo(pair.OriginalManifestBytes));
        Assert.That(logger.WithCode(ArtifactPublisher.RollbackIncompleteEventCode), Is.Empty);
        AssertNoTransactionLeftovers(_tempDir);
    }

    [Test]
    public async Task PublishAsync_WhenRollbackCannotRestoreTheManifest_ReportsThePartialRollback()
    {
        var pair = DestinationPair.Create(_tempDir);
        var logger = new RecordingAppLogger();

        // FileShare.Read lets the backup copy read the manifest but denies the rename that replaces it
        // and, later, the copy that would restore it — a real half-completed rollback.
        using (new FileStream(pair.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await new ArtifactPublisher(logger).PublishAsync(pair.Plan, pair.TempHakPath, pair.TempManifestPath);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Logs.Any(line => line.Contains("Rollback INCOMPLETE", StringComparison.Ordinal)), Is.True,
                "A rollback that could not restore the destination must say so in the publication log.");

            LogRecord[] incomplete = logger.WithCode(ArtifactPublisher.RollbackIncompleteEventCode).ToArray();
            Assert.That(incomplete, Has.Length.EqualTo(1));
            Assert.That(incomplete[0].Level, Is.EqualTo(LogLevel.Error));
            Assert.That(incomplete[0].Data!["destinationRestored"], Is.EqualTo("false"));
            Assert.That(logger.WithCode(ArtifactPublisher.RollbackStepFailedEventCode), Is.Not.Empty);

            Assert.That(File.ReadAllBytes(pair.HakPath), Is.EqualTo(pair.OriginalHakBytes),
                "The HAK half of the pair must still be rolled back even when the manifest half cannot be.");
            Assert.That(Directory.GetFiles(_tempDir, "*" + JournalSuffix), Is.Not.Empty,
                "The journal must survive a partial rollback so recovery can finish the job.");
            Assert.That(Directory.GetFiles(_tempDir, "*.bak"), Is.Not.Empty,
                "Backups must survive a partial rollback; they are the only way back.");
        }

        // With the handle released, recovery finishes what the rollback could not.
        bool recovered = await new ArtifactPublisher().RecoverPendingJournalAsync(_tempDir);

        Assert.That(recovered, Is.True);
        Assert.That(File.ReadAllBytes(pair.HakPath), Is.EqualTo(pair.OriginalHakBytes));
        Assert.That(File.ReadAllBytes(pair.ManifestPath), Is.EqualTo(pair.OriginalManifestBytes));
        AssertNoTransactionLeftovers(_tempDir);
    }

    internal const string JournalSuffix = ".publication-journal.json";

    /// <summary>Asserts no journal, backup or temp artifact of a transaction was left behind.</summary>
    internal static void AssertNoTransactionLeftovers(string directory)
    {
        Assert.That(Directory.GetFiles(directory, "*" + JournalSuffix), Is.Empty, "A journal file was left behind.");
        Assert.That(Directory.GetFiles(directory, PublicationCrashScene.LegacyJournalName), Is.Empty, "A legacy journal file was left behind.");
        Assert.That(Directory.GetFiles(directory, "*.bak"), Is.Empty, "A backup file was left behind.");
        Assert.That(Directory.GetFiles(directory, "*.tmp.*"), Is.Empty, "A temp artifact was left behind.");
    }

    /// <summary>A live destination pair plus staged temp artifacts, ready for one publication.</summary>
    internal sealed class DestinationPair
    {
        private DestinationPair(
            string hakPath,
            string manifestPath,
            string tempHakPath,
            string tempManifestPath,
            BuildPlan plan)
        {
            HakPath = hakPath;
            ManifestPath = manifestPath;
            TempHakPath = tempHakPath;
            TempManifestPath = tempManifestPath;
            Plan = plan;
            OriginalHakBytes = File.ReadAllBytes(hakPath);
            OriginalManifestBytes = File.ReadAllBytes(manifestPath);
        }

        public string HakPath { get; }
        public string ManifestPath { get; }
        public string TempHakPath { get; }
        public string TempManifestPath { get; }
        public BuildPlan Plan { get; }
        public byte[] OriginalHakBytes { get; }
        public byte[] OriginalManifestBytes { get; }

        public static DestinationPair Create(string directory)
        {
            string hakPath = Path.Combine(directory, "content.hak");
            string manifestPath = Path.Combine(directory, "content.manifest.json");
            string tempHakPath = Path.Combine(directory, "staged.tmp.hak");
            string tempManifestPath = Path.Combine(directory, "staged.tmp.manifest.json");

            PublicationCrashScene.WriteOldHak(hakPath);
            File.WriteAllBytes(manifestPath, PublicationCrashScene.OldManifest());
            PublicationCrashScene.WriteNewHak(tempHakPath);
            File.WriteAllBytes(tempManifestPath, PublicationCrashScene.NewManifest());

            var plan = new BuildPlan
            {
                DestinationHakPath = hakPath,
                DestinationManifestPath = manifestPath,
                FrozenSources = Array.Empty<AssetSource>(),
                Items = Array.Empty<BuildItem>(),
                CreatedUtc = DateTime.UtcNow
            };

            return new DestinationPair(hakPath, manifestPath, tempHakPath, tempManifestPath, plan);
        }
    }
}

/// <summary>
/// Recreates on disk exactly what a crash at a given <see cref="PublicationState"/> leaves behind.
/// </summary>
/// <remarks>
/// The publisher writes its journal before each destructive step, so "crashed at state X" is a
/// precise, reproducible arrangement of files rather than a guess. Building that arrangement by hand
/// is the only way to reach the states a publication cannot be steered into from outside — a process
/// that dies between two renames leaves no seam for a test to hook.
/// </remarks>
internal sealed class PublicationCrashScene
{
    internal const string LegacyJournalName = "publication-journal.json";
    private const string TransactionId = "a1b2c3d4";

    private PublicationCrashScene(
        string destinationHakPath,
        string destinationManifestPath,
        string journalPath,
        byte[] oldHakBytes,
        byte[] oldManifestBytes,
        byte[] newHakBytes,
        byte[] newManifestBytes,
        PublicationJournal journal)
    {
        DestinationHakPath = destinationHakPath;
        DestinationManifestPath = destinationManifestPath;
        JournalPath = journalPath;
        OldHakBytes = oldHakBytes;
        OldManifestBytes = oldManifestBytes;
        NewHakBytes = newHakBytes;
        NewManifestBytes = newManifestBytes;
        Journal = journal;
    }

    public string DestinationHakPath { get; }
    public string DestinationManifestPath { get; }
    public string JournalPath { get; }
    public byte[] OldHakBytes { get; }
    public byte[] OldManifestBytes { get; }
    public byte[] NewHakBytes { get; }
    public byte[] NewManifestBytes { get; }
    public PublicationJournal Journal { get; }

    public string HakBackupPath => Journal.HakBackupPath;
    public string ManifestBackupPath => Journal.ManifestBackupPath;

    /// <summary>Lays out the destination, backup, temp and journal files for one crashed transaction.</summary>
    /// <param name="directory">Directory that holds the whole transaction.</param>
    /// <param name="state">State the journal last recorded before the crash.</param>
    /// <param name="destinationsExistedBefore">Whether the pair already existed when publication began.</param>
    /// <param name="baseName">Base file name, so several scenes can share one directory.</param>
    /// <param name="useLegacyJournalName">Writes the pre-transaction-id fixed journal name instead.</param>
    public static PublicationCrashScene Create(
        string directory,
        PublicationState state,
        bool destinationsExistedBefore,
        string baseName = "content",
        bool useLegacyJournalName = false)
    {
        string destinationHak = Path.Combine(directory, baseName + ".hak");
        string destinationManifest = Path.Combine(directory, baseName + ".manifest.json");
        string tempHak = Path.Combine(directory, baseName + ".staged.tmp.hak");
        string tempManifest = Path.Combine(directory, baseName + ".staged.tmp.manifest.json");
        string hakBackup = destinationHak + $".{TransactionId}.bak";
        string manifestBackup = destinationManifest + $".{TransactionId}.bak";
        string journalPath = useLegacyJournalName
            ? Path.Combine(directory, LegacyJournalName)
            : Path.Combine(directory, $"{baseName}.hak.{TransactionId}{ArtifactPublisherJournalStateTests.JournalSuffix}");

        byte[] oldHakBytes = Array.Empty<byte>();
        byte[] oldManifestBytes = Array.Empty<byte>();

        if (destinationsExistedBefore)
        {
            WriteOldHak(destinationHak);
            oldHakBytes = File.ReadAllBytes(destinationHak);
            oldManifestBytes = OldManifest();
            File.WriteAllBytes(destinationManifest, oldManifestBytes);

            // Backups exist from BackedUp onwards.
            if (state >= PublicationState.BackedUp)
            {
                File.Copy(destinationHak, hakBackup, overwrite: true);
                File.Copy(destinationManifest, manifestBackup, overwrite: true);
            }
        }

        WriteNewHak(tempHak);
        byte[] newHakBytes = File.ReadAllBytes(tempHak);
        byte[] newManifestBytes = NewManifest();
        File.WriteAllBytes(tempManifest, newManifestBytes);

        // The journal records HakReplaced before the rename, so the worst case a crash in that state
        // can leave is a completed rename: that is what is reproduced here.
        if (state >= PublicationState.HakReplaced)
        {
            File.Move(tempHak, destinationHak, overwrite: true);
        }

        if (state >= PublicationState.ManifestReplaced)
        {
            File.Move(tempManifest, destinationManifest, overwrite: true);
        }

        var journal = new PublicationJournal
        {
            DestinationHakPath = destinationHak,
            DestinationManifestPath = destinationManifest,
            TempHakPath = tempHak,
            TempManifestPath = tempManifest,
            HakBackupPath = hakBackup,
            ManifestBackupPath = manifestBackup,
            HakExistedBefore = destinationsExistedBefore,
            ManifestExistedBefore = destinationsExistedBefore,
            State = state,
            CreatedUtc = DateTime.UtcNow,
            LastUpdatedUtc = DateTime.UtcNow
        };

        WriteJournal(journalPath, journal);

        return new PublicationCrashScene(
            destinationHak,
            destinationManifest,
            journalPath,
            oldHakBytes,
            oldManifestBytes,
            newHakBytes,
            newManifestBytes,
            journal);
    }

    /// <summary>Serializes a journal the way the publisher does, so recovery can read it back.</summary>
    public static void WriteJournal(string journalPath, PublicationJournal journal)
    {
        File.WriteAllBytes(journalPath, JsonSerializer.SerializeToUtf8Bytes(journal));
    }

    public static void WriteOldHak(string path) =>
        RealHakFixtureFactory.WriteHak(path, RealHakFixtureFactory.DefaultCorpus());

    /// <summary>Genuine HAK bytes that differ from <see cref="WriteOldHak"/>, so a swap is detectable.</summary>
    public static void WriteNewHak(string path) =>
        RealHakFixtureFactory.WriteHak(path, RealHakFixtureFactory.DefaultCorpus().Take(4).ToList());

    public static byte[] OldManifest() =>
        Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"BuildId\":\"old-build\"}");

    public static byte[] NewManifest() =>
        Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"BuildId\":\"new-build\"}");
}

/// <summary>
/// An <see cref="IAppLogger"/> that keeps every record for assertions.
/// </summary>
/// <remarks>
/// Deliberately self-contained: a shared test logger would couple these tests to another fixture's
/// lifetime, and the promotion of <see cref="AppLogger.EventCodeKey"/> into
/// <see cref="LogRecord.EventCode"/> is repeated here rather than borrowed so that a regression in
/// the real logger cannot silently disable these assertions.
/// </remarks>
internal sealed class RecordingAppLogger : IAppLogger
{
    private readonly List<LogRecord> _records = new();

    public IReadOnlyList<LogRecord> Records
    {
        get { lock (_records) { return _records.ToArray(); } }
    }

    public void Log(
        LogLevel level,
        string category,
        string message,
        Exception? exception = null,
        IReadOnlyDictionary<string, string>? data = null)
    {
        string? eventCode = null;
        if (data is not null && data.TryGetValue(AppLogger.EventCodeKey, out string? code))
        {
            eventCode = code;
        }

        var record = new LogRecord(
            DateTimeOffset.UtcNow,
            level,
            category,
            message,
            eventCode,
            data,
            exception?.GetType().FullName,
            exception?.Message);

        lock (_records)
        {
            _records.Add(record);
        }
    }

    public IEnumerable<LogRecord> WithCode(string eventCode) =>
        Records.Where(record => string.Equals(record.EventCode, eventCode, StringComparison.Ordinal));
}
