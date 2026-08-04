using System.Text;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;

namespace SRN.CC.Preview;

public sealed class BoundedHexPreviewProvider : IPreviewProvider
{
    public PreviewFamily Family => PreviewFamily.Hex;

    public bool CanPreview(PreviewRequest request) => true;

    public async Task<PreviewResult> GeneratePreviewAsync(
        PreviewRequest request,
        Stream payloadStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloadStream);

        long budget = Math.Min(request.SafetyBudgetBytes, 1048576L); // Max 1 MiB budget
        List<string> diagnostics = new();

        long bytesToRead = payloadStream.Length;
        bool isTruncated = false;
        if (bytesToRead > budget)
        {
            bytesToRead = budget;
            isTruncated = true;
            diagnostics.Add($"Preview payload truncated to budget limit of {budget:N0} bytes (total stream: {payloadStream.Length:N0} bytes).");
        }

        byte[] buffer = new byte[bytesToRead];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await payloadStream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }

        StringBuilder sb = new();
        sb.AppendLine($"=== HEX DUMP ({totalRead:N0} bytes{(isTruncated ? " [TRUNCATED]" : "")}) ===");
        sb.AppendLine("Offset    00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  ASCII");
        sb.AppendLine("--------  -------------------------------------------------  ----------------");

        for (int offset = 0; offset < totalRead; offset += 16)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sb.Append($"{offset:X8}  ");

            int lineLen = Math.Min(16, totalRead - offset);
            for (int i = 0; i < 16; i++)
            {
                if (i == 8) sb.Append(' ');
                if (i < lineLen)
                {
                    sb.Append($"{buffer[offset + i]:X2} ");
                }
                else
                {
                    sb.Append("   ");
                }
            }

            sb.Append(" ");
            for (int i = 0; i < lineLen; i++)
            {
                byte b = buffer[offset + i];
                char c = (b >= 32 && b <= 126) ? (char)b : '.';
                sb.Append(c);
            }

            sb.AppendLine();
        }

        return new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Hex,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: buffer,
            FormattedContent: sb.ToString(),
            ErrorMessage: null,
            Diagnostics: diagnostics
        );
    }
}
