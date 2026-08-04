using CommunityToolkit.Mvvm.ComponentModel;
using SRN.CC.Core.Preview;

namespace SRN.CC.App.ViewModels.Preview;

/// <summary>
/// Base type for whatever renders inside a comparison slot's content area, per architecture
/// decision A3 (polymorphic slot content, per-family templates). <see cref="PreviewSlotViewModel"/>
/// holds exactly one <c>Content</c> instance at a time; Avalonia resolves the visual for it via an
/// implicit <c>DataTemplate</c> keyed by the CONCRETE C# type (see
/// <c>Views/PreviewTemplates/*.axaml</c>), never by <see cref="Family"/> — that property exists for
/// diagnostics/tests, not template dispatch.
///
/// Subclasses that own external resources (a decoded bitmap, an audio player, a GPU scene) must
/// release them in <see cref="Dispose"/>; <see cref="PreviewSlotViewModel"/> disposes the previous
/// <c>Content</c> on every reassignment, including when a slot is cleared.
/// </summary>
public abstract class PreviewContentViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The preview family that produced this content. Diagnostic/informational only — template
    /// selection in AXAML is by concrete type, not by this property, so that families sharing a
    /// rendering (e.g. Metadata/Text/Tree/Hex all rendering as plain text via
    /// <see cref="TextContentViewModel"/>) share a single template without a converter.
    /// </summary>
    public abstract PreviewFamily Family { get; }

    public virtual void Dispose()
    {
    }
}
