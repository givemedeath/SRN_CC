using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Settings;
using SRN.CC.Infrastructure.Persistence;

namespace SRN.CC.Tests.Persistence;

[TestFixture]
public class SettingsStoreTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_SettingsStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Test]
    public async Task MissingFile_ReturnsDefaultSettings()
    {
        string settingsPath = Path.Combine(_tempDir, "nonexistent.json");
        SettingsStore store = new SettingsStore();

        SettingsLoadResult result = await store.LoadAsync(settingsPath);

        result.Status.Should().Be(SettingsLoadStatus.Loaded);
        result.QuarantinedPath.Should().BeNull();
        result.Settings.LastProjectPath.Should().BeNull();
        result.Settings.RecentProjectPaths.Should().BeEmpty();
        result.Settings.NwnInstallOverride.Should().BeNull();
        result.Settings.IsReadOnly.Should().BeFalse();
    }

    [Test]
    public async Task RoundTrip_PersistsAndLoadsSettings()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        SettingsStore store = new SettingsStore();

        ApplicationSettings settings = new ApplicationSettings(
            lastProjectPath: "c:/proj/my.srnccproj",
            recentProjectPaths: new[] { "c:/proj/my.srnccproj", "c:/proj/other.srnccproj" },
            nwnInstallOverride: "c:/nwn");

        await store.SaveAsync(settings, settingsPath);

        File.Exists(settingsPath).Should().BeTrue();

        SettingsLoadResult result = await store.LoadAsync(settingsPath);

        result.Status.Should().Be(SettingsLoadStatus.Loaded);
        result.QuarantinedPath.Should().BeNull();

        ApplicationSettings loaded = result.Settings;
        loaded.LastProjectPath.Should().Be("c:/proj/my.srnccproj");
        loaded.RecentProjectPaths.Should().Equal("c:/proj/my.srnccproj", "c:/proj/other.srnccproj");
        loaded.NwnInstallOverride.Should().Be("c:/nwn");
        loaded.IsReadOnly.Should().BeFalse();
    }

    [Test]
    public async Task CorruptFile_QuarantinesFile_ReturnsDefaults()
    {
        string settingsPath = Path.Combine(_tempDir, "corrupt_settings.json");
        await File.WriteAllTextAsync(settingsPath, "{ malformed json }");

        SettingsStore store = new SettingsStore();

        SettingsLoadResult result = await store.LoadAsync(settingsPath);

        result.Status.Should().Be(SettingsLoadStatus.RebuiltAfterQuarantine);
        result.Settings.LastProjectPath.Should().BeNull("Corrupt file should return default settings");

        File.Exists(settingsPath).Should().BeFalse("Corrupt file should have been renamed/quarantined");

        string[] corruptFiles = Directory.GetFiles(_tempDir, "corrupt_settings.json.corrupt.*");
        corruptFiles.Should().HaveCount(1, "Corrupt file should be renamed with a timestamp suffix");

        result.QuarantinedPath.Should().Be(corruptFiles[0], "the quarantine path is reported, not swallowed");
    }
}
