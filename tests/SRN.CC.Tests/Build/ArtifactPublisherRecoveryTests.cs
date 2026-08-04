using System.Text;
using NUnit.Framework;
using SRN.CC.Core.Build;
using SRN.CC.Core.Logging;
using SRN.CC.Infrastructure.Build;

namespace SRN.CC.Tests.Build;

/// <summary>
/// Covers <see cref="ArtifactPublisher.RecoverPendingJournalAsync"/>, which had no test at all.
/// </summary>
/// <remarks>
/// Recovery is the code that runs after the process that owned a transaction is gone, so it is the
/// only thing standing between a crashed build and a half-replaced content pack. Every case here is
/// driven through the real file system: the held lock is a real exclusive handle, the undeletable
/// backup is a real open file, and the corrupt journal is real garbage on disk.
/// </remarks>
[TestFixture]
public class ArtifactPublisherRecoveryTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "srncc_pub_recover_" + Guid.NewGuid().ToString("N"));
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

    [Test]
    public async Task RecoverPendingJournalAsync_MissingDirectory_ReportsNothingRecovered()
    {
        string missing = Path.Combine(_tempDir, "not-there");

        Assert.That(await new ArtifactPublisher().RecoverPendingJournalAsync(missing), Is.False);
    }

    [Test]
    public async Task RecoverPendingJournalAsync_NoJournals_ReportsNothingRecovered()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "unrelated.hak"), "not a journal");

        Assert.That(await new ArtifactPublisher().RecoverPendingJournalAsync(_tempDir), Is.False);
        Assert.That(File.Exists(Path.Combine(_tempDir, "unrelated.hak")), Is.True);
    }

    [Test]
    public async Task RecoverPendingJournalAsync_LegacyFixedNameJournal_IsRolledBackAndRemoved()
    {
        var scene = PublicationCrashScene.Create(
            _tempDir,
            PublicationState.ManifestReplaced,
            destinationsExistedBefore: true,
            useLegacyJournalName: true);

        Assert.That(Path.GetFileName(scene.JournalPath), Is.EqualTo(PublicationCrashScene.LegacyJournalName),
            "This case exists only to prove the pre-transaction-id journal name is still honoured.");

        bool recovered = await new ArtifactPublisher().RecoverPendingJournalAsync(_tempDir);

        Assert.That(recovered, Is.True);
        Assert.That(File.ReadAllBytes(scene.DestinationHakPath), Is.EqualTo(scene.OldHakBytes));
        Assert.That(File.ReadAllBytes(scene.DestinationManifestPath), Is.EqualTo(scene.OldManifestBytes));
        Assert.That(File.Exists(scene.JournalPath), Is.False);
        ArtifactPublisherJournalStateTests.AssertNoTransactionLeftovers(_tempDir);
    }

    [TestCase("{ this is not json", TestName = "RecoverPendingJournalAsync_CorruptJournal_Garbage")]
    [TestCase("null", TestName = "RecoverPendingJournalAsync_CorruptJournal_NullDocument")]
    [TestCase("{\"DestinationHakPath\":\"x.hak\"}", TestName = "RecoverPendingJournalAsync_CorruptJournal_MissingRequiredMembers")]
    public async Task RecoverPendingJournalAsync_CorruptJournal_IsReportedAndLeftInPlace(string journalContent)
    {
        string journalPath = Path.Combine(_tempDir, "content.hak.deadbeef.publication-journal.json");
        await File.WriteAllTextAsync(journalPath, journalContent, Encoding.UTF8);
        string destinationHak = Path.Combine(_tempDir, "content.hak");
        PublicationCrashScene.WriteOldHak(destinationHak);
        byte[] before = await File.ReadAllBytesAsync(destinationHak);
        var logger = new RecordingAppLogger();

        bool recovered = await new ArtifactPublisher(logger).RecoverPendingJournalAsync(_tempDir);

        Assert.That(recovered, Is.False);
        Assert.That(File.Exists(journalPath), Is.True, "An unreadable journal must be preserved for diagnosis.");
        Assert.That(await File.ReadAllBytesAsync(destinationHak), Is.EqualTo(before),
            "A journal that cannot be understood must not lead to the destination being touched.");
        Assert.That(logger.WithCode(ArtifactPublisher.JournalUnreadableEventCode), Is.Not.Empty,
            "The silent swallow this replaced made a corrupt journal indistinguishable from no journal.");
    }

    [Test]
    public async Task RecoverPendingJournalAsync_RunTwice_IsIdempotent()
    {
        var scene = PublicationCrashScene.Create(_tempDir, PublicationState.ManifestReplaced, destinationsExistedBefore: true);
        var publisher = new ArtifactPublisher();

        Assert.That(await publisher.RecoverPendingJournalAsync(_tempDir), Is.True);
        byte[] hakAfterFirst = await File.ReadAllBytesAsync(scene.DestinationHakPath);
        byte[] manifestAfterFirst = await File.ReadAllBytesAsync(scene.DestinationManifestPath);

        bool secondPass = await publisher.RecoverPendingJournalAsync(_tempDir);

        Assert.That(secondPass, Is.False, "There is nothing left to recover after a completed recovery.");
        Assert.That(hakAfterFirst, Is.EqualTo(scene.OldHakBytes));
        Assert.That(await File.ReadAllBytesAsync(scene.DestinationHakPath), Is.EqualTo(hakAfterFirst));
        Assert.That(await File.ReadAllBytesAsync(scene.DestinationManifestPath), Is.EqualTo(manifestAfterFirst));
        ArtifactPublisherJournalStateTests.AssertNoTransactionLeftovers(_tempDir);
    }

    [Test]
    public async Task RecoverPendingJournalAsync_RunTwiceOverCommittedTransaction_IsIdempotent()
    {
        var scene = PublicationCrashScene.Create(_tempDir, PublicationState.Committed, destinationsExistedBefore: true);
        var publisher = new ArtifactPublisher();

        Assert.That(await publisher.RecoverPendingJournalAsync(_tempDir), Is.True);
        Assert.That(await publisher.RecoverPendingJournalAsync(_tempDir), Is.False);
        Assert.That(await File.ReadAllBytesAsync(scene.DestinationHakPath), Is.EqualTo(scene.NewHakBytes),
            "A committed transaction must survive any number of recovery passes untouched.");
        Assert.That(await File.ReadAllBytesAsync(scene.DestinationManifestPath), Is.EqualTo(scene.NewManifestBytes));
        ArtifactPublisherJournalStateTests.AssertNoTransactionLeftovers(_tempDir);
    }

    [Test]
    public async Task RecoverPendingJournalAsync_CleanupFailureAfterCommitted_StillReportsSuccess()
    {
        var scene = PublicationCrashScene.Create(_tempDir, PublicationState.Committed, destinationsExistedBefore: true);
        var logger = new RecordingAppLogger();

        // A real undeletable backup: an exclusive handle, the same thing an antivirus scan or a
        // straggling process leaves behind.
        using (new FileStream(scene.HakBackupPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            bool recovered = await new ArtifactPublisher(logger).RecoverPendingJournalAsync(_tempDir);

            Assert.That(recovered, Is.True,
                "The artifacts are already published; failing to tidy a backup is not a failed recovery.");
            Assert.That(logger.WithCode(ArtifactPublisher.RollbackStepFailedEventCode), Is.Not.Empty,
                "The cleanup failure must still be visible in the log.");
            Assert.That(await File.ReadAllBytesAsync(scene.DestinationHakPath), Is.EqualTo(scene.NewHakBytes));
            Assert.That(File.Exists(scene.JournalPath), Is.True,
                "The journal is the only record of the orphaned backup, so it must outlive the failure.");
        }

        // Once the handle is gone the retained journal lets a later pass finish the cleanup.
        Assert.That(await new ArtifactPublisher().RecoverPendingJournalAsync(_tempDir), Is.True);
        Assert.That(await File.ReadAllBytesAsync(scene.DestinationHakPath), Is.EqualTo(scene.NewHakBytes));
        ArtifactPublisherJournalStateTests.AssertNoTransactionLeftovers(_tempDir);
    }

    [Test]
    public async Task RecoverPendingJournalAsync_WhilePublicationLockIsHeld_ReportsContentionAndLeavesDestinationAlone()
    {
        // A first recovery over a committed transaction both cleans that transaction up and
        // materialises the publication lock file for this destination pair, so the test can hold the
        // real lock instead of recomputing its name.
        var committedScene = PublicationCrashScene.Create(_tempDir, PublicationState.Committed, destinationsExistedBefore: true);
        Assert.That(await new ArtifactPublisher().RecoverPendingJournalAsync(_tempDir), Is.True);

        string lockPath = Directory.GetFiles(_tempDir, "*.publication.lock").Single();
        byte[] publishedHak = await File.ReadAllBytesAsync(committedScene.DestinationHakPath);

        // A second, pending transaction over the same pair, left behind by a crash.
        var pendingScene = PublicationCrashScene.Create(_tempDir, PublicationState.ManifestReplaced, destinationsExistedBefore: true);
        var logger = new RecordingAppLogger();

        using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool recovered = await new ArtifactPublisher(logger).RecoverPendingJournalAsync(_tempDir);

            Assert.That(recovered, Is.False, "Recovery must not claim success while somebody else owns the pair.");
            Assert.That(logger.WithCode(ArtifactPublisher.RecoveryContendedEventCode), Is.Not.Empty,
                "Contention must be reported, not silently swallowed as 'nothing to recover'.");
            Assert.That(File.Exists(pendingScene.JournalPath), Is.True,
                "A contended journal must stay on disk for the next attempt.");
        }

        // The destination is whatever the lock holder left there, not a half-applied rollback.
        Assert.That(await File.ReadAllBytesAsync(pendingScene.DestinationHakPath), Is.EqualTo(pendingScene.NewHakBytes));
        Assert.That(publishedHak, Is.Not.EqualTo(pendingScene.OldHakBytes));

        // Released lock, same journal: recovery now completes.
        Assert.That(await new ArtifactPublisher().RecoverPendingJournalAsync(_tempDir), Is.True);
        Assert.That(await File.ReadAllBytesAsync(pendingScene.DestinationHakPath), Is.EqualTo(pendingScene.OldHakBytes));
        Assert.That(await File.ReadAllBytesAsync(pendingScene.DestinationManifestPath), Is.EqualTo(pendingScene.OldManifestBytes));
    }

    [Test]
    public async Task RecoverPendingJournalAsync_SeveralPendingJournals_RecoversEveryOne()
    {
        var first = PublicationCrashScene.Create(_tempDir, PublicationState.HakReplaced, destinationsExistedBefore: true, baseName: "alpha");
        var second = PublicationCrashScene.Create(_tempDir, PublicationState.ManifestReplaced, destinationsExistedBefore: true, baseName: "beta");

        bool recovered = await new ArtifactPublisher().RecoverPendingJournalAsync(_tempDir);

        Assert.That(recovered, Is.True);
        Assert.That(await File.ReadAllBytesAsync(first.DestinationHakPath), Is.EqualTo(first.OldHakBytes));
        Assert.That(await File.ReadAllBytesAsync(second.DestinationHakPath), Is.EqualTo(second.OldHakBytes));
        Assert.That(await File.ReadAllBytesAsync(second.DestinationManifestPath), Is.EqualTo(second.OldManifestBytes));
        ArtifactPublisherJournalStateTests.AssertNoTransactionLeftovers(_tempDir);
    }

    [Test]
    public async Task RecoverPendingJournalAsync_WhenTheDestinationCannotBeRestored_ReportsFailureAndKeepsTheEvidence()
    {
        var scene = PublicationCrashScene.Create(_tempDir, PublicationState.ManifestReplaced, destinationsExistedBefore: true);
        var logger = new RecordingAppLogger();

        // FileShare.Read admits the reader but denies the write that would restore the manifest, so
        // the rollback genuinely half-completes: HAK restored, manifest still the new one.
        using (new FileStream(scene.DestinationManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool recovered = await new ArtifactPublisher(logger).RecoverPendingJournalAsync(_tempDir);

            Assert.That(recovered, Is.False,
                "A rollback that could not restore the destination is not a successful recovery.");

            LogRecord[] incomplete = logger.WithCode(ArtifactPublisher.RollbackIncompleteEventCode).ToArray();
            Assert.That(incomplete, Has.Length.EqualTo(1));
            Assert.That(incomplete[0].Level, Is.EqualTo(LogLevel.Error));
            Assert.That(incomplete[0].Data!["destinationRestored"], Is.EqualTo("false"));

            Assert.That(await File.ReadAllBytesAsync(scene.DestinationHakPath), Is.EqualTo(scene.OldHakBytes));
            Assert.That(File.Exists(scene.JournalPath), Is.True);
            Assert.That(File.Exists(scene.ManifestBackupPath), Is.True,
                "The backup must be kept: discarding it would make the pair unrecoverable.");
        }

        Assert.That(await new ArtifactPublisher().RecoverPendingJournalAsync(_tempDir), Is.True);
        Assert.That(await File.ReadAllBytesAsync(scene.DestinationManifestPath), Is.EqualTo(scene.OldManifestBytes));
        ArtifactPublisherJournalStateTests.AssertNoTransactionLeftovers(_tempDir);
    }
}
