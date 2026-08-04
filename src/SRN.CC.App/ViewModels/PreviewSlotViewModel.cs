using System.Timers;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.Wave;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Cache;
using SRN.CC.Preview;

namespace SRN.CC.App.ViewModels;

public enum AudioPlaybackState
{
    Idle,
    Playing,
    Paused,
    Stopped
}

public partial class PreviewSlotViewModel : ObservableObject, IDisposable
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;

    private readonly PreviewEngine _previewEngine;
    private readonly Func<AssetOccurrence, Task> _onPinRequested;
    private readonly ISqliteCacheService? _cacheService;
    private CancellationTokenSource? _currentCts;
    private bool _canPin = true;
    private bool _isApplyingLinkedState;

    private WaveOutEvent? _wavePlayer;
    private WaveFileReader? _waveReader;
    private MemoryStream? _audioStream;
    private System.Timers.Timer? _positionTimer;

    public event EventHandler? ZoomPanChanged;
    public event EventHandler? AudioStateChanged;

    public int SlotIndex { get; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _slotTitle = string.Empty;

    [ObservableProperty]
    private CuratedAsset? _assignedAsset;

    [ObservableProperty]
    private AssetOccurrence? _assignedOccurrence;

    [ObservableProperty]
    private AssetSource? _assignedSource;

    [ObservableProperty]
    private PreviewFamily _preferredFamily = PreviewFamily.Metadata;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _formattedContent;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _diagnosticsText;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isPinEnabled;

    [ObservableProperty]
    private double _zoomScale = 1.0;

    [ObservableProperty]
    private double _panX;

    [ObservableProperty]
    private double _panY;

    [ObservableProperty]
    private Bitmap? _rawImageBitmap;

    [ObservableProperty]
    private AudioPlaybackState _playbackState = AudioPlaybackState.Idle;

    [ObservableProperty]
    private double _positionProgress;

    public PreviewSlotViewModel(
        int slotIndex,
        PreviewEngine previewEngine,
        Func<AssetOccurrence, Task> onPinRequested,
        ISqliteCacheService? cacheService = null)
    {
        SlotIndex = slotIndex;
        _previewEngine = previewEngine ?? throw new ArgumentNullException(nameof(previewEngine));
        _onPinRequested = onPinRequested ?? throw new ArgumentNullException(nameof(onPinRequested));
        _cacheService = cacheService;
        SlotTitle = $"Slot {slotIndex + 1}";
    }

    private void RunAsLinkedApply(Action action)
    {
        _isApplyingLinkedState = true;
        try
        {
            action();
        }
        finally
        {
            _isApplyingLinkedState = false;
        }
    }

    private void RaiseIfNotApplyingLinkedState(EventHandler? handler)
    {
        if (!_isApplyingLinkedState)
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
    }

    private static double ClampZoom(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 1.0;
        return Math.Clamp(value, MinZoom, MaxZoom);
    }

    private static double SanitizePan(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0.0;
        return value;
    }

    public void SetZoomPan(double zoom, double panX, double panY)
    {
        ZoomScale = ClampZoom(zoom);
        PanX = SanitizePan(panX);
        PanY = SanitizePan(panY);

        RaiseIfNotApplyingLinkedState(ZoomPanChanged);
    }

    public void ApplyLinkedZoomPan(double zoom, double panX, double panY)
    {
        RunAsLinkedApply(() =>
        {
            ZoomScale = ClampZoom(zoom);
            PanX = SanitizePan(panX);
            PanY = SanitizePan(panY);
        });
    }

    public void ApplyLinkedAudioState(AudioPlaybackState state, double positionProgress)
    {
        RunAsLinkedApply(() =>
        {
            switch (state)
            {
                case AudioPlaybackState.Playing:
                    SeekToProgress(positionProgress);
                    StartPlayback();
                    break;
                case AudioPlaybackState.Paused:
                    SeekToProgress(positionProgress);
                    PausePlayback();
                    break;
                case AudioPlaybackState.Stopped:
                case AudioPlaybackState.Idle:
                    StopPlayback();
                    break;
            }
        });
    }

    public async Task AssignOccurrenceAsync(CuratedAsset? asset, AssetOccurrence? occ, AssetSource? source)
    {
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _currentCts = new CancellationTokenSource();

        StopAndDisposeAudio();
        RawImageBitmap = null;
        ZoomScale = 1.0;
        PanX = 0.0;
        PanY = 0.0;

        AssignedAsset = asset;
        AssignedOccurrence = occ;
        AssignedSource = source;

        if (occ == null || source == null)
        {
            IsActive = false;
            IsLoading = false;
            SlotTitle = $"Slot {SlotIndex + 1} [Empty]";
            // Revert to a plain-text family so the message below actually renders — the
            // Image/Audio panels are hidden once a slot has no assignment.
            PreferredFamily = PreviewFamily.Metadata;
            FormattedContent = "No asset assigned to this slot.";
            ErrorMessage = null;
            DiagnosticsText = null;
            IsPinned = false;
            UpdatePinAvailability();
            return;
        }

        IsActive = true;
        SlotTitle = $"Slot {SlotIndex + 1}: {occ.Identity.Resref}.{occ.Identity.ResourceType} ({source.Kind})";
        IsPinned = asset?.Pin != null && asset.Pin.SourceId == source.Id && asset.Pin.Locator.Equals(occ.Locator);

        IsLoading = true;
        UpdatePinAvailability();
        FormattedContent = null;
        ErrorMessage = null;
        DiagnosticsText = null;

        var token = _currentCts.Token;

        try
        {
            if (PreferredFamily == PreviewFamily.Image && _cacheService != null && source.Fingerprint != null)
            {
                PreviewCachePayload? cached = null;
                try
                {
                    cached = await _cacheService.TryGetPreviewAsync(source.Fingerprint, occ, token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception cacheEx)
                {
                    DiagnosticsText = $"Cache read failed: {cacheEx.Message}";
                }

                if (cached != null)
                {
                    if (token.IsCancellationRequested) return;

                    try
                    {
                        using var ms = new MemoryStream(cached.PngBytes);
                        RawImageBitmap = new Bitmap(ms);
                        FormattedContent = null;
                        ErrorMessage = null;
                        IsLoading = false;
                        UpdatePinAvailability();
                        return;
                    }
                    catch (Exception decodeEx)
                    {
                        DiagnosticsText = $"Cache decode failed: {decodeEx.Message}";
                        RawImageBitmap = null;
                    }
                }
            }

            var req = new PreviewRequest(occ, source, PreferredFamily);
            var res = await _previewEngine.ExecutePreviewAsync(req, debounceMs: 150, cancellationToken: token).ConfigureAwait(true);

            if (token.IsCancellationRequested) return;

            IsLoading = false;
            if (res.IsSuccess)
            {
                FormattedContent = res.FormattedContent;
                ErrorMessage = null;
                DiagnosticsText = res.Diagnostics.Count > 0 ? string.Join("\n", res.Diagnostics) : null;

                if (res.Family == PreviewFamily.Image && res.RawPayload != null && res.Width is int w && res.Height is int h && w > 0 && h > 0)
                {
                    try
                    {
                        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                        using (var fb = bmp.Lock())
                        {
                            System.Runtime.InteropServices.Marshal.Copy(res.RawPayload, 0, fb.Address, res.RawPayload.Length);
                        }

                        RawImageBitmap = bmp;

                        if (_cacheService != null && source.Fingerprint != null)
                        {
                            try
                            {
                                using var pngStream = new MemoryStream();
                                bmp.Save(pngStream, options: new PngBitmapEncoderOptions());
                                await _cacheService.SavePreviewAsync(source.Fingerprint, occ, w, h, pngStream.ToArray(), token).ConfigureAwait(true);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception cacheSaveEx)
                            {
                                DiagnosticsText = string.IsNullOrEmpty(DiagnosticsText)
                                    ? $"Cache save failed: {cacheSaveEx.Message}"
                                    : $"{DiagnosticsText}\nCache save failed: {cacheSaveEx.Message}";
                            }
                        }
                    }
                    catch (Exception bmpEx)
                    {
                        RawImageBitmap = null;
                        ErrorMessage = bmpEx.Message;
                    }
                }
                else if (res.Family == PreviewFamily.Audio && res.RawPayload != null)
                {
                    SetupAudio(res.RawPayload);
                }
            }
            else
            {
                FormattedContent = null;
                ErrorMessage = res.ErrorMessage;
                DiagnosticsText = string.Join("\n", res.Diagnostics);
            }

            UpdatePinAvailability();
        }
        catch (OperationCanceledException)
        {
            // Canceled
            UpdatePinAvailability();
        }
        catch (Exception ex)
        {
            IsLoading = false;
            ErrorMessage = ex.Message;
            UpdatePinAvailability();
        }
    }

    private void SetupAudio(byte[] wavBytes)
    {
        StopAndDisposeAudio();

        try
        {
            _audioStream = new MemoryStream(wavBytes);
            _waveReader = new WaveFileReader(_audioStream);
            _wavePlayer = new WaveOutEvent();
            _wavePlayer.Init(_waveReader);
            _wavePlayer.PlaybackStopped += OnPlaybackStopped;
            PlaybackState = AudioPlaybackState.Idle;
            PositionProgress = 0.0;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            PlaybackState = AudioPlaybackState.Idle;
            StopAndDisposeAudio();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.Exception != null)
            {
                ErrorMessage = e.Exception.Message;
            }

            // WaveOutEvent raises this both on explicit Stop() (state already transitioned there)
            // and when playback finishes naturally — only the latter needs a transition here.
            if (PlaybackState == AudioPlaybackState.Playing)
            {
                PlaybackState = AudioPlaybackState.Stopped;
                StopPositionTimer();
                RaiseIfNotApplyingLinkedState(AudioStateChanged);
            }
        });
    }

    [RelayCommand]
    private void Play()
    {
        StartPlayback();
        RaiseIfNotApplyingLinkedState(AudioStateChanged);
    }

    [RelayCommand]
    private void Pause()
    {
        PausePlayback();
        RaiseIfNotApplyingLinkedState(AudioStateChanged);
    }

    [RelayCommand]
    private void Stop()
    {
        StopPlayback();
        RaiseIfNotApplyingLinkedState(AudioStateChanged);
    }

    private void StartPlayback()
    {
        if (_wavePlayer == null || _waveReader == null) return;

        try
        {
            _wavePlayer.Play();
            PlaybackState = AudioPlaybackState.Playing;
            StartPositionTimer();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            PlaybackState = AudioPlaybackState.Idle;
        }
    }

    private void PausePlayback()
    {
        if (_wavePlayer == null) return;

        try
        {
            _wavePlayer.Pause();
            PlaybackState = AudioPlaybackState.Paused;
            StopPositionTimer();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void StopPlayback()
    {
        if (_wavePlayer == null) return;

        try
        {
            _wavePlayer.Stop();
            if (_waveReader != null)
            {
                _waveReader.CurrentTime = TimeSpan.Zero;
            }
            PositionProgress = 0.0;
            PlaybackState = AudioPlaybackState.Stopped;
            StopPositionTimer();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void SeekToProgress(double progress)
    {
        if (_waveReader == null) return;

        try
        {
            double clamped = Math.Clamp(double.IsNaN(progress) || double.IsInfinity(progress) ? 0.0 : progress, 0.0, 1.0);
            // Seek via CurrentTime rather than a raw byte offset: WaveFileReader block-aligns
            // TimeSpan-based seeks internally, whereas an arbitrary byte offset can land mid-sample.
            _waveReader.CurrentTime = TimeSpan.FromTicks((long)(clamped * _waveReader.TotalTime.Ticks));
            PositionProgress = clamped;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void StartPositionTimer()
    {
        StopPositionTimer();
        _positionTimer = new System.Timers.Timer(150);
        _positionTimer.Elapsed += OnPositionTimerElapsed;
        _positionTimer.AutoReset = true;
        _positionTimer.Start();
    }

    private void StopPositionTimer()
    {
        if (_positionTimer != null)
        {
            _positionTimer.Elapsed -= OnPositionTimerElapsed;
            _positionTimer.Stop();
            _positionTimer.Dispose();
            _positionTimer = null;
        }
    }

    private void OnPositionTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        // Timer callbacks fire on a thread-pool thread; marshal onto the UI thread before touching
        // ObservableProperty state or NAudio objects that AssignOccurrenceAsync may concurrently dispose.
        Dispatcher.UIThread.Post(() =>
        {
            if (_waveReader == null || PlaybackState != AudioPlaybackState.Playing) return;

            try
            {
                double progress = _waveReader.TotalTime.Ticks > 0
                    ? (double)_waveReader.CurrentTime.Ticks / _waveReader.TotalTime.Ticks
                    : 0.0;
                PositionProgress = Math.Clamp(progress, 0.0, 1.0);

                RaiseIfNotApplyingLinkedState(AudioStateChanged);
            }
            catch
            {
                // Best-effort progress tick; ignore transient failures.
            }
        });
    }

    private void StopAndDisposeAudio()
    {
        StopPositionTimer();

        if (_wavePlayer != null)
        {
            try
            {
                _wavePlayer.PlaybackStopped -= OnPlaybackStopped;
                _wavePlayer.Stop();
            }
            catch
            {
                // Best-effort cleanup.
            }

            _wavePlayer.Dispose();
            _wavePlayer = null;
        }

        _waveReader?.Dispose();
        _waveReader = null;

        _audioStream?.Dispose();
        _audioStream = null;

        PlaybackState = AudioPlaybackState.Idle;
        PositionProgress = 0.0;
    }

    [RelayCommand(CanExecute = nameof(CanPinThisOccurrence))]
    private async Task PinThisOccurrenceAsync()
    {
        if (AssignedOccurrence != null)
        {
            await _onPinRequested(AssignedOccurrence).ConfigureAwait(false);
            IsPinned = true;
        }
    }

    private bool CanPinThisOccurrence() => IsPinEnabled;

    public void SetPinEnabled(bool canPin)
    {
        _canPin = canPin;
        UpdatePinAvailability();
        PinThisOccurrenceCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SwitchFamilyAsync(string familyName)
    {
        if (Enum.TryParse<PreviewFamily>(familyName, out var fam))
        {
            PreferredFamily = fam;
            if (AssignedAsset != null && AssignedOccurrence != null && AssignedSource != null)
            {
                await AssignOccurrenceAsync(AssignedAsset, AssignedOccurrence, AssignedSource).ConfigureAwait(false);
            }
        }
    }

    partial void OnIsActiveChanged(bool value) => UpdatePinAvailability();
    partial void OnAssignedOccurrenceChanged(AssetOccurrence? value) => UpdatePinAvailability();

    private void UpdatePinAvailability()
    {
        IsPinEnabled = IsActive && _canPin && AssignedOccurrence != null && AssignedSource != null && !IsLoading;
        PinThisOccurrenceCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        StopAndDisposeAudio();
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _currentCts = null;
    }
}
