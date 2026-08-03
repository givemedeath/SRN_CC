using System.Text.Json;
using SRN.CC.Core.Build;
using SRN.CC.Core.Services;

namespace SRN.CC.Infrastructure.Build;

public sealed class ArtifactPublisher : IArtifactPublisher
{
    private const string JournalFileName = "publication-journal.json";

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

        string journalPath = Path.Combine(destDir, JournalFileName);
        string hakBackupPath = plan.DestinationHakPath + ".bak";
        string manifestBackupPath = plan.DestinationManifestPath + ".bak";

        bool hakExisted = File.Exists(plan.DestinationHakPath);
        bool manifestExisted = File.Exists(plan.DestinationManifestPath);

        PublicationJournal journal = new()
        {
            DestinationHakPath = plan.DestinationHakPath,
            DestinationManifestPath = plan.DestinationManifestPath,
            TempHakPath = tempHakPath,
            TempManifestPath = tempManifestPath,
            HakBackupPath = hakBackupPath,
            ManifestBackupPath = manifestBackupPath,
            HakExistedBefore = hakExisted,
            ManifestExistedBefore = manifestExisted,
            State = PublicationState.Prepared,
            CreatedUtc = DateTime.UtcNow,
            LastUpdatedUtc = DateTime.UtcNow
        };

        try
        {
            logs.Add("Writing publication journal (Prepared)...");
            await SaveJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

            // Step 1: Preflight locks and create backups (BackedUp)
            logs.Add("Creating target backups...");
            if (hakExisted)
            {
                File.Copy(plan.DestinationHakPath, hakBackupPath, overwrite: true);
            }
            if (manifestExisted)
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

            // Step 4: Commit & Cleanup
            logs.Add("Committing transaction and removing backups...");
            journal.State = PublicationState.Committed;

            if (File.Exists(hakBackupPath)) File.Delete(hakBackupPath);
            if (File.Exists(manifestBackupPath)) File.Delete(manifestBackupPath);
            if (File.Exists(journalPath)) File.Delete(journalPath);

            logs.Add("Publication committed successfully.");

            return new PublicationResult(
                IsSuccess: true,
                PublishedHakPath: plan.DestinationHakPath,
                PublishedManifestPath: plan.DestinationManifestPath,
                ErrorMessage: null,
                Logs: logs
            );
        }
        catch (Exception ex)
        {
            logs.Add($"Publication error: {ex.Message}. Rolling back transaction...");
            await RollbackJournalAsync(journal, journalPath).ConfigureAwait(false);

            return new PublicationResult(
                IsSuccess: false,
                PublishedHakPath: null,
                PublishedManifestPath: null,
                ErrorMessage: ex.Message,
                Logs: logs
            );
        }
    }

    public async Task<bool> RecoverPendingJournalAsync(
        string journalDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalDirectory);

        string journalPath = Path.Combine(journalDirectory, JournalFileName);
        if (!File.Exists(journalPath))
        {
            return false;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(journalPath, cancellationToken).ConfigureAwait(false);
            var journal = JsonSerializer.Deserialize<PublicationJournal>(bytes);
            if (journal == null) return false;

            if (journal.State == PublicationState.Committed)
            {
                if (File.Exists(journal.HakBackupPath)) File.Delete(journal.HakBackupPath);
                if (File.Exists(journal.ManifestBackupPath)) File.Delete(journal.ManifestBackupPath);
                File.Delete(journalPath);
                return true;
            }

            await RollbackJournalAsync(journal, journalPath).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task SaveJournalAsync(string journalPath, PublicationJournal journal, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllBytesAsync(journalPath, bytes, cancellationToken).ConfigureAwait(false);
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
}
