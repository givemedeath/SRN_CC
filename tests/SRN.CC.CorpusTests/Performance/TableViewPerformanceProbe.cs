using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.App.Views;

namespace SRN.CC.CorpusTests.Performance;

[TestFixture]
[Category("Performance")]
public class TableViewPerformanceProbe
{
    private const int RowCount = 187_943;
    private const int IterationCount = 5;

    [AvaloniaTest]
    public void TableView_PerformanceProbe_ShouldRecordFiveIterationBaseline()
    {
        if (Environment.GetEnvironmentVariable("SRNCC_RUN_PERF") != "1")
        {
            Assert.Ignore("Opt-in performance probe skipped because SRNCC_RUN_PERF != 1.");
            return;
        }

        List<Measurement> measurements = new(IterationCount);
        for (int iteration = 1; iteration <= IterationCount; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocatedBefore = GC.GetTotalAllocatedBytes(true);
            Stopwatch timer = Stopwatch.StartNew();
            var vm = new MainWindowViewModel();
            timer.Stop();
            double constructionMs = timer.Elapsed.TotalMilliseconds;

            var window = new MainWindow { DataContext = vm, Width = 1024, Height = 768 };
            try
            {
                timer.Restart();
                window.Show();
                window.UpdateLayout();
                timer.Stop();
                double firstLayoutMs = timer.Elapsed.TotalMilliseconds;

                TableView table = window.FindControl<TableView>("MainTableView")!;
                int realizedHighWater = CountRows(window);

                timer.Restart();
                table.ScrollIntoView(RowCount / 2);
                window.UpdateLayout();
                table.ScrollIntoView(RowCount - 1);
                window.UpdateLayout();
                timer.Stop();
                double scrollLayoutMs = timer.Elapsed.TotalMilliseconds;
                realizedHighWater = Math.Max(realizedHighWater, CountRows(window));

                timer.Restart();
                IReadOnlyList<AssetRowItem> sorted = vm.SortByResref();
                timer.Stop();
                double sortMs = timer.Elapsed.TotalMilliseconds;

                timer.Restart();
                IReadOnlyList<AssetRowItem> filtered = vm.FilterByResourceType("2DA (2001)");
                timer.Stop();
                double filterMs = timer.Elapsed.TotalMilliseconds;

                sorted.Count.Should().Be(RowCount);
                filtered.Should().NotBeEmpty();
                realizedHighWater.Should().BeLessThan(200);

                measurements.Add(new Measurement(
                    iteration,
                    constructionMs,
                    GC.GetTotalAllocatedBytes(false) - allocatedBefore,
                    firstLayoutMs,
                    scrollLayoutMs,
                    realizedHighWater,
                    sortMs,
                    filterMs,
                    Process.GetCurrentProcess().PrivateMemorySize64));
            }
            finally
            {
                window.Close();
            }
        }

        string repoRoot = FindRepoRoot();
        string outputDir = Path.Combine(repoRoot, "artifacts", "performance");
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, "tableview.json");

        var report = new
        {
            schemaVersion = 1,
            timestampUtc = DateTime.UtcNow.ToString("o"),
            rowCount = RowCount,
            measuredIterations = IterationCount,
            environment = new
            {
                os = Environment.OSVersion.ToString(),
                dotnet = Environment.Version.ToString(),
                cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
                logicalProcessorCount = Environment.ProcessorCount,
                configuration = "Release"
            },
            median = Summarize(measurements, 0.5),
            worst = Summarize(measurements, 1.0),
            measurements
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.Progress.WriteLine($"Performance baseline written to {outputPath}");
    }

    private static int CountRows(Window window) => window.GetVisualDescendants().OfType<TableViewRow>().Count();

    private static object Summarize(IReadOnlyList<Measurement> values, double percentile) => new
    {
        constructionMs = Select(values.Select(value => value.ConstructionMs), percentile),
        allocatedBytes = (long)Select(values.Select(value => (double)value.AllocatedBytes), percentile),
        firstLayoutMs = Select(values.Select(value => value.FirstLayoutMs), percentile),
        scrollLayoutMs = Select(values.Select(value => value.ScrollLayoutMs), percentile),
        realizedRowHighWater = (int)Select(values.Select(value => (double)value.RealizedRowHighWater), percentile),
        sortMs = Select(values.Select(value => value.SortMs), percentile),
        filterMs = Select(values.Select(value => value.FilterMs), percentile),
        privateWorkingSetBytes = (long)Select(values.Select(value => (double)value.PrivateWorkingSetBytes), percentile)
    };

    private static double Select(IEnumerable<double> source, double percentile)
    {
        double[] sorted = source.Order().ToArray();
        int index = percentile >= 1 ? sorted.Length - 1 : sorted.Length / 2;
        return sorted[index];
    }

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(current, "SRN.CC.sln")))
        {
            current = Directory.GetParent(current)?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate SRN.CC.sln from the test output directory.");
        }
        return current;
    }

    private sealed record Measurement(
        int Iteration,
        double ConstructionMs,
        long AllocatedBytes,
        double FirstLayoutMs,
        double ScrollLayoutMs,
        int RealizedRowHighWater,
        double SortMs,
        double FilterMs,
        long PrivateWorkingSetBytes);
}
