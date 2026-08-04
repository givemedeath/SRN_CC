using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.Wave;
using SRN.CC.Core.Preview;

namespace SRN.CC.App.ViewModels.Preview;

/// <summary>
/// Audio transport for <see cref="PreviewFamily.Audio"/> slots (architecture decision A3). Closes
/// the milestone-5 UI debt described in <c>docs/MILESTONE-5.md</c> — audio playback was specified
/// (<c>PlayCommand</c>/<c>PauseCommand</c>/<c>StopCommand</c>/<c>PositionProgress</c>, "bind NAudio
/// wave player") but never built; the preview surface stayed a formatted-metadata text dump.
///
/// <para>
/// <see cref="AudioPreviewProvider"/> hands back the raw WAV byte content in
/// <see cref="PreviewResult.RawPayload"/> (RIFF/WAVE, any BMU 8-byte preamble already stripped) plus
/// a formatted metadata block in <see cref="PreviewResult.FormattedContent"/>. This view model wraps
/// a NAudio playback pipeline directly over those bytes: an owned <see cref="MemoryStream"/> feeds a
/// <see cref="WaveFileReader"/> (parses the RIFF chunks and exposes decoded PCM plus
/// <c>CurrentTime</c>/<c>TotalTime</c>), which in turn feeds a <see cref="WaveOutEvent"/> output
/// device. <see cref="WaveOutEvent"/> is the simplest, most broadly compatible desktop output NAudio
/// offers (event-callback waveOut/WASAPI hybrid, no dedicated worker-thread callback plumbing to
/// manage) — appropriate for a short preview clip, not a low-latency pro-audio scenario.
/// </para>
///
/// <para>
/// Construction never touches an audio device: only the WAV header is parsed (to populate
/// <see cref="Duration"/> and validate the bytes), via a throwaway <see cref="WaveFileReader"/> over
/// a separate probe stream that is disposed immediately. The real device is opened lazily on the
/// first <see cref="PlayAsync"/>, and any failure to open one (no sound card, driver failure,
/// headless CI) is caught and surfaced through <see cref="ErrorMessage"/> instead of throwing into
/// the UI.
/// </para>
///
/// <para>
/// Testability: NAudio already exposes <see cref="IWavePlayer"/> as a public interface covering
/// exactly <c>Play</c>/<c>Pause</c>/<c>Stop</c>/<c>Init</c>/<c>PlaybackState</c>/<c>PlaybackStopped</c>
/// — so no bespoke wrapper is introduced. The concrete player is created through an injectable
/// factory (defaulting to <c>() =&gt; new WaveOutEvent()</c>), so tests can substitute a fake
/// implementation and exercise Play/Pause/Stop state transitions and disposal without a real audio
/// device. <see cref="RefreshPositionProgress"/> is public specifically so tests can drive the
/// position calculation deterministically (advance <see cref="WaveFileReader.Position"/> directly,
/// then call it) without waiting on a real timer tick.
/// </para>
///
/// <para>
/// Slot isolation (the milestone-5 spec's explicit requirement): <see cref="Dispose"/> is idempotent
/// — safe to call multiple times, never throws even if playback already stopped or a device was
/// never opened — and unconditionally stops the position-polling timer, unsubscribes from
/// <see cref="IWavePlayer.PlaybackStopped"/> (so a late callback can never reach a disposed view
/// model), stops and disposes the wave player, and disposes the <see cref="WaveFileReader"/> and its
/// backing <see cref="MemoryStream"/>. <see cref="PreviewSlotViewModel"/> calls this on every
/// <c>Content</c> reassignment (including reassignment to <c>null</c> on slot clear), so a
/// still-playing slot can never survive a slot switch.
/// </para>
/// </summary>
public sealed partial class AudioContentViewModel : PreviewContentViewModel
{
    private readonly byte[] _wavBytes;
    private readonly Func<IWavePlayer> _wavePlayerFactory;
    private readonly System.Timers.Timer _positionTimer;

    private MemoryStream? _playbackStream;
    private WaveFileReader? _playbackReader;
    private IWavePlayer? _wavePlayer;
    private bool _disposed;

    public override PreviewFamily Family => PreviewFamily.Audio;

    /// <summary>The formatted WAV metadata block from <see cref="AudioPreviewProvider"/> (format,
    /// channels, sample rate, bit depth, etc.) for display alongside the transport controls.</summary>
    public string? FormattedContent { get; }

    /// <summary>Total clip duration, parsed once at construction time from the WAV header.</summary>
    public TimeSpan Duration { get; }

    [ObservableProperty]
    private bool _isPlaying;

    /// <summary>Playback position as a 0.0-1.0 fraction of <see cref="Duration"/>.</summary>
    [ObservableProperty]
    private double _positionProgress;

    /// <summary>Human-readable "mm:ss / mm:ss" position text for display next to the transport.</summary>
    [ObservableProperty]
    private string _positionText = "00:00 / 00:00";

    [ObservableProperty]
    private string? _errorMessage;

    /// <param name="result">
    /// A successful, <see cref="PreviewFamily.Audio"/> preview result whose
    /// <see cref="PreviewResult.RawPayload"/> is non-empty raw WAV bytes. This is the contract
    /// enforced by <c>PreviewContentFactory</c>'s <c>Family == PreviewFamily.Audio</c> switch arm.
    /// </param>
    /// <param name="wavePlayerFactory">
    /// Creates the <see cref="IWavePlayer"/> used for actual playback. Defaults to
    /// <c>() =&gt; new WaveOutEvent()</c>; tests substitute a fake to exercise state transitions and
    /// disposal without a real audio device.
    /// </param>
    public AudioContentViewModel(PreviewResult result, Func<IWavePlayer>? wavePlayerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RawPayload is not { Length: > 0 } rawPayload)
        {
            throw new ArgumentException(
                "Audio content requires PreviewResult.RawPayload to contain raw WAV bytes.",
                nameof(result));
        }

        _wavBytes = rawPayload;
        FormattedContent = result.FormattedContent;
        _wavePlayerFactory = wavePlayerFactory ?? (static () => new WaveOutEvent());

        using (var probeStream = new MemoryStream(_wavBytes, writable: false))
        using (var probeReader = new WaveFileReader(probeStream))
        {
            Duration = probeReader.TotalTime;
        }

        _positionText = FormatPosition(TimeSpan.Zero, Duration);

        _positionTimer = new System.Timers.Timer(100) { AutoReset = true };
        _positionTimer.Elapsed += OnPositionTimerElapsed;
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task PlayAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        try
        {
            if (_wavePlayer is null)
            {
                _playbackStream = new MemoryStream(_wavBytes, writable: false);
                _playbackReader = new WaveFileReader(_playbackStream);
                _wavePlayer = _wavePlayerFactory();
                _wavePlayer.PlaybackStopped += OnPlaybackStopped;
                _wavePlayer.Init(_playbackReader);
            }

            _wavePlayer.Play();
            IsPlaying = true;
            ErrorMessage = null;
            _positionTimer.Start();
        }
        catch (Exception ex)
        {
            // No output device, driver failure, unsupported format, etc. Degrade to an error
            // message rather than throwing into the UI or crashing the slot.
            ErrorMessage = $"Playback unavailable: {ex.Message}";
            IsPlaying = false;
            TearDownPlaybackResources();
        }
        finally
        {
            RaiseCommandsCanExecuteChanged();
        }

        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (_disposed || _wavePlayer is null)
        {
            return;
        }

        try
        {
            _wavePlayer.Pause();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Pause failed: {ex.Message}";
        }
        finally
        {
            IsPlaying = false;
            _positionTimer.Stop();
            RaiseCommandsCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _positionTimer.Stop();
        TearDownPlaybackResources();
        IsPlaying = false;
        PositionProgress = 0d;
        PositionText = FormatPosition(TimeSpan.Zero, Duration);
        RaiseCommandsCanExecuteChanged();
    }

    private bool CanPlay() => !_disposed && !IsPlaying;
    private bool CanPause() => !_disposed && IsPlaying;
    private bool CanStop() => !_disposed && (IsPlaying || _wavePlayer is not null);

    private void RaiseCommandsCanExecuteChanged()
    {
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// NAudio's timer callback (<see cref="Timer.Elapsed"/>) fires on a thread-pool thread with no
    /// synchronization-context capture, unlike <see cref="IWavePlayer.PlaybackStopped"/> (which NAudio
    /// itself posts back through whatever <see cref="SynchronizationContext"/> was current when
    /// <see cref="IWavePlayer.Init"/> was called — the UI thread, in normal use, since
    /// <see cref="PlayAsync"/> runs as a UI-bound command). So this one callback is explicitly
    /// marshaled onto the UI thread; <see cref="RefreshPositionProgress"/> itself stays a plain,
    /// synchronous method that tests call directly, bypassing the timer and the dispatcher entirely.
    /// </summary>
    private void OnPositionTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e) =>
        Dispatcher.UIThread.Post(RefreshPositionProgress);

    /// <summary>
    /// Recomputes <see cref="PositionProgress"/>/<see cref="PositionText"/> from the underlying
    /// reader's current position. Public so tests can drive it deterministically — advance
    /// <see cref="WaveFileReader.Position"/> directly and call this — without a real timer tick or a
    /// real output device.
    /// </summary>
    public void RefreshPositionProgress()
    {
        if (_disposed || _playbackReader is null)
        {
            return;
        }

        TimeSpan current = _playbackReader.CurrentTime;
        PositionProgress = Duration > TimeSpan.Zero
            ? Math.Clamp(current.TotalSeconds / Duration.TotalSeconds, 0d, 1d)
            : 0d;
        PositionText = FormatPosition(current, Duration);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        IsPlaying = false;
        _positionTimer.Stop();

        if (e.Exception is not null)
        {
            ErrorMessage = $"Playback stopped unexpectedly: {e.Exception.Message}";
        }
        else if (_playbackReader is not null)
        {
            // Natural end of stream (our own Stop() unsubscribes before calling player.Stop(), so
            // this branch is only reached on completion) - rewind so pressing Play again restarts
            // from the beginning instead of immediately re-triggering PlaybackStopped.
            _playbackReader.Position = 0;
            PositionProgress = 0d;
            PositionText = FormatPosition(TimeSpan.Zero, Duration);
        }

        RaiseCommandsCanExecuteChanged();
    }

    private void TearDownPlaybackResources()
    {
        IsPlaying = false;

        if (_wavePlayer is not null)
        {
            _wavePlayer.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                _wavePlayer.Stop();
            }
            catch
            {
                // Best-effort: device may already be closed or faulted. Disposal must never throw.
            }

            _wavePlayer.Dispose();
            _wavePlayer = null;
        }

        _playbackReader?.Dispose();
        _playbackReader = null;

        _playbackStream?.Dispose();
        _playbackStream = null;
    }

    private static string FormatPosition(TimeSpan current, TimeSpan total) =>
        $"{FormatTimeSpan(current)} / {FormatTimeSpan(total)}";

    private static string FormatTimeSpan(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");

    /// <summary>
    /// Idempotent - safe to call multiple times (including implicitly, e.g. if a slot is cleared
    /// twice) and never throws even if playback already stopped or a device was never opened. Stops
    /// the polling timer, tears down the wave player/reader/stream, and forgets them so a leaked
    /// NAudio device or a still-running timer can never outlive the slot that owned this instance.
    /// </summary>
    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _positionTimer.Stop();
        _positionTimer.Elapsed -= OnPositionTimerElapsed;
        _positionTimer.Dispose();

        TearDownPlaybackResources();

        GC.SuppressFinalize(this);
    }
}
