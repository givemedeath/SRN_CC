using SRN.CC.Core.Services;

namespace SRN.CC.Preview.Render;

/// <summary>
/// Decoded, GPU-ready texture image (BGRA8) attached to a <see cref="RenderMaterial"/>.
/// </summary>
public sealed record TextureImage(
    string Name,
    int Width,
    int Height,
    byte[] Bgra,
    bool HasAlpha,
    TextureOrigin Origin);

/// <summary>How a material's diffuse texture should be composited.</summary>
public enum MaterialBlendMode
{
    Opaque,
    AlphaTest,
    AlphaBlend,
}
