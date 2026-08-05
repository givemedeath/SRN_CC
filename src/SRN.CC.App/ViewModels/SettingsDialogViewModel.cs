using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRN.CC.Core.Services;
using SRN.CC.Core.Settings;
using SRN.CC.Infrastructure.Install;

namespace SRN.CC.App.ViewModels;

/// <summary>
/// The auto-discovery context the settings dialog shows beside the install override: what the
/// locator finds on this machine when no override is configured.
/// </summary>
/// <param name="InstallRoot">The discovered install root, or null when nothing valid was found.</param>
/// <param name="IsValid">Whether <paramref name="InstallRoot"/> holds a usable installation.</param>
/// <param name="Candidates">Every probed candidate, valid or not, for the operator to choose from.</param>
/// <remarks>
/// This is a projection of <see cref="NwnInstallLocation"/> rather than the type itself so the view
/// model has a seam a headless test can drive: the real locator reads the Windows registry, which
/// makes it neither deterministic nor available on every build agent.
/// </remarks>
public sealed record NwnInstallDiscovery(
    string? InstallRoot,
    bool IsValid,
    IReadOnlyList<string> Candidates)
{
    /// <summary>Nothing was probed, or probing is not possible on this platform.</summary>
    public static NwnInstallDiscovery None { get; } = new(null, false, Array.Empty<string>());
}

/// <summary>
/// The Settings dialog: the game install override, the auto-discovery result that override replaces,
/// and the recent-project list, over the single injected <see cref="ISettingsStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The store is injected and never constructed here. Its read-only-newer write guard is per-instance
/// state keyed by path, so a second store over the same path would not have observed the load that
/// set the guard and could overwrite a future build's settings. The composition root owns the only
/// instance.
/// </para>
/// <para>
/// A settings document from a newer build leaves <see cref="IsReadOnly"/> set: the save command is
/// disabled and <see cref="ReadOnlyReason"/> says why, rather than the dialog silently discarding
/// the operator's edit at write time.
/// </para>
/// </remarks>
public partial class SettingsDialogViewModel : ObservableObject
{
    private readonly ISettingsStore? _settingsStore;
    private readonly string? _settingsPath;

    private ApplicationSettings _settings;

    /// <summary>The configured install root, or empty to auto-discover.</summary>
    [ObservableProperty]
    private string? _nwnInstallOverride;

    /// <summary>
    /// What auto-discovery finds on this machine with no override applied. Shown as context so the
    /// operator can tell "I need an override" from "the default is already right".
    /// </summary>
    [ObservableProperty]
    private string? _autoDiscoveredInstallRoot;

    /// <summary>A one-line rendering of <see cref="AutoDiscoveredInstallRoot"/> for the view.</summary>
    [ObservableProperty]
    private string _autoDiscoverySummary = "Auto-discovery has not run.";

    /// <summary>
    /// True when the loaded settings document belongs to a newer build. Saving is refused.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isReadOnly;

    /// <summary>Why saving is disabled, or null when it is not.</summary>
    [ObservableProperty]
    private string? _readOnlyReason;

    /// <summary>The outcome of the last save attempt, or null before one.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>True between <see cref="ShowDialog"/> and the dialog closing.</summary>
    [ObservableProperty]
    private bool _isDialogOpen;

    /// <summary>
    /// The settings actually written, or null when the operator cancelled or the write failed. The
    /// shell adopts this value so the running session reflects the edit without a reload.
    /// </summary>
    [ObservableProperty]
    private ApplicationSettings? _savedSettings;

    /// <summary>Candidate install roots auto-discovery probed, in probe order.</summary>
    public ObservableCollection<string> AutoDiscoveredCandidates { get; } = new();

    /// <summary>Recent projects as the persisted settings hold them, newest first.</summary>
    public ObservableCollection<string> RecentProjectPaths { get; } = new();

    /// <summary>Raised when the dialog wants its host window closed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Parameterless constructor for the XAML designer.</summary>
    public SettingsDialogViewModel()
        : this(null, new ApplicationSettings(), SettingsLoadStatus.Loaded, null, _ => NwnInstallDiscovery.None)
    {
    }

    /// <summary>Creates the dialog over a loaded settings document.</summary>
    /// <param name="settingsStore">The single application settings store. Never construct one here.</param>
    /// <param name="settings">The settings as loaded.</param>
    /// <param name="status">Why <paramref name="settings"/> holds the values it holds.</param>
    /// <param name="settingsPath">The path to write, or null for the shipped location.</param>
    /// <param name="installDiscovery">
    /// Auto-discovery probe. Defaults to the real locator on Windows and to
    /// <see cref="NwnInstallDiscovery.None"/> everywhere else.
    /// </param>
    public SettingsDialogViewModel(
        ISettingsStore? settingsStore,
        ApplicationSettings settings,
        SettingsLoadStatus status = SettingsLoadStatus.Loaded,
        string? settingsPath = null,
        Func<string?, NwnInstallDiscovery>? installDiscovery = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settingsStore = settingsStore;
        _settings = settings;
        _settingsPath = settingsPath;

        NwnInstallOverride = settings.NwnInstallOverride;

        foreach (string path in settings.RecentProjectPaths)
        {
            RecentProjectPaths.Add(path);
        }

        // The read-only flag is carried on the settings object as well as the load status, precisely
        // so the guard survives being passed around detached from the load result.
        IsReadOnly = settings.IsReadOnly || status == SettingsLoadStatus.ReadOnlyNewer;
        ReadOnlyReason = IsReadOnly
            ? "The settings file was written by a newer build of this application. It is being read "
              + "but will not be overwritten, so changes made here apply to this session only."
            : null;

        ApplyDiscovery((installDiscovery ?? ProbeInstallLocation)(null));
    }

    /// <summary>Marks the dialog open. The host window binds to <see cref="IsDialogOpen"/>.</summary>
    public void ShowDialog()
    {
        SavedSettings = null;
        StatusMessage = null;
        IsDialogOpen = true;
    }

    /// <summary>Copies the discovered install root into the override field.</summary>
    [RelayCommand]
    private void UseAutoDiscoveredRoot()
    {
        if (!string.IsNullOrWhiteSpace(AutoDiscoveredInstallRoot))
        {
            NwnInstallOverride = AutoDiscoveredInstallRoot;
        }
    }

    /// <summary>Clears the override so the next session auto-discovers.</summary>
    [RelayCommand]
    private void ClearInstallOverride() => NwnInstallOverride = null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_settingsStore is null)
        {
            StatusMessage = "No settings store is available in this session.";
            return;
        }

        string? trimmed = string.IsNullOrWhiteSpace(NwnInstallOverride)
            ? null
            : NwnInstallOverride.Trim();

        ApplicationSettings updated = _settings with { NwnInstallOverride = trimmed };

        try
        {
            await _settingsStore.SaveAsync(updated, _settingsPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A refused write is an operational fact the operator has to see, not an unhandled
            // exception on a UI callback.
            StatusMessage = $"Could not save settings: {ex.Message}";
            return;
        }

        _settings = updated;
        SavedSettings = updated;
        NwnInstallOverride = trimmed;
        StatusMessage = "Settings saved.";
        Close();
    }

    private bool CanSave() => !IsReadOnly;

    [RelayCommand]
    private void Cancel()
    {
        SavedSettings = null;
        Close();
    }

    private void Close()
    {
        IsDialogOpen = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyDiscovery(NwnInstallDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        AutoDiscoveredInstallRoot = discovery.IsValid ? discovery.InstallRoot : null;

        AutoDiscoveredCandidates.Clear();
        foreach (string candidate in discovery.Candidates)
        {
            AutoDiscoveredCandidates.Add(candidate);
        }

        AutoDiscoverySummary = AutoDiscoveredInstallRoot is { } root
            ? $"Auto-discovered installation: {root}"
            : AutoDiscoveredCandidates.Count > 0
                ? $"No valid installation auto-discovered; {AutoDiscoveredCandidates.Count} candidate "
                  + "location(s) were probed."
                : "No installation was auto-discovered on this machine.";
    }

    private static NwnInstallDiscovery ProbeInstallLocation(string? explicitInstallRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NwnInstallDiscovery.None;
        }

        try
        {
            NwnInstallLocation location = new NwnInstallLocator().Locate(explicitInstallRoot);
            return new NwnInstallDiscovery(
                location.InstallRoot,
                location.IsValid,
                location.AutoDiscoveredCandidates);
        }
        catch (Exception)
        {
            // Discovery is context shown beside an editable field. A registry probe that fails must
            // not stop the operator from setting the override that makes the probe unnecessary.
            return NwnInstallDiscovery.None;
        }
    }
}
