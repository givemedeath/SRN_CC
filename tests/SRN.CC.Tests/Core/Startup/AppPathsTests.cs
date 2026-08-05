using NUnit.Framework;
using FluentAssertions;
using SRN.CC.Core.Startup;
using SRN.CC.Infrastructure.Persistence;

namespace SRN.CC.Tests.Core.Startup;

[TestFixture]
public class AppPathsTests
{
    /// <summary>
    /// Reproduces the default database path computed by
    /// <c>src/SRN.CC.Infrastructure/Cache/SqliteCacheService.cs:38-41</c> without constructing the
    /// service, because that constructor creates the directory and initializes a real database in
    /// the developer's own %LOCALAPPDATA%. Keep this literal in sync with that constructor.
    /// </summary>
    private static string SqliteCacheServiceDefaultDatabasePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string directory = Path.Combine(localAppData, "SRN.CC");
        return Path.Combine(directory, "cache-v1.sqlite");
    }

    [Test]
    public void Default_CacheDatabasePath_MatchesSqliteCacheServiceDefault()
    {
        // Guards against wave 1 silently relocating an existing user's cache.
        AppPaths.Default.CacheDatabasePath.Should().Be(SqliteCacheServiceDefaultDatabasePath());
    }

    [Test]
    public void Default_SettingsPath_MatchesSettingsStoreDefaultSettingsPath()
    {
        // Guards against wave 1 silently relocating an existing user's settings.
        AppPaths.Default.SettingsPath.Should().Be(SettingsStore.DefaultSettingsPath);
    }

    [Test]
    public void Default_Root_IsLocalApplicationDataSrnCc()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SRN.CC");

        AppPaths.Default.Root.Should().Be(expected);
    }

    [Test]
    public void Default_AllPaths_ShareTheSameRootDirectory()
    {
        AppPaths paths = AppPaths.Default;

        Path.GetDirectoryName(paths.CacheDatabasePath).Should().Be(paths.Root);
        Path.GetDirectoryName(paths.SettingsPath).Should().Be(paths.Root);
        Path.GetDirectoryName(paths.LogDirectory).Should().Be(paths.Root);
    }

    [Test]
    public void Paths_ExplicitRoot_ComposeExpectedFileNames()
    {
        var paths = new AppPaths(Path.Combine("C:", "data", "srncc"));

        Path.GetFileName(paths.CacheDatabasePath).Should().Be("cache-v1.sqlite");
        Path.GetFileName(paths.SettingsPath).Should().Be("settings.json");
        Path.GetFileName(paths.LogDirectory).Should().Be("Logs");
    }

    [Test]
    public void Paths_ExplicitRoot_AreRootedAtThatRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "srncc-apppaths-test");
        var paths = new AppPaths(root);

        paths.Root.Should().Be(root);
        paths.CacheDatabasePath.Should().Be(Path.Combine(root, "cache-v1.sqlite"));
        paths.SettingsPath.Should().Be(Path.Combine(root, "settings.json"));
        paths.LogDirectory.Should().Be(Path.Combine(root, "Logs"));
    }

    [Test]
    public void Construction_DoesNotTouchTheFileSystem()
    {
        string root = Path.Combine(Path.GetTempPath(), $"srncc-apppaths-{Guid.NewGuid():N}");

        var paths = new AppPaths(root);
        _ = paths.CacheDatabasePath;
        _ = paths.SettingsPath;
        _ = paths.LogDirectory;

        Directory.Exists(root).Should().BeFalse("AppPaths is declarative and must not create directories");
        Directory.Exists(paths.LogDirectory).Should().BeFalse();
        File.Exists(paths.CacheDatabasePath).Should().BeFalse();
    }

    [Test]
    public void Default_ReturnsEqualValueOnEveryAccess()
    {
        AppPaths.Default.Should().Be(AppPaths.Default);
    }

    [Test]
    public void Records_WithSameRoot_AreEqual()
    {
        var first = new AppPaths(Path.Combine("C:", "srncc"));
        var second = new AppPaths(Path.Combine("C:", "srncc"));
        var different = new AppPaths(Path.Combine("D:", "srncc"));

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
        first.Should().NotBe(different);
    }
}
