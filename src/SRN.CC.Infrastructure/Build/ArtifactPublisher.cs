using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SRN.CC.Core.Build;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Services;
using SRN.CC.Infrastructure.Logging;

namespace SRN.CC.Infrastructure.Build;

public sealed class ArtifactPublisher : IArtifactPublisher
{
    private const string JournalFileName = "publication-journal.json";
    private const string TransactionJournalSuffix = ".publication-journal.json";
    private const int PublicationLockTimeoutMs = 20_000;

    /// <summary>A single rollback or recovery file operation did not complete.</summary>
    public const string RollbackStepFailedEventCode = "PublicationRollbackStepFailed";

    /// <summary>Rollback finished with at least one failed step; the transaction is only partly undone.</summary>
    public const string RollbackIncompleteEventCode = "PublicationRollbackIncomplete";

    /// <summary>The transaction committed, but post-commit cleanup was left for recovery.</summary>
    public const string CommitCleanupDeferredEventCode = "PublicationCommitCleanupDeferred";

    /// <summary>A journal file on disk could not be read or parsed.</summary>
    public const string JournalUnreadableEventCode = "PublicationJournalUnreadable";

    /// <summary>Recovery could not take the publication lock, so it left the destination untouched.</summary>
    public const string RecoveryContendedEventCode = "PublicationRecoveryContended";

    private readonly IAppLogger _logger;

    /// <param name="logger">
    /// Optional and last so every existing call site keeps compiling unchanged; defaults to
    /// <see cref="NullAppLogger.Instance"/>.
    /// </param>
    public ArtifactPublisher(IAppLogger? logger = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
    }

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
                        _logger.Log(
                            LogLevel.Warn,
                            nameof(ArtifactPublisher),
                            $"Publication committed; post-commit cleanup deferred to recovery for journal '{journalPath}'.",
                            cleanupException,
                            new Dictionary<string, string>
                            {
                                [AppLogger.EventCodeKey] = CommitCleanupDeferredEventCode,
                                ["journalPath"] = journalPath
                            });
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
                            RollbackOutcome cancelRollback = await RollbackJournalAsync(journal, journalPath).ConfigureAwait(false);
                            ReportRollback(cancelRollback, logs, journalPath);
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
                        RollbackOutcome rollback = await RollbackJournalAsync(journal, journalPath).ConfigureAwait(false);
                        ReportRollback(rollback, logs, journalPath);
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
                RollbackOutcome outerRollback = await RollbackJournalAsync(journal, journalPath).ConfigureAwait(false);
                ReportRollback(outerRollback, logs, journalPath);
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
            PublicationJournal? journal;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(journalPath, ct).ConfigureAwait(false);
                journal = JsonSerializer.Deserialize<PublicationJournal>(bytes);
            }
            catch (Exception ex)
            {
                // As broad as the bare `catch` this replaced — including cancellation, which recovery
                // has always reported as "not recovered" rather than propagated. Narrowing it here
                // would be a behaviour change smuggled in behind a logging change.
                _logger.Log(
                    LogLevel.Warn,
                    nameof(ArtifactPublisher),
                    $"Publication journal '{journalPath}' could not be read; leaving it in place for a later attempt.",
                    ex,
                    new Dictionary<string, string>
                    {
                        [AppLogger.EventCodeKey] = JournalUnreadableEventCode,
                        ["journalPath"] = journalPath
                    });
                return false;
            }

            if (journal is null)
            {
                _logger.Log(
                    LogLevel.Warn,
                    nameof(ArtifactPublisher),
                    $"Publication journal '{journalPath}' deserialized to null; leaving it in place for a later attempt.",
                    exception: null,
                    new Dictionary<string, string>
                    {
                        [AppLogger.EventCodeKey] = JournalUnreadableEventCode,
                        ["journalPath"] = journalPath
                    });
                return false;
            }

            string lockDir = Path.GetDirectoryName(journal.DestinationHakPath) ?? journalDirectory;
            string publicationLockPath = GetPublicationLockPath(
                journal.DestinationHakPath,
                journal.DestinationManifestPath,
                lockDir);

            PublicationLock publicationLock;
            try
            {
                publicationLock = await AcquirePublicationLockAsync(publicationLockPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Contention is the expected reason to arrive here: another publication holds the
                // lock. Reporting it beats touching a destination somebody else is mid-way through.
                _logger.Log(
                    LogLevel.Warn,
                    nameof(ArtifactPublisher),
                    $"Recovery of '{journalPath}' skipped: the publication lock '{publicationLockPath}' is held.",
                    ex,
                    new Dictionary<string, string>
                    {
                        [AppLogger.EventCodeKey] = RecoveryContendedEventCode,
                        ["journalPath"] = journalPath,
                        ["lockPath"] = publicationLockPath
                    });
                return false;
            }

            await using (publicationLock.ConfigureAwait(false))
            {
                if (journal.State == PublicationState.Committed)
                {
                    // The artifacts are already in place; everything below is hygiene. A failure
                    // must not be reported as a failed recovery, but it must not be silent either.
                    List<string> cleanupFailures = new();
                    TryFileStep(cleanupFailures, LogLevel.Warn, "delete HAK backup", journal.HakBackupPath, () =>
                    {
                        if (File.Exists(journal.HakBackupPath)) File.Delete(journal.HakBackupPath);
                    });
                    TryFileStep(cleanupFailures, LogLevel.Warn, "delete manifest backup", journal.ManifestBackupPath, () =>
                    {
                        if (File.Exists(journal.ManifestBackupPath)) File.Delete(journal.ManifestBackupPath);
                    });

                    // The journal is the only record that these backups exist, so it outlives a
                    // cleanup failure and a later recovery pass finishes the job.
                    if (cleanupFailures.Count == 0)
                    {
                        TryFileStep(cleanupFailures, LogLevel.Warn, "delete journal", journalPath, () =>
                        {
                            if (File.Exists(journalPath)) File.Delete(journalPath);
                        });
                    }

                    return true;
                }

                RollbackOutcome outcome = await RollbackJournalAsync(journal, journalPath).ConfigureAwait(false);
                ReportRollback(outcome, logs: null, journalPath);
                return outcome.DestinationRestored;
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

    /// <summary>
    /// Undoes as much of a transaction as its recorded state requires, one file operation at a time.
    /// </summary>
    /// <remarks>
    /// Every step is attempted even when an earlier one failed, and the failures are returned rather
    /// than swallowed: the single <c>catch { }</c> this replaced made a rollback that left the new
    /// HAK sitting on top of the old one indistinguishable from a clean restore.
    /// </remarks>
    private Task<RollbackOutcome> RollbackJournalAsync(PublicationJournal journal, string journalPath)
    {
        List<string> critical = new();
        List<string> cleanup = new();

        // Critical failures that no future pass can do anything about, because the backup they would
        // need is already gone. Counted separately so a transaction that is merely unfinishable does
        // not masquerade as one that is still worth retrying — see the journal cleanup below.
        int unrecoverableCount = 0;

        // If HakReplaced state was reached, restore or delete target HAK
        if (journal.State >= PublicationState.HakReplaced)
        {
            if (journal.HakExistedBefore && File.Exists(journal.HakBackupPath))
            {
                TryFileStep(critical, LogLevel.Error, "restore destination HAK from backup", journal.DestinationHakPath, () =>
                    File.Copy(journal.HakBackupPath, journal.DestinationHakPath, overwrite: true));
            }
            else if (!journal.HakExistedBefore && File.Exists(journal.DestinationHakPath))
            {
                TryFileStep(critical, LogLevel.Error, "delete destination HAK created by this transaction", journal.DestinationHakPath, () =>
                    File.Delete(journal.DestinationHakPath));
            }
            else if (journal.HakExistedBefore)
            {
                RecordMissingBackup(critical, "HAK", journal.HakBackupPath, journal.DestinationHakPath);
                unrecoverableCount++;
            }
        }

        // If ManifestReplaced state was reached, restore or delete target Manifest
        if (journal.State >= PublicationState.ManifestReplaced)
        {
            if (journal.ManifestExistedBefore && File.Exists(journal.ManifestBackupPath))
            {
                TryFileStep(critical, LogLevel.Error, "restore destination manifest from backup", journal.DestinationManifestPath, () =>
                    File.Copy(journal.ManifestBackupPath, journal.DestinationManifestPath, overwrite: true));
            }
            else if (!journal.ManifestExistedBefore && File.Exists(journal.DestinationManifestPath))
            {
                TryFileStep(critical, LogLevel.Error, "delete destination manifest created by this transaction", journal.DestinationManifestPath, () =>
                    File.Delete(journal.DestinationManifestPath));
            }
            else if (journal.ManifestExistedBefore)
            {
                RecordMissingBackup(critical, "manifest", journal.ManifestBackupPath, journal.DestinationManifestPath);
                unrecoverableCount++;
            }
        }

        // Cleanup temp files
        TryFileStep(cleanup, LogLevel.Warn, "delete temp HAK", journal.TempHakPath, () =>
        {
            if (File.Exists(journal.TempHakPath)) File.Delete(journal.TempHakPath);
        });
        TryFileStep(cleanup, LogLevel.Warn, "delete temp manifest", journal.TempManifestPath, () =>
        {
            if (File.Exists(journal.TempManifestPath)) File.Delete(journal.TempManifestPath);
        });

        // Cleanup backup and journal files. Both are kept when the destination could not be
        // restored: they are the only means of finishing the job, and the journal is what keeps the
        // unfinished transaction visible to the next recovery pass instead of letting it vanish.
        //
        // The exception is a transaction whose every critical failure is a *missing* backup. Retrying
        // that can only ever produce the same failure, so keeping the journal would report the same
        // unrecoverable transaction on every launch forever. It is reported once — the outcome below
        // still says the destination was not restored — and then cleared. This case is reached
        // routinely when a previous pass restored the destination and deleted the backups but could
        // not delete the journal itself.
        bool nothingLeftToRetry = critical.Count > 0 && critical.Count == unrecoverableCount;
        if (critical.Count == 0 || nothingLeftToRetry)
        {
            TryFileStep(cleanup, LogLevel.Warn, "delete HAK backup", journal.HakBackupPath, () =>
            {
                if (File.Exists(journal.HakBackupPath)) File.Delete(journal.HakBackupPath);
            });
            TryFileStep(cleanup, LogLevel.Warn, "delete manifest backup", journal.ManifestBackupPath, () =>
            {
                if (File.Exists(journal.ManifestBackupPath)) File.Delete(journal.ManifestBackupPath);
            });
            if (!File.Exists(journal.HakBackupPath) && !File.Exists(journal.ManifestBackupPath))
            {
                TryFileStep(cleanup, LogLevel.Warn, "delete journal", journalPath, () =>
                {
                    if (File.Exists(journalPath)) File.Delete(journalPath);
                });
            }
        }

        return Task.FromResult(new RollbackOutcome(critical, cleanup));
    }

    /// <summary>Runs one rollback/recovery file operation, recording and logging any failure.</summary>
    private void TryFileStep(List<string> failures, LogLevel level, string operation, string path, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            // Deliberately as broad as the bare `catch { }` this replaced: the point of the change is
            // that the failure is now visible, not that fewer failures are tolerated.
            failures.Add($"{operation} '{path}': {ex.Message}");
            _logger.Log(
                level,
                nameof(ArtifactPublisher),
                $"Publication rollback step failed: {operation} '{path}'.",
                ex,
                new Dictionary<string, string>
                {
                    [AppLogger.EventCodeKey] = RollbackStepFailedEventCode,
                    ["operation"] = operation,
                    ["path"] = path
                });
        }
    }

    private void RecordMissingBackup(List<string> failures, string artifact, string backupPath, string destinationPath)
    {
        string detail = $"restore destination {artifact} '{destinationPath}': backup '{backupPath}' is missing";
        failures.Add(detail);
        _logger.Log(
            LogLevel.Error,
            nameof(ArtifactPublisher),
            $"Publication rollback cannot restore {artifact} '{destinationPath}': backup '{backupPath}' is missing.",
            exception: null,
            new Dictionary<string, string>
            {
                [AppLogger.EventCodeKey] = RollbackStepFailedEventCode,
                ["operation"] = $"restore destination {artifact}",
                ["path"] = destinationPath
            });
    }

    /// <summary>Surfaces a partial rollback to the caller's log list and to the application log.</summary>
    private void ReportRollback(RollbackOutcome outcome, List<string>? logs, string journalPath)
    {
        if (outcome.IsComplete)
        {
            return;
        }

        string detail = outcome.Describe();
        string message = outcome.DestinationRestored
            ? $"Rollback restored the destination but left files behind: {detail}"
            : $"Rollback INCOMPLETE - the destination may not match the previous artifacts: {detail}";

        logs?.Add(message);
        _logger.Log(
            outcome.DestinationRestored ? LogLevel.Warn : LogLevel.Error,
            nameof(ArtifactPublisher),
            message,
            exception: null,
            new Dictionary<string, string>
            {
                [AppLogger.EventCodeKey] = RollbackIncompleteEventCode,
                ["journalPath"] = journalPath,
                ["destinationRestored"] = outcome.DestinationRestored ? "true" : "false"
            });
    }

    /// <summary>Result of one rollback attempt, split by whether the destination itself is sound.</summary>
    private sealed record RollbackOutcome(IReadOnlyList<string> CriticalFailures, IReadOnlyList<string> CleanupFailures)
    {
        /// <summary>True when the destination pair is back to its pre-transaction contents.</summary>
        public bool DestinationRestored => CriticalFailures.Count == 0;

        /// <summary>True when the destination is sound and no temp, backup or journal file was left.</summary>
        public bool IsComplete => CriticalFailures.Count == 0 && CleanupFailures.Count == 0;

        public string Describe() => string.Join("; ", CriticalFailures.Concat(CleanupFailures));
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
