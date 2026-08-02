using NUnit.Framework;
using System.Diagnostics;
using System.Text.Json;

namespace SRN.CC.CorpusTests.Performance;

[TestFixture]
[Category("Performance")]
public class TableViewPerformanceProbe
{
    [Test]
    public void TableView_PerformanceProbe_ShouldRecordBaselineJson()
    {
        string? runPerf = Environment.GetEnvironmentVariable("SRNCC_RUN_PERF");
        if (runPerf != "1")
        {
            Assert.Ignore("Opt-in performance probe skipped because SRNCC_RUN_PERF != 1.");
            return;
        }

        long initialMemory = GC.GetTotalMemory(true);
        Stopwatch sw = Stopwatch.StartNew();

        // Simulate 187,943 synthetic row allocations
        var items = new List<(int Id, string Resref, ushort Type, string Source, long Size)>(187943);
        for (int i = 0; i < 187943; i++)
        {
            items.Add((i + 1, $"resref_{i:D6}", 2000, "sample.hak", 1024));
        }

        sw.Stop();
        long postMemory = GC.GetTotalMemory(false);

        var report = new
        {
            TimestampUtc = DateTime.UtcNow.ToString("o"),
            RowCount = items.Count,
            ConstructionTimeMs = sw.ElapsedMilliseconds,
            AllocatedBytes = postMemory - initialMemory,
            OSVersion = Environment.OSVersion.ToString(),
            DotNetVersion = Environment.Version.ToString(),
            MachineName = Environment.MachineName
        };

        string outputDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "performance");
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, "tableview.json");

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputPath, json);

        Console.WriteLine($"Performance probe written to {outputPath}");
    }
}
