using SRN.CC.Core.Indexing;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Services;

public interface IAssetIndexService
{
    Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default);
}
