using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;

namespace SRN.CC.Tests.Preview;

[TestFixture]
public class AudioPreviewProviderTests
{
    private static byte[] BuildWav(
        ushort channels = 1,
        uint sampleRate = 8000,
        ushort bitsPerSample = 16,
        byte[]? sampleData = null)
    {
        sampleData ??= new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

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
        // RIFF chunk size = 4 ("WAVE") + (8 + fmtChunkSize) + (8 + dataChunkSize)
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

    private static AssetOccurrence CreateOccurrence(string originalName)
    {
        AssetIdentity id = new AssetIdentity("audio_resref", 2005);
        AssetSource source = AssetSource.CreateFolder("c:/test/audio", 0);
        return new AssetOccurrence(
            identity: id,
            sourceId: source.Id,
            locator: new FolderFileLocator(originalName),
            originalName: originalName,
            size: 100,
            validationState: ValidationState.Valid,
            extensionMetadata: null,
            sha256: Encoding.ASCII.GetBytes("hash1234567890123456789012345678"));
    }

    private static PreviewRequest CreateRequest(string originalName)
    {
        AssetOccurrence occ = CreateOccurrence(originalName);
        AssetSource source = AssetSource.CreateFolder("c:/test/audio", 0);
        return new PreviewRequest(occ, source, PreviewFamily.Audio);
    }

    [Test]
    public async Task GeneratePreviewAsync_ValidWav_ReturnsSuccessWithFormattedMetadata()
    {
        byte[] wavBytes = BuildWav(channels: 1, sampleRate: 8000, bitsPerSample: 16);
        var request = CreateRequest("sound.wav");
        var registry = new StubRegistry();
        var provider = new AudioPreviewProvider(registry);

        using var stream = new MemoryStream(wavBytes);
        var result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Family, Is.EqualTo(PreviewFamily.Audio));
        Assert.That(result.RawPayload, Is.Not.Null);
        Assert.That(result.RawPayload, Is.EqualTo(wavBytes));
        Assert.That(result.FormattedContent, Is.Not.Null);
        Assert.That(result.FormattedContent, Does.Contain("PCM"));
        Assert.That(result.FormattedContent, Does.Contain("Channels: 1"));
        Assert.That(result.FormattedContent, Does.Contain("Sample Rate: 8000 Hz"));
        Assert.That(result.FormattedContent, Does.Contain("Bits Per Sample: 16"));
    }

    [Test]
    public async Task GeneratePreviewAsync_BmuWithEmbeddedWav_StripsPreambleAndSucceeds()
    {
        byte[] wavBytes = BuildWav(channels: 2, sampleRate: 44100, bitsPerSample: 8);
        byte[] preamble = { 0xAA, 0xBB, 0xCC, 0xDD, 0x01, 0x02, 0x03, 0x04 };
        byte[] bmuBytes = new byte[preamble.Length + wavBytes.Length];
        Buffer.BlockCopy(preamble, 0, bmuBytes, 0, preamble.Length);
        Buffer.BlockCopy(wavBytes, 0, bmuBytes, preamble.Length, wavBytes.Length);

        var request = CreateRequest("sound.bmu");
        var registry = new StubRegistry();
        var provider = new AudioPreviewProvider(registry);

        using var stream = new MemoryStream(bmuBytes);
        var result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.RawPayload, Is.Not.Null);
        Assert.That(result.RawPayload, Is.EqualTo(wavBytes));
        Assert.That(result.FormattedContent, Does.Contain("Channels: 2"));
        Assert.That(result.Diagnostics, Has.Some.Contains("BMU header skipped"));
    }

    [Test]
    public async Task GeneratePreviewAsync_InvalidHeader_ReturnsFailureWithMessage()
    {
        byte[] garbage = Encoding.ASCII.GetBytes("NOTAWAVEFILEATALLXXXXXXXXXXXXXX");

        var request = CreateRequest("bad.wav");
        var registry = new StubRegistry();
        var provider = new AudioPreviewProvider(registry);

        using var stream = new MemoryStream(garbage);
        var result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.RawPayload, Is.Null);
        Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task GeneratePreviewAsync_TruncatedDataChunk_ReturnsFailureWithoutThrowing()
    {
        byte[] wavBytes = BuildWav(channels: 1, sampleRate: 22050, bitsPerSample: 16, sampleData: new byte[64]);
        // Truncate the payload to cut off partway through the declared data chunk.
        byte[] truncated = wavBytes.AsSpan(0, wavBytes.Length - 20).ToArray();

        var request = CreateRequest("truncated.wav");
        var registry = new StubRegistry();
        var provider = new AudioPreviewProvider(registry);

        using var stream = new MemoryStream(truncated);

        PreviewResult? result = null;
        Assert.DoesNotThrowAsync(async () => result = await provider.GeneratePreviewAsync(request, stream));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task GeneratePreviewAsync_HeaderCutBeforeDataChunk_ReturnsFailure()
    {
        byte[] wavBytes = BuildWav(channels: 1, sampleRate: 16000, bitsPerSample: 16);
        // Cut off right after the "fmt " chunk, before the "data" chunk id appears.
        byte[] truncated = wavBytes.AsSpan(0, 12 + 8 + 16).ToArray();

        var request = CreateRequest("cutoff.wav");
        var registry = new StubRegistry();
        var provider = new AudioPreviewProvider(registry);

        using var stream = new MemoryStream(truncated);
        var result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
    }

    private sealed class StubRegistry : IResourceTypeRegistry
    {
        public bool TryGetExtension(ushort typeId, out string extension) { extension = string.Empty; return false; }
        public bool TryGetType(string extension, out ushort typeId) { typeId = 0; return false; }
    }
}
