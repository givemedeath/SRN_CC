namespace SRN.CC.Formats.Hak;

public sealed class OwnedBoundedStream : Stream
{
    private readonly Stream _baseStream;
    private readonly long _startOffset;
    private readonly long _length;
    private readonly bool _ownsStream;
    private long _position;

    public OwnedBoundedStream(Stream baseStream, long startOffset, long length, bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        if (!baseStream.CanRead) throw new ArgumentException("Base stream must be readable.", nameof(baseStream));
        if (!baseStream.CanSeek) throw new ArgumentException("Base stream must be seekable.", nameof(baseStream));
        if (startOffset < 0) throw new ArgumentOutOfRangeException(nameof(startOffset), "Start offset cannot be negative.");
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
        if (checked(startOffset + length) > baseStream.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Bounded range extends past base stream length.");
        }

        _baseStream = baseStream;
        _startOffset = startOffset;
        _length = length;
        _ownsStream = ownsStream;
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
            if (value < 0 || value > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Position {value} out of range [0, {_length}].");
            }
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (_position >= _length) return 0;
        int bytesToRead = (int)Math.Min(count, _length - _position);

        if (_ownsStream)
        {
            _baseStream.Position = _startOffset + _position;
            int read = _baseStream.Read(buffer, offset, bytesToRead);
            _position += read;
            return read;
        }
        else
        {
            lock (_baseStream)
            {
                _baseStream.Position = _startOffset + _position;
                int read = _baseStream.Read(buffer, offset, bytesToRead);
                _position += read;
                return read;
            }
        }
    }

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length) return 0;
        int bytesToRead = (int)Math.Min(buffer.Length, _length - _position);

        if (_ownsStream)
        {
            _baseStream.Position = _startOffset + _position;
            int read = _baseStream.Read(buffer[..bytesToRead]);
            _position += read;
            return read;
        }
        else
        {
            lock (_baseStream)
            {
                _baseStream.Position = _startOffset + _position;
                int read = _baseStream.Read(buffer[..bytesToRead]);
                _position += read;
                return read;
            }
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (_position >= _length) return 0;
        int bytesToRead = (int)Math.Min(count, _length - _position);

        _baseStream.Position = _startOffset + _position;
        int read = await _baseStream.ReadAsync(buffer.AsMemory(offset, bytesToRead), cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= _length) return 0;
        int bytesToRead = (int)Math.Min(buffer.Length, _length - _position);

        _baseStream.Position = _startOffset + _position;
        int read = await _baseStream.ReadAsync(buffer[..bytesToRead], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsStream)
        {
            _baseStream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_ownsStream)
        {
            await _baseStream.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
