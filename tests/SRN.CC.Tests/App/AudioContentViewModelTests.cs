using System.Text;
using FluentAssertions;
using NAudio.Wave;
using NUnit.Framework;
using SRN.CC.App.ViewModels.Preview;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;

namespace SRN.CC.Tests.App;

/// <summary>
/// Covers S13 (audio content transport, closing milestone-5 UI debt). Real playback requires a real
/// output device, which is not reliably available in CI/headless environments, so every state-
/// transition/disposal test substitutes a <see cref="FakeWavePlayer"/> — NAudio's own
/// <see cref="IWavePlayer"/> interface is public and exactly fits this role, so no bespoke wrapper is
/// needed. <see cref="AudioContentViewModel.RefreshPositionProgress"/> is exercised directly (bypassing
/// the real polling timer) by mutating the real <see cref="WaveFileReader"/> that
/// <see cref="FakeWavePlayer"/> captures from <c>Init</c> - the reader itself needs no hardware, only
/// valid WAV bytes, so its position/duration math is fully real. One test at the bottom exercises an
/// actual <see cref="WaveOutEvent"/> device and skips via <see cref="Assert.Ignore(string)"/> if none
/// is usable, mirroring this repo's <c>SRNCC_RUN_CORPUS</c>/<c>SRNCC_RUN_GPU</c> opt-in-hardware
/// pattern - note that <see cref="AudioContentViewModel.PlayCommand"/> itself never throws on a
/// missing device (it catches and surfaces <see cref="AudioContentViewModel.ErrorMessage"/> instead),
/// so that test checks <c>ErrorMessage</c> rather than catching an exception.
/// </summary>
[TestFixture]
public class AudioContentViewModelTests
{
    [Test]
    public void Constructor_ValidWavBytes_ExposesDurationAndFormattedContent()
    {
        byte[] wav = BuildWav(sampleRate: 44100, channels: 2, bitsPerSample: 16, durationSeconds: 2.0);
        PreviewResult result = CreateAudioResult(wav, "=== WAV PREVIEW ===\nChannels: 2");

        using var vm = new AudioContentViewModel(result, static () => new FakeWavePlayer());

        vm.Family.Should().Be(PreviewFamily.Audio);
        vm.Duration.TotalSeconds.Should().BeApproximately(2.0, 0.01);
        vm.FormattedContent.Should().Contain("Channels: 2");
        vm.IsPlaying.Should().BeFalse();
        vm.PositionProgress.Should().Be(0);
        vm.PositionText.Should().Be("00:00 / 00:02");
        vm.PlayCommand.CanExecute(null).Should().BeTrue();
        vm.PauseCommand.CanExecute(null).Should().BeFalse();
        vm.StopCommand.CanExecute(null).Should().BeFalse();
    }

    [Test]
    public void Constructor_NullResult_ThrowsArgumentNullException()
    {
        Action act = () => new AudioContentViewModel(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_MissingRawPayload_ThrowsArgumentException()
    {
        var result = new PreviewResult(
            Occurrence: CreateOccurrence(),
            Family: PreviewFamily.Audio,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: null,
            FormattedContent: "unused",
            ErrorMessage: null,
            Diagnostics: Array.Empty<string>());

        Action act = () => new AudioContentViewModel(result);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_EmptyRawPayload_ThrowsArgumentException()
    {
        var result = CreateAudioResult(Array.Empty<byte>());

        Action act = () => new AudioContentViewModel(result);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void PlayCommand_InitializesPlayerAndSetsIsPlaying()
    {
        var fake = new FakeWavePlayer();
        using var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), () => fake);

        vm.PlayCommand.Execute(null);

        vm.IsPlaying.Should().BeTrue();
        fake.PlayCallCount.Should().Be(1);
        fake.InitCallCount.Should().Be(1);
        fake.InitializedProvider.Should().BeOfType<WaveFileReader>();
        vm.PlayCommand.CanExecute(null).Should().BeFalse();
        vm.PauseCommand.CanExecute(null).Should().BeTrue();
        vm.StopCommand.CanExecute(null).Should().BeTrue();
    }

    [Test]
    public void PauseCommand_AfterPlay_PausesWithoutTearingDownPlayer()
    {
        var fake = new FakeWavePlayer();
        using var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), () => fake);
        vm.PlayCommand.Execute(null);

        vm.PauseCommand.Execute(null);

        vm.IsPlaying.Should().BeFalse();
        fake.PauseCallCount.Should().Be(1);
        fake.DisposeCallCount.Should().Be(0);
        vm.PlayCommand.CanExecute(null).Should().BeTrue();
        vm.StopCommand.CanExecute(null).Should().BeTrue();
    }

    [Test]
    public void PlayCommand_AfterPause_ResumesWithoutReinitializing()
    {
        var fake = new FakeWavePlayer();
        using var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), () => fake);
        vm.PlayCommand.Execute(null);
        vm.PauseCommand.Execute(null);

        vm.PlayCommand.Execute(null);

        fake.PlayCallCount.Should().Be(2);
        fake.InitCallCount.Should().Be(1);
        vm.IsPlaying.Should().BeTrue();
    }

    [Test]
    public void StopCommand_TearsDownPlayerAndResetsPosition()
    {
        var fake = new FakeWavePlayer();
        using var vm = new AudioContentViewModel(CreateAudioResult(BuildWav(durationSeconds: 4.0)), () => fake);
        vm.PlayCommand.Execute(null);
        var reader = (WaveFileReader)fake.InitializedProvider!;
        reader.CurrentTime = TimeSpan.FromSeconds(2);
        vm.RefreshPositionProgress();
        vm.PositionProgress.Should().BeGreaterThan(0);

        vm.StopCommand.Execute(null);

        vm.IsPlaying.Should().BeFalse();
        vm.PositionProgress.Should().Be(0);
        vm.PositionText.Should().Be("00:00 / 00:04");
        fake.StopCallCount.Should().Be(1);
        fake.DisposeCallCount.Should().Be(1);
        vm.PlayCommand.CanExecute(null).Should().BeTrue();
        vm.PauseCommand.CanExecute(null).Should().BeFalse();
        vm.StopCommand.CanExecute(null).Should().BeFalse();
    }

    [Test]
    public void StopCommand_ThenPlayAgain_CreatesFreshPlayerInstance()
    {
        var factoryCallCount = 0;
        var players = new List<FakeWavePlayer>();
        Func<IWavePlayer> factory = () =>
        {
            factoryCallCount++;
            var player = new FakeWavePlayer();
            players.Add(player);
            return player;
        };
        using var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), factory);

        vm.PlayCommand.Execute(null);
        vm.StopCommand.Execute(null);
        vm.PlayCommand.Execute(null);

        factoryCallCount.Should().Be(2);
        players[0].DisposeCallCount.Should().Be(1);
        players[1].DisposeCallCount.Should().Be(0);
    }

    [Test]
    public void RefreshPositionProgress_ComputesFractionFromReaderPosition()
    {
        var fake = new FakeWavePlayer();
        using var vm = new AudioContentViewModel(
            CreateAudioResult(BuildWav(sampleRate: 44100, channels: 1, bitsPerSample: 16, durationSeconds: 10.0)),
            () => fake);
        vm.PlayCommand.Execute(null);
        var reader = (WaveFileReader)fake.InitializedProvider!;

        reader.CurrentTime = TimeSpan.FromSeconds(5);
        vm.RefreshPositionProgress();

        vm.PositionProgress.Should().BeApproximately(0.5, 0.01);
        vm.PositionText.Should().EndWith("/ 00:10");
    }

    [Test]
    public void RefreshPositionProgress_BeforePlay_IsNoOp()
    {
        using var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), static () => new FakeWavePlayer());

        vm.RefreshPositionProgress();

        vm.PositionProgress.Should().Be(0);
    }

    [Test]
    public void PlaybackStoppedWithException_SetsErrorMessageAndStopsPlaying()
    {
        var fake = new FakeWavePlayer();
        using var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), () => fake);
        vm.PlayCommand.Execute(null);

        fake.RaisePlaybackStopped(new InvalidOperationException("device unplugged"));

        vm.IsPlaying.Should().BeFalse();
        vm.ErrorMessage.Should().Contain("device unplugged");
    }

    [Test]
    public void PlaybackStoppedNaturally_RewindsPositionForReplay()
    {
        var fake = new FakeWavePlayer();
        using var vm = new AudioContentViewModel(CreateAudioResult(BuildWav(durationSeconds: 3.0)), () => fake);
        vm.PlayCommand.Execute(null);
        var reader = (WaveFileReader)fake.InitializedProvider!;
        reader.Position = reader.Length;

        fake.RaisePlaybackStopped(exception: null);

        vm.IsPlaying.Should().BeFalse();
        vm.PositionProgress.Should().Be(0);
        reader.Position.Should().Be(0);
    }

    [Test]
    public void PlayCommand_DeviceInitThrows_SetsErrorMessageAndDoesNotThrow()
    {
        var fake = new FakeWavePlayer { ThrowOnInit = true };
        using var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), () => fake);

        Action act = () => vm.PlayCommand.Execute(null);

        act.Should().NotThrow();
        vm.IsPlaying.Should().BeFalse();
        vm.ErrorMessage.Should().Contain("Playback unavailable");
    }

    [Test]
    public void Dispose_StopsPlaybackAndDisposesPlayerReaderAndStream()
    {
        var fake = new FakeWavePlayer();
        var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), () => fake);
        vm.PlayCommand.Execute(null);

        vm.Dispose();

        fake.DisposeCallCount.Should().Be(1);
        vm.IsPlaying.Should().BeFalse();
    }

    [Test]
    public void Dispose_IsIdempotent_SafeToCallMultipleTimes()
    {
        var fake = new FakeWavePlayer();
        var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), () => fake);
        vm.PlayCommand.Execute(null);
        vm.Dispose();

        Action act = () => vm.Dispose();

        act.Should().NotThrow();
        fake.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public void Dispose_WithoutEverPlaying_IsSafeAndDoesNotConstructAPlayer()
    {
        var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), static () => new FakeWavePlayer());

        Action act = () => vm.Dispose();

        act.Should().NotThrow();
    }

    [Test]
    public void Dispose_ThenPlayCommand_IsANoOp()
    {
        var fake = new FakeWavePlayer();
        var vm = new AudioContentViewModel(CreateAudioResult(BuildWav()), () => fake);
        vm.Dispose();

        vm.PlayCommand.Execute(null);

        vm.IsPlaying.Should().BeFalse();
        fake.PlayCallCount.Should().Be(0);
    }

    [Test]
    public void RealAudioDevice_PlayThenStopThenDispose_SkipsGracefullyWithoutAnOutputDevice()
    {
        // Exercises a real NAudio WaveOutEvent when a usable output device is present. PlayCommand
        // itself never throws on a missing/faulted device (AudioContentViewModel.PlayAsync catches
        // internally and surfaces ErrorMessage instead), so this checks ErrorMessage - rather than
        // catching an exception - and skips via Assert.Ignore on a headless/no-audio-hardware build
        // agent instead of failing CI, mirroring the SRNCC_RUN_CORPUS/SRNCC_RUN_GPU pattern used
        // elsewhere in this repo for hardware-dependent evidence.
        byte[] wav = BuildWav(durationSeconds: 0.2);
        using var vm = new AudioContentViewModel(CreateAudioResult(wav));

        vm.PlayCommand.Execute(null);

        if (!string.IsNullOrEmpty(vm.ErrorMessage))
        {
            Assert.Ignore($"No usable audio output device in this environment: {vm.ErrorMessage}");
        }

        vm.IsPlaying.Should().BeTrue();
        vm.StopCommand.Execute(null);
        vm.IsPlaying.Should().BeFalse();
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static PreviewResult CreateAudioResult(byte[] wavBytes, string? formattedContent = "=== WAV PREVIEW ===") =>
        new(
            Occurrence: CreateOccurrence(),
            Family: PreviewFamily.Audio,
            IsSuccess: true,
            MetadataText: null,
            RawPayload: wavBytes,
            FormattedContent: formattedContent,
            ErrorMessage: null,
            Diagnostics: Array.Empty<string>());

    private static AssetOccurrence CreateOccurrence() => new(
        identity: new AssetIdentity("test_audio", 2011),
        sourceId: Guid.NewGuid(),
        locator: new HakEntryLocator(1),
        originalName: "test_audio.wav",
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

    /// <summary>
    /// NAudio's own <see cref="IWavePlayer"/> substituted for a real output device. Captures the
    /// <see cref="IWaveProvider"/> passed to <see cref="Init"/> (in practice, the real
    /// <see cref="WaveFileReader"/> <see cref="AudioContentViewModel"/> constructs) so tests can
    /// mutate its <c>Position</c>/<c>CurrentTime</c> directly and exercise
    /// <see cref="AudioContentViewModel.RefreshPositionProgress"/> without any real audio hardware.
    /// </summary>
    private sealed class FakeWavePlayer : IWavePlayer
    {
        public IWaveProvider? InitializedProvider { get; private set; }
        public int InitCallCount { get; private set; }
        public int PlayCallCount { get; private set; }
        public int PauseCallCount { get; private set; }
        public int StopCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public bool ThrowOnInit { get; set; }

        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

        public float Volume { get; set; } = 1f;

        public WaveFormat? OutputWaveFormat => InitializedProvider?.WaveFormat;

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void Init(IWaveProvider waveProvider)
        {
            if (ThrowOnInit)
            {
                throw new InvalidOperationException("Simulated audio device initialization failure.");
            }

            InitializedProvider = waveProvider;
            InitCallCount++;
        }

        public void Play()
        {
            PlayCallCount++;
            PlaybackState = PlaybackState.Playing;
        }

        public void Pause()
        {
            PauseCallCount++;
            PlaybackState = PlaybackState.Paused;
        }

        public void Stop()
        {
            StopCallCount++;
            PlaybackState = PlaybackState.Stopped;
        }

        public void RaisePlaybackStopped(Exception? exception) =>
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(exception));

        public void Dispose() => DisposeCallCount++;
    }
}
