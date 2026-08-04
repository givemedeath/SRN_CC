using SRN.CC.Core.Identity;

namespace SRN.CC.Core.Services;

/// <summary>
/// Resolves a texture-family resref (optionally suffixed with an explicit extension, e.g.
/// <c>"c_rat.dds"</c>, to disambiguate among candidate resource types) to a byte payload.
/// Curated workspace first, base-game KEY/BIF second. Null when unresolved. Never throws for
/// resolution failures — implementations should catch and swallow I/O/format errors and return
/// null instead (cancellation is the one exception permitted to propagate).
/// </summary>
/// <remarks>
/// Canonical definition per architecture decision A7 in the milestone-6 plan. Consumed by
/// <c>SRN.CC.Preview.Render.TextureResolver</c> (slice S7), which owns the resolution ladder
/// (MTR override, then DDS/TGA/PLT, then TXI sidecar) and issues one call per candidate; and
/// implemented over this app's workspace + base-game catalog by
/// <c>SRN.CC.App.Services.WorkspaceTextureSource</c> (also slice S7).
/// </remarks>
public interface ITextureSource
{
    /// <summary>Null when the resref cannot be resolved. Never throws.</summary>
    Task<TextureLookupResult?> OpenTextureAsync(
        string resref, TextureKind kind, CancellationToken cancellationToken = default);
}

/// <summary>The purpose a texture lookup serves, letting implementations classify a resref without
/// depending on any particular caller-side extension convention.</summary>
public enum TextureKind
{
    Diffuse,
    Lightmap,
    Material,
    EnvironmentMap,
    TextureInfo,
}

/// <summary>Where a resolved texture byte stream came from.</summary>
public enum TextureOrigin
{
    Workspace,
    BaseGame,
}

/// <summary>A successfully resolved texture-family payload plus the identity/resource-type/origin
/// metadata needed to decode and attribute it.</summary>
public sealed record TextureLookupResult(
    AssetIdentity Identity, ushort ResourceType, Stream Payload, TextureOrigin Origin);
