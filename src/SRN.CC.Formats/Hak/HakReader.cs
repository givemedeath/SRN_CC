using System.Text;

namespace SRN.CC.Formats.Hak;

public sealed class HakReader
{
    public string FileType { get; }
    public string Version { get; }
    public uint LanguageCount { get; }
    public uint LocalizedStringSize { get; }
    public uint EntryCount { get; }
    public uint OffsetToLocalizedString { get; }
    public uint OffsetToKeyList { get; }
    public uint OffsetToResourceList { get; }
    public uint BuildYear { get; }
    public uint BuildDay { get; }
    public uint DescriptionStrRef { get; }

    private readonly List<HakEntry> _entries = new();
    public IReadOnlyList<HakEntry> Entries => _entries;

    public HakReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

        long streamLength = stream.Length;
        if (streamLength < 160)
        {
            throw new InvalidDataException("HAK file header is smaller than 160 bytes.");
        }

        using BinaryReader reader = new(stream, Encoding.ASCII, leaveOpen: true);

        FileType = Encoding.ASCII.GetString(ReadExactly(reader, 4));
        Version = Encoding.ASCII.GetString(ReadExactly(reader, 4));

        if (FileType != "HAK ")
        {
            throw new InvalidDataException($"Unsupported ERF file type '{FileType}'; expected 'HAK '.");
        }

        if (Version != "V1.0")
        {
            throw new InvalidDataException($"Unsupported ERF/HAK version '{Version}'; expected 'V1.0'.");
        }

        LanguageCount = reader.ReadUInt32();
        LocalizedStringSize = reader.ReadUInt32();
        EntryCount = reader.ReadUInt32();
        OffsetToLocalizedString = reader.ReadUInt32();
        OffsetToKeyList = reader.ReadUInt32();
        OffsetToResourceList = reader.ReadUInt32();
        BuildYear = reader.ReadUInt32();
        BuildDay = reader.ReadUInt32();
        DescriptionStrRef = reader.ReadUInt32();

        if (EntryCount > int.MaxValue)
        {
            throw new InvalidDataException("HAK entry count exceeds the supported in-memory table size.");
        }

        long keyListSize = checked((long)EntryCount * 24); // 16 resref + 4 resId + 2 resType + 2 unused
        long resourceListSize = checked((long)EntryCount * 8); // 4 offset + 4 size

        // Validate table offsets and bounds
        if (OffsetToKeyList < 160 || OffsetToResourceList < 160)
        {
            throw new InvalidDataException("HAK table offsets overlap the fixed header.");
        }

        if (LocalizedStringSize > 0 &&
            (OffsetToLocalizedString < 160 || checked((long)OffsetToLocalizedString + LocalizedStringSize) > streamLength))
        {
            throw new InvalidDataException("HAK localized-string data extends past the file bounds.");
        }

        if (checked((long)OffsetToKeyList + keyListSize) > streamLength)
        {
            throw new InvalidDataException("HAK KeyList table extends past the end of the file.");
        }

        if (checked((long)OffsetToResourceList + resourceListSize) > streamLength)
        {
            throw new InvalidDataException("HAK ResourceList table extends past the end of the file.");
        }

        // Validate metadata non-overlap
        List<(long Start, long Size, string Name)> metaRanges = new()
        {
            (0, 160, "Header"),
            (OffsetToKeyList, keyListSize, "KeyList"),
            (OffsetToResourceList, resourceListSize, "ResourceList")
        };
        if (LocalizedStringSize > 0)
        {
            metaRanges.Add((OffsetToLocalizedString, LocalizedStringSize, "LocalizedString"));
        }

        for (int i = 0; i < metaRanges.Count; i++)
        {
            for (int j = i + 1; j < metaRanges.Count; j++)
            {
                if (RangesOverlap(metaRanges[i].Start, metaRanges[i].Size, metaRanges[j].Start, metaRanges[j].Size))
                {
                    throw new InvalidDataException($"HAK metadata table {metaRanges[i].Name} overlaps {metaRanges[j].Name}.");
                }
            }
        }

        // Validate localized string records if present
        if (LocalizedStringSize > 0)
        {
            stream.Position = OffsetToLocalizedString;
            long bytesRead = 0;
            for (int i = 0; i < LanguageCount; i++)
            {
                if (bytesRead + 8 > LocalizedStringSize)
                {
                    throw new InvalidDataException("HAK localized-string table truncated before entry header.");
                }
                uint langId = reader.ReadUInt32();
                uint strSize = reader.ReadUInt32();
                bytesRead += 8;

                if (bytesRead + strSize > LocalizedStringSize)
                {
                    throw new InvalidDataException("HAK localized-string entry length extends past declared LocalizedStringSize.");
                }
                reader.ReadBytes(checked((int)strSize));
                bytesRead += strSize;
            }
        }

        // Read KeyList and ResourceList
        stream.Position = OffsetToKeyList;
        int entryCount = checked((int)EntryCount);
        var keyRecords = new (byte[] Resref, uint ResId, ushort ResType)[entryCount];
        for (int i = 0; i < EntryCount; i++)
        {
            byte[] resref = ReadExactly(reader, 16);
            uint resId = reader.ReadUInt32();
            ushort resType = reader.ReadUInt16();
            reader.ReadUInt16(); // Unused padding

            keyRecords[i] = (resref, resId, resType);
        }

        stream.Position = OffsetToResourceList;
        var resourceRecords = new (uint Offset, uint Size)[entryCount];
        for (int i = 0; i < EntryCount; i++)
        {
            uint offset = reader.ReadUInt32();
            uint size = reader.ReadUInt32();

            if (checked((long)offset + size) > streamLength)
            {
                throw new InvalidDataException($"Resource entry {i} payload extends past file length.");
            }

            resourceRecords[i] = (offset, size);
        }

        for (int i = 0; i < entryCount; i++)
        {
            var keyRecord = keyRecords[i];
            if (keyRecord.ResId >= EntryCount)
            {
                throw new InvalidDataException($"HAK key references invalid resource index {keyRecord.ResId} for {EntryCount} resources.");
            }

            var resource = resourceRecords[checked((int)keyRecord.ResId)];

            // Ensure payload does not overlap metadata
            if (resource.Size > 0)
            {
                foreach (var meta in metaRanges)
                {
                    if (RangesOverlap(resource.Offset, resource.Size, meta.Start, meta.Size))
                    {
                        throw new InvalidDataException($"Payload entry {i} overlaps metadata table {meta.Name}.");
                    }
                }
            }

            HakFormatKey key = new(keyRecord.Resref, keyRecord.ResType);
            _entries.Add(new HakEntry(key, keyRecord.ResId, resource.Offset, resource.Size));
        }

        // Validate payload partial overlaps
        for (int i = 0; i < _entries.Count; i++)
        {
            var e1 = _entries[i];
            if (e1.ResourceSize == 0) continue;

            for (int j = i + 1; j < _entries.Count; j++)
            {
                var e2 = _entries[j];
                if (e2.ResourceSize == 0) continue;

                if (RangesOverlap(e1.OffsetToResource, e1.ResourceSize, e2.OffsetToResource, e2.ResourceSize))
                {
                    // Exact shared ranges are allowed, partial overlaps are invalid
                    if (!(e1.OffsetToResource == e2.OffsetToResource && e1.ResourceSize == e2.ResourceSize))
                    {
                        throw new InvalidDataException($"Payload entry {i} partially overlaps payload entry {j}.");
                    }
                }
            }
        }
    }

    private static bool RangesOverlap(long startA, long sizeA, long startB, long sizeB)
    {
        if (sizeA <= 0 || sizeB <= 0) return false;
        return startA < startB + sizeB && startB < startA + sizeA;
    }

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new EndOfStreamException($"Expected {count} bytes but only read {bytes.Length}.");
        }

        return bytes;
    }

    public static Stream OpenPayloadStream(HakEntry entry, string hakPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(hakPath);

        FileStream fileStream = new(hakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new OwnedBoundedStream(fileStream, entry.OffsetToResource, entry.ResourceSize, ownsStream: true);
        }
        catch
        {
            fileStream.Dispose();
            throw;
        }
    }

    public static Stream OpenPayloadStream(HakEntry entry, Stream sourceStream)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(sourceStream);
        if (!sourceStream.CanRead || !sourceStream.CanSeek)
        {
            throw new ArgumentException("Payload source streams must be readable and seekable.", nameof(sourceStream));
        }

        if (checked((long)entry.OffsetToResource + entry.ResourceSize) > sourceStream.Length)
        {
            throw new InvalidDataException("The payload range extends past the source stream.");
        }

        return new OwnedBoundedStream(sourceStream, entry.OffsetToResource, entry.ResourceSize, ownsStream: false);
    }
}
