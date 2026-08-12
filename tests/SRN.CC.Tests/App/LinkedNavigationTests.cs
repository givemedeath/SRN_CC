using System.ComponentModel;
using System.Numerics;
using System.Text;
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
using SRN.CC.Preview.Render;
using SRN.CC.Tests.Preview.Render.Gl;

namespace SRN.CC.Tests.App;

/// <summary>
/// Covers slice S14 (linked slot navigation), implementing <c>PLAN.md:120</c> verbatim: link image
/// navigation and compatible 3D cameras by default; link audio position only when every visible slot
/// is audio; leave text/tree/table scrolling unlinked. See the extensive remarks on
/// <see cref="ComparisonPanelViewModel"/> for the full design (re-entrancy guard, the strict
/// "all three slots" audio detection, and why audio linking writes only the observable
/// <see cref="AudioContentViewModel.PositionProgress"/> property).
///
/// Every test constructs <see cref="ComparisonPanelViewModel"/>/<see cref="PreviewSlotViewModel"/>
/// directly with minimal fakes (mirroring <c>PreviewSlotViewModelTests</c>) and assigns
/// <see cref="PreviewSlotViewModel.Content"/> directly - its generated setter is public - rather than
/// driving a real preview through <see cref="PreviewEngine"/>, so these tests never touch a real
/// source, disk, or audio device. <see cref="PreviewSlotViewModel.IsActive"/> is set explicitly
/// alongside <c>Content</c> in every test, matching the invariant
/// <see cref="PreviewSlotViewModel.AssignOccurrenceAsync"/> itself maintains (a slot is only ever
/// given non-placeholder content while active).
/// </summary>
[TestFixture]
public class LinkedNavigationTests
{
    // -------------------------------------------------------------------
    // Image linking
    // -------------------------------------------------------------------

    [Test]
    public void ImageZoomAndPanChange_PropagatesToOtherActiveImageSlots()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        var sourceImage = new ImageContentViewModel(CreateImageResult());
        var siblingImage = new ImageContentViewModel(CreateImageResult());
        SetActiveContent(panel, 0, sourceImage);
        SetActiveContent(panel, 1, siblingImage);

        sourceImage.ZoomScale = 3.5;
        sourceImage.PanX = 12.0;
        sourceImage.PanY = -7.5;

        siblingImage.ZoomScale.Should().Be(3.5);
        siblingImage.PanX.Should().Be(12.0);
        siblingImage.PanY.Should().Be(-7.5);
    }

    [Test]
    public void ImageZoomChange_DoesNotAffectNonImageSlot()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        var sourceImage = new ImageContentViewModel(CreateImageResult());
        var textSibling = new TextContentViewModel(PreviewFamily.Text, "unrelated text");
        SetActiveContent(panel, 0, sourceImage);
        SetActiveContent(panel, 1, textSibling);

        sourceImage.ZoomScale = 5.0;

        // Nothing to zoom on text content - assert it is simply untouched (same instance, same text).
        panel.Slots[1].Content.Should().BeSameAs(textSibling);
        textSibling.FormattedContent.Should().Be("unrelated text");
    }

    [Test]
    public void ImageZoomChange_DoesNotCascadeUnboundedly()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        var sourceImage = new ImageContentViewModel(CreateImageResult());
        var siblingImage = new ImageContentViewModel(CreateImageResult());
        SetActiveContent(panel, 0, sourceImage);
        SetActiveContent(panel, 1, siblingImage);

        int siblingEventCount = 0;
        siblingImage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageContentViewModel.ZoomScale))
            {
                siblingEventCount++;
            }
        };

        sourceImage.ZoomScale = 2.0;

        // Exactly one write reaches the sibling per source change - the re-entrancy guard means the
        // sibling's own resulting PropertyChanged never triggers a second write back, and a repeat
        // assignment of the same value (which would happen on any accidental cascade) is suppressed
        // by the equality check before every propagated write.
        siblingEventCount.Should().Be(1);

        // Setting the sibling to the value it already holds must not re-trigger propagation back to
        // the source (equality guard) - assert no stack overflow / no further cascade by changing the
        // source again and confirming the count increments by exactly one more.
        sourceImage.ZoomScale = 2.0; // same value -> ImageContentViewModel itself won't raise again
        siblingEventCount.Should().Be(1);

        sourceImage.ZoomScale = 4.0;
        siblingEventCount.Should().Be(2);
    }

    // -------------------------------------------------------------------
    // 3D camera linking
    // -------------------------------------------------------------------

    [Test]
    public void CameraChange_PropagatesToOtherActiveModelSlots()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        var sourceViewport = new ModelViewportViewModel(scene);
        var siblingViewport = new ModelViewportViewModel(scene);
        SetActiveContent(panel, 0, sourceViewport);
        SetActiveContent(panel, 1, siblingViewport);
        RenderCamera before = siblingViewport.Camera;

        sourceViewport.Orbit(0.4f, -0.2f);

        siblingViewport.Camera.Should().Be(sourceViewport.Camera);
        siblingViewport.Camera.Should().NotBe(before);
    }

    [Test]
    public void CameraChange_DoesNotAffectNonModelSlot()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        var sourceViewport = new ModelViewportViewModel(scene);
        var textSibling = new TextContentViewModel(PreviewFamily.Model, "fallback text");
        SetActiveContent(panel, 0, sourceViewport);
        SetActiveContent(panel, 1, textSibling);

        sourceViewport.Dolly(0.5f);

        panel.Slots[1].Content.Should().BeSameAs(textSibling);
    }

    [Test]
    public void CameraChange_ShowWalkmeshIsNeverLinked()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        RenderScene scene = TestSceneFactory.CreateSceneWithArtworkAndWalkmesh();
        var sourceViewport = new ModelViewportViewModel(scene);
        var siblingViewport = new ModelViewportViewModel(scene);
        SetActiveContent(panel, 0, sourceViewport);
        SetActiveContent(panel, 1, siblingViewport);

        sourceViewport.ShowWalkmesh = true;

        siblingViewport.ShowWalkmesh.Should().BeFalse();
    }

    // -------------------------------------------------------------------
    // Audio linking - conditional on every slot being audio
    // -------------------------------------------------------------------

    [Test]
    public void AudioPositionChange_WhenAllThreeSlotsAudio_PropagatesToOtherTwo()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        var audio0 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        var audio1 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        var audio2 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        SetActiveContent(panel, 0, audio0);
        SetActiveContent(panel, 1, audio1);
        SetActiveContent(panel, 2, audio2);

        audio0.PositionProgress = 0.42;

        audio1.PositionProgress.Should().Be(0.42);
        audio2.PositionProgress.Should().Be(0.42);
    }

    [Test]
    public void AudioPositionChange_WhenOneSlotBecomesNonAudio_StopsLinkingEntirely()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        var audio0 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        var audio1 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        var audio2 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        SetActiveContent(panel, 0, audio0);
        SetActiveContent(panel, 1, audio1);
        SetActiveContent(panel, 2, audio2);

        // Establish the baseline while all three are audio.
        audio0.PositionProgress = 0.1;
        audio1.PositionProgress.Should().Be(0.1);
        audio2.PositionProgress.Should().Be(0.1);

        // Slot 2 becomes a different family - per PLAN.md:120's literal "every visible slot is audio"
        // reading, this design suspends linking entirely, not just for slot 2.
        var textReplacement = new TextContentViewModel(PreviewFamily.Text, "now text");
        SetActiveContent(panel, 2, textReplacement);

        audio0.PositionProgress = 0.9;

        // Slot 2 is obviously untouched - it isn't audio content at all.
        panel.Slots[2].Content.Should().BeSameAs(textReplacement);

        // Slot 1 - still an AudioContentViewModel - no longer participates either, because the
        // all-three-audio condition now fails. This is the deliberate design choice documented on
        // ComparisonPanelViewModel: linking does not degenerate to "link whichever remain audio".
        audio1.PositionProgress.Should().Be(0.1);
    }

    [Test]
    public void AudioPositionChange_WhenOneSlotBecomesInactiveEmpty_StopsLinkingEntirely()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        var audio0 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        var audio1 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        var audio2 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        SetActiveContent(panel, 0, audio0);
        SetActiveContent(panel, 1, audio1);
        SetActiveContent(panel, 2, audio2);

        panel.Slots[2].IsActive = false;
        panel.Slots[2].Content = null;

        audio0.PositionProgress = 0.75;

        audio1.PositionProgress.Should().Be(0d);
    }

    [Test]
    public void AudioPositionChange_WhenNotAllSlotsAudioFromTheStart_NeverLinks()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        var audio0 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        var audio1 = new AudioContentViewModel(CreateAudioResult(BuildWav()));
        var image2 = new ImageContentViewModel(CreateImageResult());
        SetActiveContent(panel, 0, audio0);
        SetActiveContent(panel, 1, audio1);
        SetActiveContent(panel, 2, image2);

        audio0.PositionProgress = 0.33;

        audio1.PositionProgress.Should().Be(0d);
    }

    [Test]
    public void LinkNavigationEnabled_DefaultsTrue_AndDisablingItSuspendsAllLinking()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        panel.LinkNavigationEnabled.Should().BeTrue();

        var sourceImage = new ImageContentViewModel(CreateImageResult());
        var siblingImage = new ImageContentViewModel(CreateImageResult());
        SetActiveContent(panel, 0, sourceImage);
        SetActiveContent(panel, 1, siblingImage);

        panel.LinkNavigationEnabled = false;
        sourceImage.ZoomScale = 6.0;

        siblingImage.ZoomScale.Should().Be(1.0);
    }

    // -------------------------------------------------------------------
    // Lifecycle: subscription follows reassignment, never fires against stale content
    // -------------------------------------------------------------------

    [Test]
    public void ContentReassignment_UnsubscribesFromOldInstance_NoStalePropagation()
    {
        ComparisonPanelViewModel panel = CreatePanel();
        var oldImage = new ImageContentViewModel(CreateImageResult());
        var siblingImage = new ImageContentViewModel(CreateImageResult());
        SetActiveContent(panel, 0, oldImage);
        SetActiveContent(panel, 1, siblingImage);

        // Confirm the link works before reassignment.
        oldImage.ZoomScale = 2.0;
        siblingImage.ZoomScale.Should().Be(2.0);

        // Reassign slot 0 to a brand-new instance - PreviewSlotViewModel's own OnContentChanging
        // disposes oldImage as part of this assignment (real lifecycle behavior; not simulated here).
        var newImage = new ImageContentViewModel(CreateImageResult());
        SetActiveContent(panel, 0, newImage);

        // Mutate the OLD, now-detached (and disposed) instance directly. This must not throw and must
        // not reach the sibling - the ComparisonPanelViewModel-level subscription was moved to
        // newImage when Content changed.
        Action mutateOld = () => oldImage.ZoomScale = 9.0;
        mutateOld.Should().NotThrow();
        siblingImage.ZoomScale.Should().Be(2.0);

        // The new instance is the one actually linked now.
        newImage.ZoomScale = 3.3;
        siblingImage.ZoomScale.Should().Be(3.3);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static ComparisonPanelViewModel CreatePanel()
    {
        var engine = new PreviewEngine(new FakeDispatcher(), Array.Empty<IPreviewProvider>());
        return new ComparisonPanelViewModel(engine, static _ => Task.FromResult(true));
    }

    /// <summary>
    /// Assigns <paramref name="content"/> to slot <paramref name="index"/> and marks it active,
    /// matching the invariant real assignment (<see cref="PreviewSlotViewModel.AssignOccurrenceAsync"/>)
    /// maintains: a slot only ever hosts non-placeholder content while <see cref="PreviewSlotViewModel.IsActive"/>
    /// is true.
    /// </summary>
    private static void SetActiveContent(ComparisonPanelViewModel panel, int index, PreviewContentViewModel? content)
    {
        panel.Slots[index].IsActive = content != null;
        panel.Slots[index].Content = content;
    }

    private static PreviewResult CreateImageResult() =>
        new(
            Occurrence: CreateOccurrence("test_image", 3000),
            Family: PreviewFamily.Image,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: null,
            FormattedContent: null, // dimensions absent -> Bitmap stays null, irrelevant to zoom/pan
            ErrorMessage: null,
            Diagnostics: Array.Empty<string>());

    private static PreviewResult CreateAudioResult(byte[] wavBytes) =>
        new(
            Occurrence: CreateOccurrence("test_audio", 2011),
            Family: PreviewFamily.Audio,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: wavBytes,
            FormattedContent: "=== WAV PREVIEW ===",
            ErrorMessage: null,
            Diagnostics: Array.Empty<string>());

    private static AssetOccurrence CreateOccurrence(string resref, ushort resourceType) => new(
        identity: new AssetIdentity(resref, resourceType),
        sourceId: Guid.NewGuid(),
        locator: new HakEntryLocator(1),
        originalName: $"{resref}.bin",
        size: 10,
        validationState: ValidationState.Valid,
        extensionMetadata: null,
        sha256: null);

    private static byte[] BuildWav(
        int sampleRate = 44100,
        short channels = 1,
        short bitsPerSample = 16,
        double durationSeconds = 1.0)
    {
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;
        int dataSize = (int)(durationSeconds * byteRate);
        dataSize -= dataSize % blockAlign; // keep the data chunk frame-aligned

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));

            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write(channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write(bitsPerSample);

            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);
            bw.Write(new byte[dataSize]); // silence
        }

        return ms.ToArray();
    }

    private sealed class FakeDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(
            AssetSource source,
            AssetOccurrence occurrence,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }));
    }
}
