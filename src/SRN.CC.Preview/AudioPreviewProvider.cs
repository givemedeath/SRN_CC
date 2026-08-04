using System.Buffers.Binary;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;

namespace SRN.CC.Preview;

public sealed class AudioPreviewProvider : IPreviewProvider
{
    private readonly IResourceTypeRegistry _registry;
    private static readonly string[] SupportedExtensions = ["wav", "bmu"];

    public AudioPreviewProvider(IResourceTypeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public PreviewFamily Family => PreviewFamily.Audio;

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
            return Failure("Unsupported audio type.", request, ["Cannot resolve audio extension."]);
        }

        var read = await PreviewStreamHelpers
            .ReadStreamBoundedAsync(payloadStream, PreviewStreamHelpers.AudioPayloadBudgetBytes, cancellationToken)
            .ConfigureAwait(false);

        List<string> diagnostics = read.Diagnostics.ToList();
        if (read.IsTruncated)
        {
            diagnostics.Add("Audio input was truncated to the preview budget.");
        }

        if (read.Bytes.Length == 0)
        {
            return Failure("Audio payload is empty.", request, diagnostics);
        }

        long contentOffset = 0;
        if (extension.Equals("bmu", StringComparison.OrdinalIgnoreCase))
        {
            if (read.Bytes.Length < 8)
            {
                return Failure("BMU payload is too small.", request, diagnostics);
            }

            contentOffset = 8;
            if (!HasWavSignature(read.Bytes, 8))
            {
                return Failure("BMU payload missing embedded RIFF/WAVE data at offset 8.", request, diagnostics);
            }
            diagnostics.Add("BMU header skipped (8-byte preamble).");
        }
        else if (!extension.Equals("wav", StringComparison.OrdinalIgnoreCase) || !HasWavSignature(read.Bytes, 0))
        {
            return Failure("Audio payload does not start with RIFF/WAVE signature.", request, diagnostics);
        }

        byte[] audioPayload = read.Bytes.AsSpan((int)contentOffset).ToArray();
        if (!payloadStream.CanSeek)
        {
            diagnostics.Add("Source stream is non-seekable; materialized preview bytes into memory for deterministic seeking.");
        }

        if (!TryParseWavHeader(audioPayload, out WavMetadata? metadata, out string? parseError))
        {
            return Failure($"Audio parse failed: {parseError}", request, diagnostics);
        }

        var formatted = BuildFormattedMetadata(metadata!);
        return new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Audio,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: audioPayload,
            FormattedContent: formatted,
            ErrorMessage: null,
            Diagnostics: diagnostics);
    }

    private static string BuildFormattedMetadata(WavMetadata metadata)
    {
        var duration = metadata.DataSize > 0 && metadata.AverageBytesPerSecond > 0
            ? metadata.DataSize / (double)metadata.AverageBytesPerSecond
            : 0d;

        return string.Join(
            Environment.NewLine,
            "=== WAV PREVIEW ===",
            $"Format: {metadata.AudioFormatDescription}",
            $"Channels: {metadata.Channels}",
            $"Sample Rate: {metadata.SampleRate} Hz",
            $"Bits Per Sample: {metadata.BitsPerSample}",
            $"Avg Bytes/Sec: {metadata.AverageBytesPerSecond}",
            $"Data Offset: {metadata.DataOffset} bytes",
            $"Data Size: {metadata.DataSize} bytes",
            $"Estimated Duration: {duration.ToString("0.###", CultureInfo.InvariantCulture)} sec");
    }

    private static bool HasWavSignature(byte[] bytes, int offset)
    {
        if (offset + 12 > bytes.Length)
        {
            return false;
        }

        return IsAscii(bytes, offset, 4, "RIFF") && IsAscii(bytes, offset + 8, 4, "WAVE");
    }

    private static bool TryParseWavHeader(byte[] bytes, [NotNullWhen(true)] out WavMetadata? metadata, out string? error)
    {
        metadata = null;
        error = null;

        if (bytes.Length < 12)
        {
            error = "WAV header is too small.";
            return false;
        }

        if (!IsAscii(bytes, 0, 4, "RIFF"))
        {
            error = "WAV header is missing RIFF signature.";
            return false;
        }

        if (!IsAscii(bytes, 8, 4, "WAVE"))
        {
            error = "WAV header is missing WAVE signature.";
            return false;
        }

        int cursor = 12;
        ushort? format = null;
        ushort? channels = null;
        uint? sampleRate = null;
        uint? avgBytesPerSec = null;
        ushort? blockAlign = null;
        ushort? bitsPerSample = null;
        uint? dataOffset = null;
        uint? dataSize = null;

        while (cursor + 8 <= bytes.Length)
        {
            if (!IsAscii(bytes, cursor, 4, out var chunkId))
            {
                error = $"WAV chunk at {cursor} is malformed.";
                return false;
            }

            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4, 4));
            long chunkDataOffset = (long)cursor + 8;
            long chunkDataEnd = chunkDataOffset + chunkSize;
            if (chunkDataEnd > bytes.Length)
            {
                error = $"WAV chunk '{chunkId}' claims {chunkSize:N0} bytes but input is truncated.";
                return false;
            }

            if (chunkId is "fmt ")
            {
                if (chunkSize < 16)
                {
                    error = "WAV 'fmt ' chunk is too small.";
                    return false;
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)chunkDataOffset));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)chunkDataOffset + 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)chunkDataOffset + 4));
                avgBytesPerSec = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)chunkDataOffset + 8));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)chunkDataOffset + 12));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)chunkDataOffset + 14));
            }
            else if (chunkId is "data")
            {
                dataOffset = (uint)chunkDataOffset;
                dataSize = chunkSize;
                if (dataOffset + dataSize > bytes.Length)
                {
                    error = "WAV data chunk is truncated.";
                    return false;
                }
            }

            cursor = (int)(chunkDataEnd + (chunkSize & 1u));
        }

        if (dataOffset is null || dataSize is null)
        {
            error = "WAV data chunk was not found.";
            return false;
        }

        if (format is null || channels is null || sampleRate is null || avgBytesPerSec is null ||
            blockAlign is null || bitsPerSample is null)
        {
            error = "WAV fmt chunk was not found or incomplete.";
            return false;
        }

        string audioFormatDescription = format.Value switch
        {
            1 => "PCM",
            3 => "IEEE Float",
            0xFFFE => "Extensible",
            _ => $"Unknown ({format.Value})"
        };

        metadata = new WavMetadata(
            AudioFormatDescription: audioFormatDescription,
            Channels: channels.Value,
            SampleRate: sampleRate.Value,
            BitsPerSample: bitsPerSample.Value,
            BlockAlign: blockAlign.Value,
            AverageBytesPerSecond: avgBytesPerSec.Value,
            DataOffset: dataOffset.Value,
            DataSize: dataSize.Value);

        return true;
    }

    private static bool IsAscii(byte[] bytes, int offset, int length, string expected)
    {
        if (!IsSpanInRange(bytes, offset, length))
        {
            return false;
        }

        return IsAscii(bytes, offset, length, out string value) && value.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAscii(byte[] bytes, int offset, int length, out string value)
    {
        if (!IsSpanInRange(bytes, offset, length))
        {
            value = string.Empty;
            return false;
        }

        value = Encoding.ASCII.GetString(bytes, offset, length);
        return true;
    }

    private static bool IsSpanInRange(byte[] bytes, int offset, int length) =>
        offset >= 0 && length >= 0 && bytes != null && offset + length <= bytes.Length;

    private static PreviewResult Failure(string reason, PreviewRequest request, IReadOnlyList<string> diagnostics) =>
        new PreviewResult(
            Occurrence: request.Occurrence,
            Family: PreviewFamily.Audio,
            IsSuccess: false,
            MetadataText: null,
            RawPayload: null,
            FormattedContent: null,
            ErrorMessage: reason,
            Diagnostics: diagnostics);

    private sealed record WavMetadata(
        string AudioFormatDescription,
        ushort Channels,
        uint SampleRate,
        ushort BitsPerSample,
        ushort BlockAlign,
        uint AverageBytesPerSecond,
        uint DataOffset,
        uint DataSize);
}
