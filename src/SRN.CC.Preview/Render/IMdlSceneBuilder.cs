using SRN.CC.Core.Services;
using SWLOR.NWN.Formats.Mdl;

namespace SRN.CC.Preview.Render;

/// <summary>
/// Builds a <see cref="RenderScene"/> from a parsed MDL model. Declaration only in this slice;
/// implemented by <c>MdlSceneBuilder</c> in slice S6b.
/// </summary>
public interface IMdlSceneBuilder
{
    Task<RenderScene> BuildAsync(
        MdlModel model,
        bool isAsciiSource,
        ITextureSource? textures,
        SceneBuildBudget budget,
        CancellationToken cancellationToken = default);
}

/// <summary>Resource caps enforced while building a <see cref="RenderScene"/>; exceeding any budget degrades gracefully rather than failing.</summary>
public sealed record SceneBuildBudget(long CpuBytes, long TextureBytes, int MaxTextures, int MaxDrawCalls);
