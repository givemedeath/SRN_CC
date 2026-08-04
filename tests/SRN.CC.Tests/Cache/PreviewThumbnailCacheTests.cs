using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Preview;

namespace SRN.CC.Tests.Cache;

/// <summary>
/// Covers S8b: wiring <c>ISqliteCacheService</c>'s preview_cache table into <see cref="PreviewEngine"/>
/// via <see cref="IPreviewThumbnailCache"/>. Proves a cache hit skips the provider, a fingerprint
/// change misses, and Model-family/<see cref="IPreviewPayload"/>-bearing results never touch the
/// cache (architecture decision A2 — IPreviewPayload is process-local and must never enter
/// preview_cache, which stores PNGs only).
/// </summary>
[TestFixture]
public class PreviewThumbnailCacheTests
{
    private static SourceFingerprint MakeFingerprint(byte seed)
    {
        byte[] digest = new byte[32];
        Array.Fill(digest, seed);
        return new SourceFingerprint(AssetSourceKind.Hak, 1, digest);
    }

    private static AssetOccurrence MakeOccurrence(string resref = "test_tex", int entryIndex = 0)
    {
        AssetIdentity identity = new(resref, 2000); // arbitrary image-ish resource type
        return new AssetOccurrence(
            identity: identity,
            sourceId: Guid.NewGuid(),
            locator: new HakEntryLocator(entryIndex),
            originalName: $"{resref}.tga",
            size: 100);
    }

    private static byte[] MakeBgra(int width, int height, byte seed)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int i = 0; i < bgra.Length; i++)
        {
            bgra[i] = (byte)(seed + i);
        }

        return bgra;
    }

    private sealed class FakeImageProvider : IPreviewProvider
    {
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _bgra;

        public FakeImageProvider(int width, int height, byte[] bgra)
        {
            _width = width;
            _height = height;
            _bgra = bgra;
        }

        public int InvocationCount { get; private set; }

        public PreviewFamily Family => PreviewFamily.Image;

        public bool CanPreview(PreviewRequest request) => true;

        public Task<PreviewResult> GeneratePreviewAsync(
            PreviewRequest request,
            Stream payloadStream,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(new PreviewResult(
                Occurrence: request.Occurrence,
                Family: PreviewFamily.Image,
                IsSuccess: true,
                MetadataText: null,
                RawPayload: _bgra,
                FormattedContent: $"{_width}:{_height}",
                ErrorMessage: null,
                Diagnostics: Array.Empty<string>()));
        }
    }

    private sealed class FakeModelPayload : IPreviewPayload
    {
        public string PayloadKind => "model-scene/v1";
        public long ApproximateByteSize => 1;
    }

    private sealed class FakeModelProvider : IPreviewProvider
    {
        public int InvocationCount { get; private set; }

        public PreviewFamily Family => PreviewFamily.Model;

        public bool CanPreview(PreviewRequest request) => true;

        public Task<PreviewResult> GeneratePreviewAsync(
            PreviewRequest request,
            Stream payloadStream,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(new PreviewResult(
                Occurrence: request.Occurrence,
                Family: PreviewFamily.Model,
                IsSuccess: true,
                MetadataText: null,
                RawPayload: null,
                FormattedContent: "model-scene",
                ErrorMessage: null,
                Diagnostics: Array.Empty<string>(),
                Payload: new FakeModelPayload()));
        }
    }

    private sealed class RecordingThumbnailCache : IPreviewThumbnailCache
    {
        private readonly Dictionary<(string Fingerprint, string Locator), CachedThumbnail> _store = new();

        public int TryGetCallCount { get; private set; }
        public int SaveCallCount { get; private set; }

        public Task<CachedThumbnail?> TryGetAsync(
            SourceFingerprint fingerprint,
            AssetOccurrence occurrence,
            CancellationToken cancellationToken = default)
        {
            TryGetCallCount++;
            return Task.FromResult(_store.TryGetValue(Key(fingerprint, occurrence), out CachedThumbnail? value) ? value : null);
        }

        public Task SaveAsync(
            SourceFingerprint fingerprint,
            AssetOccurrence occurrence,
            int width,
            int height,
            byte[] pngBytes,
            CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            _store[Key(fingerprint, occurrence)] = new CachedThumbnail(width, height, pngBytes);
            return Task.CompletedTask;
        }

        private static (string, string) Key(SourceFingerprint fingerprint, AssetOccurrence occurrence) =>
            (fingerprint.ToHexString(), occurrence.Locator.ToString() ?? string.Empty);
    }

    private sealed class NullDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(
            AssetSource source,
            AssetOccurrence occurrence,
            CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream());
    }

    [Test]
    public async Task ImagePreview_SecondIdenticalRequest_HitsCacheAndSkipsProvider()
    {
        byte[] bgra = MakeBgra(4, 3, 10);
        FakeImageProvider provider = new(4, 3, bgra);
        RecordingThumbnailCache cache = new();
        PreviewEngine engine = new(new NullDispatcher(), new IPreviewProvider[] { provider }, cache);

        AssetSource source = AssetSource.CreateHak(@"c:\test.hak") with { Fingerprint = MakeFingerprint(1) };
        AssetOccurrence occurrence = MakeOccurrence();
        PreviewRequest request = new(occurrence, source, PreviewFamily.Image);

        PreviewResult first = await engine.ExecutePreviewAsync(request, debounceMs: 0);
        PreviewResult second = await engine.ExecutePreviewAsync(request, debounceMs: 0);

        provider.InvocationCount.Should().Be(1, "the second identical request must be served from the cache, not the provider");
        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.RawPayload.Should().Equal(bgra, "the cached PNG must decode back to pixel-identical BGRA");
        second.FormattedContent.Should().Be("4:3");
        cache.SaveCallCount.Should().Be(1);
        cache.TryGetCallCount.Should().Be(2);
    }

    [Test]
    public async Task ImagePreview_FingerprintChange_MissesCacheAndReinvokesProvider()
    {
        byte[] bgra = MakeBgra(2, 2, 5);
        FakeImageProvider provider = new(2, 2, bgra);
        RecordingThumbnailCache cache = new();
        PreviewEngine engine = new(new NullDispatcher(), new IPreviewProvider[] { provider }, cache);

        AssetOccurrence occurrence = MakeOccurrence();
        AssetSource sourceA = AssetSource.CreateHak(@"c:\test.hak") with { Fingerprint = MakeFingerprint(1) };
        AssetSource sourceB = AssetSource.CreateHak(@"c:\test.hak") with { Fingerprint = MakeFingerprint(2) };

        PreviewResult first = await engine.ExecutePreviewAsync(new PreviewRequest(occurrence, sourceA, PreviewFamily.Image), debounceMs: 0);
        PreviewResult second = await engine.ExecutePreviewAsync(new PreviewRequest(occurrence, sourceB, PreviewFamily.Image), debounceMs: 0);

        provider.InvocationCount.Should().Be(2, "a different SourceFingerprint must be treated as a distinct cache key");
        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        cache.SaveCallCount.Should().Be(2);
    }

    [Test]
    public async Task ModelFamilyPayloadBearingResult_NeverTouchesThumbnailCache()
    {
        FakeModelProvider provider = new();
        RecordingThumbnailCache cache = new();
        PreviewEngine engine = new(new NullDispatcher(), new IPreviewProvider[] { provider }, cache);

        AssetSource source = AssetSource.CreateHak(@"c:\test.hak") with { Fingerprint = MakeFingerprint(9) };
        AssetOccurrence occurrence = MakeOccurrence("test_mdl");
        PreviewRequest request = new(occurrence, source, PreviewFamily.Model);

        PreviewResult result = await engine.ExecutePreviewAsync(request, debounceMs: 0);
        PreviewResult resultAgain = await engine.ExecutePreviewAsync(request, debounceMs: 0);

        result.IsSuccess.Should().BeTrue();
        result.Payload.Should().NotBeNull("this is the IPreviewPayload-bearing Model-family case the cache must never see");
        provider.InvocationCount.Should().Be(2, "with no cache short-circuit possible, both requests must reach the provider");
        cache.TryGetCallCount.Should().Be(0, "IPreviewPayload results must never be read from the PNG-only thumbnail cache");
        cache.SaveCallCount.Should().Be(0, "IPreviewPayload results must never be written to the PNG-only thumbnail cache");
        resultAgain.Payload.Should().NotBeNull();
    }

    [Test]
    public async Task NoThumbnailCacheConfigured_BehavesExactlyAsBefore()
    {
        byte[] bgra = MakeBgra(2, 2, 3);
        FakeImageProvider provider = new(2, 2, bgra);
        PreviewEngine engine = new(new NullDispatcher(), new IPreviewProvider[] { provider }); // no cache passed

        AssetSource source = AssetSource.CreateHak(@"c:\test.hak") with { Fingerprint = MakeFingerprint(1) };
        PreviewRequest request = new(MakeOccurrence(), source, PreviewFamily.Image);

        await engine.ExecutePreviewAsync(request, debounceMs: 0);
        await engine.ExecutePreviewAsync(request, debounceMs: 0);

        provider.InvocationCount.Should().Be(2, "without a thumbnail cache, every request must still reach the provider");
    }

    [Test]
    public void SqlitePreviewThumbnailCache_SaveThenTryGet_RoundTripsThroughRealSqliteCacheService()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"SRNCC_ThumbCacheTests_{Guid.NewGuid():N}.sqlite");
        try
        {
            using SqliteCacheService sqliteService = new(dbPath);
            IPreviewThumbnailCache adapter = new SqlitePreviewThumbnailCache(sqliteService);

            SourceFingerprint fingerprint = MakeFingerprint(7);
            AssetOccurrence occurrence = MakeOccurrence("round_trip");
            byte[] pngBytes = { 1, 2, 3, 4, 5, 6, 7, 8 };

            adapter.SaveAsync(fingerprint, occurrence, width: 16, height: 9, pngBytes).GetAwaiter().GetResult();
            CachedThumbnail? roundTripped = adapter.TryGetAsync(fingerprint, occurrence).GetAwaiter().GetResult();

            roundTripped.Should().NotBeNull();
            roundTripped!.Width.Should().Be(16);
            roundTripped.Height.Should().Be(9);
            roundTripped.PngBytes.Should().Equal(pngBytes);

            CachedThumbnail? missAfterDifferentOccurrence = adapter
                .TryGetAsync(fingerprint, MakeOccurrence("round_trip", entryIndex: 99))
                .GetAwaiter()
                .GetResult();
            missAfterDifferentOccurrence.Should().BeNull();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
            if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
        }
    }
}
