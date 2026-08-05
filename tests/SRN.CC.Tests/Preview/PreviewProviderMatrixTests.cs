using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;
using SRN.CC.Preview.Render;
using SWLOR.NWN.Formats.Mdl;

namespace SRN.CC.Tests.Preview;

/// <summary>
/// The full <c>PLAN.md:223</c> matrix: "valid/truncated/malformed/canceled/oversized inputs for
/// every provider; one successful, one failed, and one canceled concurrent slot".
/// </summary>
/// <remarks>
/// <para>
/// "Every provider" is the real registration list in
/// <c>src/SRN.CC.App/Services/AppServices.cs</c>: <see cref="MetadataPreviewProvider"/>,
/// <see cref="ImagePreviewProvider"/>, <see cref="TextPreviewProvider"/>,
/// <see cref="AudioPreviewProvider"/>, <see cref="TreePreviewProvider"/>,
/// <see cref="MdlPreviewProvider"/> and <see cref="BoundedHexPreviewProvider"/> — seven providers,
/// five conditions, thirty-five tests, one region per provider.
/// </para>
/// <para>
/// <b>What each condition means here.</b>
/// <list type="bullet">
/// <item><i>valid</i> — a well-formed payload of the provider's own format.</item>
/// <item><i>truncated</i> — a payload that starts well-formed and stops early, either because the
/// stream ends before the declared length or because an internal chunk/section claims more bytes
/// than the payload contains.</item>
/// <item><i>malformed</i> — a payload of the right nominal type whose contents are not that
/// format at all.</item>
/// <item><i>canceled</i> — an already-canceled token; every provider must surface
/// <see cref="OperationCanceledException"/> rather than a synthesised failure result, because
/// <c>PreviewEngine</c> rethrows cancellation and maps only real errors to a failure result.</item>
/// <item><i>oversized</i> — input past the provider's own safety budget. The budget differs per
/// provider (see <c>PreviewStreamHelpers</c>), so each oversized test drives the specific limit
/// that provider enforces, and does so without materialising a real file:
/// <see cref="SyntheticStream"/> reports a length and yields bytes without ever holding them.
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>One deliberate substitution.</b> <see cref="ImagePreviewProvider"/> enforces two size
/// budgets: a 128 MiB <i>input</i> budget and a 4096-pixel/64 MiB <i>decoded</i> budget. The
/// oversized image test drives the decoded-dimension budget, which rejects the payload from an
/// 18-byte header; driving the input budget instead would require reading and buffering 128 MiB
/// of synthetic bytes for no additional behavioural coverage.
/// </para>
/// </remarks>
[TestFixture]
public class PreviewProviderMatrixTests
{
    // =====================================================================================
    // MetadataPreviewProvider — Family.Metadata, CanPreview always true, never reads the payload
    // =====================================================================================

    [Test]
    public async Task Metadata_ValidPayload_ReportsIdentitySourceAndHash()
    {
        MetadataPreviewProvider provider = new(new MatrixRegistry("2da"));
        AssetOccurrence occurrence = Occurrence("meta_ok", "meta_ok.2da", size: 19);
        PreviewRequest request = Request(occurrence, PreviewFamily.Metadata);

        using MemoryStream stream = new(Encoding.ASCII.GetBytes("sample payload data"));
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue();
        result.Family.Should().Be(PreviewFamily.Metadata);
        result.FormattedContent.Should().Contain("meta_ok").And.Contain("2DA");
        result.FormattedContent.Should().Contain("Declared Size     : 19 bytes");
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public async Task Metadata_TruncatedPayload_ReportsDeclaredAndActualLengthDisagreeing()
    {
        MetadataPreviewProvider provider = new(new MatrixRegistry("2da"));
        AssetOccurrence occurrence = Occurrence("meta_short", "meta_short.2da", size: 4096);
        PreviewRequest request = Request(occurrence, PreviewFamily.Metadata);

        using MemoryStream stream = new(new byte[10]);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue("metadata is derived from the index, not the payload");
        result.FormattedContent.Should().Contain("Declared Size     : 4,096 bytes");
        result.FormattedContent.Should().Contain("Actual Stream Len : 10 bytes");
    }

    [Test]
    public async Task Metadata_MalformedOccurrence_SurfacesTheValidationStateAsADiagnostic()
    {
        MetadataPreviewProvider provider = new(new MatrixRegistry("2da"));
        AssetOccurrence occurrence = Occurrence(
            "meta_bad",
            "meta_bad.2da",
            size: 8,
            validationState: ValidationState.InvalidResref);
        PreviewRequest request = Request(occurrence, PreviewFamily.Metadata);

        using MemoryStream stream = new(new byte[8]);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue();
        result.FormattedContent.Should().Contain("Validation State  : InvalidResref");
        result.Diagnostics.Should().ContainSingle().Which.Should().Contain("InvalidResref");
    }

    [Test]
    public async Task Metadata_CanceledToken_ThrowsOperationCanceled()
    {
        MetadataPreviewProvider provider = new(new MatrixRegistry("2da"));
        PreviewRequest request = Request(Occurrence("meta_cancel", "meta_cancel.2da"), PreviewFamily.Metadata);

        using MemoryStream stream = new(new byte[8]);
        using CancellationTokenSource cts = Canceled();

        Func<Task> act = () => provider.GeneratePreviewAsync(request, stream, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Metadata_OversizedPayload_SummarisesWithoutReadingASingleByte()
    {
        MetadataPreviewProvider provider = new(new MatrixRegistry("2da"));
        const long huge = 4L * 1024 * 1024 * 1024;
        AssetOccurrence occurrence = Occurrence("meta_huge", "meta_huge.2da", size: huge);
        PreviewRequest request = Request(occurrence, PreviewFamily.Metadata);

        // Reading is a hard error here: the metadata provider must stay O(1) in payload size.
        using SyntheticStream stream = new(huge, throwOnRead: true);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue();
        result.FormattedContent.Should().Contain($"Declared Size     : {huge:N0} bytes");
        stream.BytesRead.Should().Be(0);
    }

    // =====================================================================================
    // BoundedHexPreviewProvider — Family.Hex, CanPreview always true, the last-resort fallback
    // =====================================================================================

    [Test]
    public async Task Hex_ValidPayload_DumpsEveryByteWithinBudget()
    {
        BoundedHexPreviewProvider provider = new();
        PreviewRequest request = Request(
            Occurrence("hex_ok", "hex_ok.bin", size: 48),
            PreviewFamily.Hex,
            safetyBudgetBytes: 1024);

        byte[] payload = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
        using MemoryStream stream = new(payload);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue();
        result.Family.Should().Be(PreviewFamily.Hex);
        result.RawPayload.Should().Equal(payload);
        result.FormattedContent.Should().Contain("=== HEX DUMP (48 bytes) ===");
        result.FormattedContent.Should().NotContain("[TRUNCATED]");
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public async Task Hex_TruncatedPayload_DumpsOnlyTheBytesTheStreamActuallyProduced()
    {
        BoundedHexPreviewProvider provider = new();
        PreviewRequest request = Request(
            Occurrence("hex_short", "hex_short.bin", size: 100),
            PreviewFamily.Hex,
            safetyBudgetBytes: 1024);

        // Length says 100; the stream stops producing bytes after 40.
        using SyntheticStream stream = new(declaredLength: 100, availableBytes: 40);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue();
        result.FormattedContent.Should().Contain("=== HEX DUMP (40 bytes) ===");
    }

    [Test]
    public async Task Hex_MalformedPayload_StillSucceedsBecauseHexHasNoFormatToViolate()
    {
        BoundedHexPreviewProvider provider = new();
        PreviewRequest request = Request(
            Occurrence("hex_junk", "hex_junk.bin", size: 6),
            PreviewFamily.Hex,
            safetyBudgetBytes: 1024);

        byte[] payload = [0x00, 0xFF, 0x01, 0xFE, 0x7F, 0x80];
        using MemoryStream stream = new(payload);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue(
            "the hex provider is the fallback every other provider degrades to; it must never reject a payload");
        result.FormattedContent.Should().Contain("00 FF 01 FE 7F 80");
        result.FormattedContent.Should().Contain("......", "non-printable bytes render as dots");
    }

    [Test]
    public async Task Hex_CanceledToken_ThrowsOperationCanceled()
    {
        BoundedHexPreviewProvider provider = new();
        PreviewRequest request = Request(
            Occurrence("hex_cancel", "hex_cancel.bin", size: 64),
            PreviewFamily.Hex,
            safetyBudgetBytes: 1024);

        using MemoryStream stream = new(new byte[64]);
        using CancellationTokenSource cts = Canceled();

        Func<Task> act = () => provider.GeneratePreviewAsync(request, stream, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Hex_OversizedPayload_ClampsToTheOneMebibyteCeilingEvenWhenTheRequestAsksForMore()
    {
        BoundedHexPreviewProvider provider = new();
        const long twoMebibytes = 2L * 1024 * 1024;
        PreviewRequest request = Request(
            Occurrence("hex_huge", "hex_huge.bin", size: twoMebibytes),
            PreviewFamily.Hex,
            safetyBudgetBytes: long.MaxValue);

        using SyntheticStream stream = new(twoMebibytes);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue();
        result.RawPayload!.Length.Should().Be(1024 * 1024, "the provider caps its own budget at 1 MiB");
        result.FormattedContent.Should().Contain("[TRUNCATED]");
        result.Diagnostics.Should().ContainSingle().Which.Should().Contain("truncated to budget limit of 1,048,576 bytes");
    }

    // =====================================================================================
    // TextPreviewProvider — Family.Text, 8 MiB budget
    // =====================================================================================

    [Test]
    public async Task Text_ValidPayload_DecodesAndNormalisesLineEndings()
    {
        TextPreviewProvider provider = new(new MatrixRegistry("2da"));
        PreviewRequest request = Request(Occurrence("text_ok", "text_ok.2da", size: 32), PreviewFamily.Text);

        byte[] payload = Encoding.ASCII.GetBytes("2DA V2.0\r\n\r\n   LABEL\r\n0  alpha\r\n");
        using MemoryStream stream = new(payload);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Family.Should().Be(PreviewFamily.Text);
        result.FormattedContent.Should().Contain("2DA V2.0").And.Contain("0  alpha");
        result.FormattedContent.Should().NotContain("\r");
        result.Diagnostics.Should().Contain("Decoded as UTF-8 text.");
    }

    [Test]
    public async Task Text_TruncatedPayload_ReportsTheStreamEndingBeforeItsDeclaredLength()
    {
        TextPreviewProvider provider = new(new MatrixRegistry("2da"));
        PreviewRequest request = Request(Occurrence("text_short", "text_short.2da", size: 4096), PreviewFamily.Text);

        using SyntheticStream stream = new(declaredLength: 4096, availableBytes: 16, fill: (byte)'A');
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.FormattedContent.Should().Be(new string('A', 16));
        result.Diagnostics.Should().Contain(d => d.Contains("Stream ended before preview budget", StringComparison.Ordinal));
    }

    [Test]
    public async Task Text_MalformedPayload_RejectsBinaryContentThatIsNotDecodableText()
    {
        TextPreviewProvider provider = new(new MatrixRegistry("2da"));
        PreviewRequest request = Request(Occurrence("text_bin", "text_bin.2da", size: 8), PreviewFamily.Text);

        // Invalid UTF-8 (lone continuation bytes) plus an embedded NUL: binary by both tests.
        byte[] payload = [0x00, 0xFF, 0xFE, 0x91, 0x00, 0x03, 0x92, 0x01];
        using MemoryStream stream = new(payload);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("payload appears to be binary");
        result.FormattedContent.Should().BeNull();
    }

    [Test]
    public async Task Text_CanceledToken_ThrowsOperationCanceled()
    {
        TextPreviewProvider provider = new(new MatrixRegistry("2da"));
        PreviewRequest request = Request(Occurrence("text_cancel", "text_cancel.2da", size: 8), PreviewFamily.Text);

        using MemoryStream stream = new(Encoding.ASCII.GetBytes("abcdefgh"));
        using CancellationTokenSource cts = Canceled();

        Func<Task> act = () => provider.GeneratePreviewAsync(request, stream, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Text_OversizedPayload_TruncatesToTheEightMebibyteBudgetAndMarksTheContent()
    {
        TextPreviewProvider provider = new(new MatrixRegistry("2da"));
        const long budget = 8L * 1024 * 1024;
        PreviewRequest request = Request(Occurrence("text_huge", "text_huge.2da", size: budget + 1), PreviewFamily.Text);

        using SyntheticStream stream = new(budget + 1, fill: (byte)'A');
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.RawPayload!.Length.Should().Be((int)budget);
        result.FormattedContent.Should().EndWith("[TRUNCATED]");
        result.Diagnostics.Should().Contain("Text input was truncated to preview budget.");
    }

    // =====================================================================================
    // ImagePreviewProvider — Family.Image, TGA/DDS/PLT
    // =====================================================================================

    [Test]
    public async Task Image_ValidTga_DecodesToExactDimensionsAndPixels()
    {
        ImagePreviewProvider provider = new(new MatrixRegistry("tga"));
        PreviewRequest request = Request(Occurrence("img_ok", "img_ok.tga", size: 26), PreviewFamily.Image);

        byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 200];
        using MemoryStream stream = new(BuildTga(width: 2, height: 1, pixelDepth: 32, pixelData: pixels));
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Family.Should().Be(PreviewFamily.Image);
        result.FormattedContent.Should().Be("2:1");
        result.RawPayload.Should().Equal(pixels);
    }

    [Test]
    public async Task Image_TruncatedTga_FailsInsteadOfDecodingPartialPixels()
    {
        ImagePreviewProvider provider = new(new MatrixRegistry("tga"));
        PreviewRequest request = Request(Occurrence("img_short", "img_short.tga", size: 20), PreviewFamily.Image);

        // A well-formed 4x4 32-bit header followed by only one pixel's worth of data.
        byte[] full = BuildTga(width: 4, height: 4, pixelDepth: 32, pixelData: new byte[4 * 4 * 4]);
        byte[] truncated = full.AsSpan(0, 18 + 4).ToArray();

        using MemoryStream stream = new(truncated);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.RawPayload.Should().BeNull();
    }

    [Test]
    public async Task Image_MalformedTga_FailsWithADecoderDiagnosticAndNoPixels()
    {
        ImagePreviewProvider provider = new(new MatrixRegistry("tga"));
        PreviewRequest request = Request(Occurrence("img_bad", "img_bad.tga", size: 12), PreviewFamily.Image);

        using MemoryStream stream = new(Encoding.ASCII.GetBytes("not-a-tga!!!"));
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("TGA header is too small.");
    }

    [Test]
    public async Task Image_CanceledToken_ThrowsOperationCanceled()
    {
        ImagePreviewProvider provider = new(new MatrixRegistry("tga"));
        PreviewRequest request = Request(Occurrence("img_cancel", "img_cancel.tga", size: 26), PreviewFamily.Image);

        using MemoryStream stream = new(BuildTga(2, 1, 32, new byte[8]));
        using CancellationTokenSource cts = Canceled();

        Func<Task> act = () => provider.GeneratePreviewAsync(request, stream, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Image_OversizedTga_RejectsDimensionsBeyondThePreviewSafetyLimit()
    {
        ImagePreviewProvider provider = new(new MatrixRegistry("tga"));
        PreviewRequest request = Request(Occurrence("img_huge", "img_huge.tga", size: 18), PreviewFamily.Image);

        // 8192x8192 doubles the 4096 dimension ceiling and would need 256 MiB of BGRA; the header
        // alone must be enough to reject it, without a byte of pixel data being read.
        byte[] header = BuildTga(width: 8192, height: 8192, pixelDepth: 32, pixelData: []);
        using MemoryStream stream = new(header);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("8192x8192").And.Contain("exceed preview safety limits");
    }

    // =====================================================================================
    // AudioPreviewProvider — Family.Audio, WAV/BMU, 64 MiB budget
    // =====================================================================================

    [Test]
    public async Task Audio_ValidWav_ReportsFormatChannelsAndDuration()
    {
        AudioPreviewProvider provider = new(new MatrixRegistry("wav"));
        PreviewRequest request = Request(Occurrence("aud_ok", "aud_ok.wav", size: 64), PreviewFamily.Audio);

        using MemoryStream stream = new(BuildWav(sampleBytes: 32));
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Family.Should().Be(PreviewFamily.Audio);
        result.FormattedContent.Should().Contain("Format: PCM");
        result.FormattedContent.Should().Contain("Channels: 1");
        result.FormattedContent.Should().Contain("Sample Rate: 22050 Hz");
        result.FormattedContent.Should().Contain("Data Size: 32 bytes");
    }

    [Test]
    public async Task Audio_TruncatedWav_FailsWhenTheDataChunkClaimsMoreThanThePayloadHolds()
    {
        AudioPreviewProvider provider = new(new MatrixRegistry("wav"));
        PreviewRequest request = Request(Occurrence("aud_short", "aud_short.wav", size: 64), PreviewFamily.Audio);

        byte[] wav = BuildWav(sampleBytes: 32);
        byte[] truncated = wav.AsSpan(0, wav.Length - 16).ToArray(); // half the declared samples are gone

        using MemoryStream stream = new(truncated);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("truncated");
    }

    [Test]
    public async Task Audio_MalformedWav_RejectsAPayloadWithoutARiffWaveSignature()
    {
        AudioPreviewProvider provider = new(new MatrixRegistry("wav"));
        PreviewRequest request = Request(Occurrence("aud_bad", "aud_bad.wav", size: 16), PreviewFamily.Audio);

        using MemoryStream stream = new(Encoding.ASCII.GetBytes("RIFXnotawavefile"));
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not start with RIFF/WAVE signature");
    }

    [Test]
    public async Task Audio_CanceledToken_ThrowsOperationCanceled()
    {
        AudioPreviewProvider provider = new(new MatrixRegistry("wav"));
        PreviewRequest request = Request(Occurrence("aud_cancel", "aud_cancel.wav", size: 64), PreviewFamily.Audio);

        using MemoryStream stream = new(BuildWav(sampleBytes: 32));
        using CancellationTokenSource cts = Canceled();

        Func<Task> act = () => provider.GeneratePreviewAsync(request, stream, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Audio_OversizedPayload_StopsAtTheSixtyFourMebibyteBudget()
    {
        AudioPreviewProvider provider = new(new MatrixRegistry("wav"));
        const long budget = 64L * 1024 * 1024;
        PreviewRequest request = Request(Occurrence("aud_huge", "aud_huge.wav", size: budget + 1), PreviewFamily.Audio);

        using SyntheticStream stream = new(budget + 1);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.Diagnostics.Should().Contain("Audio input was truncated to the preview budget.");
        stream.BytesRead.Should().Be(budget, "the provider must never read past its own budget");
        result.IsSuccess.Should().BeFalse("the bytes that fit the budget are still not a WAV file");
    }

    // =====================================================================================
    // TreePreviewProvider — Family.Tree, GFF/ITP/SSF, 32 MiB budget
    // =====================================================================================

    [Test]
    public async Task Tree_ValidSsf_RendersTheHeaderAndEveryDeclaredEntry()
    {
        TreePreviewProvider provider = new(new MatrixRegistry("ssf"));
        PreviewRequest request = Request(Occurrence("tree_ok", "tree_ok.ssf", size: 52), PreviewFamily.Tree);

        using MemoryStream stream = new(BuildSsf(entryCount: 2));
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Family.Should().Be(PreviewFamily.Tree);
        result.FormattedContent.Should().Contain("=== SSF PREVIEW ===");
        result.FormattedContent.Should().Contain("Entry Count (declared): 2");
        result.FormattedContent.Should().Contain("Entries Parsed: 2");
        result.FormattedContent.Should().Contain("entry_00");
        result.FormattedContent.Should().Contain("entry_01");
    }

    [Test]
    public async Task Tree_TruncatedSsf_FailsWhenTheHeaderItselfIsIncomplete()
    {
        TreePreviewProvider provider = new(new MatrixRegistry("ssf"));
        PreviewRequest request = Request(Occurrence("tree_short", "tree_short.ssf", size: 10), PreviewFamily.Tree);

        byte[] header = BuildSsf(entryCount: 1).AsSpan(0, 10).ToArray(); // stops inside the count field
        using MemoryStream stream = new(header);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("entry count is missing");
    }

    [Test]
    public async Task Tree_MalformedGff_FailsWithAParseDiagnosticRatherThanThrowing()
    {
        TreePreviewProvider provider = new(new MatrixRegistry("gff"));
        PreviewRequest request = Request(Occurrence("tree_bad", "tree_bad.gff", size: 32), PreviewFamily.Tree);

        byte[] junk = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + (i % 16))).ToArray();
        using MemoryStream stream = new(junk);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("GFF");
        result.Family.Should().Be(PreviewFamily.Tree);
    }

    [Test]
    public async Task Tree_CanceledToken_ThrowsOperationCanceled()
    {
        TreePreviewProvider provider = new(new MatrixRegistry("ssf"));
        PreviewRequest request = Request(Occurrence("tree_cancel", "tree_cancel.ssf", size: 32), PreviewFamily.Tree);

        using MemoryStream stream = new(BuildSsf(entryCount: 1));
        using CancellationTokenSource cts = Canceled();

        Func<Task> act = () => provider.GeneratePreviewAsync(request, stream, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Tree_OversizedPayload_StopsAtTheThirtyTwoMebibyteBudget()
    {
        TreePreviewProvider provider = new(new MatrixRegistry("ssf"));
        const long budget = 32L * 1024 * 1024;
        PreviewRequest request = Request(Occurrence("tree_huge", "tree_huge.ssf", size: budget + 1), PreviewFamily.Tree);

        // A real SSF header up front so the provider gets past validation and reaches its budget
        // handling, then filler out to one byte past the budget.
        using SyntheticStream stream = new(budget + 1, prefix: BuildSsf(entryCount: 1));
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Diagnostics.Should().Contain("Tree input was truncated to preview budget.");
        result.FormattedContent.Should().Contain("=== SSF PREVIEW ===");
        stream.BytesRead.Should().Be(budget);
    }

    // =====================================================================================
    // MdlPreviewProvider — Family.Model, 64 MiB budget
    // =====================================================================================

    private const string ValidAsciiMdl = """
        newmodel matrix_sample
        beginmodelgeom matrix_sample
          node dummy matrix_sample
            parent NULL
          endnode
        endmodelgeom matrix_sample
        donemodel matrix_sample
        """;

    [Test]
    public async Task Mdl_ValidAsciiModel_ReportsTheParsedModelAndBuildsAScene()
    {
        MatrixSceneBuilder sceneBuilder = new();
        MdlPreviewProvider provider = new(new MatrixRegistry("mdl"), sceneBuilder, new ModelSceneCache());
        PreviewRequest request = Request(Occurrence("mdl_ok", "mdl_ok.mdl", size: 128), PreviewFamily.Model);

        using MemoryStream stream = new(Encoding.ASCII.GetBytes(ValidAsciiMdl));
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Family.Should().Be(PreviewFamily.Model);
        result.FormattedContent.Should().Contain("=== MDL PREVIEW ===");
        result.FormattedContent.Should().Contain("Model Name: matrix_sample");
        result.FormattedContent.Should().Contain("Format: ASCII MDL");
    }

    [Test]
    public async Task Mdl_TruncatedAsciiModel_ReportsTheIncompleteParseInsteadOfPretendingItSucceeded()
    {
        MatrixSceneBuilder sceneBuilder = new();
        MdlPreviewProvider provider = new(new MatrixRegistry("mdl"), sceneBuilder, new ModelSceneCache());
        PreviewRequest request = Request(Occurrence("mdl_short", "mdl_short.mdl", size: 300), PreviewFamily.Model);

        // Binary MDL header declaring 200 000 bytes of model data behind a payload that holds 300:
        // the file stops long before the geometry it promises.
        byte[] truncated = new byte[300];
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(4, 4), 200_000);
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(8, 4), 32);

        using MemoryStream stream = new(truncated);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        AssertModelParseWasNotClean(result);
    }

    [Test]
    public async Task Mdl_MalformedBinaryModel_ReportsAParseFailureRatherThanThrowing()
    {
        MatrixSceneBuilder sceneBuilder = new();
        MdlPreviewProvider provider = new(new MatrixRegistry("mdl"), sceneBuilder, new ModelSceneCache());
        PreviewRequest request = Request(Occurrence("mdl_bad", "mdl_bad.mdl", size: 64), PreviewFamily.Model);

        byte[] junk = Enumerable.Range(0, 64).Select(i => (byte)(0x80 + (i % 32))).ToArray();
        using MemoryStream stream = new(junk);
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        AssertModelParseWasNotClean(result);
    }

    [Test]
    public async Task Mdl_CanceledToken_ThrowsOperationCanceled()
    {
        MatrixSceneBuilder sceneBuilder = new();
        MdlPreviewProvider provider = new(new MatrixRegistry("mdl"), sceneBuilder, new ModelSceneCache());
        PreviewRequest request = Request(Occurrence("mdl_cancel", "mdl_cancel.mdl", size: 128), PreviewFamily.Model);

        using MemoryStream stream = new(Encoding.ASCII.GetBytes(ValidAsciiMdl));
        using CancellationTokenSource cts = Canceled();

        Func<Task> act = () => provider.GeneratePreviewAsync(request, stream, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Mdl_OversizedPayload_RejectsInputPastTheSixtyFourMebibyteBudget()
    {
        MatrixSceneBuilder sceneBuilder = new();
        MdlPreviewProvider provider = new(new MatrixRegistry("mdl"), sceneBuilder, new ModelSceneCache());
        const long budget = 64L * 1024 * 1024;
        PreviewRequest request = Request(Occurrence("mdl_huge", "mdl_huge.mdl", size: budget + 1), PreviewFamily.Model);

        using SyntheticStream stream = new(budget + 1, prefix: Encoding.ASCII.GetBytes(ValidAsciiMdl));
        PreviewResult result = await provider.GeneratePreviewAsync(request, stream);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Model input exceeds the preview budget (64 MiB).");
        result.Diagnostics.Should().Contain("Model input was truncated to preview budget.");
        sceneBuilder.CallCount.Should().Be(0, "an over-budget payload must never reach the scene builder");
    }

    /// <summary>
    /// The MDL provider has two acceptable answers for a payload it cannot parse cleanly: a hard
    /// failure, or the header-only fallback preview flagged with a "Primary parse failed"
    /// diagnostic. Both are correct; silently returning a preview that claims a clean parse is not.
    /// </summary>
    private static void AssertModelParseWasNotClean(PreviewResult result)
    {
        result.Family.Should().Be(PreviewFamily.Model);
        if (result.IsSuccess)
        {
            result.Diagnostics.Should().Contain(
                d => d.Contains("parse failed", StringComparison.OrdinalIgnoreCase),
                "a fallback preview must record why the primary parse did not run");
            result.Payload.Should().BeNull("the fallback path has no geometry to build a scene from");
        }
        else
        {
            result.ErrorMessage.Should().Contain("parse failed");
        }
    }

    // =====================================================================================
    // Concurrent slots — one successful, one failed, one canceled (PLAN.md:223, second clause)
    // =====================================================================================

    [Test]
    public async Task ConcurrentSlots_OneSuccessOneFailureOneCancellation_EachResolvesIndependently()
    {
        AssetSource source = AssetSource.CreateHak(Path.GetFullPath("matrix_concurrent.hak"), 0);
        AssetOccurrence success = Occurrence("slot_ok", "slot_ok.2da", size: 16, source: source);
        AssetOccurrence failure = Occurrence("slot_fail", "slot_fail.2da", size: 16, source: source);
        AssetOccurrence canceled = Occurrence("slot_cancel", "slot_cancel.2da", size: 16, source: source);

        using SemaphoreSlim cancellationObserved = new(0, 1);
        ScriptedDispatcher dispatcher = new(
            occurrence =>
            {
                if (occurrence.Identity.Resref == failure.Identity.Resref)
                {
                    throw new IOException("simulated source read failure");
                }

                if (occurrence.Identity.Resref == canceled.Identity.Resref)
                {
                    cancellationObserved.Release();
                    return new BlockingStream();
                }

                return new MemoryStream(Encoding.ASCII.GetBytes("2DA V2.0\nrow one\n"));
            });

        PreviewEngine engine = new(dispatcher, new IPreviewProvider[] { new TextPreviewProvider(new MatrixRegistry("2da")) });

        using CancellationTokenSource cancelSource = new();

        Task<PreviewResult> successTask = engine.ExecutePreviewAsync(
            Request(success, PreviewFamily.Text, source: source), debounceMs: 0);
        Task<PreviewResult> failureTask = engine.ExecutePreviewAsync(
            Request(failure, PreviewFamily.Text, source: source), debounceMs: 0);
        Task<PreviewResult> canceledTask = engine.ExecutePreviewAsync(
            Request(canceled, PreviewFamily.Text, source: source), debounceMs: 0, cancellationToken: cancelSource.Token);

        (await cancellationObserved.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeTrue(
            "the third slot must reach its blocking read before it is canceled");
        await cancelSource.CancelAsync();

        PreviewResult successResult = await successTask;
        PreviewResult failureResult = await failureTask;

        successResult.IsSuccess.Should().BeTrue(successResult.ErrorMessage);
        successResult.FormattedContent.Should().Contain("row one");

        failureResult.IsSuccess.Should().BeFalse();
        failureResult.ErrorMessage.Should().Contain("simulated source read failure");

        Func<Task> canceledAwait = async () => await canceledTask;
        await canceledAwait.Should().ThrowAsync<OperationCanceledException>(
            "PreviewEngine rethrows cancellation instead of degrading it to a failure result");

        successResult.Occurrence.Identity.Resref.Should().Be(success.Identity.Resref);
        failureResult.Occurrence.Identity.Resref.Should().Be(failure.Identity.Resref);
    }

    // =====================================================================================
    // Fixtures and fakes
    // =====================================================================================

    private static AssetOccurrence Occurrence(
        string resref,
        string originalName,
        long size = 64,
        ValidationState validationState = ValidationState.Valid,
        AssetSource? source = null) => new(
            identity: new AssetIdentity(resref, 2000),
            sourceId: (source ?? DefaultSource).Id,
            locator: new HakEntryLocator(1),
            originalName: originalName,
            size: size,
            validationState: validationState,
            extensionMetadata: null,
            sha256: Encoding.ASCII.GetBytes("hash1234567890123456789012345678"));

    private static readonly AssetSource DefaultSource = AssetSource.CreateHak(Path.GetFullPath("matrix_source.hak"), 0);

    private static PreviewRequest Request(
        AssetOccurrence occurrence,
        PreviewFamily family,
        long safetyBudgetBytes = 1048576,
        AssetSource? source = null) =>
        new(occurrence, source ?? DefaultSource, family, safetyBudgetBytes);

    private static CancellationTokenSource Canceled()
    {
        CancellationTokenSource cts = new();
        cts.Cancel();
        return cts;
    }

    /// <summary>Minimal uncompressed true-colour TGA (image type 2, top-left origin).</summary>
    private static byte[] BuildTga(int width, int height, byte pixelDepth, byte[] pixelData)
    {
        byte[] header = new byte[18];
        header[2] = 2; // uncompressed true-colour
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14, 2), (ushort)height);
        header[16] = pixelDepth;
        header[17] = 0x20; // top-left origin
        return [.. header, .. pixelData];
    }

    /// <summary>Minimal 8-bit mono PCM RIFF/WAVE with a 16-byte fmt chunk and one data chunk.</summary>
    private static byte[] BuildWav(int sampleBytes)
    {
        byte[] wav = new byte[44 + sampleBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4, 4), (uint)(36 + sampleBytes));
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22, 2), 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24, 4), 22050);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28, 4), 22050);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34, 2), 8);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40, 4), (uint)sampleBytes);
        return wav;
    }

    /// <summary>12-byte SSF header plus <paramref name="entryCount"/> 20-byte entries.</summary>
    private static byte[] BuildSsf(int entryCount)
    {
        byte[] bytes = new byte[12 + (entryCount * 20)];
        Encoding.ASCII.GetBytes("SSF ").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 0x312E3056); // "V1.0"
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            int offset = 12 + (i * 20);
            Encoding.ASCII.GetBytes($"entry_{i:00}").CopyTo(bytes, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 16, 4), (uint)(0x1000 + i));
        }

        return bytes;
    }

    private sealed class MatrixRegistry(string extension) : IResourceTypeRegistry
    {
        public bool TryGetExtension(ushort typeId, out string mapped)
        {
            mapped = extension;
            return true;
        }

        public bool TryGetType(string ext, out ushort typeId)
        {
            typeId = 2000;
            return true;
        }
    }

    /// <summary>
    /// A read-only stream that reports an arbitrary length and yields filler bytes without ever
    /// allocating them, so an "oversized" case can drive a 64 MiB budget without a 64 MiB fixture on
    /// disk. It also models the truncated case (<c>availableBytes</c> below <c>declaredLength</c>)
    /// and the never-touch-the-payload case (<c>throwOnRead</c>).
    /// </summary>
    private sealed class SyntheticStream : Stream
    {
        private readonly long _declaredLength;
        private readonly long _availableBytes;
        private readonly byte _fill;
        private readonly bool _throwOnRead;
        private readonly byte[] _prefix;
        private long _position;

        public SyntheticStream(
            long declaredLength,
            long? availableBytes = null,
            byte fill = 0,
            bool throwOnRead = false,
            byte[]? prefix = null)
        {
            _declaredLength = declaredLength;
            _availableBytes = availableBytes ?? declaredLength;
            _fill = fill;
            _throwOnRead = throwOnRead;
            _prefix = prefix ?? [];
        }

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _declaredLength;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_throwOnRead)
            {
                throw new InvalidOperationException("This payload must never be read.");
            }

            long remaining = _availableBytes - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int count = (int)Math.Min(buffer.Length, remaining);
            for (int i = 0; i < count; i++)
            {
                long absolute = _position + i;
                buffer[i] = absolute < _prefix.Length ? _prefix[absolute] : _fill;
            }

            _position += count;
            BytesRead += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _declaredLength + offset
            };

            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream whose reads never complete until the caller's token is canceled.</summary>
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            => new(Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(
                _ => 0,
                cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ScriptedDispatcher(Func<AssetOccurrence, Stream> factory) : ISourceReaderDispatcher
    {
        public Task<Stream> OpenOccurrenceAsync(
            AssetSource source,
            AssetOccurrence occurrence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(factory(occurrence));
        }
    }

    private sealed class MatrixSceneBuilder : IMdlSceneBuilder
    {
        public int CallCount { get; private set; }

        public Task<RenderScene> BuildAsync(
            MdlModel model,
            bool isAsciiSource,
            ITextureSource? textures,
            SceneBuildBudget budget,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RenderScene
            {
                ModelName = model.Name ?? string.Empty,
                SuperModel = string.Empty,
                IsAsciiSource = isAsciiSource,
                BoundsMinimum = Vector3.Zero,
                BoundsMaximum = Vector3.Zero,
                Radius = 0f,
                ArtworkMeshes = Array.Empty<RenderMesh>(),
                WalkmeshMeshes = Array.Empty<RenderMesh>(),
                Materials = Array.Empty<RenderMaterial>(),
                UnsupportedFeatures = Array.Empty<string>(),
                Diagnostics = Array.Empty<string>(),
                ApproximateByteSize = 0
            });
        }
    }
}
