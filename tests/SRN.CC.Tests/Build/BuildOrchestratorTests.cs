using System.Security.Cryptography;
using NUnit.Framework;
using SRN.CC.Core.Build;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Formats.Hak;
using SRN.CC.Infrastructure.Build;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Tests.Build;

/// <summary>
/// Covers <see cref="BuildOrchestrator"/>'s preflight gate and its temp-file lifetime.
/// </summary>
/// <remarks>
/// Each preflight asserts on its own message rather than on "the build failed": the gate exists so a
/// user is told which of five different problems they have, and five tests that all assert failure
/// would pass even if the orchestrator collapsed them into one generic error.
/// </remarks>
[TestFixture]
public class BuildOrchestratorTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "srncc_orchestrator_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Preflight 1a: the destination is one of the source files.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task Preflight_OutputPathIsASourceFile_ReportsDirectOverlap()
    {
        string sourceHak = Path.Combine(_tempDir, "source.hak");
        RealHakFixtureFactory.WriteHak(sourceHak, RealHakFixtureFactory.DefaultCorpus());

        var source = AssetSource.CreateHak(sourceHak);
        var workspace = BuildWorkspace(new[] { source }, new[] { ResolvedAsset(source, "dtl_wall", "mdl", 512) });

        var packer = new ThrowingPacker();
        var result = await CreateOrchestrator(packer).ExecuteBuildAsync(workspace, sourceHak);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("overlaps directly with source path"));
            Assert.That(result.ErrorMessage, Does.Contain(sourceHak));
            Assert.That(packer.WasCalled, Is.False, "Preflight must reject before any packing work starts.");
        });
    }

    // ---------------------------------------------------------------------------------------
    // Preflight 1b: the destination is nested inside a folder source.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task Preflight_OutputInsideSourceFolder_ReportsContainment()
    {
        string folder = Path.Combine(_tempDir, "loose");
        RealHakFixtureFactory.WriteFolderSource(folder, RealHakFixtureFactory.DefaultCorpus());

        var source = AssetSource.CreateFolder(folder);
        var workspace = BuildWorkspace(new[] { source }, new[] { ResolvedAsset(source, "dtl_wall", "mdl", 512) });

        string destination = Path.Combine(folder, "nested", "out.hak");
        var packer = new ThrowingPacker();
        var result = await CreateOrchestrator(packer).ExecuteBuildAsync(workspace, destination);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("is located inside source folder"));
            Assert.That(result.ErrorMessage, Does.Contain(folder));
            // Distinct from the direct-overlap wording: the two situations need different remedies.
            Assert.That(result.ErrorMessage, Does.Not.Contain("overlaps directly"));
            Assert.That(packer.WasCalled, Is.False);
        });
    }

    // ---------------------------------------------------------------------------------------
    // Preflight 2a: nothing selected.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task Preflight_NothingSelected_ReportsEmptySelection()
    {
        string sourceHak = Path.Combine(_tempDir, "source.hak");
        RealHakFixtureFactory.WriteHak(sourceHak, RealHakFixtureFactory.DefaultCorpus());

        var source = AssetSource.CreateHak(sourceHak);
        var asset = ResolvedAsset(source, "dtl_wall", "mdl", 512);
        var deselected = new CuratedAsset(
            asset.Identity, asset.AllOccurrences, asset.ResolvedOccurrence, null, ResolutionStatus.Resolved, isSelected: false);

        var workspace = BuildWorkspace(new[] { source }, new[] { deselected });

        var packer = new ThrowingPacker();
        var result = await CreateOrchestrator(packer).ExecuteBuildAsync(workspace, Path.Combine(_tempDir, "out.hak"));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Is.EqualTo("No assets are selected for building."));
            Assert.That(result.Logs, Is.EquivalentTo(new[] { "No assets are selected for building." }));
            Assert.That(packer.WasCalled, Is.False);
        });
    }

    // ---------------------------------------------------------------------------------------
    // Preflight 2b: a selected asset that is not resolved.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task Preflight_SelectedAssetUnresolved_NamesTheAssetAndItsStatus()
    {
        string sourceHak = Path.Combine(_tempDir, "source.hak");
        RealHakFixtureFactory.WriteHak(sourceHak, RealHakFixtureFactory.DefaultCorpus());

        var source = AssetSource.CreateHak(sourceHak);
        var resolved = ResolvedAsset(source, "dtl_wall", "mdl", 512);
        var unresolved = new CuratedAsset(
            resolved.Identity,
            resolved.AllOccurrences,
            resolved.ResolvedOccurrence,
            pin: null,
            status: ResolutionStatus.UnresolvedDuplicate,
            isSelected: true);

        var workspace = BuildWorkspace(new[] { source }, new[] { unresolved });

        var packer = new ThrowingPacker();
        var result = await CreateOrchestrator(packer).ExecuteBuildAsync(workspace, Path.Combine(_tempDir, "out.hak"));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("is not resolved"));
            // The status has to survive into the message; "unresolved" alone is not actionable.
            Assert.That(result.ErrorMessage, Does.Contain(nameof(ResolutionStatus.UnresolvedDuplicate)));
            Assert.That(result.ErrorMessage, Does.Contain(resolved.Identity.ToString()));
            Assert.That(packer.WasCalled, Is.False);
        });
    }

    // ---------------------------------------------------------------------------------------
    // Preflight 3: the estimated output exceeds the single-HAK limit.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task Preflight_EstimateExceedsSingleHakLimit_ReportsBothNumbers()
    {
        string sourceHak = Path.Combine(_tempDir, "source.hak");
        RealHakFixtureFactory.WriteHak(sourceHak, RealHakFixtureFactory.DefaultCorpus());

        var source = AssetSource.CreateHak(sourceHak);

        // Declared size only. The gate must fire on the estimate, before a single payload byte is
        // read — otherwise every over-limit build would first spend minutes hashing 2 GiB.
        var identity = new AssetIdentity("hugefile", RealHakFixtureFactory.TypeOf("mdl"));
        var occurrence = new AssetOccurrence(
            identity,
            source.Id,
            new HakEntryLocator(0),
            "hugefile.mdl",
            HakWriter.LegacySingleHakLimit,
            ValidationState.Valid,
            null,
            SHA256.HashData(Array.Empty<byte>()));
        var asset = new CuratedAsset(identity, new[] { occurrence }, occurrence, null, ResolutionStatus.Resolved, isSelected: true);

        var workspace = BuildWorkspace(new[] { source }, new[] { asset });

        var packer = new ThrowingPacker();
        var result = await CreateOrchestrator(packer).ExecuteBuildAsync(workspace, Path.Combine(_tempDir, "out.hak"));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("single-HAK limit"));
            Assert.That(result.ErrorMessage, Does.Contain(HakWriter.LegacySingleHakLimit.ToString("N0")));
            Assert.That(packer.WasCalled, Is.False);
        });
    }

    [Test]
    public async Task Preflight_TheFiveGates_ProduceFiveDistinctMessages()
    {
        // A single test that the five messages are pairwise distinct. Without it, collapsing the
        // gate into one generic error would leave the five tests above still green individually
        // only by accident of substring overlap.
        List<string> messages = new();

        foreach (Func<Task<PublicationResult>> scenario in new Func<Task<PublicationResult>>[]
                 {
                     RunDirectOverlapAsync,
                     RunInsideFolderAsync,
                     RunNothingSelectedAsync,
                     RunUnresolvedAsync,
                     RunOverLimitAsync
                 })
        {
            var result = await scenario();
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
            messages.Add(result.ErrorMessage!);
        }

        Assert.That(messages.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(5));
    }

    // ---------------------------------------------------------------------------------------
    // Temp-file lifetime.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteBuild_OnSuccess_LeavesNoTempFiles()
    {
        var scenario = FolderBackedScenario("out.hak");

        var result = await CreateOrchestrator().ExecuteBuildAsync(scenario.Workspace, scenario.DestinationPath);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        AssertNoTempFiles(scenario.DestinationDirectory);
    }

    [Test]
    public async Task ExecuteBuild_WhenVerificationFails_StillDeletesTempFiles()
    {
        var scenario = FolderBackedScenario("out.hak");

        var orchestrator = CreateOrchestrator(verifier: new FailingVerifier());
        var result = await orchestrator.ExecuteBuildAsync(scenario.Workspace, scenario.DestinationPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("HAK build verification failed"));
        });
        AssertNoTempFiles(scenario.DestinationDirectory);
    }

    [Test]
    public void ExecuteBuild_WhenPackerThrows_StillDeletesTempFiles()
    {
        var scenario = FolderBackedScenario("out.hak");

        // Must create the temp files first: a packer that throws before touching disk would let this
        // test pass without the `finally` ever doing anything.
        var orchestrator = CreateOrchestrator(new CreatingThenThrowingPacker());

        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await orchestrator.ExecuteBuildAsync(scenario.Workspace, scenario.DestinationPath));

        AssertNoTempFiles(scenario.DestinationDirectory);
    }

    [Test]
    public async Task ExecuteBuild_WhenTempDeletionFails_LogsInsteadOfSwallowing()
    {
        // The `finally` block used to hide a delete failure behind a bare `catch { }`, so a leaked
        // temp file had no evidence anywhere but the directory listing. Hold the temp HAK open with
        // FileShare.None so File.Delete cannot succeed, then assert the logger heard about it.
        var scenario = FolderBackedScenario("out.hak");
        var logger = new CapturingLogger();

        using var packer = new LockingPacker();
        var orchestrator = CreateOrchestrator(packer, new FailingVerifier(), logger);
        var result = await orchestrator.ExecuteBuildAsync(scenario.Workspace, scenario.DestinationPath);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(
            logger.Records.Any(r => r.Level == LogLevel.Warn && r.Message.Contains("Failed to delete build temp file", StringComparison.Ordinal)),
            Is.True,
            "A temp file that could not be deleted must be logged, not swallowed.");
    }

    // ---------------------------------------------------------------------------------------
    // Scenario builders
    // ---------------------------------------------------------------------------------------

    private async Task<PublicationResult> RunDirectOverlapAsync()
    {
        string sourceHak = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".hak");
        RealHakFixtureFactory.WriteHak(sourceHak, RealHakFixtureFactory.DefaultCorpus());
        var source = AssetSource.CreateHak(sourceHak);
        var workspace = BuildWorkspace(new[] { source }, new[] { ResolvedAsset(source, "dtl_wall", "mdl", 512) });
        return await CreateOrchestrator(new ThrowingPacker()).ExecuteBuildAsync(workspace, sourceHak);
    }

    private async Task<PublicationResult> RunInsideFolderAsync()
    {
        string folder = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        RealHakFixtureFactory.WriteFolderSource(folder, RealHakFixtureFactory.DefaultCorpus());
        var source = AssetSource.CreateFolder(folder);
        var workspace = BuildWorkspace(new[] { source }, new[] { ResolvedAsset(source, "dtl_wall", "mdl", 512) });
        return await CreateOrchestrator(new ThrowingPacker())
            .ExecuteBuildAsync(workspace, Path.Combine(folder, "sub", "out.hak"));
    }

    private async Task<PublicationResult> RunNothingSelectedAsync()
    {
        var source = AssetSource.CreateHak(Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".hak"));
        var workspace = BuildWorkspace(new[] { source }, Array.Empty<CuratedAsset>());
        return await CreateOrchestrator(new ThrowingPacker())
            .ExecuteBuildAsync(workspace, Path.Combine(_tempDir, Guid.NewGuid().ToString("N"), "out.hak"));
    }

    private async Task<PublicationResult> RunUnresolvedAsync()
    {
        var source = AssetSource.CreateHak(Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".hak"));
        var resolved = ResolvedAsset(source, "dtl_wall", "mdl", 512);
        var unresolved = new CuratedAsset(
            resolved.Identity, resolved.AllOccurrences, resolved.ResolvedOccurrence, null, ResolutionStatus.InvalidPin, true);
        var workspace = BuildWorkspace(new[] { source }, new[] { unresolved });
        return await CreateOrchestrator(new ThrowingPacker())
            .ExecuteBuildAsync(workspace, Path.Combine(_tempDir, Guid.NewGuid().ToString("N"), "out.hak"));
    }

    private async Task<PublicationResult> RunOverLimitAsync()
    {
        var source = AssetSource.CreateHak(Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".hak"));
        var identity = new AssetIdentity("hugefile", RealHakFixtureFactory.TypeOf("mdl"));
        var occurrence = new AssetOccurrence(
            identity, source.Id, new HakEntryLocator(0), "hugefile.mdl",
            HakWriter.LegacySingleHakLimit, ValidationState.Valid, null, SHA256.HashData(Array.Empty<byte>()));
        var asset = new CuratedAsset(identity, new[] { occurrence }, occurrence, null, ResolutionStatus.Resolved, true);
        var workspace = BuildWorkspace(new[] { source }, new[] { asset });
        return await CreateOrchestrator(new ThrowingPacker())
            .ExecuteBuildAsync(workspace, Path.Combine(_tempDir, Guid.NewGuid().ToString("N"), "out.hak"));
    }

    private sealed record Scenario(WorkspaceState Workspace, string DestinationPath, string DestinationDirectory);

    /// <summary>
    /// A workspace over a genuine loose-file source, packable end to end through the real packer,
    /// verifier, manifest generator, and publisher.
    /// </summary>
    private Scenario FolderBackedScenario(string outputFileName)
    {
        string folder = Path.Combine(_tempDir, "src");
        var corpus = RealHakFixtureFactory.DefaultCorpus();
        RealHakFixtureFactory.WriteFolderSource(folder, corpus);

        var source = AssetSource.CreateFolder(folder);
        List<CuratedAsset> assets = new();
        foreach (var entry in corpus)
        {
            string fileName = RealHakFixtureFactory.FileNameFor(entry);
            var identity = new AssetIdentity(entry.ResrefBytes, entry.ResourceType);
            var occurrence = new AssetOccurrence(
                identity,
                source.Id,
                new FolderFileLocator(fileName),
                fileName,
                entry.Payload.Length,
                ValidationState.Valid,
                null,
                SHA256.HashData(entry.Payload));
            assets.Add(new CuratedAsset(identity, new[] { occurrence }, occurrence, null, ResolutionStatus.Resolved, true));
        }

        string destinationDirectory = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(destinationDirectory);

        return new Scenario(
            BuildWorkspace(new[] { source }, assets),
            Path.Combine(destinationDirectory, outputFileName),
            destinationDirectory);
    }

    private CuratedAsset ResolvedAsset(AssetSource source, string resref, string extension, long size)
    {
        var identity = new AssetIdentity(resref, RealHakFixtureFactory.TypeOf(extension));
        var occurrence = new AssetOccurrence(
            identity,
            source.Id,
            new HakEntryLocator(0),
            $"{resref}.{extension}",
            size,
            ValidationState.Valid,
            null,
            SHA256.HashData(Array.Empty<byte>()));
        return new CuratedAsset(identity, new[] { occurrence }, occurrence, null, ResolutionStatus.Resolved, isSelected: true);
    }

    private static WorkspaceState BuildWorkspace(IReadOnlyList<AssetSource> sources, IReadOnlyList<CuratedAsset> assets) =>
        new(
            sources,
            new Dictionary<Guid, SourceIndexSnapshot>(),
            assets,
            SelectionState.IncludeAll(),
            Array.Empty<WinnerPin>());

    private static BuildOrchestrator CreateOrchestrator(
        IAssetPacker? packer = null,
        IBuildVerifier? verifier = null,
        IAppLogger? logger = null)
    {
        var registry = new ResourceTypeRegistry();
        var dispatcher = new SourceReaderDispatcher(typeRegistry: registry);
        return new BuildOrchestrator(
            packer ?? new AssetPacker(dispatcher),
            verifier ?? new BuildVerifier(),
            new ProvenanceManifestGenerator(registry),
            new ArtifactPublisher(),
            registry,
            dispatcher,
            logger);
    }

    private static void AssertNoTempFiles(string directory)
    {
        var leftovers = Directory.GetFiles(directory, "*.tmp.*", SearchOption.TopDirectoryOnly);
        Assert.That(leftovers, Is.Empty, "Build temp files must be removed on every exit path.");
    }

    // ---------------------------------------------------------------------------------------
    // Doubles
    // ---------------------------------------------------------------------------------------

    /// <summary>A packer that must never run; records the attempt so a preflight test can prove it.</summary>
    private sealed class ThrowingPacker : IAssetPacker
    {
        public bool WasCalled { get; private set; }

        public Task<BuildArtifact> PackAsync(
            BuildPlan plan, string tempHakPath, string tempManifestPath,
            IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Packer invoked.");
        }
    }

    /// <summary>Writes both temp files and then fails, exercising the cleanup path after real IO.</summary>
    private sealed class CreatingThenThrowingPacker : IAssetPacker
    {
        public Task<BuildArtifact> PackAsync(
            BuildPlan plan, string tempHakPath, string tempManifestPath,
            IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(tempHakPath, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(tempManifestPath, new byte[] { 4 });
            throw new InvalidOperationException("Packer failed after writing temp files.");
        }
    }

    /// <summary>Writes both temp files, then keeps the HAK open exclusively so deletion must fail.</summary>
    private sealed class LockingPacker : IAssetPacker, IDisposable
    {
        private FileStream? _held;

        public Task<BuildArtifact> PackAsync(
            BuildPlan plan, string tempHakPath, string tempManifestPath,
            IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(tempManifestPath, Array.Empty<byte>());
            _held = new FileStream(tempHakPath, FileMode.Create, FileAccess.Write, FileShare.None);
            _held.WriteByte(0);
            _held.Flush();
            return Task.FromResult(new BuildArtifact(plan.DestinationHakPath, plan.DestinationManifestPath, 1, string.Empty, 0));
        }

        public void Dispose() => _held?.Dispose();
    }

    private sealed class FailingVerifier : IBuildVerifier
    {
        public Task<BuildVerificationReport> VerifyAsync(BuildPlan plan, string tempHakPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BuildVerificationReport(
                false, 0, 0, string.Empty, new[] { "injected verification failure" }, Array.Empty<string>()));
    }

    private sealed class CapturingLogger : IAppLogger
    {
        private readonly List<(LogLevel Level, string Category, string Message)> _records = new();

        public IReadOnlyList<(LogLevel Level, string Category, string Message)> Records => _records;

        public void Log(LogLevel level, string category, string message, Exception? exception = null, IReadOnlyDictionary<string, string>? data = null) =>
            _records.Add((level, category, message));
    }
}
