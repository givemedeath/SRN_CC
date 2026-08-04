using System.Text;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;

namespace SRN.CC.Preview;

public sealed class TextPreviewProvider : IPreviewProvider
{
    private readonly IResourceTypeRegistry _registry;

    private static readonly string[] SupportedExtensions =
    [
        "2da",
        "mtr",
        "txi",
        "set",
        "ini",
        "gui",
        "txt",
        "shader"
    ];

    public TextPreviewProvider(IResourceTypeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public PreviewFamily Family => PreviewFamily.Text;

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

        var read = await PreviewStreamHelpers
            .ReadStreamBoundedAsync(payloadStream, PreviewStreamHelpers.TextPreviewBudgetBytes, cancellationToken)
            .ConfigureAwait(false);

        List<string> diagnostics = read.Diagnostics.ToList();
        if (read.IsTruncated)
        {
            diagnostics.Add("Text input was truncated to preview budget.");
        }

        if (read.Bytes.Length == 0)
        {
            return Failure("Text payload is empty.", request, diagnostics);
        }

        bool isBinary = PreviewStreamHelpers.IsLikelyBinary(read.Bytes);
        string text = PreviewStreamHelpers.DecodeTextPayload(read.Bytes, out bool isUtf8);

        if (!isUtf8 && isBinary)
        {
            return Failure("Text preview is unsupported: payload appears to be binary.", request, diagnostics);
        }

        text = PreviewStreamHelpers.NormalizeLineEndings(text);
        if (read.IsTruncated)
        {
            text = string.Concat(text, Environment.NewLine, "[TRUNCATED]");
        }

        diagnostics.Add(isUtf8 ? "Decoded as UTF-8 text." : "Decoded as CP1252 fallback text.");
        if (isBinary)
        {
            diagnostics.Add("Payload still appears binary-like, but decode succeeded.");
        }

        return new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Text,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: read.Bytes,
            FormattedContent: text,
            ErrorMessage: null,
            Diagnostics: diagnostics);
    }

    private static PreviewResult Failure(string reason, PreviewRequest request, IReadOnlyList<string> diagnostics) =>
        new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Text,
            IsSuccess: false,
            MetadataText: null,
            RawPayload: null,
            FormattedContent: null,
            ErrorMessage: reason,
            Diagnostics: diagnostics);
}
