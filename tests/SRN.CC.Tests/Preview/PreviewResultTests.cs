using System.Text;
using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Preview;
using SRN.CC.Core.Sources;

namespace SRN.CC.Tests.Preview;

[TestFixture]
public class PreviewResultTests
{
    private static PreviewResult CreateResult()
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

        return new PreviewResult(
            Occurrence: occ,
            Family: PreviewFamily.Metadata,
            IsSuccess: true,
            MetadataText: "meta",
            RawPayload: null,
            FormattedContent: "formatted",
            ErrorMessage: null,
            Diagnostics: Array.Empty<string>());
    }

    [Test]
    public void Constructor_WithoutPayloadArgument_LeavesPayloadNull()
    {
        PreviewResult result = CreateResult();

        Assert.That(result.Payload, Is.Null);
    }

    [Test]
    public void With_PayloadSet_RoundTripsCorrectly()
    {
        PreviewResult original = CreateResult();
        FakePreviewPayload payload = new FakePreviewPayload("model-scene/v1", 1234);

        PreviewResult updated = original with { Payload = payload };

        Assert.That(updated.Payload, Is.SameAs(payload));
        Assert.That(updated.Payload?.PayloadKind, Is.EqualTo("model-scene/v1"));
        Assert.That(updated.Payload?.ApproximateByteSize, Is.EqualTo(1234));
        Assert.That(original.Payload, Is.Null, "original record must remain unmodified");
    }

    private sealed class FakePreviewPayload : IPreviewPayload
    {
        public FakePreviewPayload(string payloadKind, long approximateByteSize)
        {
            PayloadKind = payloadKind;
            ApproximateByteSize = approximateByteSize;
        }

        public string PayloadKind { get; }
        public long ApproximateByteSize { get; }
    }
}
