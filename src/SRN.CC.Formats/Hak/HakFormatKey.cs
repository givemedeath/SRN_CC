namespace SRN.CC.Formats.Hak;

/// <summary>
/// Representation of a raw HAK/ERF resource key (16-byte resref buffer plus resource type).
/// Keeps Formats independent of Core's AssetIdentity.
/// </summary>
public readonly struct HakFormatKey : IEquatable<HakFormatKey>
{
    private readonly byte[]? _resrefBytes;
    private readonly byte[]? _canonicalResrefBytes;
    private readonly byte[]? _rawResrefField;

    public ReadOnlyMemory<byte> ResrefBytes => _resrefBytes ?? Array.Empty<byte>();
    public ReadOnlyMemory<byte> CanonicalResrefBytes => _canonicalResrefBytes ?? Array.Empty<byte>();
    public ReadOnlyMemory<byte> RawResrefField => _rawResrefField ?? Array.Empty<byte>();
    public ushort ResourceType { get; }

    public HakFormatKey(ReadOnlySpan<byte> resrefBytes, ushort resourceType)
    {
        if (resrefBytes.Length is < 1 or > 16)
        {
            throw new ArgumentException($"HAK resref fields must contain 1-16 bytes. Got {resrefBytes.Length}.", nameof(resrefBytes));
        }

        _rawResrefField = new byte[16];
        resrefBytes.CopyTo(_rawResrefField);

        int length = resrefBytes.Length;
        while (length > 0 && resrefBytes[length - 1] == 0)
        {
            length--;
        }

        if (length == 0)
        {
            throw new ArgumentException("HAK resrefs cannot be empty.", nameof(resrefBytes));
        }

        _resrefBytes = resrefBytes[..length].ToArray();
        _canonicalResrefBytes = _resrefBytes.ToArray();
        for (int i = 0; i < _canonicalResrefBytes.Length; i++)
        {
            byte value = _canonicalResrefBytes[i];
            if (value == 0)
            {
                throw new ArgumentException("HAK resrefs cannot contain embedded NUL bytes.", nameof(resrefBytes));
            }

            if (value is >= (byte)'A' and <= (byte)'Z')
            {
                _canonicalResrefBytes[i] = (byte)(value + ((byte)'a' - (byte)'A'));
            }
        }

        ResourceType = resourceType;
    }

    public bool Equals(HakFormatKey other)
    {
        if (ResourceType != other.ResourceType) return false;
        return CanonicalResrefBytes.Span.SequenceEqual(other.CanonicalResrefBytes.Span);
    }

    public override bool Equals(object? obj) => obj is HakFormatKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hc = new();
        hc.Add(ResourceType);
        hc.AddBytes(CanonicalResrefBytes.Span);
        return hc.ToHashCode();
    }
}
