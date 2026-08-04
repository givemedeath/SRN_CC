namespace SRN.CC.Core.Services;

/// <summary>
/// Minimal placeholder introduced by slice S6a so <c>SRN.CC.Preview.Render.IMdlSceneBuilder</c> can
/// reference a texture-lookup abstraction without depending on the not-yet-landed texture pipeline.
/// </summary>
/// <remarks>
/// Slice S7 owns the canonical definition of this interface (see architecture decision A7 in the
/// milestone-6 plan), including <c>TextureKind</c>, <c>TextureOrigin</c>, and a
/// <c>TextureLookupResult</c> return type carrying identity/resource-type/origin metadata. S7 should
/// replace this placeholder outright rather than layer on top of it.
/// </remarks>
public interface ITextureSource
{
    /// <summary>Null when the resref cannot be resolved. Never throws.</summary>
    Task<Stream?> OpenTextureAsync(string resref, CancellationToken cancellationToken = default);
}
