namespace SRN.CC.Core.Services;

public interface IResourceTypeRegistry
{
    bool TryGetType(string extension, out ushort typeId);
    bool TryGetExtension(ushort typeId, out string extension);
}
