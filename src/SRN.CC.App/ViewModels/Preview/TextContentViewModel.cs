using SRN.CC.Core.Preview;

namespace SRN.CC.App.ViewModels.Preview;

/// <summary>
/// Renders a preview result as plain formatted text. This is the shared fallback content type for
/// every family that has no dedicated visual yet — Metadata, Text, Tree, and Hex all render this
/// way permanently (a tree or hex dump IS text), and Model renders this way only until a
/// <see cref="ModelViewportViewModel"/>-producing branch is added to
/// <see cref="PreviewContentFactory"/> once its scene payload can be consumed.
///
/// <see cref="Family"/> is a constructor parameter (not hard-coded) purely so slot diagnostics/tests
/// can tell which actual family produced a given instance; AXAML template dispatch is by the
/// concrete <see cref="TextContentViewModel"/> type via Avalonia's implicit
/// DataTemplate-by-<c>x:DataType</c> resolution, so one shared template
/// (<c>Views/PreviewTemplates/TextPreviewTemplate.axaml</c>) covers all of them regardless of
/// <see cref="Family"/>.
/// </summary>
public sealed class TextContentViewModel : PreviewContentViewModel
{
    private readonly PreviewFamily _family;

    public override PreviewFamily Family => _family;

    public string? FormattedContent { get; }

    public TextContentViewModel(PreviewFamily family, string? formattedContent)
    {
        _family = family;
        FormattedContent = formattedContent;
    }
}
