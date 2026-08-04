using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;

namespace SRN.CC.Preview;

internal static class PreviewStreamHelpers
{
    public const int MaxImageDimension = 4096;
    public const long TextPreviewBudgetBytes = 8L * 1024 * 1024;
    public const long ImagePixelBudgetBytes = 64L * 1024 * 1024;
    public const long ImageInputBudgetBytes = 128L * 1024 * 1024;
    public const long AudioPayloadBudgetBytes = 64L * 1024 * 1024;
    public const long TreePayloadBudgetBytes = 32L * 1024 * 1024;
    public const long MdlPayloadBudgetBytes = 64L * 1024 * 1024;

    public sealed record BoundedReadResult(byte[] Bytes, bool IsTruncated, IReadOnlyList<string> Diagnostics);

    private const int DefaultChunkSize = 8192;
    private const double BinarySuspiciousRatioThreshold = 0.16;
    private const int ProbeBytesForBinaryCheck = 4 * 1024;
    private static readonly Encoding Cp1252Encoding;
    private static readonly Encoding AsciiEncoding = Encoding.ASCII;
    private static readonly Encoding Utf8Encoding = Encoding.UTF8;

    static PreviewStreamHelpers()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252Encoding = Encoding.GetEncoding(1252);
    }

    public static async Task<BoundedReadResult> ReadStreamBoundedAsync(
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Maximum bytes must be non-negative.");
        }

        List<string> diagnostics = new();

        if (!source.CanRead)
        {
            throw new NotSupportedException("Source stream is not readable.");
        }

        if (source.CanSeek)
        {
            long remaining;
            try
            {
                remaining = source.Length - source.Position;
            }
            catch (NotSupportedException)
            {
                remaining = -1;
            }

            if (remaining >= 0)
            {
                return ReadFromSeekable(source, remaining, maxBytes, diagnostics);
            }
        }

        return await ReadFromStreamingAsync(source, maxBytes, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    public static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    public static string DecodeTextPayload(ReadOnlySpan<byte> bytes, out bool isUtf8)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            isUtf8 = true;
            return Utf8Encoding.GetString(bytes[3..]);
        }

        if (Utf8.IsValid(bytes))
        {
            isUtf8 = true;
            return Utf8Encoding.GetString(bytes);
        }

        isUtf8 = false;
        return Cp1252Encoding.GetString(bytes);
    }

    public static bool IsLikelyBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        int suspicious = 0;
        int scanned = Math.Min(bytes.Length, ProbeBytesForBinaryCheck);
        for (int i = 0; i < scanned; i++)
        {
            byte value = bytes[i];
            bool isText = value is 0x09 or 0x0A or 0x0D
                || (value >= 0x20 && value <= 0x7E)
                || value >= 0xA0;

            if (!isText)
            {
                suspicious++;
            }

            if (value == 0)
            {
                return true;
            }
        }

        double ratio = (double)suspicious / scanned;
        return ratio > BinarySuspiciousRatioThreshold;
    }

    public static bool TryGetOccurrenceExtension(PreviewRequest request, IResourceTypeRegistry? registry, out string extension)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (registry != null && registry.TryGetExtension(request.Occurrence.Identity.ResourceType, out var mapped))
        {
            extension = mapped;
            return true;
        }

        string raw = Path.GetExtension(request.Occurrence.OriginalName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            extension = string.Empty;
            return false;
        }

        extension = raw.TrimStart('.');
        return !string.IsNullOrWhiteSpace(extension);
    }

    public static bool HasAnyExtension(PreviewRequest request, IResourceTypeRegistry? registry, params string[] extensions)
    {
        if (!TryGetOccurrenceExtension(request, registry, out string extension))
        {
            return false;
        }

        foreach (string extensionCandidate in extensions)
        {
            if (string.Equals(extension, extensionCandidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string ReadFixedAsciiString(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        string value = AsciiEncoding.GetString(bytes.Slice(offset, count));
        int terminator = value.IndexOf('\0');
        if (terminator >= 0)
        {
            value = value[..terminator];
        }

        return value.TrimEnd();
    }

    public static string ReadFourCC(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return AsciiEncoding.GetString(bytes.Slice(offset, 4));
    }

    public static bool TryReadUInt16LittleEndian(ReadOnlySpan<byte> bytes, int offset, out ushort value)
    {
        if (offset < 0 || offset + 2 > bytes.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
        return true;
    }

    public static bool TryReadUInt32LittleEndian(ReadOnlySpan<byte> bytes, int offset, out uint value)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        return true;
    }

    public static bool IsValidImageDimensions(long width, long height)
    {
        return width > 0 && height > 0 && width <= MaxImageDimension && height <= MaxImageDimension
            && width * height <= ImagePixelBudgetBytes / 4;
    }

    public static string FormatDimensions(long width, long height) => $"{width}:{height}";

    private static BoundedReadResult ReadFromSeekable(
        Stream source,
        long remaining,
        long maxBytes,
        List<string> diagnostics)
    {
        long toRead = Math.Min(remaining, maxBytes);
        byte[] result = toRead <= int.MaxValue
            ? new byte[toRead]
            : throw new NotSupportedException("Preview budget exceeds supported in-memory read size.");
        int totalRead = 0;

        while (totalRead < result.Length)
        {
            int read = source.Read(result, totalRead, result.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead < result.Length)
        {
            diagnostics.Add($"Stream ended before preview budget ({toRead:N0} bytes requested).");
            result = result[..totalRead];
        }

        bool isTruncated = remaining > maxBytes;
        if (isTruncated)
        {
            diagnostics.Add($"Stream truncated to preview budget {maxBytes:N0} bytes.");
        }

        return new BoundedReadResult(result, isTruncated, diagnostics);
    }

    private static async Task<BoundedReadResult> ReadFromStreamingAsync(
        Stream source,
        long maxBytes,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(DefaultChunkSize);
        try
        {
            using MemoryStream result = new();

            long target = maxBytes + 1;
            long total = 0;
            while (total < target)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int chunkSize = (int)Math.Min(DefaultChunkSize, target - total);
                int read = await source.ReadAsync(rented.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (total < maxBytes)
                {
                    int toWrite = (int)Math.Min(read, maxBytes - total);
                    if (toWrite > 0)
                    {
                        await result.WriteAsync(rented.AsMemory(0, toWrite), cancellationToken).ConfigureAwait(false);
                    }
                }

                total += read;
            }

            bool isTruncated = total > maxBytes;
            if (isTruncated)
            {
                diagnostics.Add("Stream truncated to preview budget.");
            }

            return new BoundedReadResult(result.ToArray(), isTruncated, diagnostics);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
