using System.Text;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;

namespace SRN.CC.Preview;

public sealed class MetadataPreviewProvider : IPreviewProvider
{
    private readonly IResourceTypeRegistry _registry;

    public MetadataPreviewProvider(IResourceTypeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public PreviewFamily Family => PreviewFamily.Metadata;

    public bool CanPreview(PreviewRequest request) => true;

    public Task<PreviewResult> GeneratePreviewAsync(
        PreviewRequest request,
        Stream payloadStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloadStream);

        cancellationToken.ThrowIfCancellationRequested();

        var occ = request.Occurrence;
        var src = request.Source;
        ushort typeId = occ.Identity.ResourceType;
        string typeName = _registry.TryGetExtension(typeId, out var ext) ? ext.ToUpperInvariant() : "UNKNOWN";

        string sha256Hex = occ.Sha256 != null ? Convert.ToHexString(occ.Sha256).ToLowerInvariant() : "Uncomputed";

        StringBuilder sb = new();
        sb.AppendLine("=== ASSET METADATA ===");
        sb.AppendLine($"Resref (Canonical): {occ.Identity.Resref}");
        sb.AppendLine($"Original Name     : {occ.OriginalName}");
        sb.AppendLine($"Resource Type     : {typeName} (0x{typeId:X4} / {typeId})");
        sb.AppendLine($"Source            : {src.Kind} - {src.FullPath}");
        sb.AppendLine($"Locator           : {occ.Locator}");
        sb.AppendLine($"Declared Size     : {occ.Size:N0} bytes");
        sb.AppendLine($"Actual Stream Len : {payloadStream.Length:N0} bytes");
        sb.AppendLine($"Validation State  : {occ.ValidationState}");
        sb.AppendLine($"Payload SHA-256   : {sha256Hex}");

        List<string> diagnostics = new();
        if (occ.ValidationState != Core.Occurrences.ValidationState.Valid)
        {
            diagnostics.Add($"Validation error: {occ.ValidationState}");
        }

        var result = new PreviewResult(
            Occurrence: occ,
            Family: PreviewFamily.Metadata,
            IsSuccess: true,
            MetadataText: sb.ToString(),
            RawPayload: null,
            FormattedContent: sb.ToString(),
            ErrorMessage: null,
            Diagnostics: diagnostics
        );

        return Task.FromResult(result);
    }
}
