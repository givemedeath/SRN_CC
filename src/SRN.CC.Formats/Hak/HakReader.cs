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
            throw new InvalidDataException($"Unsupported ERF/HAK version '{Version}'.");
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

        // Validate table offsets and bounds
        long keyListSize = checked((long)EntryCount * 24); // 16 resref + 4 resId + 2 resType + 2 unused
        long resourceListSize = checked((long)EntryCount * 8); // 4 offset + 4 size

        if (EntryCount > int.MaxValue)
        {
            throw new InvalidDataException("HAK entry count exceeds the supported in-memory table size.");
        }

        if (OffsetToKeyList < 160 || OffsetToResourceList < 160)
        {
            throw new InvalidDataException("HAK table offsets overlap the fixed header.");
        }

        if (LocalizedStringSize > 0 &&
            (OffsetToLocalizedString < 160 || checked((long)OffsetToLocalizedString + LocalizedStringSize) > streamLength))
        {
            throw new InvalidDataException("HAK localized-string data extends past the file bounds.");
        }

        if (OffsetToKeyList + keyListSize > streamLength)
        {
            throw new InvalidDataException("HAK KeyList table extends past the end of the file.");
        }

        if (OffsetToResourceList + resourceListSize > streamLength)
        {
            throw new InvalidDataException("HAK ResourceList table extends past the end of the file.");
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

            if ((long)offset + size > streamLength)
            {
                throw new InvalidDataException($"Resource entry {i} payload extends past file length.");
            }

            resourceRecords[i] = (offset, size);
        }

        foreach (var keyRecord in keyRecords)
        {
            if (keyRecord.ResId >= EntryCount)
            {
                throw new InvalidDataException($"HAK key references invalid resource index {keyRecord.ResId} for {EntryCount} resources.");
            }

            var resource = resourceRecords[checked((int)keyRecord.ResId)];
            HakFormatKey key = new(keyRecord.Resref, keyRecord.ResType);
            _entries.Add(new HakEntry(key, keyRecord.ResId, resource.Offset, resource.Size));
        }
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

        return new BoundedSubStream(sourceStream, entry.OffsetToResource, entry.ResourceSize);
    }

    private sealed class BoundedSubStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _startOffset;
        private readonly long _length;
        private long _position;

        public BoundedSubStream(Stream baseStream, long startOffset, long length)
        {
            _baseStream = baseStream;
            _startOffset = startOffset;
            _length = length;
            _position = 0;
        }

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _length) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length) return 0;
            int bytesToRead = (int)Math.Min(count, _length - _position);

            lock (_baseStream)
            {
                _baseStream.Position = _startOffset + _position;
                int read = _baseStream.Read(buffer, offset, bytesToRead);
                _position += read;
                return read;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = newPos;
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
