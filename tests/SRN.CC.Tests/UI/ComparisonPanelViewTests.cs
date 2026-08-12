using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.App.Views;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.Tests.UI;

/// <summary>
/// Proves the S10 slot-content-surface refactor (architecture decision A3) actually renders at
/// runtime: <c>ComparisonPanelView.axaml</c> now hosts each slot's content through
/// <c>ContentControl Content="{Binding Content}"</c>, resolving the visual for a
/// <see cref="TextContentViewModel"/> from the merged
/// <c>Views/PreviewTemplates/TextPreviewTemplate.axaml</c> dictionary via Avalonia's
/// implicit-template-by-type resolution - not via any binding to the slot's own
/// <c>FormattedContent</c> property directly.
/// </summary>
[TestFixture]
public class ComparisonPanelViewTests
{
    [AvaloniaTest]
    public void ComparisonPanelView_SlotWithTextContent_RendersFormattedContentThroughImplicitTemplate()
    {
        var engine = new PreviewEngine(new NoOpDispatcher(), Array.Empty<IPreviewProvider>());
        var vm = new ComparisonPanelViewModel(engine, _ => Task.FromResult(true));
        vm.Slots[0].Content = new TextContentViewModel(PreviewFamily.Text, "rendered via implicit template");

        var view = new ComparisonPanelView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 600 };

        try
        {
            window.Show();
            window.UpdateLayout();

            var textBoxes = window.GetVisualDescendants().OfType<TextBox>().ToList();
            textBoxes.Should().Contain(tb => tb.Text == "rendered via implicit template");
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class NoOpDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(
            AssetSource source,
            AssetOccurrence occurrence,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not exercised: this test sets slot Content directly.");
    }
}
