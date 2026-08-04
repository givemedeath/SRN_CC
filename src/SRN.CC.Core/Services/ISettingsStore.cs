using SRN.CC.Core.Settings;

namespace SRN.CC.Core.Services;

public interface ISettingsStore
{
    /// <summary>
    /// Reads the settings document, or produces defaults when it is absent, newer than this build,
    /// or corrupt.
    /// </summary>
    /// <param name="overrideFilePath">
    /// Path to read instead of the shipped <c>%LOCALAPPDATA%\SRN.CC\settings.json</c> location.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The settings together with the reason they hold the values they do. The result type exists
    /// because "loaded", "left untouched because it is newer" and "rebuilt after quarantine" are
    /// three distinct operational events that a bare settings return reported identically.
    /// </returns>
    Task<SettingsLoadResult> LoadAsync(string? overrideFilePath = null, CancellationToken cancellationToken = default);

    /// <summary>Atomically writes the settings document.</summary>
    /// <param name="settings">The settings to persist.</param>
    /// <param name="overrideFilePath">Path to write instead of the shipped location.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">
    /// The target path was loaded as <see cref="SettingsLoadStatus.ReadOnlyNewer"/>, or
    /// <paramref name="settings"/> carries <see cref="ApplicationSettings.IsReadOnly"/>. Writing
    /// either would destroy a future build's settings.
    /// </exception>
    Task SaveAsync(ApplicationSettings settings, string? overrideFilePath = null, CancellationToken cancellationToken = default);
}
