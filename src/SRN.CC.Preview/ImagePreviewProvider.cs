using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Preview.Render;

namespace SRN.CC.Preview;

public sealed class ImagePreviewProvider : IPreviewProvider
{
    private readonly IResourceTypeRegistry _registry;

    private static readonly string[] SupportedExtensions = ["tga", "dds", "plt"];

    public ImagePreviewProvider(IResourceTypeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public PreviewFamily Family => PreviewFamily.Image;

    public bool CanPreview(PreviewRequest request) =>
        PreviewStreamHelpers.HasAnyExtension(request, _registry, SupportedExtensions);

    public async Task<PreviewResult> GeneratePreviewAsync(
        PreviewRequest request,
        Stream payloadStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloadStream);
        cancellationToken.ThrowIfCancellationRequested();

        if (!PreviewStreamHelpers.TryGetOccurrenceExtension(request, _registry, out var extension))
        {
            return Failure("Unsupported image type.", request, ["Cannot resolve image extension."]);
        }

        var read = await PreviewStreamHelpers
            .ReadStreamBoundedAsync(payloadStream, PreviewStreamHelpers.ImageInputBudgetBytes, cancellationToken)
            .ConfigureAwait(false);

        var diagnostics = read.Diagnostics.ToList();
        if (read.IsTruncated)
        {
            diagnostics.Add("Image input was truncated to the preview budget.");
            return Failure("Image input exceeds the preview budget (128 MiB).", request, diagnostics);
        }

        if (read.Bytes.Length == 0)
        {
            return Failure("Image payload is empty.", request, diagnostics);
        }

        if (!TextureDecoder.TryDecodeToBgra(read.Bytes, extension, out var texture, out var error))
        {
            return Failure(error ?? "Unsupported image type.", request, diagnostics);
        }

        if (texture.DecodeDiagnostic is { } decodeDiagnostic)
        {
            diagnostics.Add(decodeDiagnostic);
        }

        string formatted = PreviewStreamHelpers.FormatDimensions(texture.Width, texture.Height);
        return Success(request, formatted, texture.Bgra, diagnostics);
    }

    private static PreviewResult Success(
        PreviewRequest request,
        string formatted,
        byte[] bgra,
        List<string> diagnostics)
    {
        if (bgra.Length > PreviewStreamHelpers.ImagePixelBudgetBytes)
        {
            diagnostics.Add(
                $"Decoded image exceeds preview safety limit ({PreviewStreamHelpers.ImagePixelBudgetBytes:N0} bytes).");
            return Failure(
                "Decoded image is larger than preview budget.",
                request,
                diagnostics);
        }

        return new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Image,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: bgra,
            FormattedContent: formatted,
            ErrorMessage: null,
            Diagnostics: diagnostics);
    }

    private static PreviewResult Failure(string reason, PreviewRequest request, IReadOnlyList<string> diagnostics) =>
        new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Image,
            IsSuccess: false,
            MetadataText: null,
            RawPayload: null,
            FormattedContent: null,
            ErrorMessage: reason,
            Diagnostics: diagnostics);
}
