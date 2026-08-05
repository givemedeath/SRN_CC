using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.App.Views;
using SRN.CC.Core.Identity;
using SRN.CC.Preview;

namespace SRN.CC.Tests.UI;

/// <summary>
/// Renders <c>ConfirmDependenciesDialog.axaml</c> headlessly to prove the truncation banner actually
/// binds.
/// </summary>
/// <remarks>
/// <para>
/// This project does not set <c>AvaloniaUseCompiledBindingsByDefault</c>, so the <c>x:DataType</c> on
/// the dialog is declarative only: a binding path that does not resolve fails silently at runtime and
/// still compiles. A green build is therefore no evidence that the banner works, which is exactly why
/// these assertions render the real view rather than inspecting the view model.
/// </para>
/// <para>
/// The null case matters most. <c>ConfirmDependenciesDialogViewModel.Summary</c> is nullable, and an
/// unresolved binding leaves <see cref="Visual.IsVisible"/> at its default of <c>true</c> — so a
/// banner bound naively to <c>Summary.IsTruncated</c> would announce a truncated closure on a dialog
/// that has no closure at all.
/// </para>
/// </remarks>
[TestFixture]
public class ConfirmDependenciesDialogTests
{
    private const ushort MdlType = 2002;

    [AvaloniaTest]
    public void TruncatedClosure_ShowsTheTruncationBannerNamingTheBudgetThatFired()
    {
        var window = Render(Summary(TraversalLimit.Size));

        try
        {
            BannerTexts(window).Should().ContainSingle(t => t.Contains("incomplete") && t.Contains("Size"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void CompleteClosure_ShowsNoTruncationBanner()
    {
        var window = Render(Summary(TraversalLimit.None));

        try
        {
            BannerTexts(window).Should().BeEmpty("a complete closure must not be reported as truncated");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void NoClosureAtAll_ShowsNoTruncationBanner()
    {
        // Summary is null until ShowDialogAsync assigns it. An unresolved binding path leaves
        // IsVisible at its default of true, so this is the case a naive binding gets wrong.
        var window = Render(summary: null);

        try
        {
            BannerTexts(window).Should().BeEmpty("a dialog with no closure must not claim one was truncated");
        }
        finally
        {
            window.Close();
        }
    }

    private static Window Render(ClosureSummary? summary)
    {
        var vm = new ConfirmDependenciesDialogViewModel();
        if (summary != null)
        {
            _ = vm.ShowDialogAsync(summary);
        }

        var window = new Window { Content = new ConfirmDependenciesDialog { DataContext = vm }.Content, DataContext = vm, Width = 600, Height = 500 };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static IEnumerable<string> BannerTexts(Window window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(tb => tb.IsEffectivelyVisible && tb.Text != null && tb.Text.Contains("incomplete"))
            .Select(tb => tb.Text!);

    private static ClosureSummary Summary(TraversalLimit limitHit) =>
        new(
            resolved: new HashSet<AssetIdentity> { new("root", MdlType) },
            unresolvedGroups: Array.Empty<UnresolvedDependencyGroup>(),
            totalBytes: 100,
            maxDepth: 1,
            duplicatesSuppressed: 0,
            limitHit: limitHit);
}
