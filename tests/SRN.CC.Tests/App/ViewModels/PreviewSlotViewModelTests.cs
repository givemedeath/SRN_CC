using NUnit.Framework;
using SRN.CC.App.ViewModels;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.Tests.App.ViewModels;

[TestFixture]
public class PreviewSlotViewModelTests
{
    private static PreviewEngine CreateEngine(params IPreviewProvider[] providers) =>
        new PreviewEngine(new StubDispatcher(), providers);

    private static PreviewSlotViewModel CreateSlot(PreviewEngine? engine = null, int slotIndex = 0) =>
        new PreviewSlotViewModel(
            slotIndex,
            engine ?? CreateEngine(),
            _ => Task.CompletedTask);

    // ---- Zoom / Pan ----

    [Test]
    public void SetZoomPan_UpdatesPropertiesAndRaisesEventExactlyOnce()
    {
        var slot = CreateSlot();
        int raiseCount = 0;
        slot.ZoomPanChanged += (_, _) => raiseCount++;

        slot.SetZoomPan(2.0, 10, -5);

        Assert.That(slot.ZoomScale, Is.EqualTo(2.0));
        Assert.That(slot.PanX, Is.EqualTo(10));
        Assert.That(slot.PanY, Is.EqualTo(-5));
        Assert.That(raiseCount, Is.EqualTo(1));
    }

    [Test]
    public void ApplyLinkedZoomPan_UpdatesPropertiesButDoesNotRaiseEvent()
    {
        var slot = CreateSlot();
        int raiseCount = 0;
        slot.ZoomPanChanged += (_, _) => raiseCount++;

        slot.ApplyLinkedZoomPan(3.0, 1, 1);

        Assert.That(slot.ZoomScale, Is.EqualTo(3.0));
        Assert.That(slot.PanX, Is.EqualTo(1));
        Assert.That(slot.PanY, Is.EqualTo(1));
        Assert.That(raiseCount, Is.EqualTo(0));
    }

    [Test]
    public void SetZoomPan_ThenApplyLinkedZoomPan_OnlyFirstRaisesEvent()
    {
        var slot = CreateSlot();
        int raiseCount = 0;
        slot.ZoomPanChanged += (_, _) => raiseCount++;

        slot.SetZoomPan(2.0, 10, -5);
        slot.ApplyLinkedZoomPan(3.0, 1, 1);

        Assert.That(slot.ZoomScale, Is.EqualTo(3.0));
        Assert.That(slot.PanX, Is.EqualTo(1));
        Assert.That(slot.PanY, Is.EqualTo(1));
        Assert.That(raiseCount, Is.EqualTo(1));
    }

    [Test]
    public void SetZoomPan_WithNaNOrInfiniteInputs_IsSanitizedAndDoesNotThrow()
    {
        var slot = CreateSlot();

        Assert.DoesNotThrow(() => slot.SetZoomPan(double.NaN, double.PositiveInfinity, double.NegativeInfinity));

        Assert.That(double.IsNaN(slot.ZoomScale), Is.False);
        Assert.That(double.IsInfinity(slot.PanX), Is.False);
        Assert.That(double.IsInfinity(slot.PanY), Is.False);

        // Zoom falls back to 1.0 when NaN, pan falls back to 0.0 when NaN/Infinity.
        Assert.That(slot.ZoomScale, Is.EqualTo(1.0));
        Assert.That(slot.PanX, Is.EqualTo(0.0));
        Assert.That(slot.PanY, Is.EqualTo(0.0));
    }

    [Test]
    public void SetZoomPan_ClampsZoomToConfiguredRange()
    {
        var slot = CreateSlot();

        slot.SetZoomPan(1000.0, 0, 0);
        Assert.That(slot.ZoomScale, Is.LessThanOrEqualTo(8.0));

        slot.SetZoomPan(-5.0, 0, 0);
        Assert.That(slot.ZoomScale, Is.GreaterThanOrEqualTo(0.1));
    }

    // ---- Audio state (indirect, via ApplyLinkedAudioState) ----

    [Test]
    public void ApplyLinkedAudioState_StoppedWithNoAudioLoaded_IsNoOpAndDoesNotThrow()
    {
        var slot = CreateSlot();
        int raiseCount = 0;
        slot.AudioStateChanged += (_, _) => raiseCount++;

        Assert.DoesNotThrow(() => slot.ApplyLinkedAudioState(AudioPlaybackState.Stopped, 0.0));

        Assert.That(raiseCount, Is.EqualTo(0));
    }

    [Test]
    public void ApplyLinkedAudioState_IdleWithNoAudioLoaded_IsNoOpAndDoesNotThrow()
    {
        var slot = CreateSlot();
        int raiseCount = 0;
        slot.AudioStateChanged += (_, _) => raiseCount++;

        Assert.DoesNotThrow(() => slot.ApplyLinkedAudioState(AudioPlaybackState.Idle, 0.5));

        Assert.That(raiseCount, Is.EqualTo(0));
    }

    [Test]
    public void ApplyLinkedAudioState_PlayingWithNoAudioLoaded_DoesNotThrow()
    {
        // With no wave player wired up, StartPlayback/SeekToProgress are no-ops guarded by null checks.
        var slot = CreateSlot();

        Assert.DoesNotThrow(() => slot.ApplyLinkedAudioState(AudioPlaybackState.Playing, 0.25));
        Assert.That(slot.PlaybackState, Is.EqualTo(AudioPlaybackState.Idle));
    }

    [Test]
    [Ignore("Exercising PlayCommand/PauseCommand/StopCommand against a real WaveOutEvent requires a live audio output device, which is not reliably available in a headless CI test environment and can hang or throw depending on the host.")]
    public void PlayPauseStopCommands_DriveRealPlaybackState()
    {
    }

    // ---- Dispose ----

    [Test]
    public void Dispose_DoesNotThrow()
    {
        var slot = CreateSlot();
        Assert.DoesNotThrow(() => slot.Dispose());
    }

    [Test]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        var slot = CreateSlot();
        slot.Dispose();
        Assert.DoesNotThrow(() => slot.Dispose());
    }

    // ---- Test doubles ----

    private sealed class StubDispatcher : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3, 4 }));
    }
}
