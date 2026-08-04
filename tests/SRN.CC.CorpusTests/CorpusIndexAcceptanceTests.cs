using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Microsoft.Data.Sqlite;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.CorpusTests;

[TestFixture]
[Category("Corpus")]
public class CorpusIndexAcceptanceTests
{
    private string? _corpusRoot;

    [SetUp]
    public void SetUp() => _corpusRoot = CorpusGate.RequireCorpusRoot();

    [Test]
    public async Task VerifyCorpusIndexing_ExactCountsAndPerformanceAcceptance()
    {
        string corpusDir = Path.GetFullPath(_corpusRoot!);
        string[] hakFiles = Directory.GetFiles(corpusDir, "*.hak", SearchOption.AllDirectories);

        hakFiles.Length.Should().Be(117, "Corpus must contain exactly 117 HAK instances.");

        ResourceTypeRegistry typeRegistry = new();

        // 3 cold cache runs using fresh SQLite DBs
        List<double> coldDurationsSec = new();
        SourceIndexSnapshot[]? firstRunSnapshots = null;

        for (int run = 0; run < 3; run++)
        {
            string tempDb = Path.Combine(Path.GetTempPath(), $"SRNCC_CorpusDb_{run}_" + Guid.NewGuid().ToString("N") + ".sqlite");
            try
            {
                using SqliteCacheService cacheService = new(tempDb);
                AssetIndexService indexService = new(cacheService, typeRegistry);

                Stopwatch sw = Stopwatch.StartNew();
                List<SourceIndexSnapshot> snapshots = new();

                foreach (string hakPath in hakFiles)
                {
                    AssetSource source = AssetSource.CreateHak(hakPath);
                    SourceIndexSnapshot snapshot = await indexService.IndexAsync(source);
                    snapshots.Add(snapshot);
                }

                sw.Stop();
                coldDurationsSec.Add(sw.Elapsed.TotalSeconds);

                if (firstRunSnapshots == null)
                {
                    firstRunSnapshots = snapshots.ToArray();
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(tempDb)) File.Delete(tempDb);
                if (File.Exists(tempDb + "-wal")) File.Delete(tempDb + "-wal");
                if (File.Exists(tempDb + "-shm")) File.Delete(tempDb + "-shm");
            }
        }

        coldDurationsSec.Sort();
        double medianColdSec = coldDurationsSec[1];
        Console.WriteLine($"Corpus Cold Indexing Median Duration: {medianColdSec:F3}s across 3 runs.");

        medianColdSec.Should().BeLessThan(10.0, "Cold indexing median duration must be under 10 seconds.");

        // Assert snapshot counts against firstRunSnapshots
        long totalOccurrences = 0;
        long totalLod2078Entries = 0;
        List<string> archivesWithDuplicates = new();

        foreach (var snap in firstRunSnapshots!)
        {
            snap.Source.IsAvailable.Should().BeTrue();
            string fileName = Path.GetFileName(snap.Source.FullPath).ToLowerInvariant();

            bool hasDuplicateInArchive = false;
            foreach (var rec in snap.Records)
            {
                if (rec.IsValid && rec.Occurrence != null)
                {
                    totalOccurrences++;
                    if (rec.Occurrence.Identity.ResourceType == 2078)
                    {
                        totalLod2078Entries++;
                    }

                    if (rec.Occurrence.ValidationState == ValidationState.DuplicateIdentity)
                    {
                        hasDuplicateInArchive = true;
                    }
                }
            }

            if (hasDuplicateInArchive)
            {
                archivesWithDuplicates.Add(fileName);
            }
        }

        totalOccurrences.Should().Be(187943, "Corpus must contain exactly 187,943 occurrences.");
        totalLod2078Entries.Should().Be(212, "Corpus must contain exactly 212 type-2078 'lod' entries.");

        var distinctDuplicateArchives = archivesWithDuplicates.Distinct().OrderBy(s => s).ToList();
        distinctDuplicateArchives.Should().HaveCount(4, "Exactly 4 archives must contain internal duplicate identities.");
        distinctDuplicateArchives.Should().Contain("nwncq.hak");
        distinctDuplicateArchives.Should().Contain("udp2_off_int.hak");
        distinctDuplicateArchives.Should().Contain("udp2_off_pl.hak");

        // Warm cache verification
        string warmDb = Path.Combine(Path.GetTempPath(), "SRNCC_WarmCorpusDb_" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (SqliteCacheService cacheService = new(warmDb))
            {
                AssetIndexService indexService = new(cacheService, typeRegistry);

                // Populate warm cache
                foreach (string hakPath in hakFiles)
                {
                    await indexService.IndexAsync(AssetSource.CreateHak(hakPath));
                }

                // Second scan - must hit warm cache
                foreach (string hakPath in hakFiles)
                {
                    SourceIndexSnapshot warmSnap = await indexService.IndexAsync(AssetSource.CreateHak(hakPath));
                    warmSnap.IsCacheHit.Should().BeTrue("Second scan must be a warm cache hit.");
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(warmDb)) File.Delete(warmDb);
            if (File.Exists(warmDb + "-wal")) File.Delete(warmDb + "-wal");
            if (File.Exists(warmDb + "-shm")) File.Delete(warmDb + "-shm");
        }

        // Memory check
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using Process proc = Process.GetCurrentProcess();
        long privateWorkingSet = proc.WorkingSet64;
        double workingSetMb = privateWorkingSet / (1024.0 * 1024.0);
        Console.WriteLine($"Process Private Working Set after GC: {workingSetMb:F2} MiB");

        workingSetMb.Should().BeLessThan(750.0, "Private working set must remain under 750 MiB.");
    }
}
