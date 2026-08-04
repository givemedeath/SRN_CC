using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.Views;

namespace SRN.CC.Tests.UI;

/// <summary>
/// Covers what slice S11 can verify standing alone: <see cref="ModelViewportControl"/> constructed
/// and attached directly (not through the full app data-binding pipeline — that end-to-end path needs
/// <c>PreviewContentFactory</c>'s Model arm and <c>ComparisonPanelView.axaml</c>'s template
/// registration, both landed together by the orchestrator once every Wave-3 UI slice reports back),
/// plus the <see cref="ModelViewportRegistry"/> concurrency backstop on its own.
///
/// The plan's full exit criterion for this slice — "a headless visual-tree walk shows zero
/// <see cref="ModelViewportControl"/> instances when all three slots are text-family, exactly one
/// when one slot is Model, three when all three are, and zero again after
/// <c>ClearSelectionAsync</c>" — is an end-to-end scenario deferred to slice S17 (corpus/integration),
/// once the factory/AXAML wiring lands. What IS provable here, and is the structural core of
/// architecture decision A4's "only for visible 3D slots" rule: this control never attempts GPU work
/// in a headless host (no GL backend), and the registry it uses to bound concurrent viewports behaves
/// correctly on its own.
/// </summary>
[TestFixture]
public class ModelViewportControlTests
{
    [SetUp]
    public void SetUp()
    {
        // Isolate registry counter state between tests instead of sharing it across the whole
        // assembly's lifetime — see ModelViewportControl.SharedRegistry's remarks.
        ModelViewportControl.SharedRegistry = new ModelViewportRegistry();
    }

    [AvaloniaTest]
    public void AttachAndDetachInHeadlessHost_NeverInvokesOnOpenGlInit_AndRegistryReturnsToZero()
    {
        var control = new ModelViewportControl();
        var window = new Window { Content = control, Width = 200, Height = 200 };

        try
        {
            window.Show();
            window.UpdateLayout();

            control.HasAttemptedGlInit.Should().BeFalse(
                "the headless test host has no real GL backend, so OnOpenGlInit must never fire");
            ModelViewportControl.SharedRegistry.ActiveCount.Should().Be(
                1, "OnAttachedToVisualTree acquires exactly one registry slot for this control");
        }
        finally
        {
            window.Close();
        }

        control.HasAttemptedGlInit.Should().BeFalse();
        ModelViewportControl.SharedRegistry.ActiveCount.Should().Be(
            0, "OnDetachedFromVisualTree releases the slot acquired on attach");
    }

    [AvaloniaTest]
    public void AttachAndDetach_DoesNotThrow_EvenWithoutAModelViewportViewModelDataContext()
    {
        var control = new ModelViewportControl();
        var window = new Window { Content = control, Width = 200, Height = 200 };

        Action act = () =>
        {
            window.Show();
            window.UpdateLayout();
            window.Close();
        };

        act.Should().NotThrow();
    }

    [Test]
    public void ModelViewportRegistry_TryAcquire_AllowsUpToMaxConcurrentAndRefusesTheNext()
    {
        var registry = new ModelViewportRegistry();

        for (int i = 0; i < ModelViewportRegistry.MaxConcurrent; i++)
        {
            registry.TryAcquire().Should().BeTrue($"slot {i} is within MaxConcurrent ({ModelViewportRegistry.MaxConcurrent})");
        }

        registry.TryAcquire().Should().BeFalse("a fourth concurrent viewport must be refused");
        registry.ActiveCount.Should().Be(ModelViewportRegistry.MaxConcurrent);
    }

    [Test]
    public void ModelViewportRegistry_Release_FreesASlotForTheNextAcquire()
    {
        var registry = new ModelViewportRegistry();
        for (int i = 0; i < ModelViewportRegistry.MaxConcurrent; i++)
        {
            registry.TryAcquire().Should().BeTrue();
        }

        registry.TryAcquire().Should().BeFalse();

        registry.Release();

        registry.ActiveCount.Should().Be(ModelViewportRegistry.MaxConcurrent - 1);
        registry.TryAcquire().Should().BeTrue("releasing a slot must allow exactly one more acquire");
    }

    [Test]
    public void ModelViewportRegistry_Release_WithoutAcquire_NeverGoesNegative()
    {
        var registry = new ModelViewportRegistry();

        registry.Release();
        registry.Release();

        registry.ActiveCount.Should().Be(0);
    }
}
