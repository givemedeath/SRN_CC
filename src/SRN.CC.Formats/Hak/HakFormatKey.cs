namespace SRN.CC.Formats.Hak;

/// <summary>
/// Representation of a raw HAK/ERF resource key (16-byte resref buffer plus resource type).
/// Keeps Formats independent of Core's AssetIdentity.
/// </summary>
public readonly struct HakFormatKey : IEquatable<HakFormatKey>
{
    public byte[] ResrefBytes { get; }
    public ushort ResourceType { get; }

    public HakFormatKey(ReadOnlySpan<byte> resrefBytes, ushort resourceType)
    {
        int length = resrefBytes.Length;
        while (length > 0 && resrefBytes[length - 1] == 0)
        {
            length--;
        }

        ResrefBytes = resrefBytes[..length].ToArray();
        ResourceType = resourceType;
    }

    public bool Equals(HakFormatKey other)
    {
        if (ResourceType != other.ResourceType) return false;
        return ResrefBytes.AsSpan().SequenceEqual(other.ResrefBytes);
    }

    public override bool Equals(object? obj) => obj is HakFormatKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hc = new();
        hc.Add(ResourceType);
        hc.AddBytes(ResrefBytes);
        return hc.ToHashCode();
    }
}
