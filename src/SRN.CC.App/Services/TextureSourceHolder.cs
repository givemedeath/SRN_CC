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
    private ITextureSource? _current;

    public ITextureSource? Current
    {
        get => _current;
        set
        {
            if (ReferenceEquals(_current, value))
            {
                return;
            }

            _current = value;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Raised whenever <see cref="Current"/> is reassigned to a different instance (including to or
    /// from null). A scene built while one texture source was current has that source baked into its
    /// resolved textures — this signal is what lets a consumer (e.g. <c>App.axaml.cs</c> wiring a
    /// <c>ModelSceneCache</c>) invalidate anything cached against the previous source instead of
    /// serving stale texture state forever.
    /// </summary>
    public event Action? Changed;
}
