using SRN.CC.Core.Identity;

namespace SRN.CC.Core.Services;

public interface IBaseGameResourceCatalog
{
    bool Contains(AssetIdentity identity);
    Task<Stream> OpenAsync(AssetIdentity identity, CancellationToken cancellationToken = default);
}
