using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Occurrences;
using SWLOR.NWN.Formats;
using SWLOR.NWN.Formats.Mdl;

namespace SRN.CC.Preview;

public sealed class MdlPreviewProvider : IPreviewProvider
{
    private readonly IResourceTypeRegistry _registry;

    public MdlPreviewProvider(IResourceTypeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public PreviewFamily Family => PreviewFamily.Model;

    public bool CanPreview(PreviewRequest request) =>
        PreviewStreamHelpers.HasAnyExtension(request, _registry, ["mdl"]);

    public async Task<PreviewResult> GeneratePreviewAsync(
        PreviewRequest request,
        Stream payloadStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloadStream);
        cancellationToken.ThrowIfCancellationRequested();

        var read = await PreviewStreamHelpers
            .ReadStreamBoundedAsync(payloadStream, PreviewStreamHelpers.MdlPayloadBudgetBytes, cancellationToken)
            .ConfigureAwait(false);

        List<string> diagnostics = read.Diagnostics.ToList();
        if (read.IsTruncated)
        {
            diagnostics.Add("Model input was truncated to preview budget.");
            return Failure("Model input exceeds the preview budget (64 MiB).", request, diagnostics);
        }

        if (read.Bytes.Length == 0)
        {
            return Failure("Model payload is empty.", request, diagnostics);
        }

        try
        {
            bool isAscii = IsLikelyAsciiFormat(read.Bytes);
            MdlModel parsed = new MdlReader().Parse(read.Bytes);

            string formatted = BuildModelFormatted(request.Occurrence, parsed, isAscii, diagnostics, isFallback: false, parseWarning: null);
            return new PreviewResult(
                Occurrence: request.Occurrence,
                Family: PreviewFamily.Model,
                IsSuccess: true,
                MetadataText: null,
                RawPayload: null,
                FormattedContent: formatted,
                ErrorMessage: null,
                Diagnostics: diagnostics);
        }
        catch (NwnFormatException ex)
        {
            if (TryBuildMdlFallback(read.Bytes, out var fallback, out var warning))
            {
                diagnostics.Add($"Primary parse failed: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    diagnostics.Add(warning);
                }

                string formatted = BuildModelFormatted(
                    request.Occurrence,
                    fallback,
                    IsLikelyAsciiFormat(read.Bytes),
                    diagnostics,
                    isFallback: true,
                    parseWarning: ex.Message);
                return new PreviewResult(
                    Occurrence: request.Occurrence,
                    Family: PreviewFamily.Model,
                    IsSuccess: true,
                    MetadataText: null,
                    RawPayload: null,
                    FormattedContent: formatted,
                    ErrorMessage: null,
                    Diagnostics: diagnostics);
            }

            return Failure($"Model parse failed: {ex.Message}", request, diagnostics);
        }
        catch (Exception ex)
        {
            return Failure($"Model parse failed: {ex.Message}", request, diagnostics);
        }
    }

    private static string BuildModelFormatted(
        AssetOccurrence occurrence,
        MdlModel model,
        bool isAscii,
        List<string> diagnostics,
        bool isFallback,
        string? parseWarning)
    {
        List<string> lines = new();
        string familyHint = isAscii ? "ASCII" : "Binary";
        lines.Add("=== MDL PREVIEW ===");
        lines.Add($"Resref: {occurrence.Identity.Resref}");
        lines.Add($"Format: {familyHint} MDL");
        lines.Add($"Model Name: {model.Name}");
        lines.Add($"SuperModel: {model.SuperModel}");
        lines.Add($"Model Type: {model.ModelType}");
        lines.Add($"Scale: {model.Scale.ToString(CultureInfo.InvariantCulture)}");
        lines.Add($"Radius: {model.Radius.ToString(CultureInfo.InvariantCulture)}");
        lines.Add($"Animations: {model.Animations.Count:N0}");

        MdlTrimeshNode[] meshes = model.GetMeshNodes().ToArray();
        lines.Add($"Mesh Blocks: {meshes.Length:N0}");
        if (meshes.Length == 0 && !isFallback)
        {
            diagnostics.Add("Parsed model has no mesh nodes (renderable geometry was missing).");
        }

        var textureCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in meshes)
        {
            if (!string.IsNullOrWhiteSpace(mesh.Bitmap))
            {
                AddTexture(textureCounts, mesh.Bitmap);
            }

            if (!string.IsNullOrWhiteSpace(mesh.Lightmap))
            {
                AddTexture(textureCounts, mesh.Lightmap);
            }
        }

        lines.Add($"Texture References: {textureCounts.Count:N0} unique");
        foreach (var key in textureCounts.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"  {key} x{textureCounts[key]}");
        }

        if (!string.IsNullOrWhiteSpace(parseWarning))
        {
            lines.Add($"Parse Warning: {parseWarning}");
        }

        if (isFallback)
        {
            lines.Add("Status: Partial metadata extracted from header fallback.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildModelFormatted(
        AssetOccurrence occurrence,
        MdlFallbackMetadata fallback,
        bool isAscii,
        List<string> diagnostics,
        bool isFallback,
        string? parseWarning)
    {
        return BuildModelFormatted(
            occurrence,
            new MdlModel
            {
                Name = fallback.Name,
                SuperModel = fallback.SuperModel,
                ModelType = fallback.ModelType,
                Radius = fallback.Radius,
                Scale = fallback.Scale
            },
            isAscii,
            diagnostics,
            isFallback: isFallback,
            parseWarning: parseWarning);
    }

    private static bool IsLikelyAsciiFormat(byte[] bytes) =>
        bytes.Length >= 3 && bytes.AsSpan(0, 3).IndexOf((byte)0) == -1;

    private static void AddTexture(Dictionary<string, int> textures, string texture)
    {
        if (!string.IsNullOrWhiteSpace(texture))
        {
            textures[texture] = textures.GetValueOrDefault(texture) + 1;
        }
    }

    private static bool TryBuildMdlFallback(byte[] bytes, out MdlFallbackMetadata fallback, out string warning)
    {
        fallback = new("", string.Empty, 0, 1f, 0f);
        warning = string.Empty;

        if (bytes.Length < 200)
        {
            warning = "Fallback parser requires at least 200 bytes.";
            return false;
        }

        if (IsLikelyAsciiFormat(bytes))
        {
            string text = Encoding.UTF8.GetString(bytes, 0, Math.Min(4096, bytes.Length));
            string name = "unknown";
            string superModel = "unknown";
            bool foundName = false;

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("newmodel", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        name = parts[1];
                        foundName = true;
                        break;
                    }
                }

                if (trimmed.StartsWith("setsupermodel", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        superModel = parts[1];
                    }
                }
            }

            if (!foundName)
            {
                warning = "Fallback ASCII parser could not find a `newmodel` token.";
                return false;
            }

            fallback = new MdlFallbackMetadata(name, superModel, 0, 1f, 0f);
            return true;
        }

        try
        {
            string name = ReadAsciiFixed(bytes, 20, 64);
            string superModel = ReadAsciiFixed(bytes, 184, 64);
            byte modelType = bytes[108];
            float scale = ReadSingle(bytes, 164);
            float radius = ReadSingle(bytes, 160);
            fallback = new MdlFallbackMetadata(name, superModel, modelType, scale, radius);
            warning = "Used binary header fallback parse for malformed payload.";
            return true;
        }
        catch (Exception ex)
        {
            warning = $"Binary fallback parse failed: {ex.Message}";
            return false;
        }
    }

    private static float ReadSingle(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            throw new InvalidDataException("Binary MDL fallback read exceeds payload bounds.");
        }

        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));
    }

    private static string ReadAsciiFixed(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > bytes.Length)
        {
            return string.Empty;
        }

        string value = Encoding.ASCII.GetString(bytes, offset, length);
        int nullTerminator = value.IndexOf('\0');
        if (nullTerminator >= 0)
        {
            value = value[..nullTerminator];
        }

        return value.Trim();
    }

    private static PreviewResult Failure(string reason, PreviewRequest request, IReadOnlyList<string> diagnostics) =>
        new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Model,
            IsSuccess: false,
            MetadataText: null,
            RawPayload: null,
            FormattedContent: null,
            ErrorMessage: reason,
            Diagnostics: diagnostics);

    private readonly record struct MdlFallbackMetadata(
        string Name,
        string SuperModel,
        byte ModelType,
        float Scale,
        float Radius);
}
