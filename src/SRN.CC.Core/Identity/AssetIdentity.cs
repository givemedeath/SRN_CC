using System.Text;

namespace SRN.CC.Core.Identity;

/// <summary>
/// Immutable canonical identity for a Neverwinter Nights resource asset.
/// Identity is defined by a 1-16 byte Windows-1252 canonical resref and a ushort ResourceType.
/// </summary>
public sealed class AssetIdentity : IEquatable<AssetIdentity>
{
    private static readonly Encoding Windows1252;
    private readonly byte[] _canonicalResrefBytes;
    private readonly byte[] _originalResrefBytes;

    static AssetIdentity()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public string OriginalName { get; }
    public string Resref => OriginalName;
    public ushort ResourceType { get; }
    public ReadOnlyMemory<byte> CanonicalResrefBytes => _canonicalResrefBytes;
    public ReadOnlyMemory<byte> OriginalResrefBytes => _originalResrefBytes;

    public AssetIdentity(string resref, ushort resourceType)
    {
        ArgumentNullException.ThrowIfNull(resref);

        OriginalName = resref;
        ResourceType = resourceType;

        byte[] rawBytes;
        try
        {
            rawBytes = Windows1252.GetBytes(resref);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException($"Resref '{resref}' contains characters unencodable in CP1252.", nameof(resref), ex);
        }

        _originalResrefBytes = rawBytes.ToArray();
        _canonicalResrefBytes = ValidateAndCanonicalize(rawBytes);
    }

    public AssetIdentity(ReadOnlySpan<byte> resrefBytes, ushort resourceType)
    {
        ResourceType = resourceType;
        _originalResrefBytes = resrefBytes.ToArray();
        _canonicalResrefBytes = ValidateAndCanonicalize(resrefBytes);

        try
        {
            OriginalName = Windows1252.GetString(resrefBytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ArgumentException("Resref byte sequence is not valid CP1252.", nameof(resrefBytes), ex);
        }
    }

    private static byte[] ValidateAndCanonicalize(ReadOnlySpan<byte> input)
    {
        if (input.Length is < 1 or > 16)
        {
            throw new ArgumentException($"Resref byte length must be between 1 and 16 bytes. Got {input.Length}.", nameof(input));
        }

        byte[] canonical = new byte[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            if (b is 0 or (byte)'/' or (byte)'\\')
            {
                throw new ArgumentException($"Resref contains invalid character/byte (0x{b:X2}).", nameof(input));
            }

            // ASCII case folding: 'A'-'Z' -> 'a'-'z'
            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                b = (byte)(b + ((byte)'a' - (byte)'A'));
            }

            canonical[i] = b;
        }

        return canonical;
    }

    public bool Equals(AssetIdentity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (ResourceType != other.ResourceType) return false;
        return _canonicalResrefBytes.AsSpan().SequenceEqual(other._canonicalResrefBytes);
    }

    public override bool Equals(object? obj) => Equals(obj as AssetIdentity);

    public override int GetHashCode()
    {
        HashCode hc = new();
        hc.Add(ResourceType);
        hc.AddBytes(_canonicalResrefBytes);
        return hc.ToHashCode();
    }

    public override string ToString() => $"{OriginalName}.{ResourceType}";
}
