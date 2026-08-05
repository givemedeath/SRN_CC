using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Project;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Startup;
using SRN.CC.Core.Workspace;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Infrastructure.Startup;
using SRN.CC.Tests.Startup;

namespace SRN.CC.Tests.App;

/// <summary>
/// Closes the project-file extension loop.
/// </summary>
/// <remarks>
/// <para>
/// The application wrote and filtered on <c>.srncc</c> while the plan, every test, every fixture,
/// and <see cref="PublicationJournalStartupCheck"/> use <c>.srnccproj</c>. That mismatch was not
/// cosmetic: the startup journal recovery's project-file branch — the one that adds a project's
/// configured output directory to the recovery set — compared the launch argument's extension
/// against <c>".srncc"</c> and therefore never matched. A publish interrupted while writing to an
/// output directory outside the project folder was never recovered and never could be.
/// </para>
/// <para>
/// The check was corrected in wave 1, but the branch still matched nothing in practice while the
/// application named its own files <c>.srncc</c>. These tests are what prove the loop is closed:
/// the extension the application writes is the extension the recovery pass looks for, and a project
/// saved by the real store really does contribute its configured output directory.
/// </para>
/// </remarks>
[TestFixture]
public class ProjectExtensionTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_ProjectExtensionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Test]
    public void TheApplicationsProjectExtension_IsTheOneTheRecoveryCheckLooksFor()
    {
        MainWindowViewModel.ProjectFileExtension
            .Should().Be(PublicationJournalStartupCheck.ProjectFileExtension);

        MainWindowViewModel.DefaultProjectFileName
            .Should().EndWith(MainWindowViewModel.ProjectFileExtension);
    }

    [Test]
    public async Task AProjectSavedByTheRealStore_ContributesItsConfiguredOutputDirectoryToJournalRecovery()
    {
        string projectDir = Path.Combine(_tempDir, "project");
        string outputDir = Path.Combine(_tempDir, "elsewhere", "output");
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(outputDir);

        string projectPath = Path.Combine(projectDir, "demo" + MainWindowViewModel.ProjectFileExtension);
        string targetHak = Path.Combine(outputDir, "demo.hak");

        var store = new ProjectStore(new EmptyIndexService(), new WorkspaceResolver(new ConstantHashService()));
        await store.SaveAsync(BuildStateWithTargetHak(targetHak), projectPath);

        File.Exists(projectPath).Should().BeTrue();

        var publisher = new FakeArtifactPublisher();
        var check = new PublicationJournalStartupCheck(
            publisher,
            store,
            startupArgs: new[] { projectPath },
            recentProjectPaths: null,
            currentDirectory: () => _tempDir);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Ok);
        publisher.ScannedDirectories.Should().Contain(Path.GetFullPath(projectDir));
        publisher.ScannedDirectories.Should().Contain(
            Path.GetFullPath(outputDir),
            "the configured output directory is exactly what the wrong extension made unreachable");
    }

    [Test]
    public async Task TheOldExtension_WouldStillBeInvisibleToJournalRecovery()
    {
        // The negative half of the pair: a project file named with the old extension contributes its
        // own folder (every existing startup-argument file does) but never its configured output
        // directory, because the project-file branch does not fire. This is what shipped before.
        string projectDir = Path.Combine(_tempDir, "legacy");
        string outputDir = Path.Combine(_tempDir, "legacy-output");
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(outputDir);

        string projectPath = Path.Combine(projectDir, "demo.srncc");
        string targetHak = Path.Combine(outputDir, "demo.hak");

        var store = new ProjectStore(new EmptyIndexService(), new WorkspaceResolver(new ConstantHashService()));
        await store.SaveAsync(BuildStateWithTargetHak(targetHak), projectPath);

        var publisher = new FakeArtifactPublisher();
        var check = new PublicationJournalStartupCheck(
            publisher,
            store,
            startupArgs: new[] { projectPath },
            recentProjectPaths: null,
            currentDirectory: () => _tempDir);

        await check.RunAsync();

        publisher.ScannedDirectories.Should().Contain(Path.GetFullPath(projectDir));
        publisher.ScannedDirectories.Should().NotContain(Path.GetFullPath(outputDir));
    }

    [Test]
    public void NoSourceFileStillCarriesTheOldExtensionAsAStringLiteral()
    {
        string srcRoot = Path.Combine(FindRepoRoot(), "src");
        Directory.Exists(srcRoot).Should().BeTrue();

        // A double-quoted literal containing ".srncc" that is not ".srnccproj" and not
        // ".srncc-manifest.json" — the latter is a different, correct literal.
        var offender = new Regex(@"""[^""\r\n]*\.srncc(?![A-Za-z\-])[^""\r\n]*""", RegexOptions.CultureInvariant);

        var hits = new List<string>();
        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".axaml" or ".csproj"))
            {
                continue;
            }

            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            int lineNumber = 0;
            foreach (string rawLine in File.ReadLines(file))
            {
                lineNumber++;
                string line = StripComment(rawLine);
                if (offender.IsMatch(line))
                {
                    hits.Add($"{file}:{lineNumber}: {rawLine.Trim()}");
                }
            }
        }

        hits.Should().BeEmpty("picker patterns and default file names must all be *"
            + MainWindowViewModel.ProjectFileExtension);
    }

    private static string StripComment(string line)
    {
        int index = line.IndexOf("//", StringComparison.Ordinal);
        return index >= 0 ? line[..index] : line;
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "SRN.CC.sln")))
        {
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        return dir;
    }

    private static WorkspaceState BuildStateWithTargetHak(string targetHak) => new(
        Array.Empty<AssetSource>(),
        new Dictionary<Guid, SourceIndexSnapshot>(),
        Array.Empty<CuratedAsset>(),
        new SelectionState(),
        Array.Empty<WinnerPin>(),
        new ProjectPreferences(outputSettings: new JsonObject { ["targetHak"] = targetHak }));

    private sealed class EmptyIndexService : IAssetIndexService
    {
        public Task<SourceIndexSnapshot> IndexAsync(
            AssetSource source,
            IProgress<IndexProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SourceIndexSnapshot(
                source,
                new SourceFingerprint(source.Kind, 1, new byte[32]),
                Array.Empty<SRN.CC.Core.Records.IndexedAssetRecord>(),
                Array.Empty<SRN.CC.Core.Diagnostics.AssetDiagnosticRecord>(),
                isCacheHit: false,
                scanStatistics: new SourceScanStatistics(0, 0, TimeSpan.Zero)));
    }

    private sealed class ConstantHashService : IStreamingHashService
    {
        public Task<byte[]> ComputeSha256Async(
            AssetSource source,
            AssetOccurrence occurrence,
            CancellationToken cancellationToken = default)
            => Task.FromResult(SHA256.HashData(Encoding.UTF8.GetBytes(occurrence.Locator.ToString() ?? string.Empty)));
    }
}
