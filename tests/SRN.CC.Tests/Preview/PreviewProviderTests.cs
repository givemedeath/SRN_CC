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
public class PreviewProviderTests
{
    [Test]
    public async Task MetadataPreviewProvider_GeneratePreview_FormatsMetadataString()
    {
        AssetIdentity id = new AssetIdentity("test_resref", 2000);
        AssetSource source = AssetSource.CreateHak("c:/test/source.hak", 0);
        AssetOccurrence occ = new AssetOccurrence(
            identity: id,
            sourceId: source.Id,
            locator: new HakEntryLocator(5),
            originalName: "test_resref.2da",
            size: 100,
            validationState: ValidationState.Valid,
            extensionMetadata: null,
            sha256: Encoding.ASCII.GetBytes("hash1234567890123456789012345678"));

        var request = new PreviewRequest(occ, source, PreviewFamily.Metadata);
        var registry = new StubRegistry();
        var provider = new MetadataPreviewProvider(registry);

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("sample payload data"));
        var result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.FormattedContent, Does.Contain("test_resref"));
        Assert.That(result.FormattedContent, Does.Contain("2DA"));
    }

    [Test]
    public async Task BoundedHexPreviewProvider_GeneratePreview_RespectsSafetyBudget()
    {
        AssetIdentity id = new AssetIdentity("hex_resref", 2000);
        AssetSource source = AssetSource.CreateFolder("c:/test/folder", 0);
        AssetOccurrence occ = new AssetOccurrence(
            identity: id,
            sourceId: source.Id,
            locator: new FolderFileLocator("hex_resref.txt"),
            size: 500,
            originalName: "hex_resref.txt",
            validationState: ValidationState.Valid,
            extensionMetadata: null,
            sha256: Encoding.ASCII.GetBytes("hash1234567890123456789012345678"));

        var request = new PreviewRequest(occ, source, PreviewFamily.Hex, SafetyBudgetBytes: 32);
        var provider = new BoundedHexPreviewProvider();

        byte[] payload = new byte[100];
        for (int i = 0; i < 100; i++) payload[i] = (byte)(i % 256);

        using var stream = new MemoryStream(payload);
        var result = await provider.GeneratePreviewAsync(request, stream);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.RawPayload?.Length, Is.EqualTo(32));
        Assert.That(result.Diagnostics.Count, Is.GreaterThan(0));
        Assert.That(result.FormattedContent, Does.Contain("HEX DUMP"));
    }

    private sealed class StubRegistry : IResourceTypeRegistry
    {
        public bool TryGetExtension(ushort typeId, out string extension) { extension = "2da"; return true; }
        public bool TryGetType(string extension, out ushort typeId) { typeId = 2000; return true; }
    }
}
