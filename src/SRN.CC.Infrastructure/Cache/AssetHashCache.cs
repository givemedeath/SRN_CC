using System.Collections.Concurrent;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;

namespace SRN.CC.Infrastructure.Cache;

public sealed class AssetHashCache
{
    private readonly ConcurrentDictionary<CacheKey, byte[]> _cache = new();

    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        public Guid SourceId { get; }
        public AssetIdentity Identity { get; }
        public OccurrenceLocator Locator { get; }
        public long Size { get; }
        public SourceFingerprint Fingerprint { get; }

        public CacheKey(Guid sourceId, AssetIdentity identity, OccurrenceLocator locator, long size, SourceFingerprint fingerprint)
        {
            SourceId = sourceId;
            Identity = identity;
            Locator = locator;
            Size = size;
            Fingerprint = fingerprint;
        }

        public bool Equals(CacheKey other)
        {
            return SourceId.Equals(other.SourceId) &&
                   Identity.Equals(other.Identity) &&
                   Locator.Equals(other.Locator) &&
                   Size == other.Size &&
                   Fingerprint.Equals(other.Fingerprint);
        }

        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

        public override int GetHashCode()
        {
            HashCode hc = new();
            hc.Add(SourceId);
            hc.Add(Identity);
            hc.Add(Locator);
            hc.Add(Size);
            hc.Add(Fingerprint);
            return hc.ToHashCode();
        }
    }

    public bool TryGetHash(
        Guid sourceId,
        AssetIdentity identity,
        OccurrenceLocator locator,
        long size,
        SourceFingerprint? fingerprint,
        out byte[] sha256)
    {
        if (fingerprint is null)
        {
            sha256 = Array.Empty<byte>();
            return false;
        }

        var key = new CacheKey(sourceId, identity, locator, size, fingerprint);
        if (_cache.TryGetValue(key, out byte[]? cached))
        {
            sha256 = cached.ToArray();
            return true;
        }
        sha256 = Array.Empty<byte>();
        return false;
    }

    public void PutHash(
        Guid sourceId,
        AssetIdentity identity,
        OccurrenceLocator locator,
        long size,
        SourceFingerprint? fingerprint,
        byte[] sha256)
    {
        if (fingerprint is null) return;
        if (sha256.Length != 32) throw new ArgumentException("SHA256 must be 32 bytes.", nameof(sha256));

        var key = new CacheKey(sourceId, identity, locator, size, fingerprint);
        _cache[key] = sha256.ToArray();
    }

    public void InvalidateSource(Guid sourceId)
    {
        var keysToRemove = _cache.Keys.Where(k => k.SourceId == sourceId).ToList();
        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
        }
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
