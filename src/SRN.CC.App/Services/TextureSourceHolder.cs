using SRN.CC.Core.Services;

namespace SRN.CC.App.Services;

/// <summary>
/// Mutable single-slot holder bridging the late-bound texture-source accessor pattern from
/// architecture decision A7. <c>App.axaml.cs</c> constructs providers (including
/// <c>MdlPreviewProvider</c>) before <see cref="SRN.CC.App.ViewModels.MainWindowViewModel"/>
/// exists, so it cannot pass a concrete <see cref="ITextureSource"/> at construction time.
/// Instead it creates one <see cref="TextureSourceHolder"/>, captures <c>() =&gt; holder.Current</c>
/// as the provider's <c>textureSourceAccessor</c>, and also hands the same holder to
/// <see cref="SRN.CC.App.ViewModels.MainWindowViewModel"/>, which refreshes
/// <see cref="Current"/> from the loaded <c>WorkspaceState</c> every time that state changes.
/// A null <see cref="Current"/> (no workspace loaded yet, or the designer-preview constructor)
/// yields an untextured model scene plus a diagnostic rather than a failure.
/// </summary>
public sealed class TextureSourceHolder
{
    public ITextureSource? Current { get; set; }
}
