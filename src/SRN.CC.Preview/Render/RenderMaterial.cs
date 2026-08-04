using System.Numerics;

namespace SRN.CC.Preview.Render;

/// <summary>
/// A deduplicated material referenced by index from <see cref="RenderMesh.MaterialIndex"/>.
/// Declaration only in this slice; populated by <see cref="IMdlSceneBuilder"/> (slice S6b) using
/// textures resolved through <c>ITextureSource</c> (slice S7).
/// </summary>
public sealed class RenderMaterial
{
    /// <summary>Lowercased; empty when untextured.</summary>
    public required string Name { get; init; }

    /// <summary>From <c>MdlTrimeshNode.Diffuse</c> — do not drop even when a texture is present.</summary>
    public required Vector3 DiffuseColor { get; init; }

    public required TextureImage? Diffuse { get; init; }

    public required TextureImage? Lightmap { get; init; }

    /// <summary>Resolved name only; environment maps are reported, never shaded (decision A7/scope).</summary>
    public required string? EnvironmentMapName { get; init; }

    public required MaterialBlendMode BlendMode { get; init; }

    public required float AlphaTestThreshold { get; init; }

    public required IReadOnlyList<string> UnresolvedTextures { get; init; }
}
