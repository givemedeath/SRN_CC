using SRN.CC.Core.Sources;

namespace SRN.CC.Core.Fingerprints;

public sealed record SourceFingerprint
{
    public AssetSourceKind Kind { get; }
    public int AlgorithmVersion { get; }
    public ReadOnlyMemory<byte> Digest { get; }

    public SourceFingerprint(AssetSourceKind kind, int algorithmVersion, byte[] digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (digest.Length != 32)
        {
            throw new ArgumentException($"Fingerprint digest must be exactly 32 bytes, got {digest.Length}.", nameof(digest));
        }

        Kind = kind;
        AlgorithmVersion = algorithmVersion;
        Digest = digest.ToArray();
    }

    public SourceFingerprint(AssetSourceKind kind, int algorithmVersion, ReadOnlyMemory<byte> digest)
    {
        if (digest.Length != 32)
        {
            throw new ArgumentException($"Fingerprint digest must be exactly 32 bytes, got {digest.Length}.", nameof(digest));
        }

        Kind = kind;
        AlgorithmVersion = algorithmVersion;
        Digest = digest.ToArray();
    }

    public bool Equals(SourceFingerprint? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Kind == other.Kind &&
               AlgorithmVersion == other.AlgorithmVersion &&
               Digest.Span.SequenceEqual(other.Digest.Span);
    }

    public override int GetHashCode()
    {
        HashCode hc = new();
        hc.Add(Kind);
        hc.Add(AlgorithmVersion);
        hc.AddBytes(Digest.Span);
        return hc.ToHashCode();
    }

    public string ToHexString() => Convert.ToHexStringLower(Digest.Span);
}
