using System.Collections.Concurrent;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Services;
using SWLOR.NWN.Formats.Bif;
using SWLOR.NWN.Formats.Key;

namespace SRN.CC.Infrastructure.Catalog;

public sealed class BaseGameResourceCatalog : IBaseGameResourceCatalog
{
    private readonly string _installRoot;
    private readonly KeyFile _keyFile;
    private readonly Dictionary<AssetIdentity, KeyResourceEntry> _identityIndex;
    private readonly ConcurrentDictionary<int, BifFile> _bifCache = new();

    public string InstallRoot => _installRoot;
    public KeyFile KeyFile => _keyFile;

    public BaseGameResourceCatalog(string installRoot, string? nwnBaseKeyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        _installRoot = Path.GetFullPath(installRoot);

        string keyPath = nwnBaseKeyPath ?? Path.Combine(_installRoot, "data", "nwn_base.key");
        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException($"NWN base key file not found at '{keyPath}'.", keyPath);
        }

        _keyFile = KeyReader.Read(keyPath);
        _identityIndex = new Dictionary<AssetIdentity, KeyResourceEntry>();

        foreach (var entry in _keyFile.ResourceEntries)
        {
            try
            {
                AssetIdentity identity = new(entry.RawResRef, entry.ResourceType);
                _identityIndex.TryAdd(identity, entry);
            }
            catch (ArgumentException)
            {
                // Ignore malformed resrefs in key file
            }
        }
    }

    public bool Contains(AssetIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _identityIndex.ContainsKey(identity);
    }

    public Task<Stream> OpenAsync(AssetIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!_identityIndex.TryGetValue(identity, out var keyResource))
        {
            throw new KeyNotFoundException($"Resource identity '{identity}' not found in base-game catalog.");
        }

        if (keyResource.BifIndex < 0 || keyResource.BifIndex >= _keyFile.BifEntries.Count)
        {
            throw new InvalidDataException($"KEY resource references invalid BIF index {keyResource.BifIndex}.");
        }

        var bifEntry = _keyFile.BifEntries[keyResource.BifIndex];
        string rootPrefix = _installRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string bifPath = Path.GetFullPath(Path.Combine(_installRoot, bifEntry.Filename));

        if (!bifPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"BIF path '{bifEntry.Filename}' escapes install root boundary.");
        }

        if (!File.Exists(bifPath))
        {
            throw new FileNotFoundException($"Declared BIF file not found at '{bifPath}'.", bifPath);
        }

        BifFile bifFile = _bifCache.GetOrAdd(keyResource.BifIndex, _ => BifReader.ReadMetadataOnly(bifPath));
        if (keyResource.VariableTableIndex < 0 || keyResource.VariableTableIndex >= bifFile.VariableResources.Count)
        {
            throw new InvalidDataException($"KEY resource references invalid BIF variable index {keyResource.VariableTableIndex} for BIF with {bifFile.VariableResources.Count} entries.");
        }

        var bifResource = bifFile.VariableResources[keyResource.VariableTableIndex];
        Stream stream = BifReader.OpenPayloadStream(bifResource, bifPath);
        return Task.FromResult(stream);
    }
}
