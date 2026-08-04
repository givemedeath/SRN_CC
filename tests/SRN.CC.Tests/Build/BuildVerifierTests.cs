using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SRN.CC.Core.Build;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Sources;
using SRN.CC.Formats.Hak;
using SRN.CC.Infrastructure.Build;

namespace SRN.CC.Tests.Build;

[TestFixture]
public class BuildVerifierTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "srncc_verifier_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Test]
    public async Task VerifyAsync_ValidHak_ReturnsSuccess()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hello verifier");
        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        AssetSource source = AssetSource.CreateHak("dummy.hak", 0);
        AssetIdentity id = new AssetIdentity("testres", 2000);

        var plan = new BuildPlan
        {
            DestinationHakPath = Path.Combine(_tempDir, "out.hak"),
            DestinationManifestPath = Path.Combine(_tempDir, "out.manifest.json"),
            FrozenSources = new[] { source },
            Items = new[] { new BuildItem(id, source.Id, new HakEntryLocator(0), payload.Length, hash, false) },
            CreatedUtc = DateTime.UtcNow
        };

        string tempHak = Path.Combine(_tempDir, "valid.hak");
        using (FileStream fs = new FileStream(tempHak, FileMode.Create, FileAccess.Write))
        {
            var item = new HakWriter.WriteItem(new HakFormatKey(id.OriginalResrefBytes.Span, id.ResourceType), new MemoryStream(payload), (uint)payload.Length);
            await HakWriter.WriteAsync(fs, new[] { item });
        }

        var verifier = new BuildVerifier();
        var report = await verifier.VerifyAsync(plan, tempHak);

        Assert.That(report.IsSuccess, Is.True);
        Assert.That(report.VerifiedEntryCount, Is.EqualTo(1));
        Assert.That(report.Errors, Is.Empty);
    }

    [Test]
    public async Task VerifyAsync_MismatchedPayloadHash_ReturnsError()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hello verifier");
        string wrongHash = "0000000000000000000000000000000000000000000000000000000000000000";

        AssetSource source = AssetSource.CreateHak("dummy.hak", 0);
        AssetIdentity id = new AssetIdentity("testres", 2000);

        var plan = new BuildPlan
        {
            DestinationHakPath = Path.Combine(_tempDir, "out.hak"),
            DestinationManifestPath = Path.Combine(_tempDir, "out.manifest.json"),
            FrozenSources = new[] { source },
            Items = new[] { new BuildItem(id, source.Id, new HakEntryLocator(0), payload.Length, wrongHash, false) },
            CreatedUtc = DateTime.UtcNow
        };

        string tempHak = Path.Combine(_tempDir, "invalid.hak");
        using (FileStream fs = new FileStream(tempHak, FileMode.Create, FileAccess.Write))
        {
            var item = new HakWriter.WriteItem(new HakFormatKey(id.OriginalResrefBytes.Span, id.ResourceType), new MemoryStream(payload), (uint)payload.Length);
            await HakWriter.WriteAsync(fs, new[] { item });
        }

        var verifier = new BuildVerifier();
        var report = await verifier.VerifyAsync(plan, tempHak);

        Assert.That(report.IsSuccess, Is.False);
        Assert.That(report.Errors.Count, Is.GreaterThan(0));
    }
}
