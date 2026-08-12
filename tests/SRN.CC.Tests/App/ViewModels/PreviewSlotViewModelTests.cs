using FluentAssertions;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.Tests.App.ViewModels;

/// <summary>
/// Covers the S10 slot-content-surface refactor (architecture decision A3):
/// <see cref="PreviewSlotViewModel.Content"/> is populated from a successful preview via
/// <see cref="PreviewContentFactory"/>, and every reassignment disposes whatever content was
/// previously hosted, regardless of which code path performed the assignment.
/// </summary>
[TestFixture]
public class PreviewSlotViewModelTests
{
    [Test]
    public async Task AssignOccurrenceAsync_SuccessfulTextPreview_SetsTextContentViewModelWithExpectedText()
    {
        var provider = new FakeProvider
        {
            Family = PreviewFamily.Text,
            ResultToReturn = new PreviewResult(
                Occurrence: CreateOccurrence(),
                Family: PreviewFamily.Text,
                IsSuccess: true,
                MetadataText: null,
                RawPayload: null,
                FormattedContent: "hello world",
                ErrorMessage: null,
                Diagnostics: Array.Empty<string>()),
        };
        PreviewSlotViewModel slot = CreateSlot(provider);
        slot.PreferredFamily = PreviewFamily.Text;

        await slot.AssignOccurrenceAsync(null, CreateOccurrence(), CreateSource());

        slot.Content.Should().BeOfType<TextContentViewModel>();
        var content = (TextContentViewModel)slot.Content!;
        content.FormattedContent.Should().Be("hello world");
        content.Family.Should().Be(PreviewFamily.Text);

        // FormattedContent stays populated too - Content is additive in this slice, not a
        // replacement (existing bindings/callers must keep working unchanged).
        slot.FormattedContent.Should().Be("hello world");
    }

    [Test]
    public async Task AssignOccurrenceAsync_FailedPreview_LeavesContentNullAndSetsErrorMessage()
    {
        var provider = new FakeProvider
        {
            Family = PreviewFamily.Text,
            ResultToReturn = new PreviewResult(
                Occurrence: CreateOccurrence(),
                Family: PreviewFamily.Text,
                IsSuccess: false,
                MetadataText: null,
                RawPayload: null,
                FormattedContent: null,
                ErrorMessage: "boom",
                Diagnostics: Array.Empty<string>()),
        };
        PreviewSlotViewModel slot = CreateSlot(provider);
        slot.PreferredFamily = PreviewFamily.Text;

        await slot.AssignOccurrenceAsync(null, CreateOccurrence(), CreateSource());

        // Design decision: a failed preview keeps Content null (nothing renders through the
        // content template) so only the pre-existing ErrorMessage TextBlock shows - matching the
        // pre-refactor behavior where FormattedContent stayed null on failure.
        slot.Content.Should().BeNull();
        slot.ErrorMessage.Should().Be("boom");
    }

    [Test]
    public async Task AssignOccurrenceAsync_EmptySlot_SetsPlaceholderTextContent()
    {
        // Design decision: a cleared/unassigned slot gets a TextContentViewModel wrapping the
        // existing placeholder message, rather than Content == null, so the visual behavior through
        // the new ContentControl/TextPreviewTemplate path matches what the old inline TextBox bound
        // directly to FormattedContent used to show for an empty slot.
        PreviewSlotViewModel slot = CreateSlot(new FakeProvider());

        await slot.AssignOccurrenceAsync(null, null, null);

        slot.Content.Should().BeOfType<TextContentViewModel>();
        ((TextContentViewModel)slot.Content!).FormattedContent.Should().Be("No asset assigned to this slot.");
        slot.FormattedContent.Should().Be("No asset assigned to this slot.");
    }

    [Test]
    public void Content_ReassignedToNewInstance_DisposesThePreviousInstance()
    {
        PreviewSlotViewModel slot = CreateSlot(new FakeProvider());
        var first = new TrackingContent();
        slot.Content = first;

        slot.Content = new TrackingContent();

        first.Disposed.Should().BeTrue();
    }

    [Test]
    public void Content_ReassignedToNull_DisposesThePreviousInstance()
    {
        PreviewSlotViewModel slot = CreateSlot(new FakeProvider());
        var first = new TrackingContent();
        slot.Content = first;

        slot.Content = null;

        first.Disposed.Should().BeTrue();
    }

    [Test]
    public void Content_ReassignedToSameInstance_DoesNotDisposeIt()
    {
        PreviewSlotViewModel slot = CreateSlot(new FakeProvider());
        var only = new TrackingContent();
        slot.Content = only;

        slot.Content = only;

        only.Disposed.Should().BeFalse();
    }

    [Test]
    public async Task PinThisOccurrence_CallbackReturnsFalse_LeavesSlotUnpinned()
    {
        var engine = new PreviewEngine(new FakeDispatcher(), new IPreviewProvider[] { new FakeProvider() });
        bool called = false;
        var slot = new PreviewSlotViewModel(0, engine, _ => { called = true; return Task.FromResult(false); });

        await slot.AssignOccurrenceAsync(null, CreateOccurrence(), CreateSource());
        slot.SetPinEnabled(true);

        await slot.PinThisOccurrenceCommand.ExecuteAsync(null);

        called.Should().BeTrue("the pin callback is still invoked");
        slot.IsPinned.Should().BeFalse("a refused/aborted pin must not be displayed as pinned");
    }

    [Test]
    public async Task PinThisOccurrence_CallbackReturnsTrue_MarksSlotPinned()
    {
        var engine = new PreviewEngine(new FakeDispatcher(), new IPreviewProvider[] { new FakeProvider() });
        var slot = new PreviewSlotViewModel(0, engine, _ => Task.FromResult(true));

        await slot.AssignOccurrenceAsync(null, CreateOccurrence(), CreateSource());
        slot.SetPinEnabled(true);

        await slot.PinThisOccurrenceCommand.ExecuteAsync(null);

        slot.IsPinned.Should().BeTrue("a persisted pin is reflected in the slot");
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static PreviewSlotViewModel CreateSlot(IPreviewProvider provider)
    {
        var engine = new PreviewEngine(new FakeDispatcher(), new[] { provider });
        return new PreviewSlotViewModel(0, engine, _ => Task.FromResult(true));
    }

    private static AssetOccurrence CreateOccurrence() => new(
        identity: new AssetIdentity("test_resref", 2000),
        sourceId: Guid.NewGuid(),
        locator: new HakEntryLocator(1),
        originalName: "test_resref.txt",
        size: 10,
        validationState: ValidationState.Valid,
        extensionMetadata: null,
        sha256: null);

    private static AssetSource CreateSource() => AssetSource.CreateHak("c:/test/source.hak", 0);

    private sealed class FakeDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(
            AssetSource source,
            AssetOccurrence occurrence,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }));
    }

    private sealed class FakeProvider : IPreviewProvider
    {
        public PreviewFamily Family { get; init; } = PreviewFamily.Text;

        public PreviewResult? ResultToReturn { get; set; }

        public bool CanPreview(PreviewRequest request) => true;

        public Task<PreviewResult> GeneratePreviewAsync(
            PreviewRequest request,
            Stream payloadStream,
            CancellationToken cancellationToken = default)
        {
            PreviewResult result = ResultToReturn ?? new PreviewResult(
                Occurrence: request.Occurrence,
                Family: request.PreferredFamily,
                IsSuccess: true,
                MetadataText: null,
                RawPayload: null,
                FormattedContent: "default",
                ErrorMessage: null,
                Diagnostics: Array.Empty<string>());
            return Task.FromResult(result);
        }
    }

    private sealed class TrackingContent : PreviewContentViewModel
    {
        public override PreviewFamily Family => PreviewFamily.Text;

        public bool Disposed { get; private set; }

        public override void Dispose() => Disposed = true;
    }
}
