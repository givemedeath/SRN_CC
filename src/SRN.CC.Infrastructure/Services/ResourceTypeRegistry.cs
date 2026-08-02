using SRN.CC.Core.Services;
using SWLOR.NWN.Formats.Common;

namespace SRN.CC.Infrastructure.Services;

public sealed class ResourceTypeRegistry : IResourceTypeRegistry
{
    public bool TryGetType(string extension, out ushort typeId)
    {
        return ResourceTypes.TryGetType(extension, out typeId);
    }

    public bool TryGetExtension(ushort typeId, out string extension)
    {
        return ResourceTypes.TryGetExtension(typeId, out extension);
    }
}
