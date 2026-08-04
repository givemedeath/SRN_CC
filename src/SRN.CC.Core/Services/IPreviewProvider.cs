using SRN.CC.Core.Preview;

namespace SRN.CC.Core.Services;

public interface IPreviewProvider
{
    PreviewFamily Family { get; }

    bool CanPreview(PreviewRequest request);

    Task<PreviewResult> GeneratePreviewAsync(
        PreviewRequest request,
        Stream payloadStream,
        CancellationToken cancellationToken = default);
}
