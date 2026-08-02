using System.Text;

namespace SRN.CC.Core.Identity;

/// <summary>
/// Immutable canonical identity for a Neverwinter Nights resource asset.
/// Identity is defined by a 1-16 byte Windows-1252 canonical resref and a ushort ResourceType.
/// </summary>
public sealed class AssetIdentity : IEquatable<AssetIdentity>
{
    private static readonly Encoding Windows1252;

    static AssetIdentity()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public string OriginalName { get; }
    public ushort ResourceType { get; }
    public byte[] CanonicalResrefBytes { get; }

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

        CanonicalResrefBytes = ValidateAndCanonicalize(rawBytes);
    }

    public AssetIdentity(ReadOnlySpan<byte> resrefBytes, ushort resourceType)
    {
        ResourceType = resourceType;
        CanonicalResrefBytes = ValidateAndCanonicalize(resrefBytes);

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
        // Trim trailing NULs or whitespace if present, but require 1..16 length
        int length = input.Length;
        while (length > 0 && input[length - 1] == 0)
        {
            length--;
        }

        if (length is < 1 or > 16)
        {
            throw new ArgumentException($"Resref byte length must be between 1 and 16 bytes. Got {length}.", nameof(input));
        }

        byte[] canonical = new byte[length];
        for (int i = 0; i < length; i++)
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
        return CanonicalResrefBytes.AsSpan().SequenceEqual(other.CanonicalResrefBytes);
    }

    public override bool Equals(object? obj) => Equals(obj as AssetIdentity);

    public override int GetHashCode()
    {
        HashCode hc = new();
        hc.Add(ResourceType);
        hc.AddBytes(CanonicalResrefBytes);
        return hc.ToHashCode();
    }

    public override string ToString() => $"{OriginalName}.{ResourceType}";
}
