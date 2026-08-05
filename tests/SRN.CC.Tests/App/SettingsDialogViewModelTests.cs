using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Services;
using SRN.CC.Core.Settings;
using SRN.CC.Infrastructure.Persistence;

namespace SRN.CC.Tests.App;

/// <summary>
/// Covers the Settings dialog milestone 7 added over the now-live <see cref="ISettingsStore"/>:
/// the install override round-trip, the auto-discovery context beside it, the recent-project list,
/// and the read-only-newer refusal.
/// </summary>
/// <remarks>
/// Every test injects the install-discovery seam rather than letting the real locator run. The real
/// probe reads the Windows registry and the Steam library file, so a test that used it would assert
/// something different on every machine.
/// </remarks>
[TestFixture]
public class SettingsDialogViewModelTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_SettingsDialogTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task SaveCommand_RoundTripsTheInstallOverride_ThroughTheStore()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new SettingsStore();
        string installRoot = Path.Combine(_tempDir, "game");

        var dialog = new SettingsDialogViewModel(
            store,
            new ApplicationSettings(),
            SettingsLoadStatus.Loaded,
            settingsPath,
            _ => NwnInstallDiscovery.None);

        dialog.ShowDialog();
        dialog.NwnInstallOverride = installRoot;
        await dialog.SaveCommand.ExecuteAsync(null);

        dialog.SavedSettings.Should().NotBeNull();
        dialog.SavedSettings!.NwnInstallOverride.Should().Be(installRoot);
        dialog.IsDialogOpen.Should().BeFalse("saving closes the dialog");

        // The exit criterion: observable on the next load, through the same store.
        SettingsLoadResult reloaded = await store.LoadAsync(settingsPath);
        reloaded.Status.Should().Be(SettingsLoadStatus.Loaded);
        reloaded.Settings.NwnInstallOverride.Should().Be(installRoot);
    }

    [Test]
    public async Task SaveCommand_NormalizesAnAllWhitespaceOverride_ToNoOverride()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new SettingsStore();

        var dialog = new SettingsDialogViewModel(
            store,
            new ApplicationSettings(nwnInstallOverride: Path.Combine(_tempDir, "old")),
            SettingsLoadStatus.Loaded,
            settingsPath,
            _ => NwnInstallDiscovery.None);

        dialog.NwnInstallOverride = "   ";
        await dialog.SaveCommand.ExecuteAsync(null);

        dialog.SavedSettings!.NwnInstallOverride.Should().BeNull();
        (await store.LoadAsync(settingsPath)).Settings.NwnInstallOverride.Should().BeNull();
    }

    [Test]
    public async Task SaveCommand_PreservesTheRestOfTheSettingsDocument()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new SettingsStore();
        string lastProject = Path.Combine(_tempDir, "demo.srnccproj");

        var dialog = new SettingsDialogViewModel(
            store,
            new ApplicationSettings(
                lastProjectPath: lastProject,
                recentProjectPaths: new[] { lastProject }),
            SettingsLoadStatus.Loaded,
            settingsPath,
            _ => NwnInstallDiscovery.None);

        dialog.NwnInstallOverride = Path.Combine(_tempDir, "game");
        await dialog.SaveCommand.ExecuteAsync(null);

        ApplicationSettings reloaded = (await store.LoadAsync(settingsPath)).Settings;
        reloaded.LastProjectPath.Should().Be(lastProject);
        reloaded.RecentProjectPaths.Should().ContainSingle().Which.Should().Be(lastProject);
    }

    [Test]
    public void ReadOnlyNewer_DisablesSaving_AndSaysWhy()
    {
        var dialog = new SettingsDialogViewModel(
            new ThrowingSettingsStore(),
            new ApplicationSettings(isReadOnly: true),
            SettingsLoadStatus.ReadOnlyNewer,
            Path.Combine(_tempDir, "settings.json"),
            _ => NwnInstallDiscovery.None);

        dialog.IsReadOnly.Should().BeTrue();
        dialog.SaveCommand.CanExecute(null).Should().BeFalse();
        dialog.ReadOnlyReason.Should().NotBeNullOrWhiteSpace();
        dialog.ReadOnlyReason.Should().Contain("newer build");
    }

    [Test]
    public async Task ReadOnlyNewer_LeavesTheFileUntouched_WhenTheStoreRefusesTheWrite()
    {
        // Belt and braces: the command is disabled, but a caller that invokes the underlying task
        // anyway must surface the refusal rather than throw out of a UI callback.
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        var store = new ThrowingSettingsStore();

        var dialog = new SettingsDialogViewModel(
            store,
            new ApplicationSettings(isReadOnly: true),
            SettingsLoadStatus.ReadOnlyNewer,
            settingsPath,
            _ => NwnInstallDiscovery.None);

        dialog.NwnInstallOverride = Path.Combine(_tempDir, "game");
        await dialog.SaveCommand.ExecuteAsync(null);

        dialog.SavedSettings.Should().BeNull();
        dialog.StatusMessage.Should().Contain("Could not save settings");
        File.Exists(settingsPath).Should().BeFalse();
    }

    [Test]
    public void AutoDiscovery_IsShownAsContextBesideTheOverride()
    {
        string discovered = Path.Combine(_tempDir, "discovered");

        var dialog = new SettingsDialogViewModel(
            new ThrowingSettingsStore(),
            new ApplicationSettings(),
            SettingsLoadStatus.Loaded,
            Path.Combine(_tempDir, "settings.json"),
            _ => new NwnInstallDiscovery(discovered, IsValid: true, new[] { discovered, Path.Combine(_tempDir, "other") }));

        dialog.AutoDiscoveredInstallRoot.Should().Be(discovered);
        dialog.AutoDiscoverySummary.Should().Contain(discovered);
        dialog.AutoDiscoveredCandidates.Should().HaveCount(2);

        dialog.UseAutoDiscoveredRootCommand.Execute(null);
        dialog.NwnInstallOverride.Should().Be(discovered);

        dialog.ClearInstallOverrideCommand.Execute(null);
        dialog.NwnInstallOverride.Should().BeNull();
    }

    [Test]
    public void AutoDiscovery_WithNoValidInstall_SaysSoWithoutClaimingARoot()
    {
        var dialog = new SettingsDialogViewModel(
            new ThrowingSettingsStore(),
            new ApplicationSettings(),
            SettingsLoadStatus.Loaded,
            Path.Combine(_tempDir, "settings.json"),
            _ => new NwnInstallDiscovery(Path.Combine(_tempDir, "bogus"), IsValid: false, Array.Empty<string>()));

        dialog.AutoDiscoveredInstallRoot.Should().BeNull("an invalid probe result is not a location");
        dialog.AutoDiscoverySummary.Should().Contain("No installation");

        dialog.UseAutoDiscoveredRootCommand.Execute(null);
        dialog.NwnInstallOverride.Should().BeNull("there is nothing to copy in");
    }

    [Test]
    public void RecentProjects_MirrorTheLoadedSettings()
    {
        string[] recents =
        {
            Path.Combine(_tempDir, "a.srnccproj"),
            Path.Combine(_tempDir, "b.srnccproj")
        };

        var dialog = new SettingsDialogViewModel(
            new ThrowingSettingsStore(),
            new ApplicationSettings(recentProjectPaths: recents),
            SettingsLoadStatus.Loaded,
            Path.Combine(_tempDir, "settings.json"),
            _ => NwnInstallDiscovery.None);

        dialog.RecentProjectPaths.Should().Equal(recents);
    }

    [Test]
    public void CancelCommand_ClosesWithoutSaving()
    {
        var dialog = new SettingsDialogViewModel(
            new ThrowingSettingsStore(),
            new ApplicationSettings(),
            SettingsLoadStatus.Loaded,
            Path.Combine(_tempDir, "settings.json"),
            _ => NwnInstallDiscovery.None);

        bool closed = false;
        dialog.CloseRequested += (_, _) => closed = true;

        dialog.ShowDialog();
        dialog.IsDialogOpen.Should().BeTrue();

        dialog.NwnInstallOverride = Path.Combine(_tempDir, "game");
        dialog.CancelCommand.Execute(null);

        closed.Should().BeTrue();
        dialog.IsDialogOpen.Should().BeFalse();
        dialog.SavedSettings.Should().BeNull();
    }

    [Test]
    public void DesignerConstructor_DoesNotThrow_AndProbesNothing()
    {
        SettingsDialogViewModel? dialog = null;
        Action act = () => dialog = new SettingsDialogViewModel();

        act.Should().NotThrow();
        dialog!.IsReadOnly.Should().BeFalse();
        dialog.AutoDiscoveredCandidates.Should().BeEmpty();
    }

    /// <summary>
    /// Stands in for the store's read-only-newer write guard, which refuses the write rather than
    /// destroying a future build's settings.
    /// </summary>
    private sealed class ThrowingSettingsStore : ISettingsStore
    {
        public Task<SettingsLoadResult> LoadAsync(string? overrideFilePath = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SettingsLoadResult(
                new ApplicationSettings(isReadOnly: true), SettingsLoadStatus.ReadOnlyNewer, null));

        public Task SaveAsync(ApplicationSettings settings, string? overrideFilePath = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Settings at this path were loaded as newer than this build.");
    }
}
