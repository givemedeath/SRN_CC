using System.Numerics;

namespace SRN.CC.Preview.Render;

/// <summary>
/// CPU-side, GPU-agnostic description of a renderable MDL scene: geometry, materials, bounds, and
/// degradation diagnostics. Produced by <see cref="IMdlSceneBuilder"/> (slice S6b) and consumed by
/// the GL renderer (slice S8) and <c>ModelViewportViewModel</c> (slice S11). Declaration only in
/// this slice — no builder logic lives here.
/// </summary>
public sealed class RenderScene
{
    public required string ModelName { get; init; }

    public required string SuperModel { get; init; }

    public required bool IsAsciiSource { get; init; }

    public required Vector3 BoundsMinimum { get; init; }

    public required Vector3 BoundsMaximum { get; init; }

    public required float Radius { get; init; }

    public required IReadOnlyList<RenderMesh> ArtworkMeshes { get; init; }

    public required IReadOnlyList<RenderMesh> WalkmeshMeshes { get; init; }

    /// <summary>Indexed by <see cref="RenderMesh.MaterialIndex"/>.</summary>
    public required IReadOnlyList<RenderMaterial> Materials { get; init; }

    public required IReadOnlyList<string> UnsupportedFeatures { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }

    public required long ApproximateByteSize { get; init; }
}
