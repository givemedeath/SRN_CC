using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Logging;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Infrastructure.Logging;
using SRN.CC.Infrastructure.Startup;

namespace SRN.CC.Tests.Startup;

/// <summary>
/// An <see cref="ISqliteCacheService"/> whose only interesting members are the two the startup
/// check reads. Every data path throws, because the check must never touch one.
/// </summary>
internal sealed class FakeSqliteCacheService : ISqliteCacheService
{
    public bool IsAvailable { get; init; } = true;

    public string? LastQuarantineReason { get; init; }

    public Task<SourceIndexSnapshot?> TryGetSnapshotAsync(AssetSource source, SourceFingerprint fingerprint, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The startup check must not read the cache.");

    public Task SaveSnapshotAsync(SourceIndexSnapshot snapshot, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The startup check must not write the cache.");

    public Task<PreviewCachePayload?> TryGetPreviewAsync(SourceFingerprint sourceFingerprint, AssetOccurrence occurrence, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The startup check must not read the cache.");

    public Task SavePreviewAsync(SourceFingerprint sourceFingerprint, AssetOccurrence occurrence, int width, int height, byte[] pngBytes, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The startup check must not write the cache.");

    public Task ClearCacheAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The startup check must not clear the cache.");
}

[TestFixture]
public class CacheStartupCheckTests
{
    private string _tempDir = null!;
    private AppPaths _paths = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_CacheStartupCheckTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new AppPaths(_tempDir);
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
    public async Task CleanCache_IsOk()
    {
        CacheStartupCheck check = new(new FakeSqliteCacheService(), _paths);

        StartupCheckResult result = await check.RunAsync();

        check.CheckId.Should().Be(CacheStartupCheck.Id);
        result.Severity.Should().Be(StartupCheckSeverity.Ok);
        result.Code.Should().BeNull();
        result.Details.Should().Contain(d => d.Contains(_paths.CacheDatabasePath, StringComparison.Ordinal));
    }

    [Test]
    public async Task SuccessfulQuarantine_IsDegradedEvenThoughTheCacheIsAvailable()
    {
        // The recovery worked, but the user's index is gone. Silently reporting Ok here is exactly
        // the observability hole this check exists to close.
        FakeSqliteCacheService cache = new()
        {
            IsAvailable = true,
            LastQuarantineReason = "database disk image is malformed"
        };
        CacheStartupCheck check = new(cache, _paths);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Degraded);
        result.Code.Should().Be(DiagnosticCode.CorruptedCacheQuarantined);
        result.Summary.Should().Contain("rebuilt empty");
        result.Details.Should().Contain(d => d.Contains("database disk image is malformed", StringComparison.Ordinal));
    }

    [Test]
    public async Task FailedQuarantine_IsDegradedWithADistinctSummary()
    {
        FakeSqliteCacheService cache = new()
        {
            IsAvailable = false,
            LastQuarantineReason = "the quarantine rename was denied"
        };
        CacheStartupCheck check = new(cache, _paths);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Degraded);
        result.Code.Should().Be(DiagnosticCode.CorruptedCacheQuarantined);
        result.Summary.Should().Contain("could not be opened or rebuilt");
        result.Details.Should().Contain(d => d.Contains("the quarantine rename was denied", StringComparison.Ordinal));
    }

    [Test]
    public async Task TheTwoDegradedPathsDoNotShareASummary()
    {
        StartupCheckResult recovered = await new CacheStartupCheck(
            new FakeSqliteCacheService { IsAvailable = true, LastQuarantineReason = "corrupt" }, _paths).RunAsync();
        StartupCheckResult dead = await new CacheStartupCheck(
            new FakeSqliteCacheService { IsAvailable = false, LastQuarantineReason = "corrupt" }, _paths).RunAsync();

        recovered.Summary.Should().NotBe(dead.Summary);
    }

    [Test]
    public async Task UnavailableWithoutAReason_StillReportsSomething()
    {
        CacheStartupCheck check = new(new FakeSqliteCacheService { IsAvailable = false }, _paths);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Degraded);
        result.Details.Should().Contain(d => d.Contains("(none reported)", StringComparison.Ordinal));
    }

    [Test]
    public async Task Quarantine_IsLoggedWithTheDiagnosticEventCode()
    {
        RecordingAppLogger logger = new();
        CacheStartupCheck check = new(
            new FakeSqliteCacheService { IsAvailable = true, LastQuarantineReason = "corrupt" },
            _paths,
            logger);

        await check.RunAsync();

        RecordingAppLogger.Entry entry = logger.Entries.Single();
        entry.Level.Should().Be(LogLevel.Warn);
        entry.Category.Should().Be(CacheStartupCheck.LogCategory);
        entry.Data[AppLogger.EventCodeKey].Should().Be(nameof(DiagnosticCode.CorruptedCacheQuarantined));
    }

    [Test]
    public async Task RealCacheServiceAgainstATempRoot_IsOkAndHeadless()
    {
        using SqliteCacheService cache = new(_paths.CacheDatabasePath);
        CacheStartupCheck check = new(cache, _paths);

        StartupCheckResult result = await check.RunAsync();

        result.Severity.Should().Be(StartupCheckSeverity.Ok);
    }

    [Test]
    public void NullArguments_Throw()
    {
        Action nullCache = () => _ = new CacheStartupCheck(null!, _paths);
        Action nullPaths = () => _ = new CacheStartupCheck(new FakeSqliteCacheService(), null!);

        nullCache.Should().Throw<ArgumentNullException>();
        nullPaths.Should().Throw<ArgumentNullException>();
    }
}
