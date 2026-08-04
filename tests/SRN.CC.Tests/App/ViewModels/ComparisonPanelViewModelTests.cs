using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.Tests.App.ViewModels;

[TestFixture]
public class ComparisonPanelViewModelTests
{
    // ---- Construction helpers ----

    private static ComparisonPanelViewModel CreatePanel(PreviewEngine? engine = null) =>
        new ComparisonPanelViewModel(
            engine ?? new PreviewEngine(new StubDispatcher(Array.Empty<byte>()), Array.Empty<IPreviewProvider>()),
            _ => Task.CompletedTask);

    private static (AssetOccurrence occ, AssetSource source, CuratedAsset asset) CreateAssignment(string resref, string originalName)
    {
        var identity = new AssetIdentity(resref, 2000);
        var source = AssetSource.CreateFolder("c:/test/comparison", 0);
        var occ = new AssetOccurrence(
            identity: identity,
            sourceId: source.Id,
            locator: new FolderFileLocator(originalName),
            originalName: originalName,
            size: 64,
            validationState: ValidationState.Valid,
            extensionMetadata: null,
            sha256: Encoding.ASCII.GetBytes("hash1234567890123456789012345678"));
        var asset = new CuratedAsset(
            identity: identity,
            allOccurrences: new[] { occ },
            resolvedOccurrence: occ,
            pin: null,
            status: ResolutionStatus.Resolved,
            isSelected: true);
        return (occ, source, asset);
    }

    private static async Task ActivateSlotAsync(PreviewSlotViewModel slot, PreviewFamily family, string resref, string originalName)
    {
        slot.PreferredFamily = family;
        var (occ, source, asset) = CreateAssignment(resref, originalName);
        await slot.AssignOccurrenceAsync(asset, occ, source);
    }

    // ---- Zoom / Pan link propagation ----

    [Test]
    public async Task OnSlotZoomPanChanged_WithLinkEnabled_PropagatesToOtherActiveImageSlots()
    {
        var panel = CreatePanel();
        await ActivateSlotAsync(panel.Slots[0], PreviewFamily.Image, "img0", "a.tga");
        await ActivateSlotAsync(panel.Slots[1], PreviewFamily.Image, "img1", "b.tga");
        await ActivateSlotAsync(panel.Slots[2], PreviewFamily.Image, "img2", "c.tga");

        Assert.That(panel.IsLinkedNavigationEnabled, Is.True);

        panel.Slots[0].SetZoomPan(2.0, 5, 5);

        Assert.That(panel.Slots[1].ZoomScale, Is.EqualTo(2.0));
        Assert.That(panel.Slots[1].PanX, Is.EqualTo(5));
        Assert.That(panel.Slots[1].PanY, Is.EqualTo(5));
        Assert.That(panel.Slots[2].ZoomScale, Is.EqualTo(2.0));
        Assert.That(panel.Slots[2].PanX, Is.EqualTo(5));
        Assert.That(panel.Slots[2].PanY, Is.EqualTo(5));
    }

    [Test]
    public async Task OnSlotZoomPanChanged_WithLinkDisabled_DoesNotPropagate()
    {
        var panel = CreatePanel();
        await ActivateSlotAsync(panel.Slots[0], PreviewFamily.Image, "img0", "a.tga");
        await ActivateSlotAsync(panel.Slots[1], PreviewFamily.Image, "img1", "b.tga");

        panel.IsLinkedNavigationEnabled = false;

        panel.Slots[0].SetZoomPan(4.0, 9, 9);

        Assert.That(panel.Slots[1].ZoomScale, Is.EqualTo(1.0));
        Assert.That(panel.Slots[1].PanX, Is.EqualTo(0.0));
        Assert.That(panel.Slots[1].PanY, Is.EqualTo(0.0));
    }

    [Test]
    public async Task ToggleLinkCommand_DisablesPropagation()
    {
        var panel = CreatePanel();
        await ActivateSlotAsync(panel.Slots[0], PreviewFamily.Image, "img0", "a.tga");
        await ActivateSlotAsync(panel.Slots[1], PreviewFamily.Image, "img1", "b.tga");

        Assert.That(panel.IsLinkedNavigationEnabled, Is.True);
        panel.ToggleLinkCommand.Execute(null);
        Assert.That(panel.IsLinkedNavigationEnabled, Is.False);

        panel.Slots[0].SetZoomPan(6.0, 1, 1);

        Assert.That(panel.Slots[1].ZoomScale, Is.EqualTo(1.0));
        Assert.That(panel.Slots[1].PanX, Is.EqualTo(0.0));
        Assert.That(panel.Slots[1].PanY, Is.EqualTo(0.0));

        // Re-enabling should restore propagation for subsequent changes.
        panel.ToggleLinkCommand.Execute(null);
        Assert.That(panel.IsLinkedNavigationEnabled, Is.True);
        panel.Slots[0].SetZoomPan(6.0, 1, 1);
        Assert.That(panel.Slots[1].ZoomScale, Is.EqualTo(6.0));
    }

    // ---- Mixed-family guard ----

    [Test]
    public async Task OnSlotZoomPanChanged_SkipsNonImageFamilySlots_ButStillPropagatesToImageSlots()
    {
        var panel = CreatePanel();
        await ActivateSlotAsync(panel.Slots[0], PreviewFamily.Image, "img0", "a.tga");
        await ActivateSlotAsync(panel.Slots[1], PreviewFamily.Text, "txt1", "b.txt");
        await ActivateSlotAsync(panel.Slots[2], PreviewFamily.Image, "img2", "c.tga");

        panel.Slots[0].SetZoomPan(2.5, 3, 3);

        // Text-family slot must be unaffected by the Image-origin zoom/pan change.
        Assert.That(panel.Slots[1].ZoomScale, Is.EqualTo(1.0));
        Assert.That(panel.Slots[1].PanX, Is.EqualTo(0.0));
        Assert.That(panel.Slots[1].PanY, Is.EqualTo(0.0));

        // The other active Image-family slot must still receive the change.
        Assert.That(panel.Slots[2].ZoomScale, Is.EqualTo(2.5));
        Assert.That(panel.Slots[2].PanX, Is.EqualTo(3));
        Assert.That(panel.Slots[2].PanY, Is.EqualTo(3));
    }

    [Test]
    public async Task OnSlotZoomPanChanged_WhenOriginIsNotImageFamily_DoesNotPropagateAtAll()
    {
        var panel = CreatePanel();
        await ActivateSlotAsync(panel.Slots[0], PreviewFamily.Text, "txt0", "a.txt");
        await ActivateSlotAsync(panel.Slots[1], PreviewFamily.Image, "img1", "b.tga");

        panel.Slots[0].SetZoomPan(2.5, 3, 3);

        Assert.That(panel.Slots[1].ZoomScale, Is.EqualTo(1.0));
        Assert.That(panel.Slots[1].PanX, Is.EqualTo(0.0));
        Assert.That(panel.Slots[1].PanY, Is.EqualTo(0.0));
    }

    // ---- Audio propagation ----

    [Test]
    public async Task OnSlotAudioStateChanged_WhenNotAllActiveSlotsAreAudioFamily_DoesNotPropagate()
    {
        var panel = CreatePanel(CreateAudioEngine());
        await ActivateSlotAsync(panel.Slots[0], PreviewFamily.Audio, "aud0", "a.wav");
        await ActivateSlotAsync(panel.Slots[1], PreviewFamily.Text, "txt1", "b.txt");

        // Sentinel value unreachable by either the "device unavailable" (Idle) or
        // "device available" (Playing) outcome of a real Play() attempt on the origin slot,
        // so any change away from it would prove the guard failed to skip the mismatched slot.
        panel.Slots[1].PlaybackState = AudioPlaybackState.Paused;

        Assert.DoesNotThrow(() => panel.Slots[0].PlayCommand.Execute(null));

        Assert.That(panel.Slots[1].PlaybackState, Is.EqualTo(AudioPlaybackState.Paused));
    }

    [Test]
    public async Task OnSlotAudioStateChanged_WhenAllActiveSlotsAreAudioFamily_PropagatesResultingState()
    {
        var panel = CreatePanel(CreateAudioEngine());
        await ActivateSlotAsync(panel.Slots[0], PreviewFamily.Audio, "aud0", "a.wav");
        await ActivateSlotAsync(panel.Slots[1], PreviewFamily.Audio, "aud1", "b.wav");
        await ActivateSlotAsync(panel.Slots[2], PreviewFamily.Audio, "aud2", "c.wav");

        // Seed a sentinel that neither Play() outcome (Playing on success, Idle on failure)
        // would naturally produce, so convergence to the origin's state proves propagation ran.
        panel.Slots[1].PlaybackState = AudioPlaybackState.Paused;
        panel.Slots[2].PlaybackState = AudioPlaybackState.Paused;

        Assert.DoesNotThrow(() => panel.Slots[0].PlayCommand.Execute(null));

        // Whatever real playback outcome the environment produces for the origin slot
        // (Playing if an audio device is available, Idle otherwise after the caught
        // exception), the linked active Audio slots must converge to the same state.
        Assert.That(panel.Slots[1].PlaybackState, Is.EqualTo(panel.Slots[0].PlaybackState));
        Assert.That(panel.Slots[2].PlaybackState, Is.EqualTo(panel.Slots[0].PlaybackState));
    }

    [Test]
    [Ignore("Deterministically asserting mid-playback PositionProgress propagation requires a live NAudio output device (WaveOutEvent), which is not guaranteed to be available in a headless CI test environment. The propagation guard and resulting-state convergence are covered by OnSlotAudioStateChanged_WhenAllActiveSlotsAreAudioFamily_PropagatesResultingState instead.")]
    public void OnSlotAudioStateChanged_PropagatesLivePlaybackPosition()
    {
    }

    // ---- Test doubles ----

    private static PreviewEngine CreateAudioEngine() =>
        new PreviewEngine(new StubDispatcher(BuildWav()), new IPreviewProvider[] { new AudioPreviewProvider(new StubRegistry()) });

    private static byte[] BuildWav()
    {
        byte[] sampleData = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        ushort channels = 1;
        uint sampleRate = 8000;
        ushort bitsPerSample = 16;
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        uint byteRate = sampleRate * blockAlign;

        using var stream = new MemoryStream();
        void WriteAscii(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        void WriteUInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            stream.Write(buffer);
        }
        void WriteUInt16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            stream.Write(buffer);
        }

        uint fmtChunkSize = 16;
        uint dataChunkSize = (uint)sampleData.Length;
        uint riffChunkSize = 4 + (8 + fmtChunkSize) + (8 + dataChunkSize);

        WriteAscii("RIFF");
        WriteUInt32(riffChunkSize);
        WriteAscii("WAVE");

        WriteAscii("fmt ");
        WriteUInt32(fmtChunkSize);
        WriteUInt16(1); // PCM
        WriteUInt16(channels);
        WriteUInt32(sampleRate);
        WriteUInt32(byteRate);
        WriteUInt16(blockAlign);
        WriteUInt16(bitsPerSample);

        WriteAscii("data");
        WriteUInt32(dataChunkSize);
        stream.Write(sampleData);

        return stream.ToArray();
    }

    private sealed class StubDispatcher : ISourceReaderDispatcher
    {
        private readonly byte[] _payload;

        public StubDispatcher(byte[] payload)
        {
            _payload = payload;
        }

        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(_payload));
    }

    private sealed class StubRegistry : IResourceTypeRegistry
    {
        public bool TryGetExtension(ushort typeId, out string extension) { extension = string.Empty; return false; }
        public bool TryGetType(string extension, out ushort typeId) { typeId = 0; return false; }
    }
}
