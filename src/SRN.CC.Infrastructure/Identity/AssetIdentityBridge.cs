using SRN.CC.Core.Identity;
using SRN.CC.Formats.Hak;

namespace SRN.CC.Infrastructure.Identity;

public static class AssetIdentityBridge
{
    public static AssetIdentity ToAssetIdentity(HakFormatKey formatKey)
    {
        return new AssetIdentity(formatKey.ResrefBytes, formatKey.ResourceType);
    }
}
