namespace SRN.CC.Core.Settings;

public sealed record ApplicationSettings
{
    public string? LastProjectPath { get; init; }
    public IReadOnlyList<string> RecentProjectPaths { get; init; }
    public string? NwnInstallOverride { get; init; }

    public ApplicationSettings(
        string? lastProjectPath = null,
        IReadOnlyList<string>? recentProjectPaths = null,
        string? nwnInstallOverride = null)
    {
        LastProjectPath = lastProjectPath;
        RecentProjectPaths = recentProjectPaths ?? Array.Empty<string>();
        NwnInstallOverride = nwnInstallOverride;
    }
}
