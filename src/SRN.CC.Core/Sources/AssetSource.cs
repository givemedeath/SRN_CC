using SRN.CC.Core.Fingerprints;

namespace SRN.CC.Core.Sources;

public sealed record AssetSource
{
    public Guid Id { get; init; }
    public AssetSourceKind Kind { get; init; }
    public string FullPath { get; init; }
    public int PriorityOrdinal { get; init; }
    public bool IsAvailable { get; init; }
    public SourceFingerprint? Fingerprint { get; init; }
    public SourceMode Mode { get; init; }

    public AssetSource(
        Guid id,
        AssetSourceKind kind,
        string fullPath,
        int priorityOrdinal = 0,
        bool isAvailable = true,
        SourceFingerprint? fingerprint = null,
        SourceMode mode = SourceMode.Full)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        Id = id;
        Kind = kind;
        FullPath = Path.GetFullPath(fullPath);
        PriorityOrdinal = priorityOrdinal;
        IsAvailable = isAvailable;
        Fingerprint = fingerprint;
        Mode = mode;
    }

    public static AssetSource CreateHak(string fullPath, int priorityOrdinal = 0, Guid? id = null)
    {
        return new AssetSource(id ?? Guid.NewGuid(), AssetSourceKind.Hak, fullPath, priorityOrdinal);
    }

    public static AssetSource CreateFolder(string fullPath, int priorityOrdinal = 0, Guid? id = null)
    {
        return new AssetSource(id ?? Guid.NewGuid(), AssetSourceKind.Folder, fullPath, priorityOrdinal);
    }
}
