using System.Numerics;

namespace SRN.CC.Preview.Render;

/// <summary>
/// One drawable mesh within a <see cref="RenderScene"/> — either an artwork trimesh or a walkmesh
/// partition (<see cref="IsWalkmesh"/>). Declaration only in this slice; populated by
/// <see cref="IMdlSceneBuilder"/> (slice S6b).
/// </summary>
public sealed class RenderMesh
{
    public required string NodeName { get; init; }

    /// <summary>Rest-pose world transform, composed through ancestor nodes.</summary>
    public required Matrix4x4 WorldTransform { get; init; }

    /// <summary>xyz, length is 3 * vertex count.</summary>
    public required float[] Positions { get; init; }

    /// <summary>xyz, length is 3 * vertex count. May be generated when the source lacked normals.</summary>
    public required float[] Normals { get; init; }

    /// <summary>uv, length is 2 * vertex count.</summary>
    public required float[] TexCoords { get; init; }

    /// <summary><see cref="ushort"/> to stay within <see cref="ushort.MaxValue"/> vertices per MDL and avoid GL_OES_element_index_uint dependence.</summary>
    public required ushort[] Indices { get; init; }

    public required int MaterialIndex { get; init; }

    public required bool IsWalkmesh { get; init; }

    /// <summary>Length is Indices.Length / 3; empty for artwork meshes.</summary>
    public required int[] FaceSurfaceIds { get; init; }

    public required int TileFade { get; init; }
}
