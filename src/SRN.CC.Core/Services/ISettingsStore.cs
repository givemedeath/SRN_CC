using SRN.CC.Core.Settings;

namespace SRN.CC.Core.Services;

public interface ISettingsStore
{
    Task<ApplicationSettings> LoadAsync(string? overrideFilePath = null, CancellationToken cancellationToken = default);
    Task SaveAsync(ApplicationSettings settings, string? overrideFilePath = null, CancellationToken cancellationToken = default);
}
