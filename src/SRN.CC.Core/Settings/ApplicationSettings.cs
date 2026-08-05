namespace SRN.CC.Core.Settings;

public sealed record ApplicationSettings
{
    public string? LastProjectPath { get; init; }
    public IReadOnlyList<string> RecentProjectPaths { get; init; }
    public string? NwnInstallOverride { get; init; }

    /// <summary>
    /// <see langword="true"/> when these settings stand in for a file this build must not write —
    /// today, one declaring a newer schema version. The flag lives on the settings object rather
    /// than only on <see cref="SettingsLoadResult"/> so the write guard survives the settings being
    /// passed around detached from the load result that produced them.
    /// </summary>
    public bool IsReadOnly { get; init; }

    public ApplicationSettings(
        string? lastProjectPath = null,
        IReadOnlyList<string>? recentProjectPaths = null,
        string? nwnInstallOverride = null,
        bool isReadOnly = false)
    {
        LastProjectPath = lastProjectPath;
        RecentProjectPaths = recentProjectPaths ?? Array.Empty<string>();
        NwnInstallOverride = nwnInstallOverride;
        IsReadOnly = isReadOnly;
    }
}
