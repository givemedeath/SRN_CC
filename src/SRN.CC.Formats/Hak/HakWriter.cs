using System.Buffers;
using System.Text;

namespace SRN.CC.Formats.Hak;

public sealed class HakWriter
{
    public const long LegacySingleHakLimit = 2147483647L; // Int32.MaxValue boundary

    public record WriteItem(HakFormatKey Key, Stream PayloadStream, uint PayloadSize);

    public static void Write(Stream outputStream, IEnumerable<WriteItem> items)
    {
        WriteAsync(outputStream, items, null, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static async Task WriteAsync(
        Stream outputStream,
        IEnumerable<WriteItem> items,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        ArgumentNullException.ThrowIfNull(items);
        if (!outputStream.CanWrite) throw new ArgumentException("Output stream must be writable.", nameof(outputStream));
        if (outputStream.CanSeek && outputStream.Position != 0)
        {
            throw new ArgumentException("HAK output streams must be positioned at the beginning.", nameof(outputStream));
        }

        var itemList = items.ToList();

        HashSet<HakFormatKey> seenKeys = new();
        foreach (var item in itemList)
        {
            ArgumentNullException.ThrowIfNull(item.PayloadStream);
            if (!item.PayloadStream.CanRead)
            {
                throw new ArgumentException("Every HAK payload stream must be readable.", nameof(items));
            }

            ValidateKeyForWrite(item.Key);
            if (!seenKeys.Add(item.Key))
            {
                throw new InvalidOperationException($"Duplicate resource key found in HAK writer input: {Convert.ToHexString(item.Key.ResrefBytes.Span)}.{item.Key.ResourceType}");
            }
        }

        // Sort items by numeric type ascending, then canonical CP1252 resref bytes ascending
        itemList.Sort((a, b) =>
        {
            int typeComp = a.Key.ResourceType.CompareTo(b.Key.ResourceType);
            if (typeComp != 0) return typeComp;
            return a.Key.CanonicalResrefBytes.Span.SequenceCompareTo(b.Key.CanonicalResrefBytes.Span);
        });

        long entryCount = itemList.Count;
        const long headerSize = 160;
        long keyListOffset = headerSize;
        long keyListSize = checked(entryCount * 24);
        long resourceListOffset = checked(keyListOffset + keyListSize);
        long resourceListSize = checked(entryCount * 8);
        long payloadStartOffset = checked(resourceListOffset + resourceListSize);

        long totalEstimatedPayload = 0;
        foreach (var item in itemList)
        {
            totalEstimatedPayload = checked(totalEstimatedPayload + item.PayloadSize);
        }

        long outputSize = checked(payloadStartOffset + totalEstimatedPayload);
        if (outputSize >= LegacySingleHakLimit)
        {
            throw new InvalidOperationException($"HAK size {outputSize} exceeds single-HAK limit of {LegacySingleHakLimit} bytes.");
        }

        byte[] headerBuffer = new byte[headerSize];
        using (MemoryStream ms = new(headerBuffer))
        using (BinaryWriter bw = new(ms, Encoding.ASCII))
        {
            bw.Write(Encoding.ASCII.GetBytes("HAK "));
            bw.Write(Encoding.ASCII.GetBytes("V1.0"));
            bw.Write((uint)0); // LanguageCount
            bw.Write((uint)0); // LocalizedStringSize
            bw.Write(checked((uint)entryCount));
            bw.Write((uint)0); // OffsetToLocalizedString
            bw.Write(checked((uint)keyListOffset));
            bw.Write(checked((uint)resourceListOffset));
            bw.Write((uint)0); // BuildYear
            bw.Write((uint)0); // BuildDay
            bw.Write((uint)0); // DescriptionStrRef
        }

        await outputStream.WriteAsync(headerBuffer, cancellationToken).ConfigureAwait(false);

        // Write KeyList
        byte[] keyListBuffer = new byte[keyListSize];
        using (MemoryStream ms = new(keyListBuffer))
        using (BinaryWriter bw = new(ms, Encoding.ASCII))
        {
            for (int i = 0; i < entryCount; i++)
            {
                var item = itemList[i];
                byte[] resref16 = new byte[16];
                item.Key.ResrefBytes.Span.CopyTo(resref16);

                bw.Write(resref16);
                bw.Write((uint)i);
                bw.Write(item.Key.ResourceType);
                bw.Write((ushort)0);
            }
        }
        await outputStream.WriteAsync(keyListBuffer, cancellationToken).ConfigureAwait(false);

        // Write ResourceList
        byte[] resourceListBuffer = new byte[resourceListSize];
        long currentPayloadOffset = payloadStartOffset;
        using (MemoryStream ms = new(resourceListBuffer))
        using (BinaryWriter bw = new(ms, Encoding.ASCII))
        {
            for (int i = 0; i < entryCount; i++)
            {
                var item = itemList[i];
                bw.Write(checked((uint)currentPayloadOffset));
                bw.Write(item.PayloadSize);
                currentPayloadOffset = checked(currentPayloadOffset + item.PayloadSize);
            }
        }
        await outputStream.WriteAsync(resourceListBuffer, cancellationToken).ConfigureAwait(false);

        // Write Payloads using pooled buffer
        byte[] poolBuffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            for (int i = 0; i < entryCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = itemList[i];

                progress?.Report(i + 1);

                long remaining = item.PayloadSize;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(poolBuffer.Length, remaining);
                    int read = await item.PayloadStream.ReadAsync(poolBuffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException($"Payload stream for key {item.Key} ended unexpectedly before declared size {item.PayloadSize}.");
                    }
                    await outputStream.WriteAsync(poolBuffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(poolBuffer);
        }
    }

    private static void ValidateKeyForWrite(HakFormatKey key)
    {
        if (key.ResrefBytes.Length is < 1 or > 16)
        {
            throw new ArgumentException("HAK writer keys must contain 1-16 resref bytes.", nameof(key));
        }

        foreach (byte value in key.ResrefBytes.Span)
        {
            if (value is 0 or (byte)'/' or (byte)'\\')
            {
                throw new ArgumentException($"HAK writer key contains invalid byte 0x{value:X2}.", nameof(key));
            }
        }
    }
}
