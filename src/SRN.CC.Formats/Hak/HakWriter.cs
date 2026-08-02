using System.Text;

namespace SRN.CC.Formats.Hak;

public sealed class HakWriter
{
    public const long LegacySingleHakLimit = 2L * 1024 * 1024 * 1024;

    public record WriteItem(HakFormatKey Key, Stream PayloadStream, uint PayloadSize);

    public static void Write(Stream outputStream, IEnumerable<WriteItem> items)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        ArgumentNullException.ThrowIfNull(items);
        if (!outputStream.CanWrite) throw new ArgumentException("Output stream must be writable.", nameof(outputStream));
        if (outputStream.CanSeek && outputStream.Position != 0)
        {
            throw new ArgumentException("HAK output streams must be positioned at the beginning.", nameof(outputStream));
        }

        var itemList = items.ToList();

        // Check for duplicate keys
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

        // Sort items by numeric type, then CP1252 canonical resref bytes
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

        // Verify size limits
        long totalEstimatedPayload = 0;
        foreach (var item in itemList)
        {
            totalEstimatedPayload = checked(totalEstimatedPayload + item.PayloadSize);
        }

        long outputSize = checked(payloadStartOffset + totalEstimatedPayload);
        if (outputSize >= LegacySingleHakLimit)
        {
            throw new InvalidOperationException("HAK size exceeds single-HAK limit.");
        }

        using BinaryWriter writer = new(outputStream, Encoding.ASCII, leaveOpen: true);

        // Header (160 bytes)
        writer.Write(Encoding.ASCII.GetBytes("HAK "));
        writer.Write(Encoding.ASCII.GetBytes("V1.0"));
        writer.Write((uint)0); // LanguageCount = 0
        writer.Write((uint)0); // LocalizedStringSize = 0
        writer.Write(checked((uint)entryCount));
        writer.Write((uint)0); // OffsetToLocalizedString
        writer.Write(checked((uint)keyListOffset));
        writer.Write(checked((uint)resourceListOffset));
        writer.Write((uint)0); // BuildYear
        writer.Write((uint)0); // BuildDay
        writer.Write((uint)0); // DescriptionStrRef

        // 116 bytes reserved (zeros)
        writer.Write(new byte[116]);

        // Write KeyList
        long currentPayloadOffset = payloadStartOffset;
        for (int i = 0; i < entryCount; i++)
        {
            var item = itemList[i];
            byte[] resref16 = new byte[16];
            item.Key.ResrefBytes.Span.CopyTo(resref16);

            writer.Write(resref16);
            writer.Write((uint)i); // ResourceId
            writer.Write(item.Key.ResourceType);
            writer.Write((ushort)0); // Unused
        }

        // Write ResourceList
        for (int i = 0; i < entryCount; i++)
        {
            var item = itemList[i];
            writer.Write(checked((uint)currentPayloadOffset));
            writer.Write(item.PayloadSize);
            currentPayloadOffset = checked(currentPayloadOffset + item.PayloadSize);
        }

        // Write Payloads
        byte[] buffer = new byte[8192];
        foreach (var item in itemList)
        {
            long remaining = item.PayloadSize;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = item.PayloadStream.Read(buffer, 0, toRead);
                if (read == 0) throw new EndOfStreamException("Payload stream ended unexpectedly.");
                writer.Write(buffer, 0, read);
                remaining -= read;
            }
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
