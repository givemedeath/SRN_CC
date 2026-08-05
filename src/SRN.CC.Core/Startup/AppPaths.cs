namespace SRN.CC.Core.Startup;

/// <summary>
/// Single source of truth for the application's per-user data layout under
/// <c>%LOCALAPPDATA%\SRN.CC</c>. Purely declarative: nothing here touches the file
/// system, so constructing an <see cref="AppPaths"/> never creates a directory.
/// </summary>
/// <remarks>
/// The default layout must stay byte-identical to the paths the shipped stores already
/// compute, or an upgrade would silently relocate existing user data:
/// <list type="bullet">
/// <item><description><c>SqliteCacheService.cs:38-41</c> — <c>LocalApplicationData</c> / <c>SRN.CC</c> / <c>cache-v1.sqlite</c>.</description></item>
/// <item><description><c>SettingsStore.cs:12-13</c> — <c>LocalApplicationData</c> / <c>SRN.CC</c> / <c>settings.json</c>.</description></item>
/// </list>
/// </remarks>
public sealed record AppPaths(string Root)
{
    /// <summary>Folder name appended to <c>%LOCALAPPDATA%</c> for the default root.</summary>
    public const string ApplicationFolderName = "SRN.CC";

    /// <summary>File name of the SQLite index/preview cache.</summary>
    public const string CacheDatabaseFileName = "cache-v1.sqlite";

    /// <summary>File name of the persisted application settings document.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>Directory name holding rotated JSON-line log files.</summary>
    public const string LogDirectoryName = "Logs";

    public string CacheDatabasePath => Path.Combine(Root, CacheDatabaseFileName);

    public string SettingsPath => Path.Combine(Root, SettingsFileName);

    public string LogDirectory => Path.Combine(Root, LogDirectoryName);

    /// <summary>
    /// The shipped layout rooted at <c>%LOCALAPPDATA%\SRN.CC</c>.
    /// </summary>
    public static AppPaths Default => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationFolderName));
}
