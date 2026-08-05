using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;

namespace SRN.CC.Preview;

public sealed class PreviewEngine
{
    private readonly ISourceReaderDispatcher _dispatcher;
    private readonly IReadOnlyList<IPreviewProvider> _providers;
    private readonly IPreviewThumbnailCache? _thumbnailCache;
    private readonly SemaphoreSlim _concurrencySemaphore = new(3, 3); // Max 3 concurrent preview jobs

    public PreviewEngine(
        ISourceReaderDispatcher dispatcher,
        IEnumerable<IPreviewProvider> providers,
        IPreviewThumbnailCache? thumbnailCache = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _providers = providers?.ToList() ?? throw new ArgumentNullException(nameof(providers));
        _thumbnailCache = thumbnailCache;
    }

    /// <summary>
    /// The family that best fits <paramref name="request"/>'s occurrence, ignoring whatever family
    /// the request currently prefers. This is what a slot should show before the operator has
    /// expressed a preference — an image opens as an image rather than as a metadata table.
    /// </summary>
    /// <remarks>
    /// Metadata and Hex are skipped during the scan even though they are registered providers,
    /// because both answer <c>CanPreview</c> with an unconditional <c>true</c>: they are the
    /// universal fallbacks, so including them would make the first one in registration order the
    /// answer for every occurrence and the scan meaningless. Metadata remains the result when no
    /// specific provider claims the occurrence, which is the same fallback
    /// <see cref="ExecutePreviewAsync"/> lands on.
    /// </remarks>
    public PreviewFamily ResolveNaturalFamily(PreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (IPreviewProvider provider in _providers)
        {
            if (provider.Family is PreviewFamily.Metadata or PreviewFamily.Hex or PreviewFamily.Unknown)
            {
                continue;
            }

            if (provider.CanPreview(request))
            {
                return provider.Family;
            }
        }

        return PreviewFamily.Metadata;
    }

    public async Task<PreviewResult> ExecutePreviewAsync(
        PreviewRequest request,
        int debounceMs = 150,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (debounceMs > 0)
        {
            await Task.Delay(debounceMs, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var provider = _providers.FirstOrDefault(p => p.Family == request.PreferredFamily && p.CanPreview(request))
                           ?? _providers.FirstOrDefault(p => p.Family == PreviewFamily.Hex && p.CanPreview(request))
                           ?? _providers.FirstOrDefault(p => p.Family == PreviewFamily.Metadata && p.CanPreview(request));

            if (provider == null)
            {
                return new PreviewResult(
                    Occurrence: request.Occurrence,
                    Family: request.PreferredFamily,
                    IsSuccess: false,
                    MetadataText: null,
                    RawPayload: null,
                    FormattedContent: null,
                    ErrorMessage: $"No preview provider available for family {request.PreferredFamily}.",
                    Diagnostics: new[] { "Unsupported preview request" }
                );
            }

            // Thumbnail caching is scoped to PNG-producing Image-family previews only. Model-family
            // (or any other) results that carry an IPreviewPayload are process-local (architecture
            // decision A2) and must never be written to or read from this cache.
            SourceFingerprint? fingerprint = request.Source.Fingerprint;
            bool cacheEligible = _thumbnailCache != null && provider.Family == PreviewFamily.Image && fingerprint != null;

            if (cacheEligible)
            {
                CachedThumbnail? cached = await _thumbnailCache!
                    .TryGetAsync(fingerprint!, request.Occurrence, cancellationToken)
                    .ConfigureAwait(false);

                if (cached != null && TryBuildResultFromCache(request, cached, out PreviewResult cachedResult))
                {
                    return cachedResult;
                }
            }

            await using var stream = await _dispatcher.OpenOccurrenceAsync(request.Source, request.Occurrence, cancellationToken).ConfigureAwait(false);

            PreviewResult result = await provider.GeneratePreviewAsync(request, stream, cancellationToken).ConfigureAwait(false);

            if (cacheEligible && result.IsSuccess && result.Payload is null && result.Family == PreviewFamily.Image)
            {
                await TrySaveThumbnailAsync(fingerprint!, request.Occurrence, result, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PreviewResult(
                Occurrence: request.Occurrence,
                Family: request.PreferredFamily,
                IsSuccess: false,
                MetadataText: null,
                RawPayload: null,
                FormattedContent: null,
                ErrorMessage: $"Preview error: {ex.Message}",
                Diagnostics: new[] { ex.ToString() }
            );
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    private static bool TryBuildResultFromCache(PreviewRequest request, CachedThumbnail cached, out PreviewResult result)
    {
        try
        {
            (int width, int height, byte[] bgra) = ThumbnailPngCodec.DecodePngToBgra(cached.PngBytes);
            result = new PreviewResult(
                Occurrence: request.Occurrence,
                Family: PreviewFamily.Image,
                IsSuccess: true,
                MetadataText: null,
                RawPayload: bgra,
                FormattedContent: $"{width}:{height}",
                ErrorMessage: null,
                Diagnostics: Array.Empty<string>());
            return true;
        }
        catch
        {
            // A corrupted or unreadable cache entry must never surface as a failure; fall back to
            // re-running the provider as if this had been a cache miss.
            result = null!;
            return false;
        }
    }

    private async Task TrySaveThumbnailAsync(
        SourceFingerprint fingerprint,
        AssetOccurrence occurrence,
        PreviewResult result,
        CancellationToken cancellationToken)
    {
        if (result.RawPayload is not { Length: > 0 } bgra)
        {
            return;
        }

        if (!TryParseDimensions(result.FormattedContent, out int width, out int height))
        {
            return;
        }

        if ((long)width * height * 4 != bgra.Length)
        {
            return;
        }

        try
        {
            byte[] png = ThumbnailPngCodec.EncodeBgraToPng(width, height, bgra);
            await _thumbnailCache!.SaveAsync(fingerprint, occurrence, width, height, png, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Caching is best-effort: the provider's result is already correct and must be returned
            // to the caller regardless of a cache-write failure (unsupported locator, I/O error, etc.).
        }
    }

    private static bool TryParseDimensions(string? formatted, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrEmpty(formatted))
        {
            return false;
        }

        int separatorIndex = formatted.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == formatted.Length - 1)
        {
            return false;
        }

        return int.TryParse(formatted.AsSpan(0, separatorIndex), out width)
            && int.TryParse(formatted.AsSpan(separatorIndex + 1), out height)
            && width > 0
            && height > 0;
    }
}

/// <summary>
/// Minimal, dependency-free PNG encode/decode used only by <see cref="PreviewEngine"/> to persist and
/// restore BGRA preview thumbnails through <see cref="IPreviewThumbnailCache"/>'s PNG-oriented
/// storage. Not a general-purpose PNG library: the encoder always emits 8-bit RGBA, filter type
/// "None", as a single IDAT chunk; the decoder accepts any non-interlaced 8-bit RGB/RGBA PNG
/// (standard scanline filter types 0-4) so it can also read anything the encoder above previously
/// wrote.
/// </summary>
internal static class ThumbnailPngCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes a straight, top-left-origin BGRA buffer (4 bytes/pixel) as an RGBA PNG.</summary>
    public static byte[] EncodeBgraToPng(int width, int height, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        }

        if ((long)bgra.Length != (long)width * height * 4)
        {
            throw new ArgumentException("BGRA buffer length does not match width * height * 4.", nameof(bgra));
        }

        int stride = width * 4;
        byte[] raw = new byte[height * (1 + stride)];
        for (int y = 0; y < height; y++)
        {
            int rawRowOffset = y * (1 + stride);
            raw[rawRowOffset] = 0; // filter type: None
            int srcRowOffset = y * stride;
            for (int x = 0; x < width; x++)
            {
                int s = srcRowOffset + x * 4;
                int d = rawRowOffset + 1 + x * 4;
                raw[d + 0] = bgra[s + 2]; // R
                raw[d + 1] = bgra[s + 1]; // G
                raw[d + 2] = bgra[s + 0]; // B
                raw[d + 3] = bgra[s + 3]; // A
            }
        }

        byte[] compressed = ZlibCompress(raw);

        using MemoryStream output = new();
        output.Write(Signature);
        WriteChunk(output, "IHDR", BuildIhdr(width, height));
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Decodes a PNG back to a straight, top-left-origin BGRA buffer (4 bytes/pixel).</summary>
    public static (int Width, int Height, byte[] Bgra) DecodePngToBgra(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a valid PNG signature.");
        }

        int offset = 8;
        int width = 0, height = 0;
        byte bitDepth = 0, colorType = 0;
        using MemoryStream idat = new();

        while (offset + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            int dataOffset = offset + 8;
            if (length < 0 || dataOffset + length + 4 > png.Length)
            {
                throw new InvalidDataException("Truncated PNG chunk.");
            }

            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(dataOffset, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(dataOffset + 4, 4));
                bitDepth = png[dataOffset + 8];
                colorType = png[dataOffset + 9];
            }
            else if (type == "IDAT")
            {
                idat.Write(png, dataOffset, length);
            }
            else if (type == "IEND")
            {
                break;
            }

            offset = dataOffset + length + 4;
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG is missing a valid IHDR chunk.");
        }

        if (bitDepth != 8 || (colorType != 6 && colorType != 2))
        {
            throw new NotSupportedException($"Unsupported PNG format (bit depth {bitDepth}, color type {colorType}).");
        }

        int channels = colorType == 6 ? 4 : 3;
        int stride = width * channels;
        byte[] raw = ZlibDecompress(idat.ToArray(), height * (1 + stride));

        byte[] bgra = new byte[width * height * 4];
        byte[] previousRow = new byte[stride];
        byte[] currentRow = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            int rawRowOffset = y * (1 + stride);
            byte filterType = raw[rawRowOffset];
            Array.Copy(raw, rawRowOffset + 1, currentRow, 0, stride);
            Unfilter(filterType, currentRow, previousRow, channels);

            int destRowOffset = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = x * channels;
                int d = destRowOffset + x * 4;
                bgra[d + 0] = currentRow[s + 2]; // B
                bgra[d + 1] = currentRow[s + 1]; // G
                bgra[d + 2] = currentRow[s + 0]; // R
                bgra[d + 3] = channels == 4 ? currentRow[s + 3] : (byte)255;
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return (width, height, bgra);
    }

    private static void Unfilter(byte filterType, byte[] current, byte[] previous, int bytesPerPixel)
    {
        int length = current.Length;
        switch (filterType)
        {
            case 0: // None
                break;
            case 1: // Sub
                for (int i = bytesPerPixel; i < length; i++)
                {
                    current[i] = (byte)(current[i] + current[i - bytesPerPixel]);
                }
                break;
            case 2: // Up
                for (int i = 0; i < length; i++)
                {
                    current[i] = (byte)(current[i] + previous[i]);
                }
                break;
            case 3: // Average
                for (int i = 0; i < length; i++)
                {
                    int a = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                    int b = previous[i];
                    current[i] = (byte)(current[i] + (a + b) / 2);
                }
                break;
            case 4: // Paeth
                for (int i = 0; i < length; i++)
                {
                    int a = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                    int b = previous[i];
                    int c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                    current[i] = (byte)(current[i] + PaethPredictor(a, b, c));
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported PNG scanline filter type {filterType}.");
        }
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        byte[] data = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4, 4), height);
        data[8] = 8;  // bit depth
        data[9] = 6;  // color type: RGBA
        data[10] = 0; // compression method
        data[11] = 0; // filter method
        data[12] = 0; // interlace method: none
        return data;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> lengthBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuf, data.Length);
        output.Write(lengthBuf);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        output.Write(crcBuf);
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[] ZlibDecompress(byte[] data, int expectedLength)
    {
        using MemoryStream input = new(data);
        using ZLibStream zlib = new(input, CompressionMode.Decompress);
        using MemoryStream output = new(Math.Max(expectedLength, 16));
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }
}
