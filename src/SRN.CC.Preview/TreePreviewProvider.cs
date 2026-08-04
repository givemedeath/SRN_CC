using System.Buffers.Binary;
using System.Text;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SWLOR.NWN.Formats;
using SWLOR.NWN.Formats.Gff;

namespace SRN.CC.Preview;

public sealed class TreePreviewProvider : IPreviewProvider
{
    private readonly IResourceTypeRegistry _registry;
    private static readonly string[] SupportedExtensions = ["gff", "itp", "ssf"];

    public TreePreviewProvider(IResourceTypeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public PreviewFamily Family => PreviewFamily.Tree;

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
            return Failure("Unsupported tree-family type.", request, ["Cannot resolve tree extension."]);
        }

        var read = await PreviewStreamHelpers
            .ReadStreamBoundedAsync(payloadStream, PreviewStreamHelpers.TreePayloadBudgetBytes, cancellationToken)
            .ConfigureAwait(false);

        List<string> diagnostics = read.Diagnostics.ToList();
        if (read.IsTruncated)
        {
            diagnostics.Add("Tree input was truncated to preview budget.");
        }

        if (read.Bytes.Length == 0)
        {
            return Failure("Tree payload is empty.", request, diagnostics);
        }

        extension = extension.ToLowerInvariant();
        return extension switch
        {
            "ssf" => RenderSsf(request, read.Bytes, diagnostics),
            _ => RenderGff(request, read.Bytes, diagnostics)
        };
    }

    private static PreviewResult RenderGff(
        PreviewRequest request,
        byte[] bytes,
        List<string> diagnostics)
    {
        try
        {
            GffFile file = GffReader.Read(bytes);
            StringBuilder sb = new();
            sb.AppendLine("=== TREE PREVIEW ===");
            sb.AppendLine($"Magic: {file.FileType}");
            sb.AppendLine($"Version: {file.FileVersion}");
            sb.AppendLine($"Root: struct 0x{file.RootStruct.Type:X8}");
            RenderStruct(sb, file.RootStruct, 1);

            string formatted = sb.ToString();
            return TruncateResultIfNeeded(request, formatted, diagnostics, true);
        }
        catch (NwnFormatException ex)
        {
            return Failure($"GFF parse failed: {ex.Message}", request, diagnostics);
        }
        catch (Exception ex)
        {
            return Failure($"GFF preview failed: {ex.Message}", request, diagnostics);
        }
    }

    private static PreviewResult RenderSsf(
        PreviewRequest request,
        byte[] bytes,
        List<string> diagnostics)
    {
        try
        {
            if (!IsAscii(bytes, 0, 4, out var magic))
            {
                return Failure("SSF parse failed: header magic is missing.", request, diagnostics);
            }

            if (!TryReadUInt32(bytes, 4, out var version))
            {
                return Failure("SSF parse failed: header version is missing.", request, diagnostics);
            }

            if (!TryReadUInt32(bytes, 8, out var declaredCount))
            {
                return Failure("SSF parse failed: entry count is missing.", request, diagnostics);
            }

            const int EntrySize = 20;
            const int HeaderSize = 12;
            long possibleEntries = bytes.Length > HeaderSize ? (bytes.Length - HeaderSize) / EntrySize : 0;
            int parsedEntries = (int)Math.Min(possibleEntries, Math.Min(declaredCount, (uint)int.MaxValue));

            StringBuilder sb = new();
            sb.AppendLine("=== SSF PREVIEW ===");
            sb.AppendLine($"Magic: {magic}");
            sb.AppendLine($"Version: 0x{version:X8}");
            sb.AppendLine($"Entry Count (declared): {declaredCount:N0}");
            sb.AppendLine($"Entries Parsed: {parsedEntries:N0}");

            for (int index = 0; index < parsedEntries; index++)
            {
                int rowOffset = HeaderSize + index * EntrySize;
                if (rowOffset + EntrySize > bytes.Length)
                {
                    diagnostics.Add($"SSF entry {index} is truncated.");
                    break;
                }

                string resref = ReadFixedAscii(bytes, rowOffset, 16);
                uint strRef = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(rowOffset + 16, 4));
                sb.AppendLine($"[{index:0000}] {resref,-16} 0x{strRef:X8}");
            }

            return TruncateResultIfNeeded(request, sb.ToString(), diagnostics, parsedEntries > 0);
        }
        catch (Exception ex)
        {
            return Failure($"SSF parse failed: {ex.Message}", request, diagnostics);
        }
    }

    private static PreviewResult TruncateResultIfNeeded(
        PreviewRequest request,
        string formatted,
        List<string> diagnostics,
        bool isSuccess)
    {
        const string truncatedMarker = "[TRUNCATED]";

        byte[] utf8 = Encoding.UTF8.GetBytes(formatted);
        if (utf8.Length <= PreviewStreamHelpers.TreePayloadBudgetBytes)
        {
            if (isSuccess)
            {
                return new PreviewResult(
                    Occurrence: request.Occurrence,
                    Family: PreviewFamily.Tree,
                    IsSuccess: true,
                    MetadataText: null,
                    RawPayload: null,
                    FormattedContent: formatted,
                    ErrorMessage: null,
                    Diagnostics: diagnostics);
            }

            return Failure("Tree preview contained no entries.", request, diagnostics);
        }

        int keep = (int)Math.Max(0, PreviewStreamHelpers.TreePayloadBudgetBytes - Encoding.UTF8.GetByteCount(truncatedMarker) - 2);
        string truncated = $"{formatted[..Math.Min(formatted.Length, keep)]}{Environment.NewLine}{truncatedMarker}";
        diagnostics.Add("Tree preview truncated to payload budget.");
        return new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Tree,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: null,
            FormattedContent: truncated,
            ErrorMessage: null,
            Diagnostics: diagnostics);
    }

    private static void RenderStruct(StringBuilder sb, GffStruct node, int depth)
    {
        string nodeIndent = new string(' ', depth * 2);
        sb.AppendLine($"{nodeIndent}Struct 0x{node.Type:X8}");
        if (node.Fields.Count == 0)
        {
            sb.AppendLine($"{nodeIndent}  [empty]");
            return;
        }

        for (int i = 0; i < node.Fields.Count; i++)
        {
            GffField field = node.Fields[i];
            string fieldIndent = new string(' ', (depth + 1) * 2);
            sb.AppendLine($"{fieldIndent}{DescribeFieldLabel(field)}");
            string valueIndent = new string(' ', (depth + 2) * 2);

            if (field.Value is GffStruct childStruct)
            {
                RenderStructNode(sb, childStruct, depth + 3, $"{field.Label} ->");
            }
            else if (field.Value is GffList list)
            {
                if (list.Elements.Count == 0)
                {
                    sb.AppendLine($"{valueIndent}[empty list]");
                    continue;
                }

                sb.AppendLine($"{valueIndent}[List] {list.Elements.Count:N0} element(s)");
                for (int child = 0; child < list.Elements.Count; child++)
                {
                    RenderStructNode(
                        sb,
                        list.Elements[child],
                        depth + 3,
                        $"{field.Label}[{child}]");
                }
            }
            else
            {
                sb.AppendLine($"{valueIndent}{DescribeScalar(field)}");
            }
        }
    }

    private static void RenderStructNode(StringBuilder sb, GffStruct node, int depth, string marker)
    {
        string indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}{marker}");
        RenderStruct(sb, node, depth + 1);
    }

    private static string DescribeFieldLabel(GffField field)
    {
        return $"{field.Label} ({DescribeFieldType(field.Type)})";
    }

    private static string DescribeFieldType(uint type) =>
        type switch
        {
            GffField.BYTE => "BYTE",
            GffField.CHAR => "CHAR",
            GffField.WORD => "WORD",
            GffField.SHORT => "SHORT",
            GffField.DWORD => "DWORD",
            GffField.INT => "INT",
            GffField.DWORD64 => "DWORD64",
            GffField.INT64 => "INT64",
            GffField.FLOAT => "FLOAT",
            GffField.DOUBLE => "DOUBLE",
            GffField.CExoString => "CExoString",
            GffField.CResRef => "CResRef",
            GffField.CExoLocString => "CExoLocString",
            GffField.VOID => "VOID",
            GffField.Struct => "Struct",
            GffField.List => "List",
            _ => $"Unknown(0x{type:X2})"
        };

    private static string DescribeScalar(GffField field)
    {
        return field.Value switch
        {
            null => "<null>",
            byte value => $"{value}",
            sbyte value => $"{value}",
            ushort value => $"{value}",
            short value => $"{value}",
            uint value => $"{value}",
            int value => $"{value}",
            ulong value => $"{value}",
            long value => $"{value}",
            float value => $"{value}",
            double value => $"{value}",
            string value => value,
            CExoLocString loc => $"StrRef={loc.StrRef} Substrings={loc.LocalizedStrings.Count}",
            byte[] bytes => $"<VOID {bytes.Length} bytes>",
            _ => $"<UNKNOWN TYPE {DescribeFieldType(field.Type)}>"
        };
    }

    private static bool TryReadUInt32(byte[] bytes, int offset, out uint value)
    {
        if (!TryRange(bytes, offset, 4))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        return true;
    }

    private static bool IsAscii(byte[] bytes, int offset, int count, out string value)
    {
        if (!TryRange(bytes, offset, count))
        {
            value = string.Empty;
            return false;
        }

        value = Encoding.ASCII.GetString(bytes, offset, count);
        return true;
    }

    private static bool TryRange(byte[] bytes, int offset, int length) =>
        bytes != null && offset >= 0 && length >= 0 && offset + length <= bytes.Length;

    private static string ReadFixedAscii(byte[] bytes, int offset, int length)
    {
        if (!TryRange(bytes, offset, length))
        {
            return string.Empty;
        }

        string value = Encoding.ASCII.GetString(bytes, offset, length);
        int nullTerminator = value.IndexOf('\0');
        if (nullTerminator >= 0)
        {
            value = value[..nullTerminator];
        }

        return value.TrimEnd();
    }

    private static PreviewResult Failure(string reason, PreviewRequest request, IReadOnlyList<string> diagnostics) =>
        new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Tree,
            IsSuccess: false,
            MetadataText: null,
            RawPayload: null,
            FormattedContent: null,
            ErrorMessage: reason,
            Diagnostics: diagnostics);
}
