using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;

namespace SRN.CC.Core.Resolution;

public sealed record WinnerPin
{
    private readonly byte[] _pinHash;

    public AssetIdentity Identity { get; }
    public Guid SourceId { get; }
    public OccurrenceLocator Locator { get; }
    public byte[] PinHash => _pinHash.ToArray();
    public ReadOnlyMemory<byte> PinHashMemory => _pinHash;

    public WinnerPin(AssetIdentity identity, Guid sourceId, OccurrenceLocator locator, byte[] pinHash)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(pinHash);

        if (pinHash.Length != 32)
        {
            throw new ArgumentException($"PinHash must be exactly 32 bytes (SHA-256). Got {pinHash.Length} bytes.", nameof(pinHash));
        }

        Identity = identity;
        SourceId = sourceId;
        Locator = locator;
        _pinHash = pinHash.ToArray();
    }

    public bool Equals(WinnerPin? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Identity.Equals(other.Identity) &&
               SourceId.Equals(other.SourceId) &&
               Locator.Equals(other.Locator) &&
               _pinHash.AsSpan().SequenceEqual(other._pinHash);
    }

    public override int GetHashCode()
    {
        HashCode hc = new();
        hc.Add(Identity);
        hc.Add(SourceId);
        hc.Add(Locator);
        hc.AddBytes(_pinHash);
        return hc.ToHashCode();
    }
}
