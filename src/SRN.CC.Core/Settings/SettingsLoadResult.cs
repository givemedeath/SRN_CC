namespace SRN.CC.Core.Settings;

/// <summary>
/// How a settings document was obtained, so a caller can tell "these are your settings" from
/// "these are defaults because your real file is unreadable" — a distinction the previous
/// <c>Task&lt;ApplicationSettings&gt;</c> return type erased entirely.
/// </summary>
public enum SettingsLoadStatus
{
    /// <summary>
    /// The file was read at the current schema version, or no file existed yet and defaults were
    /// returned. Writes are permitted.
    /// </summary>
    Loaded,

    /// <summary>
    /// The file declares a schema version newer than this build understands. Defaults are returned
    /// with <see cref="ApplicationSettings.IsReadOnly"/> set and the file is left untouched, so a
    /// future build's settings survive being opened by this one.
    /// </summary>
    ReadOnlyNewer,

    /// <summary>
    /// The file was unparseable or declared a version below the first released schema. It was moved
    /// aside to <see cref="SettingsLoadResult.QuarantinedPath"/> and defaults were rebuilt.
    /// </summary>
    RebuiltAfterQuarantine
}

/// <summary>
/// The outcome of an <see cref="Services.ISettingsStore.LoadAsync"/> call.
/// </summary>
/// <param name="Settings">
/// The settings to use. Always non-null: defaults stand in whenever the file could not contribute.
/// </param>
/// <param name="Status">Why <paramref name="Settings"/> has the values it has.</param>
/// <param name="QuarantinedPath">
/// Where a corrupt file was moved to, or <see langword="null"/> when nothing was quarantined.
/// Reported rather than swallowed so a startup check can tell the operator which file to inspect.
/// </param>
public sealed record SettingsLoadResult(
    ApplicationSettings Settings,
    SettingsLoadStatus Status,
    string? QuarantinedPath);
