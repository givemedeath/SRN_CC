namespace SRN.CC.Formats.Hak;

/// <summary>
/// Represents an entry within a HAK file.
/// </summary>
public sealed class HakEntry
{
    public HakFormatKey Key { get; }
    public uint ResourceId { get; }
    public uint OffsetToResource { get; }
    public uint ResourceSize { get; }

    public HakEntry(HakFormatKey key, uint resourceId, uint offsetToResource, uint resourceSize)
    {
        Key = key;
        ResourceId = resourceId;
        OffsetToResource = offsetToResource;
        ResourceSize = resourceSize;
    }
}
