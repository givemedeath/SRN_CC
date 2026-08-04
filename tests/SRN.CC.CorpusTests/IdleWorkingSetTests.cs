using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.CorpusTests;

/// <summary>
/// The literal <c>PLAN.md:229</c> idle claim: "After indexing and 30 seconds idle, private working
/// set must remain below 750 MiB."
/// </summary>
/// <remarks>
/// <para>
/// This exists because nothing in the repository actually measured that sentence.
/// <c>CorpusIndexAcceptanceTests</c> and <c>Render/ModelPreviewWorkingSetTests</c> both sample
/// <see cref="Process.WorkingSet64"/> immediately after a forced <see cref="GC.Collect()"/> — total
/// working set, not private, and taken at the instant of maximum GC-induced tidiness rather than
/// after a genuine idle period. Those checks are useful and are deliberately left untouched; this one
/// measures the different, stricter thing the plan actually specifies.
/// </para>
/// <para>
/// Three details are load-bearing:
/// </para>
/// <list type="bullet">
/// <item><description>The 30-second wait happens with <b>nothing in flight</b> — every index task has
/// been awaited, the cache service has been disposed, and no reference to any snapshot is retained —
/// so what is measured is a settled process, not one still draining work.</description></item>
/// <item><description>The sample is taken <b>without</b> a second forced collect. Collecting
/// immediately before measuring would measure the collector, not the idle steady state; letting the
/// runtime decide for 30 seconds whether to release memory is exactly the question being
/// asked.</description></item>
/// <item><description><see cref="Process.PrivateMemorySize64"/> is private <i>commit</i>, which is
/// greater than or equal to the private working set, so asserting commit under the budget is strictly
/// conservative: passing here implies the plan's private-working-set claim holds. This is already the
/// repository's convention — <c>Performance/TableViewPerformanceProbe.cs</c> records the same counter
/// as <c>privateWorkingSetBytes</c> in <c>docs/evidence/tableview-performance-baseline.json</c>. It
/// needs no P/Invoke and no new package reference, both of which are forbidden here.</description></item>
/// </list>
/// <para>
/// Double-gated on purpose: <c>[Category("Performance")]</c> keeps it out of the default
/// <c>Category!=Corpus&amp;Category!=Performance</c> CI run, and
/// <see cref="CorpusGate.RequireIdleProbe"/> keeps a deliberate 30-second stall out of an ordinary
/// opt-in corpus run too. <see cref="CorpusGate.RequireIdleProbe"/> is checked <b>before</b>
/// <see cref="CorpusGate.RequireCorpusRoot"/> so that a <c>SRNCC_REQUIRE_CORPUS=1</c> run which never
/// asked for the probe is not failed by it; once the probe <i>is</i> opted into, the ordinary
/// require-corpus precedence applies in full.
/// </para>
/// </remarks>
[TestFixture]
[Category("Performance")]
public class IdleWorkingSetTests
{
    /// <summary>PLAN.md:229's budget, 750 MiB.</summary>
    private const long ThresholdBytes = 750L * 1024 * 1024;

    /// <summary>PLAN.md:229's "30 seconds idle".</summary>
    private static readonly TimeSpan IdleDuration = TimeSpan.FromSeconds(30);

    [Test]
    public async Task FullCorpusIndex_ThenThirtySecondsIdle_KeepsPrivateAndWorkingSetBelow750MiB()
    {
        CorpusGate.RequireIdleProbe();
        string corpusRoot = Path.GetFullPath(CorpusGate.RequireCorpusRoot());

        string[] hakFiles = Directory.GetFiles(corpusRoot, "*.hak", SearchOption.AllDirectories);
        hakFiles.Should().NotBeEmpty("the corpus root must contain at least one HAK to index.");

        string tempDb = Path.Combine(
            Path.GetTempPath(),
            "SRNCC_IdleProbeDb_" + Guid.NewGuid().ToString("N") + ".sqlite");

        try
        {
            using SqliteCacheService cacheService = new(tempDb);
            AssetIndexService indexService = new(cacheService, new ResourceTypeRegistry());

            foreach (string hakPath in hakFiles)
            {
                // The returned snapshot is intentionally not retained: the probe measures what the
                // process holds once indexing is over, not what it holds while a caller keeps every
                // snapshot alive.
                _ = await indexService.IndexAsync(AssetSource.CreateHak(hakPath));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                if (File.Exists(tempDb + suffix))
                {
                    File.Delete(tempDb + suffix);
                }
            }
        }

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        // Idle: nothing is in flight, and nothing is done to the heap for the whole window.
        await Task.Delay(IdleDuration);

        // Deliberately NO second collect here — sampling after a forced collect would measure the
        // collector rather than the idle steady state.
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long privateBytes = process.PrivateMemorySize64;
        long workingSetBytes = process.WorkingSet64;

        string report =
            $"Idle probe after indexing {hakFiles.Length} HAK(s) and {IdleDuration.TotalSeconds:F0}s idle: " +
            $"privateBytes={privateBytes} ({privateBytes / (1024.0 * 1024.0):F2} MiB), " +
            $"workingSetBytes={workingSetBytes} ({workingSetBytes / (1024.0 * 1024.0):F2} MiB), " +
            $"thresholdBytes={ThresholdBytes} (750.00 MiB).";
        Console.WriteLine(report);
        TestContext.Progress.WriteLine(report);
        TestContext.Progress.WriteLine(
            "Copy these three numbers into docs/evidence/corpus-index-baseline.json -> idle.");

        privateBytes.Should().BeLessThan(
            ThresholdBytes,
            "PLAN.md:229 requires private working set below 750 MiB after indexing and 30 seconds idle, " +
            "and private commit is an upper bound on private working set.");
        workingSetBytes.Should().BeLessThan(
            ThresholdBytes,
            "total working set is reported alongside private commit and must also stay inside the 750 MiB budget.");
    }
}
