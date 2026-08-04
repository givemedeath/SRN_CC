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

/// <summary>
/// Where a resolved texture byte stream came from.
/// </summary>
/// <remarks>
/// Placeholder owned by slice S6a so <see cref="RenderMaterial"/>/<see cref="TextureImage"/> compile
/// standalone ahead of slice S7. Per architecture decision A7, the canonical
/// <c>TextureOrigin</c> concept belongs to <c>SRN.CC.Core.Services.ITextureSource</c>
/// (<c>src/SRN.CC.Core/Services/ITextureSource.cs</c>), which S7 owns. S7 should consolidate on a
/// single definition — either reuse this one and have Core depend on it being defined here, or
/// replace this placeholder with a reference/alias to the Core enum — rather than shipping two
/// divergent <c>TextureOrigin</c> types.
/// </remarks>
public enum TextureOrigin
{
    Workspace,
    BaseGame,
}
