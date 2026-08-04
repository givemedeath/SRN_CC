using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;
using SRN.CC.Core.Build;
using SRN.CC.Core.Identity;
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
/// Proves the repeat-build determinism clause and the byte-level guarantees around it, over
/// genuine HAK bytes produced by <see cref="RealHakFixtureFactory"/>.
/// </summary>
/// <remarks>
/// The manifest is deliberately out of scope here: it carries <c>GeneratedUtc</c>, so only the HAK
/// itself is byte-reproducible. Determinism of the HAK is the property a content author can act on —
/// it is what lets them prove a redistributed pack is the pack they built.
/// </remarks>
[TestFixture]
public class HakDeterminismTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "srncc_hakdet_tests_" + Guid.NewGuid().ToString("N"));
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

    [Test]
    public void Fixture_WriteHak_IsReadableAndPreservesEveryResrefByteExactly()
    {
        var corpus = RealHakFixtureFactory.DefaultCorpus();
        string path = Path.Combine(_tempDir, "corpus.hak");
        RealHakFixtureFactory.WriteHak(path, corpus);

        using FileStream fs = File.OpenRead(path);
        var reader = new HakReader(fs);

        Assert.That(reader.FileType, Is.EqualTo("HAK "));
        Assert.That(reader.Entries, Has.Count.EqualTo(corpus.Count));

        foreach (var entry in corpus)
        {
            var match = reader.Entries.SingleOrDefault(e =>
                e.Key.ResourceType == entry.ResourceType &&
                e.Key.ResrefBytes.Span.SequenceEqual(entry.ResrefBytes));

            // Compared as raw bytes, never as decoded strings: a lossy decode is precisely the
            // defect the accented entry exists to catch, and string comparison would hide it.
            Assert.That(match, Is.Not.Null,
                $"No entry with resref bytes {Convert.ToHexString(entry.ResrefBytes)} and type {entry.ResourceType}.");
            Assert.That(match!.ResourceSize, Is.EqualTo((uint)entry.Payload.Length));
        }
    }

    [Test]
    public void Fixture_TwoDefaultCorpusInvocations_ProduceByteIdenticalFiles()
    {
        string first = Path.Combine(_tempDir, "a.hak");
        string second = Path.Combine(_tempDir, "b.hak");

        RealHakFixtureFactory.WriteHak(first, RealHakFixtureFactory.DefaultCorpus());
        RealHakFixtureFactory.WriteHak(second, RealHakFixtureFactory.DefaultCorpus());

        Assert.That(File.ReadAllBytes(second), Is.EqualTo(File.ReadAllBytes(first)));
    }

    [Test]
    public void Fixture_CorpusCoversTheDeclaredTypesAndBoundaries()
    {
        var corpus = RealHakFixtureFactory.DefaultCorpus();
        var types = corpus.Select(e => e.ResourceType).ToHashSet();

        Assert.Multiple(() =>
        {
            foreach (string extension in new[] { "mdl", "tga", "dds", "mtr", "txi", "2da", "lod" })
            {
                Assert.That(types, Does.Contain(RealHakFixtureFactory.TypeOf(extension)), $"Corpus is missing a '{extension}'.");
            }

            Assert.That(RealHakFixtureFactory.TypeOf("lod"), Is.EqualTo(2078));
            Assert.That(corpus.Any(e => e.ResrefBytes.Length == 16), Is.True, "Missing the 16-byte boundary resref.");
            Assert.That(corpus.Any(e => e.ResrefBytes.Contains((byte)0xE9)), Is.True, "Missing the CP1252-accented resref.");
            Assert.That(corpus.Any(e => e.Payload.Length == 0), Is.True, "Missing the zero-byte payload.");
            Assert.That(RealHakFixtureFactory.OversizedSourceName, Has.Length.EqualTo(17));
            Assert.That(RealHakFixtureFactory.TruncatedName, Has.Length.EqualTo(16));
            Assert.That(RealHakFixtureFactory.OversizedSourceName, Does.StartWith(RealHakFixtureFactory.TruncatedName));
            Assert.That(
                corpus.Any(e => RealHakFixtureFactory.DecodeResref(e.ResrefBytes) == RealHakFixtureFactory.TruncatedName),
                Is.True,
                "The 17-character name must appear truncated to its 16-byte resref.");
        });
    }

    [Test]
    public async Task Packer_SamePlanPackedTwice_ProducesByteIdenticalHak()
    {
        var scenario = await FolderScenarioAsync("src");

        string firstHak = Path.Combine(_tempDir, "first.hak");
        string secondHak = Path.Combine(_tempDir, "second.hak");

        var packer = new AssetPacker(scenario.Dispatcher);
        var firstArtifact = await packer.PackAsync(scenario.Plan, firstHak, Path.Combine(_tempDir, "first.manifest.json"));
        var secondArtifact = await packer.PackAsync(scenario.Plan, secondHak, Path.Combine(_tempDir, "second.manifest.json"));

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(secondHak), Is.EqualTo(File.ReadAllBytes(firstHak)));
            Assert.That(secondArtifact.HakSha256Hex, Is.EqualTo(firstArtifact.HakSha256Hex));
        });
    }

    [Test]
    public async Task Build_AccentedResref_RoundTripsAsRawBytes()
    {
        var scenario = await FolderScenarioAsync("src");
        string destination = Path.Combine(_tempDir, "dest", "out.hak");

        var result = await scenario.CreateOrchestrator().ExecuteBuildAsync(scenario.Workspace, destination);
        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);

        byte[] expected = RealHakFixtureFactory.AccentedResrefBytes();
        using FileStream fs = File.OpenRead(destination);
        var reader = new HakReader(fs);

        var accented = reader.Entries.SingleOrDefault(e => e.Key.ResrefBytes.Span.SequenceEqual(expected));
        Assert.That(accented, Is.Not.Null, "The accented resref did not survive the build as raw bytes.");
        Assert.That(accented!.Key.ResrefBytes.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public async Task Build_PathsContainingSpaces_Succeed()
    {
        var scenario = await FolderScenarioAsync("source folder with spaces");
        string destination = Path.Combine(_tempDir, "output dir with spaces", "my content pack.hak");

        var result = await scenario.CreateOrchestrator().ExecuteBuildAsync(scenario.Workspace, destination);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
            Assert.That(File.Exists(destination), Is.True);
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(destination)!, "my content pack.srncc-manifest.json")), Is.True);
        });
    }

    [Test]
    public async Task Build_TypesTheRegistryCannotName_ArePackagedOpaquely()
    {
        var scenario = await FolderScenarioAsync("src");
        string destination = Path.Combine(_tempDir, "dest", "out.hak");

        var result = await scenario.CreateOrchestrator().ExecuteBuildAsync(scenario.Workspace, destination);
        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);

        // 'lod' has no reader anywhere in the pipeline; its bytes must arrive unaltered regardless.
        var lodEntry = scenario.Corpus.Single(e => e.ResourceType == RealHakFixtureFactory.TypeOf("lod"));
        byte[] packed = ReadPayload(destination, lodEntry.ResrefBytes, lodEntry.ResourceType);
        Assert.That(packed, Is.EqualTo(lodEntry.Payload));

        // And a type the registry has no name for at all still reaches the manifest, named
        // numerically. The id is discovered from the registry rather than written as a literal, so
        // this can never assert against a type the vendored table has since learned.
        var registry = new ResourceTypeRegistry();
        ushort unnamedType = Enumerable.Range(2000, 200)
            .Select(i => (ushort)i)
            .First(t => !registry.TryGetExtension(t, out _));

        var opaqueScenario = await FolderScenarioAsync(
            "opaque",
            new[]
            {
                new RealHakFixtureFactory.FixtureEntry(
                    "opaque01"u8.ToArray(), unnamedType, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })
            });

        string opaqueDestination = Path.Combine(_tempDir, "opaque-dest", "out.hak");
        var opaqueResult = await opaqueScenario.CreateOrchestrator().ExecuteBuildAsync(opaqueScenario.Workspace, opaqueDestination);
        Assert.That(opaqueResult.IsSuccess, Is.True, opaqueResult.ErrorMessage);

        Assert.That(
            ReadPayload(opaqueDestination, "opaque01"u8.ToArray(), unnamedType),
            Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));

        // Manifest fields are PascalCase: the generator uses no PropertyNamingPolicy.
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(opaqueDestination)!, "out.srncc-manifest.json")));
        var resource = manifest.RootElement.GetProperty("Resources").EnumerateArray().Single();
        Assert.Multiple(() =>
        {
            Assert.That(resource.GetProperty("ResourceType").GetUInt16(), Is.EqualTo(unnamedType));
            Assert.That(resource.GetProperty("ResourceTypeName").GetString(), Is.EqualTo(unnamedType.ToString()));
        });
    }

    [Test]
    public async Task Build_SourceMutatedMidBuild_FailsVerification()
    {
        var scenario = await FolderScenarioAsync("src");
        string destination = Path.Combine(_tempDir, "dest", "out.hak");

        // Rewrite one source file, same length, the instant the packer asks for it. The plan's
        // frozen hash was taken before that, so verification is the only thing standing between a
        // mutated source and a silently wrong artifact.
        var target = scenario.Corpus.First(e => e.Payload.Length > 0);
        string targetPath = Path.Combine(scenario.SourceFolder, RealHakFixtureFactory.FileNameFor(target));
        byte[] mutated = target.Payload.ToArray();
        mutated[0] ^= 0xFF;

        var mutating = new MutateOnFirstOpenDispatcher(scenario.Dispatcher, targetPath, mutated);
        var result = await scenario.CreateOrchestrator(mutating).ExecuteBuildAsync(scenario.Workspace, destination);

        Assert.Multiple(() =>
        {
            Assert.That(mutating.DidMutate, Is.True, "The mutation never fired; the test proves nothing.");
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("HAK build verification failed"));
            Assert.That(result.ErrorMessage, Does.Contain("SHA-256 mismatch"));
            Assert.That(File.Exists(destination), Is.False, "A failed verification must publish nothing.");
        });
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static byte[] ReadPayload(string hakPath, byte[] resrefBytes, ushort resourceType)
    {
        using FileStream fs = File.OpenRead(hakPath);
        var reader = new HakReader(fs);
        var entry = reader.Entries.Single(e =>
            e.Key.ResourceType == resourceType && e.Key.ResrefBytes.Span.SequenceEqual(resrefBytes));

        fs.Seek(entry.OffsetToResource, SeekOrigin.Begin);
        byte[] buffer = new byte[entry.ResourceSize];
        fs.ReadExactly(buffer);
        return buffer;
    }

    private sealed class Scenario
    {
        public required IReadOnlyList<RealHakFixtureFactory.FixtureEntry> Corpus { get; init; }
        public required string SourceFolder { get; init; }
        public required WorkspaceState Workspace { get; init; }
        public required BuildPlan Plan { get; init; }
        public required ISourceReaderDispatcher Dispatcher { get; init; }

        public BuildOrchestrator CreateOrchestrator(ISourceReaderDispatcher? dispatcher = null)
        {
            var registry = new ResourceTypeRegistry();
            var effective = dispatcher ?? Dispatcher;
            return new BuildOrchestrator(
                new AssetPacker(effective),
                new BuildVerifier(),
                new ProvenanceManifestGenerator(registry),
                new ArtifactPublisher(),
                registry,
                effective);
        }
    }

    private Task<Scenario> FolderScenarioAsync(string folderName) =>
        FolderScenarioAsync(folderName, RealHakFixtureFactory.DefaultCorpus());

    private Task<Scenario> FolderScenarioAsync(
        string folderName,
        IReadOnlyList<RealHakFixtureFactory.FixtureEntry> corpus)
    {
        string folder = Path.Combine(_tempDir, folderName);
        RealHakFixtureFactory.WriteFolderSource(folder, corpus);

        var source = AssetSource.CreateFolder(folder);
        List<CuratedAsset> assets = new();
        List<BuildItem> items = new();

        foreach (var entry in corpus)
        {
            string fileName = RealHakFixtureFactory.FileNameFor(entry);
            var identity = new AssetIdentity(entry.ResrefBytes, entry.ResourceType);
            var locator = new FolderFileLocator(fileName);
            byte[] hash = SHA256.HashData(entry.Payload);

            var occurrence = new AssetOccurrence(
                identity, source.Id, locator, fileName, entry.Payload.Length, ValidationState.Valid, null, hash);
            assets.Add(new CuratedAsset(identity, new[] { occurrence }, occurrence, null, ResolutionStatus.Resolved, true));
            items.Add(new BuildItem(
                identity, source.Id, locator, entry.Payload.Length, Convert.ToHexString(hash).ToLowerInvariant(), false));
        }

        var workspace = new WorkspaceState(
            new[] { source },
            new Dictionary<Guid, SourceIndexSnapshot>(),
            assets,
            SelectionState.IncludeAll(),
            Array.Empty<WinnerPin>());

        var plan = new BuildPlan
        {
            DestinationHakPath = Path.Combine(_tempDir, folderName + "-plan", "plan.hak"),
            DestinationManifestPath = Path.Combine(_tempDir, folderName + "-plan", "plan.srncc-manifest.json"),
            FrozenSources = new[] { source },
            Items = items,
            // Fixed, never DateTime.UtcNow: a plan built from a clock cannot be replayed.
            CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        return Task.FromResult(new Scenario
        {
            Corpus = corpus,
            SourceFolder = folder,
            Workspace = workspace,
            Plan = plan,
            Dispatcher = new SourceReaderDispatcher(typeRegistry: new ResourceTypeRegistry())
        });
    }

    /// <summary>Rewrites one source file the first time the packer opens anything.</summary>
    private sealed class MutateOnFirstOpenDispatcher : ISourceReaderDispatcher
    {
        private readonly ISourceReaderDispatcher _inner;
        private readonly string _path;
        private readonly byte[] _replacement;

        public MutateOnFirstOpenDispatcher(ISourceReaderDispatcher inner, string path, byte[] replacement)
        {
            _inner = inner;
            _path = path;
            _replacement = replacement;
        }

        public bool DidMutate { get; private set; }

        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            if (!DidMutate)
            {
                DidMutate = true;
                File.WriteAllBytes(_path, _replacement);
            }

            return _inner.OpenOccurrenceAsync(source, occurrence, cancellationToken);
        }
    }
}
