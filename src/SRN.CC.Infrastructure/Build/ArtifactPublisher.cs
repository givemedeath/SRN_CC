using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SRN.CC.Core.Build;
using SRN.CC.Core.Services;

namespace SRN.CC.Infrastructure.Build;

public sealed class ArtifactPublisher : IArtifactPublisher
{
    private const string JournalFileName = "publication-journal.json";
    private const string TransactionJournalSuffix = ".publication-journal.json";
    private const int PublicationLockTimeoutMs = 20_000;

    public async Task<PublicationResult> PublishAsync(
        BuildPlan plan,
        string tempHakPath,
        string tempManifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempHakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempManifestPath);

        List<string> logs = new();
        string destDir = Path.GetDirectoryName(plan.DestinationHakPath)!;
        if (string.IsNullOrEmpty(destDir)) destDir = ".";
        string publicationLockPath = GetPublicationLockPath(plan.DestinationHakPath, plan.DestinationManifestPath, destDir);
        string transactionId = Guid.NewGuid().ToString("N");
        string journalPath = Path.Combine(destDir, $"{Path.GetFileName(plan.DestinationHakPath)}.{transactionId}{TransactionJournalSuffix}");
        string hakBackupPath = plan.DestinationHakPath + $".{transactionId}.bak";
        string manifestBackupPath = plan.DestinationManifestPath + $".{transactionId}.bak";

        PublicationJournal? journal = null;
        bool persistedCommitState = false;

        try
        {
            await using (await AcquirePublicationLockAsync(publicationLockPath, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    bool existingHak = File.Exists(plan.DestinationHakPath);
                    bool existingManifest = File.Exists(plan.DestinationManifestPath);

                    journal = new PublicationJournal
                    {
                        DestinationHakPath = plan.DestinationHakPath,
                        DestinationManifestPath = plan.DestinationManifestPath,
                        TempHakPath = tempHakPath,
                        TempManifestPath = tempManifestPath,
                        HakBackupPath = hakBackupPath,
                        ManifestBackupPath = manifestBackupPath,
                        HakExistedBefore = existingHak,
                        ManifestExistedBefore = existingManifest,
                        State = PublicationState.Prepared,
                        CreatedUtc = DateTime.UtcNow,
                        LastUpdatedUtc = DateTime.UtcNow
                    };

                    logs.Add("Writing publication journal (Prepared)...");
                    await SaveJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

                    // Step 1: Preflight locks and create backups (BackedUp)
                    logs.Add("Creating target backups...");
                    if (existingHak)
                    {
                        File.Copy(plan.DestinationHakPath, hakBackupPath, overwrite: true);
                    }
                    if (existingManifest)
                    {
                        File.Copy(plan.DestinationManifestPath, manifestBackupPath, overwrite: true);
                    }

                    journal.State = PublicationState.BackedUp;
                    journal.LastUpdatedUtc = DateTime.UtcNow;
                    await SaveJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

                    // Step 2: Journal intent and Replace HAK (HakReplaced)
                    logs.Add("Replacing HAK artifact...");
                    journal.State = PublicationState.HakReplaced;
                    journal.LastUpdatedUtc = DateTime.UtcNow;
                    await SaveJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

                    File.Move(tempHakPath, plan.DestinationHakPath, overwrite: true);

                    // Step 3: Journal intent and Replace Manifest (ManifestReplaced)
                    logs.Add("Replacing Provenance Manifest artifact...");
                    journal.State = PublicationState.ManifestReplaced;
                    journal.LastUpdatedUtc = DateTime.UtcNow;
                    await SaveJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

                    File.Move(tempManifestPath, plan.DestinationManifestPath, overwrite: true);

                    // Step 4: Persist Committed state BEFORE cleanup
                    logs.Add("Committing transaction...");
                    journal.State = PublicationState.Committed;
                    journal.LastUpdatedUtc = DateTime.UtcNow;
                    await SaveJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
                    persistedCommitState = true;

                    // Cleanup is best-effort after the commit point. A cleanup failure must
                    // never roll back one half of an already committed artifact pair.
                    try
                    {
                        if (File.Exists(hakBackupPath)) File.Delete(hakBackupPath);
                        if (File.Exists(manifestBackupPath)) File.Delete(manifestBackupPath);
                        if (!File.Exists(hakBackupPath) && !File.Exists(manifestBackupPath) && File.Exists(journalPath))
                        {
                            File.Delete(journalPath);
                        }
                    }
                    catch (Exception cleanupException)
                    {
                        logs.Add($"Publication committed; deferred cleanup after error: {cleanupException.Message}");
                    }

                    logs.Add("Publication committed successfully.");

                    return new PublicationResult(
                        IsSuccess: true,
                        PublishedHakPath: plan.DestinationHakPath,
                        PublishedManifestPath: plan.DestinationManifestPath,
                        ErrorMessage: null,
                        Logs: logs
                    );
                }
                catch (OperationCanceledException)
                {
                    if (journal is null || !(journal.State == PublicationState.Committed && persistedCommitState))
                    {
                        if (journal is not null)
                        {
                            await RollbackJournalAsync(journal, journalPath).ConfigureAwait(false);
                        }
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    if (journal is { State: PublicationState.Committed } && persistedCommitState)
                    {
                        logs.Add($"Publication committed; cleanup will be retried during recovery: {ex.Message}");
                        return new PublicationResult(true, plan.DestinationHakPath, plan.DestinationManifestPath, null, logs);
                    }

                    logs.Add($"Publication error: {ex.Message}. Rolling back transaction...");
                    if (journal is not null)
                    {
                        await RollbackJournalAsync(journal, journalPath).ConfigureAwait(false);
                    }

                    return new PublicationResult(
                        IsSuccess: false,
                        PublishedHakPath: null,
                        PublishedManifestPath: null,
                        ErrorMessage: ex.Message,
                        Logs: logs
                    );
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (journal is not null && !(journal.State == PublicationState.Committed && persistedCommitState))
            {
                await RollbackJournalAsync(journal, journalPath).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task<bool> RecoverPendingJournalAsync(
        string journalDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalDirectory);

        if (!Directory.Exists(journalDirectory))
        {
            return false;
        }

        bool recoveredAny = false;

        string fixedLegacyJournal = Path.Combine(journalDirectory, JournalFileName);
        if (File.Exists(fixedLegacyJournal))
        {
            if (await TryRecoverJournalAsync(fixedLegacyJournal, cancellationToken).ConfigureAwait(false))
            {
                recoveredAny = true;
            }
        }

        foreach (string journalPath in Directory.GetFiles(journalDirectory, $"*{TransactionJournalSuffix}"))
        {
            if (await TryRecoverJournalAsync(journalPath, cancellationToken).ConfigureAwait(false))
            {
                recoveredAny = true;
            }
        }

        return recoveredAny;

        async Task<bool> TryRecoverJournalAsync(string journalPath, CancellationToken ct)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(journalPath, ct).ConfigureAwait(false);
                var journal = JsonSerializer.Deserialize<PublicationJournal>(bytes);
                if (journal == null) return false;

                string lockDir = Path.GetDirectoryName(journal.DestinationHakPath) ?? journalDirectory;
                string publicationLockPath = GetPublicationLockPath(
                    journal.DestinationHakPath,
                    journal.DestinationManifestPath,
                    lockDir);

                await using (await AcquirePublicationLockAsync(publicationLockPath, ct).ConfigureAwait(false))
                {
                    if (journal.State == PublicationState.Committed)
                    {
                        if (File.Exists(journal.HakBackupPath)) File.Delete(journal.HakBackupPath);
                        if (File.Exists(journal.ManifestBackupPath)) File.Delete(journal.ManifestBackupPath);
                        if (File.Exists(journalPath)) File.Delete(journalPath);
                        return true;
                    }

                    await RollbackJournalAsync(journal, journalPath).ConfigureAwait(false);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task SaveJournalAsync(string journalPath, PublicationJournal journal, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, new JsonSerializerOptions { WriteIndented = true });
        string tempPath = journalPath + ".tmp";

        await using (var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        // The old journal remains valid until this same-directory atomic rename.
        File.Move(tempPath, journalPath, overwrite: true);
    }

    private static Task RollbackJournalAsync(PublicationJournal journal, string journalPath)
    {
        try
        {
            // If HakReplaced state was reached, restore or delete target HAK
            if (journal.State >= PublicationState.HakReplaced)
            {
                if (journal.HakExistedBefore && File.Exists(journal.HakBackupPath))
                {
                    File.Copy(journal.HakBackupPath, journal.DestinationHakPath, overwrite: true);
                }
                else if (!journal.HakExistedBefore && File.Exists(journal.DestinationHakPath))
                {
                    File.Delete(journal.DestinationHakPath);
                }
            }

            // If ManifestReplaced state was reached, restore or delete target Manifest
            if (journal.State >= PublicationState.ManifestReplaced)
            {
                if (journal.ManifestExistedBefore && File.Exists(journal.ManifestBackupPath))
                {
                    File.Copy(journal.ManifestBackupPath, journal.DestinationManifestPath, overwrite: true);
                }
                else if (!journal.ManifestExistedBefore && File.Exists(journal.DestinationManifestPath))
                {
                    File.Delete(journal.DestinationManifestPath);
                }
            }

            // Cleanup temp files
            if (File.Exists(journal.TempHakPath)) File.Delete(journal.TempHakPath);
            if (File.Exists(journal.TempManifestPath)) File.Delete(journal.TempManifestPath);

            // Cleanup backup files
            if (File.Exists(journal.HakBackupPath)) File.Delete(journal.HakBackupPath);
            if (File.Exists(journal.ManifestBackupPath)) File.Delete(journal.ManifestBackupPath);

            // Cleanup journal file
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }
        catch { }

        return Task.CompletedTask;
    }

    private static async Task<PublicationLock> AcquirePublicationLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        DateTime timeoutAt = DateTime.UtcNow.AddMilliseconds(PublicationLockTimeoutMs);
        while (true)
        {
            try
            {
                return new PublicationLock(lockPath);
            }
            catch (IOException) when (DateTime.UtcNow < timeoutAt)
            {
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new TimeoutException($"Timed out waiting for publication lock '{lockPath}' after {PublicationLockTimeoutMs} ms.");
            }
        }
    }

    private static string GetPublicationLockPath(string destinationHakPath, string destinationManifestPath, string destinationDirectory)
    {
        string lockSeed = $"{Path.GetFullPath(destinationHakPath).ToLowerInvariant()}::{Path.GetFullPath(destinationManifestPath).ToLowerInvariant()}";
        byte[] seedHash = SHA256.HashData(Encoding.UTF8.GetBytes(lockSeed));
        string lockFile = $"{Convert.ToHexString(seedHash).ToLowerInvariant()}.publication.lock";
        return Path.Combine(destinationDirectory, lockFile);
    }

    private sealed class PublicationLock : IAsyncDisposable
    {
        private readonly FileStream _stream;

        public PublicationLock(string lockPath)
        {
            _stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough);
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
