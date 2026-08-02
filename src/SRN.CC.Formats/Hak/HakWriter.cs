using System.Text;

namespace SRN.CC.Formats.Hak;

public sealed class HakWriter
{
    public record WriteItem(HakFormatKey Key, Stream PayloadStream, uint PayloadSize);

    public static void Write(Stream outputStream, IEnumerable<WriteItem> items)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        ArgumentNullException.ThrowIfNull(items);

        var itemList = items.ToList();

        // Check for duplicate keys
        HashSet<HakFormatKey> seenKeys = new();
        foreach (var item in itemList)
        {
            if (!seenKeys.Add(item.Key))
            {
                throw new InvalidOperationException($"Duplicate resource key found in HAK writer input: {Encoding.ASCII.GetString(item.Key.ResrefBytes)}.{item.Key.ResourceType}");
            }
        }

        // Sort items by numeric type, then CP1252 canonical resref bytes
        itemList.Sort((a, b) =>
        {
            int typeComp = a.Key.ResourceType.CompareTo(b.Key.ResourceType);
            if (typeComp != 0) return typeComp;
            return a.Key.ResrefBytes.AsSpan().SequenceCompareTo(b.Key.ResrefBytes.AsSpan());
        });

        uint entryCount = (uint)itemList.Count;
        uint headerSize = 160;
        uint keyListOffset = headerSize;
        uint keyListSize = entryCount * 24;
        uint resourceListOffset = keyListOffset + keyListSize;
        uint resourceListSize = entryCount * 8;
        uint payloadStartOffset = resourceListOffset + resourceListSize;

        // Verify size limits
        long totalEstimatedPayload = itemList.Sum(i => (long)i.PayloadSize);
        if (payloadStartOffset + totalEstimatedPayload > int.MaxValue)
        {
            throw new InvalidOperationException("HAK size exceeds single-HAK limit.");
        }

        using BinaryWriter writer = new(outputStream, Encoding.ASCII, leaveOpen: true);

        // Header (160 bytes)
        writer.Write(Encoding.ASCII.GetBytes("HAK "));
        writer.Write(Encoding.ASCII.GetBytes("V1.0"));
        writer.Write((uint)0); // LanguageCount = 0
        writer.Write((uint)0); // LocalizedStringSize = 0
        writer.Write(entryCount);
        writer.Write((uint)0); // OffsetToLocalizedString
        writer.Write(keyListOffset);
        writer.Write(resourceListOffset);
        writer.Write((uint)0); // BuildYear
        writer.Write((uint)0); // BuildDay
        writer.Write((uint)0); // DescriptionStrRef

        // 116 bytes reserved (zeros)
        writer.Write(new byte[116]);

        // Write KeyList
        uint currentPayloadOffset = payloadStartOffset;
        for (int i = 0; i < entryCount; i++)
        {
            var item = itemList[i];
            byte[] resref16 = new byte[16];
            Array.Copy(item.Key.ResrefBytes, resref16, Math.Min(16, item.Key.ResrefBytes.Length));

            writer.Write(resref16);
            writer.Write((uint)i); // ResourceId
            writer.Write(item.Key.ResourceType);
            writer.Write((ushort)0); // Unused
        }

        // Write ResourceList
        for (int i = 0; i < entryCount; i++)
        {
            var item = itemList[i];
            writer.Write(currentPayloadOffset);
            writer.Write(item.PayloadSize);
            currentPayloadOffset += item.PayloadSize;
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
}
