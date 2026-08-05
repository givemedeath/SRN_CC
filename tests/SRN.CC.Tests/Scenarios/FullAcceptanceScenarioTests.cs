using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Build;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Schema;
using SRN.CC.Core.Selection;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Startup;
using SRN.CC.Core.Workspace;
using SRN.CC.Formats.Hak;
using SRN.CC.Infrastructure.Build;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Logging;
using SRN.CC.Infrastructure.Persistence;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Infrastructure.Startup;
using SRN.CC.Tests.Build;

namespace SRN.CC.Tests.Scenarios;

/// <summary>
/// The real end-to-end acceptance flow: import, resolve, pin, save, reopen, rescan, build, verify,
/// publish and recover — on genuine HAK bytes, through a service graph containing no fake, stub or
/// hand-rolled substitute for any production type.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ControlledAcceptanceScenarioTests"/> covers the same narrative over in-memory index and
/// hash fakes and <c>.hak</c> files that are really text. That test proves the state machine; it
/// cannot prove the format, the packer, the verifier, the manifest, the publication journal, or the
/// bytes. This one does, which is why every service below is the shipped implementation and every
/// fixture is written through the production <see cref="HakWriter"/>.
/// </para>
/// <para>
/// The fixture is deliberately uncategorised so that CI's default
/// <c>Category!=Corpus&amp;Category!=Performance</c> filter runs it, and everything it touches —
/// cache database, logs, projects, sources and build output — lives under a single temp root that is
/// pointed at by an explicit <see cref="AppPaths"/>. Nothing here may reach
/// <c>%LOCALAPPDATA%</c>; <see cref="TearDown"/> asserts as much.
/// </para>
/// </remarks>
[TestFixture]
public class FullAcceptanceScenarioTests
{
    private const string BuiltHakFileName = "curated.hak";
    private const string BuiltManifestFileName = "curated.srncc-manifest.json";
    private const string LooseSourceFolderName = "loose assets";
    private const string NestedFolderWithSpace = "nested dir";
    private const string DuplicateFolderResref = "dup_tex";

    private static readonly Encoding Cp1252 = CreateCp1252();

    private string _tempRoot = null!;
    private AppPaths _appPaths = null!;
    private AppLogger _logger = null!;
    private JsonLineLogSink _logSink = null!;
    private SqliteCacheService _cache = null!;
    private bool _localAppDataRootExistedBefore;

    private static Encoding CreateCp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SRNCC_FullAcceptance_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _appPaths = new AppPaths(_tempRoot);
        LogFileSet logFiles = new(_appPaths.LogDirectory);
        logFiles.EnsureDirectory();
        _logSink = new JsonLineLogSink(logFiles);
        _logger = new AppLogger(new ILogSink[] { _logSink });
        _cache = new SqliteCacheService(_appPaths.CacheDatabasePath, logger: _logger);

        _localAppDataRootExistedBefore = Directory.Exists(AppPaths.Default.Root);
    }

    [TearDown]
    public void TearDown()
    {
        _cache?.Dispose();
        _logger?.Dispose();

        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // Exit criterion: deleting the temp root leaves no residue anywhere else on the machine. The
        // scenario points every store at the temp AppPaths, so a shipped default root that did not
        // exist before must still not exist.
        if (!_localAppDataRootExistedBefore)
        {
            Directory.Exists(AppPaths.Default.Root).Should().BeFalse(
                "the scenario must not create the shipped %LOCALAPPDATA% data directory");
        }
    }

    [Test]
    public async Task FullAcceptanceFlow_ImportResolvePinSaveRescanBuildVerifyPublishRecover()
    {
        // ==============================================================================
        // Real service graph. Every one of these is the shipped implementation.
        // ==============================================================================
        ResourceTypeRegistry registry = new();
        AssetIndexService indexService = new(_cache, registry);
        SourceReaderDispatcher dispatcher = new(typeRegistry: registry);
        StreamingHashService hashService = new(dispatcher);
        AssetHashCache hashCache = new();
        WorkspaceResolver resolver = new(hashService, hashCache);
        WorkspaceService workspaceService = new(indexService, resolver, hashCache);
        ProjectStore projectStore = new(indexService, resolver, _logger);
        ArtifactPublisher publisher = new(_logger);
        BuildOrchestrator orchestrator = new(
            new AssetPacker(dispatcher),
            new BuildVerifier(),
            new ProvenanceManifestGenerator(registry),
            publisher,
            registry,
            dispatcher,
            _logger);

        // ==============================================================================
        // Step 1 — import.
        // ==============================================================================
        IReadOnlyList<RealHakFixtureFactory.FixtureEntry> corpus = RealHakFixtureFactory.DefaultCorpus();

        RealHakFixtureFactory.FixtureEntry dtlWallMdl = Pick(corpus, "dtl_wall", "mdl");
        RealHakFixtureFactory.FixtureEntry dtlWallTga = Pick(corpus, "dtl_wall", "tga");
        RealHakFixtureFactory.FixtureEntry accented = corpus.Single(
            e => e.ResrefBytes.AsSpan().SequenceEqual(RealHakFixtureFactory.AccentedResrefBytes()));

        List<RealHakFixtureFactory.FixtureEntry> baseEntries = new()
        {
            dtlWallMdl,
            dtlWallTga,
            Pick(corpus, "crate01", "dds"),
            Pick(corpus, "crate01", "mtr"),
            Pick(corpus, "crate01", "txi"),   // zero-byte payload
            Pick(corpus, "appearance", "2da"),
            accented                          // CP1252-accented resref
        };
        baseEntries.Should().HaveCount(7);

        // The override carries a byte-identical duplicate of base's dtl_wall.mdl and a differing-hash
        // conflict against base's dtl_wall.tga.
        byte[] conflictingTgaPayload = DeterministicPayload(1024, 0x00AC_0001UL);
        conflictingTgaPayload.Should().NotEqual(dtlWallTga.Payload);

        List<RealHakFixtureFactory.FixtureEntry> overrideEntries = new()
        {
            new RealHakFixtureFactory.FixtureEntry(dtlWallMdl.ResrefBytes, dtlWallMdl.ResourceType, dtlWallMdl.Payload),
            new RealHakFixtureFactory.FixtureEntry(dtlWallTga.ResrefBytes, dtlWallTga.ResourceType, conflictingTgaPayload),
            new RealHakFixtureFactory.FixtureEntry(Cp1252.GetBytes("ovr_only"), RealHakFixtureFactory.TypeOf("mdl"), DeterministicPayload(200, 0x00AC_0002UL)),
            Pick(corpus, "tree_lod0", "lod")
        };
        overrideEntries.Should().HaveCount(4);

        string baseHakPath = Path.Combine(_tempRoot, "base.hak");
        string overrideHakPath = Path.Combine(_tempRoot, "override.hak");
        RealHakFixtureFactory.WriteHak(baseHakPath, baseEntries);
        RealHakFixtureFactory.WriteHak(overrideHakPath, overrideEntries);

        // Folder source: two files, one identity, byte-identical payloads, one relative path
        // containing a space. This is the only shape that can exercise the same-source
        // identical-duplicate rule — see the note in the class remarks for HakWriter.
        string looseDir = Path.Combine(_tempRoot, LooseSourceFolderName);
        byte[] duplicatePayload = DeterministicPayload(321, 0x00AC_0003UL);
        RealHakFixtureFactory.FixtureEntry duplicateEntry = new(
            Cp1252.GetBytes(DuplicateFolderResref),
            RealHakFixtureFactory.TypeOf("tga"),
            duplicatePayload);
        RealHakFixtureFactory.WriteFolderSource(looseDir, new[] { duplicateEntry });
        string nestedDir = Path.Combine(looseDir, NestedFolderWithSpace);
        Directory.CreateDirectory(nestedDir);
        string nestedFileName = RealHakFixtureFactory.FileNameFor(duplicateEntry);
        await File.WriteAllBytesAsync(Path.Combine(nestedDir, nestedFileName), duplicatePayload);

        AssetSource baseSource = AssetSource.CreateHak(baseHakPath, priorityOrdinal: 0);
        AssetSource overrideSource = AssetSource.CreateHak(overrideHakPath, priorityOrdinal: 1);
        AssetSource looseSource = AssetSource.CreateFolder(looseDir, priorityOrdinal: 2);

        (WorkspaceState imported, _) = await workspaceService.InitializeAsync(
            new[] { baseSource, overrideSource, looseSource });

        AssetIdentity dtlWallMdlIdentity = IdentityOf(dtlWallMdl);
        AssetIdentity dtlWallTgaIdentity = IdentityOf(dtlWallTga);
        AssetIdentity accentedIdentity = IdentityOf(accented);
        AssetIdentity appearanceIdentity = IdentityOf(Pick(corpus, "appearance", "2da"));
        AssetIdentity duplicateIdentity = IdentityOf(duplicateEntry);

        // 7 base + 2 override-only + 1 folder-only.
        imported.CuratedAssets.Should().HaveCount(10, "seven base identities, two override-only, one folder-only");

        // Priority winners: ordinal 0 outranks ordinal 1 for every shared identity.
        CuratedAsset identicalDuplicate = Asset(imported, dtlWallMdlIdentity);
        identicalDuplicate.ResolvedOccurrence!.SourceId.Should().Be(baseSource.Id);
        identicalDuplicate.HasCrossSourceCollision.Should().BeTrue();

        CuratedAsset conflicted = Asset(imported, dtlWallTgaIdentity);
        conflicted.ResolvedOccurrence!.SourceId.Should().Be(baseSource.Id);
        conflicted.HasCrossSourceCollision.Should().BeTrue("the identity exists in two sources with differing payloads");
        conflicted.AllOccurrences.Should().HaveCount(2);

        // The differing pair really does differ on disk, read through the production dispatcher.
        AssetOccurrence conflictBaseOccurrence = conflicted.AllOccurrences.Single(o => o.SourceId == baseSource.Id);
        AssetOccurrence conflictOverrideOccurrence = conflicted.AllOccurrences.Single(o => o.SourceId == overrideSource.Id);
        byte[] conflictBaseBytes = await ReadOccurrenceAsync(dispatcher, Source(imported, baseSource.Id), conflictBaseOccurrence);
        byte[] conflictOverrideBytes = await ReadOccurrenceAsync(dispatcher, Source(imported, overrideSource.Id), conflictOverrideOccurrence);
        conflictBaseBytes.Should().Equal(dtlWallTga.Payload);
        conflictOverrideBytes.Should().Equal(conflictingTgaPayload);
        conflictBaseBytes.Should().NotEqual(conflictOverrideBytes);

        // The byte-identical duplicate really is byte-identical across the two sources.
        CuratedAsset identical = Asset(imported, dtlWallMdlIdentity);
        byte[] identicalBaseBytes = await ReadOccurrenceAsync(
            dispatcher, Source(imported, baseSource.Id), identical.AllOccurrences.Single(o => o.SourceId == baseSource.Id));
        byte[] identicalOverrideBytes = await ReadOccurrenceAsync(
            dispatcher, Source(imported, overrideSource.Id), identical.AllOccurrences.Single(o => o.SourceId == overrideSource.Id));
        identicalBaseBytes.Should().Equal(identicalOverrideBytes);

        // Same-source identical duplicate resolves to the lowest deterministic locator: ordinal
        // comparison puts "dup_tex.tga" before "nested dir/dup_tex.tga".
        CuratedAsset folderDuplicate = Asset(imported, duplicateIdentity);
        folderDuplicate.HasSameSourceDuplicate.Should().BeTrue();
        folderDuplicate.Status.Should().Be(ResolutionStatus.Resolved);
        folderDuplicate.AllOccurrences.Should().HaveCount(2);
        folderDuplicate.ResolvedOccurrence!.Locator.Should().Be(new FolderFileLocator(nestedFileName));
        folderDuplicate.AllOccurrences
            .Select(o => ((FolderFileLocator)o.Locator).NormalizedRelativePath)
            .Should().Contain($"{NestedFolderWithSpace}/{nestedFileName}", "one indexed path must contain a space");

        // ==============================================================================
        // Step 2 — resolve and pin the losing occurrence, then deselect one identity.
        // ==============================================================================
        byte[] losingBytes = await ReadOccurrenceAsync(
            dispatcher, Source(imported, overrideSource.Id), conflictOverrideOccurrence);
        byte[] losingHash = SHA256.HashData(losingBytes);

        WinnerPin pin = new(dtlWallTgaIdentity, overrideSource.Id, conflictOverrideOccurrence.Locator, losingHash);
        WorkspaceState pinned = await workspaceService.PinAsync(pin);

        CuratedAsset pinnedAsset = Asset(pinned, dtlWallTgaIdentity);
        pinnedAsset.Status.Should().Be(ResolutionStatus.Resolved);
        pinnedAsset.ResolvedOccurrence!.SourceId.Should().Be(overrideSource.Id, "the pin must beat source priority");
        pinnedAsset.Pin!.Locator.Should().Be(conflictOverrideOccurrence.Locator);

        SelectionState deselected = pinned.SelectionState.SetOverride(appearanceIdentity, false);
        WorkspaceState selected = await workspaceService.UpdateSelectionAsync(deselected);
        Asset(selected, appearanceIdentity).IsSelected.Should().BeFalse();
        selected.CuratedAssets.Count(a => a.IsSelected).Should().Be(9);

        // ==============================================================================
        // Step 3 — save, reopen, and prove the round trip is byte-stable.
        // ==============================================================================
        string projectPath = Path.Combine(_tempRoot, "acceptance.srnccproj");
        await projectStore.SaveAsync(selected, projectPath);

        WorkspaceState loaded = await projectStore.LoadAsync(projectPath);
        WorkspaceState reopened = await workspaceService.LoadProjectStateAsync(loaded);

        reopened.Sources.Select(s => s.FullPath).Should().Equal(
            Path.GetFullPath(baseHakPath),
            Path.GetFullPath(overrideHakPath),
            Path.GetFullPath(looseDir));
        reopened.Sources.Select(s => s.PriorityOrdinal).Should().Equal(0, 1, 2);

        CuratedAsset reopenedPinned = Asset(reopened, dtlWallTgaIdentity);
        reopenedPinned.Pin.Should().NotBeNull();
        reopenedPinned.Pin!.SourceId.Should().Be(overrideSource.Id);
        reopenedPinned.Pin!.Locator.Should().Be(conflictOverrideOccurrence.Locator);
        reopenedPinned.Pin!.PinHash.Should().Equal(losingHash);
        reopenedPinned.ResolvedOccurrence!.SourceId.Should().Be(overrideSource.Id);

        reopened.SelectionState.Overrides.Should().ContainKey(appearanceIdentity);
        Asset(reopened, appearanceIdentity).IsSelected.Should().BeFalse();
        reopened.CuratedAssets.Count(a => a.IsSelected).Should().Be(9);

        // Round-trip stability: re-serialising what was just deserialised must reproduce the file
        // byte for byte. The second path is in the same directory so that the store's
        // relative-path projection is comparing like with like.
        string roundTripPath = Path.Combine(_tempRoot, "acceptance.roundtrip.srnccproj");
        await projectStore.SaveAsAsync(reopened, roundTripPath);
        byte[] firstSaveBytes = await File.ReadAllBytesAsync(projectPath);
        byte[] secondSaveBytes = await File.ReadAllBytesAsync(roundTripPath);
        secondSaveBytes.Should().Equal(firstSaveBytes, "a save/load/save round trip must be byte-stable");

        // ==============================================================================
        // Step 4 — rescan after the pinned payload moves to a different entry index.
        // ==============================================================================
        HakEntryLocator pinnedLocatorBefore = (HakEntryLocator)reopenedPinned.Pin!.Locator;

        // Adding a tga that sorts before "dtl_wall" shifts the pinned entry one slot along, because
        // HakWriter orders by (type, canonical resref). The payload itself is untouched.
        List<RealHakFixtureFactory.FixtureEntry> movedOverrideEntries = new(overrideEntries)
        {
            new RealHakFixtureFactory.FixtureEntry(
                Cp1252.GetBytes("aaa_shift"),
                RealHakFixtureFactory.TypeOf("tga"),
                DeterministicPayload(77, 0x00AC_0004UL))
        };
        RealHakFixtureFactory.WriteHak(overrideHakPath, movedOverrideEntries);

        (WorkspaceState rescanned, ChangedInputReport rescanReport) =
            await workspaceService.RescanAsync(new[] { overrideSource.Id });

        rescanReport.FingerprintChanges.Should().Contain(overrideSource.Id);
        rescanReport.PinReattachments.Should().Contain(dtlWallTgaIdentity);

        CuratedAsset reattached = Asset(rescanned, dtlWallTgaIdentity);
        reattached.Status.Should().Be(ResolutionStatus.Resolved);
        reattached.HasInvalidPin.Should().BeFalse();
        HakEntryLocator pinnedLocatorAfter = (HakEntryLocator)reattached.Pin!.Locator;
        pinnedLocatorAfter.EntryIndex.Should().Be(
            pinnedLocatorBefore.EntryIndex + 1, "the pinned payload moved one entry along");
        reattached.ResolvedOccurrence!.Locator.Should().Be(pinnedLocatorAfter);

        // The rescan must not have disturbed anything else the project restored.
        rescanned.CuratedAssets.Should().HaveCount(11, "the shifted fixture adds one new identity");
        Asset(rescanned, appearanceIdentity).IsSelected.Should().BeFalse();

        // ==============================================================================
        // Step 5 — build.
        // ==============================================================================
        string outputDir = Path.Combine(_tempRoot, "output");
        string builtHakPath = Path.Combine(outputDir, BuiltHakFileName);
        string builtManifestPath = Path.Combine(outputDir, BuiltManifestFileName);

        PublicationResult buildResult = await orchestrator.ExecuteBuildAsync(rescanned, builtHakPath);

        buildResult.IsSuccess.Should().BeTrue(buildResult.ErrorMessage ?? "build failed");
        buildResult.PublishedHakPath.Should().Be(Path.GetFullPath(builtHakPath));
        buildResult.PublishedManifestPath.Should().Be(Path.GetFullPath(builtManifestPath));
        File.Exists(builtHakPath).Should().BeTrue();
        File.Exists(builtManifestPath).Should().BeTrue();

        List<CuratedAsset> selectedAssets = rescanned.CuratedAssets.Where(a => a.IsSelected).ToList();
        selectedAssets.Should().HaveCount(10, "one of the eleven identities is deselected");

        // ==============================================================================
        // Step 6 — independent verification with a fresh reader and a fresh dispatcher.
        // ==============================================================================
        SourceReaderDispatcher freshDispatcher = new(typeRegistry: new ResourceTypeRegistry());
        HakReader outputReader;
        using (FileStream outputStream = File.OpenRead(builtHakPath))
        {
            outputReader = new HakReader(outputStream);
        }

        outputReader.Entries.Should().HaveCount(selectedAssets.Count);

        HakFormatKey deselectedKey = KeyOf(appearanceIdentity);
        outputReader.Entries.Should().NotContain(
            e => e.Key.Equals(deselectedKey), "the deselected identity must not be packaged");

        Dictionary<Guid, AssetSource> sourceById = rescanned.Sources.ToDictionary(s => s.Id);
        Dictionary<HakFormatKey, byte[]> packagedPayloads = new();

        foreach (HakEntry entry in outputReader.Entries)
        {
            await using Stream payloadStream = HakReader.OpenPayloadStream(entry, builtHakPath);
            packagedPayloads[entry.Key] = await ReadAllAsync(payloadStream);
        }

        foreach (CuratedAsset asset in selectedAssets)
        {
            HakFormatKey key = KeyOf(asset.Identity);
            packagedPayloads.Should().ContainKey(key, "every selected identity must be packaged");

            byte[] originalBytes = await ReadOccurrenceAsync(
                freshDispatcher, sourceById[asset.ResolvedOccurrence!.SourceId], asset.ResolvedOccurrence!);
            packagedPayloads[key].Should().Equal(
                originalBytes, $"packaged payload for {asset.Identity} must equal the original source bytes");
        }

        // The CP1252 resref is carried as raw bytes; nothing re-encoded it on the way through.
        HakEntry accentedEntry = outputReader.Entries.Single(
            e => e.Key.ResrefBytes.Span.SequenceEqual(RealHakFixtureFactory.AccentedResrefBytes()));
        accentedEntry.Key.ResrefBytes.ToArray().Should().Equal(RealHakFixtureFactory.AccentedResrefBytes());
        accentedEntry.Key.ResourceType.Should().Be(accentedIdentity.ResourceType);

        // ==============================================================================
        // Step 7 — manifest.
        // ==============================================================================
        string manifestText = await File.ReadAllTextAsync(builtManifestPath);
        using JsonDocument manifestDocument = JsonDocument.Parse(manifestText);
        JsonElement manifestRoot = manifestDocument.RootElement;

        // PascalCase is the real, documented on-disk shape (ProvenanceManifestGenerator sets no
        // PropertyNamingPolicy). Asserting it here is what keeps it from drifting.
        manifestRoot.GetProperty("SchemaVersion").GetString().Should().Be(SchemaVersions.Manifest);
        manifestRoot.GetProperty("HakFileName").GetString().Should().Be(BuiltHakFileName);
        manifestRoot.GetProperty("TotalEntries").GetInt32().Should().Be(selectedAssets.Count);

        byte[] builtHakBytes = await File.ReadAllBytesAsync(builtHakPath);
        string freshHakSha = Convert.ToHexString(SHA256.HashData(builtHakBytes)).ToLowerInvariant();
        manifestRoot.GetProperty("HakSha256Hex").GetString().Should().Be(freshHakSha);

        string expectedAppVersion = ReadInformationalVersion(typeof(ProvenanceManifestGenerator).Assembly);
        manifestRoot.GetProperty("AppVersion").GetString().Should().Be(expectedAppVersion);
        manifestRoot.GetProperty("GeneratedUtc").GetString().Should().NotBeNullOrWhiteSpace();

        JsonElement resources = manifestRoot.GetProperty("Resources");
        resources.GetArrayLength().Should().Be(selectedAssets.Count);

        long totalSizeBytes = 0;
        foreach (JsonElement resource in resources.EnumerateArray())
        {
            string resref = resource.GetProperty("Resref").GetString()!;
            ushort resourceType = resource.GetProperty("ResourceType").GetUInt16();
            resource.GetProperty("ResourceTypeName").GetString().Should().NotBeNullOrWhiteSpace();
            resource.GetProperty("SourceLabel").GetString().Should().NotBeNullOrWhiteSpace();
            resource.GetProperty("OriginLocator").GetString().Should().NotBeNullOrWhiteSpace();
            long sizeBytes = resource.GetProperty("SizeBytes").GetInt64();
            string sha256Hex = resource.GetProperty("Sha256Hex").GetString()!;
            bool isPinned = resource.GetProperty("IsPinned").GetBoolean();
            totalSizeBytes += sizeBytes;

            AssetIdentity identity = new(resref, resourceType);
            HakFormatKey key = KeyOf(identity);
            packagedPayloads.Should().ContainKey(key);
            byte[] payload = packagedPayloads[key];
            sizeBytes.Should().Be(payload.LongLength);
            sha256Hex.Should().Be(
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                $"the manifest hash for {resref} must equal a freshly computed payload hash");

            isPinned.Should().Be(
                Asset(rescanned, identity).Pin is not null, $"IsPinned must reflect the pin state of {resref}");
        }

        manifestRoot.GetProperty("TotalSizeBytes").GetInt64().Should().Be(totalSizeBytes);
        resources.EnumerateArray()
            .Any(r => r.GetProperty("IsPinned").GetBoolean())
            .Should().BeTrue("the pinned identity is selected and must be recorded as pinned");

        // No absolute filesystem path may leak into a manifest that ships alongside the HAK.
        Regex.IsMatch(manifestText, @"[A-Za-z]:[\\/]").Should().BeFalse(
            "the manifest must carry no drive-qualified path");
        manifestText.Should().NotContain(_tempRoot.Replace('\\', '/'));
        manifestText.Should().NotContain(_tempRoot.Replace("\\", "\\\\"));

        // ==============================================================================
        // Step 8 — repeat-build determinism (different directory, same file name).
        // ==============================================================================
        string secondOutputDir = Path.Combine(_tempRoot, "output-2");
        string secondHakPath = Path.Combine(secondOutputDir, BuiltHakFileName);
        string secondManifestPath = Path.Combine(secondOutputDir, BuiltManifestFileName);

        PublicationResult secondBuild = await orchestrator.ExecuteBuildAsync(rescanned, secondHakPath);
        secondBuild.IsSuccess.Should().BeTrue(secondBuild.ErrorMessage ?? "repeat build failed");

        byte[] secondHakBytes = await File.ReadAllBytesAsync(secondHakPath);
        secondHakBytes.Should().Equal(builtHakBytes, "two builds of one workspace must be byte-identical");

        string secondManifestText = await File.ReadAllTextAsync(secondManifestPath);
        NormalizeGeneratedUtc(secondManifestText).Should().Be(
            NormalizeGeneratedUtc(manifestText),
            "the two manifests must differ only in GeneratedUtc");

        // ==============================================================================
        // Step 9 — publication integrity and journal recovery.
        // ==============================================================================
        AssertNoPublicationResidue(outputDir);
        AssertNoPublicationResidue(secondOutputDir);

        byte[] previousValidHak = await File.ReadAllBytesAsync(baseHakPath);
        previousValidHak.Should().NotEqual(builtHakBytes);

        string recoveredJournalPath = WritePendingJournal(
            outputDir, builtHakPath, builtManifestPath, previousValidHak, manifestText);

        bool recovered = await publisher.RecoverPendingJournalAsync(outputDir);

        recovered.Should().BeTrue();
        (await File.ReadAllBytesAsync(builtHakPath)).Should().Equal(
            previousValidHak, "recovery must restore the previous valid HAK byte for byte");
        File.Exists(recoveredJournalPath).Should().BeFalse("a completed recovery removes its journal");
        AssertNoPublicationResidue(outputDir);

        // ==============================================================================
        // Step 10 — startup preflight over real residue.
        // ==============================================================================
        string preflightJournalPath = WritePendingJournal(
            outputDir, builtHakPath, builtManifestPath, builtHakBytes, manifestText);
        File.Exists(preflightJournalPath).Should().BeTrue();

        PublicationJournalStartupCheck journalCheck = new(
            publisher,
            projectStore,
            startupArgs: null,
            recentProjectPaths: null,
            currentDirectory: () => outputDir,
            logger: _logger);

        StartupReport startupReport = await StartupPreflight.RunAsync(
            new IStartupCheck?[] { journalCheck }, _logger);

        startupReport.Results.Should().HaveCount(1);
        StartupCheckResult journalResult = startupReport.Results[0];
        journalResult.CheckId.Should().Be(PublicationJournalStartupCheck.Id);
        journalResult.Severity.Should().Be(StartupCheckSeverity.Degraded);
        journalResult.Details.Should().Contain($"recovered: {outputDir}");
        startupReport.HasBlocking.Should().BeFalse();

        File.Exists(preflightJournalPath).Should().BeFalse();
        (await File.ReadAllBytesAsync(builtHakPath)).Should().Equal(
            builtHakBytes, "the startup check restored the journalled HAK");

        _logger.Flush();
        _logger.FailedSinkCount.Should().Be(0);
        _logSink.FailureCount.Should().Be(0);

        IReadOnlyList<LoggedLine> logLines = ReadLogLines(_appPaths.LogDirectory);
        logLines.Should().Contain(
            l => l.Category == PublicationJournalStartupCheck.LogCategory
                 && l.Message.Contains("Recovered an interrupted publication journal", StringComparison.Ordinal),
            "the recovery must be evidenced in the temp AppPaths log directory");
        logLines.Should().Contain(
            l => l.Category == StartupPreflight.LogCategory
                 && l.Message.Contains("interrupted publication journal", StringComparison.Ordinal),
            "the preflight runner must log the check's own summary");
    }

    // ------------------------------------------------------------------------------------
    // Helpers. None of these substitute for a production service; they only read and compare.
    // ------------------------------------------------------------------------------------

    private sealed record LoggedLine(string Category, string Message);

    private static RealHakFixtureFactory.FixtureEntry Pick(
        IReadOnlyList<RealHakFixtureFactory.FixtureEntry> corpus,
        string resref,
        string extension)
    {
        ushort type = RealHakFixtureFactory.TypeOf(extension);
        return corpus.Single(
            e => e.ResourceType == type
                 && string.Equals(RealHakFixtureFactory.DecodeResref(e.ResrefBytes), resref, StringComparison.Ordinal));
    }

    private static AssetIdentity IdentityOf(RealHakFixtureFactory.FixtureEntry entry) =>
        new(entry.ResrefBytes.AsSpan(), entry.ResourceType);

    private static HakFormatKey KeyOf(AssetIdentity identity) =>
        new(identity.OriginalResrefBytes.Span, identity.ResourceType);

    private static CuratedAsset Asset(WorkspaceState state, AssetIdentity identity) =>
        state.CuratedAssets.Single(a => a.Identity.Equals(identity));

    private static AssetSource Source(WorkspaceState state, Guid sourceId) =>
        state.Sources.Single(s => s.Id == sourceId);

    private static async Task<byte[]> ReadOccurrenceAsync(
        SourceReaderDispatcher dispatcher,
        AssetSource source,
        AssetOccurrence occurrence)
    {
        await using Stream stream = await dispatcher.OpenOccurrenceAsync(source, occurrence);
        return await ReadAllAsync(stream);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Fixed-seed LCG, matching the factory's own approach: a determinism assertion must not depend
    /// on a runtime's PRNG implementation.
    /// </summary>
    private static byte[] DeterministicPayload(int length, ulong seed)
    {
        byte[] buffer = new byte[length];
        ulong state = seed;
        for (int i = 0; i < length; i++)
        {
            state = unchecked((state * 6364136223846793005UL) + 1442695040888963407UL);
            buffer[i] = (byte)(state >> 33);
        }

        return buffer;
    }

    private static string ReadInformationalVersion(Assembly assembly)
    {
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? string.Empty;
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            informational = informational[..plus];
        }

        return string.IsNullOrWhiteSpace(informational) ? "0.0.0-unknown" : informational;
    }

    private static string NormalizeGeneratedUtc(string manifestText)
    {
        JsonObject root = JsonNode.Parse(manifestText)!.AsObject();
        root["GeneratedUtc"] = "<normalized>";
        return root.ToJsonString();
    }

    private static void AssertNoPublicationResidue(string directory)
    {
        Directory.GetFiles(directory, "*.bak").Should().BeEmpty("a committed publication leaves no backup");
        Directory.GetFiles(directory, "*publication-journal.json*").Should().BeEmpty("a committed publication leaves no journal");
        Directory.GetFiles(directory, "*.tmp.hak").Should().BeEmpty("a committed publication leaves no temp HAK");
        Directory.GetFiles(directory, "*.tmp.manifest.json").Should().BeEmpty("a committed publication leaves no temp manifest");
    }

    /// <summary>
    /// Hand-crafts the on-disk state an interrupted publish leaves behind: a journal stuck at
    /// <see cref="PublicationState.HakReplaced"/> plus the backup pair it names.
    /// </summary>
    /// <returns>The journal path, which a successful recovery must delete.</returns>
    private static string WritePendingJournal(
        string directory,
        string destinationHakPath,
        string destinationManifestPath,
        byte[] previousHakBytes,
        string previousManifestText)
    {
        string transactionId = Guid.NewGuid().ToString("N");
        string hakBackupPath = destinationHakPath + $".{transactionId}.bak";
        string manifestBackupPath = destinationManifestPath + $".{transactionId}.bak";

        File.WriteAllBytes(hakBackupPath, previousHakBytes);
        File.WriteAllText(manifestBackupPath, previousManifestText);

        PublicationJournal journal = new()
        {
            DestinationHakPath = Path.GetFullPath(destinationHakPath),
            DestinationManifestPath = Path.GetFullPath(destinationManifestPath),
            TempHakPath = Path.Combine(directory, $"{transactionId}.tmp.hak"),
            TempManifestPath = Path.Combine(directory, $"{transactionId}.tmp.manifest.json"),
            HakBackupPath = hakBackupPath,
            ManifestBackupPath = manifestBackupPath,
            HakExistedBefore = true,
            ManifestExistedBefore = true,
            State = PublicationState.HakReplaced,
            CreatedUtc = DateTime.UtcNow,
            LastUpdatedUtc = DateTime.UtcNow
        };

        string journalPath = Path.Combine(
            directory,
            $"{Path.GetFileName(destinationHakPath)}.{transactionId}.publication-journal.json");
        File.WriteAllText(journalPath, JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
        return journalPath;
    }

    private static IReadOnlyList<LoggedLine> ReadLogLines(string logDirectory)
    {
        List<LoggedLine> lines = new();
        if (!Directory.Exists(logDirectory))
        {
            return lines;
        }

        foreach (string path in Directory.GetFiles(logDirectory, "*.log"))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (node is not JsonObject obj)
                {
                    continue;
                }

                lines.Add(new LoggedLine(
                    obj["category"]?.GetValue<string>() ?? string.Empty,
                    obj["message"]?.GetValue<string>() ?? string.Empty));
            }
        }

        return lines;
    }
}
