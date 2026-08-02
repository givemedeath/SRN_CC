using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Infrastructure.Sources;

namespace SRN.CC.Tests.Sources;

[TestFixture]
public class FingerprintTests
{
    private string _tempDir = null!;
    private ResourceTypeRegistry _typeRegistry = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_FpTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _typeRegistry = new ResourceTypeRegistry();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public async Task GetFingerprintAsync_FolderInventoryChange_ChangesFingerprint()
    {
        string file1 = Path.Combine(_tempDir, "file1.nss");
        await File.WriteAllTextAsync(file1, "content 1");

        AssetSource source = AssetSource.CreateFolder(_tempDir);
        FolderAssetSourceReader reader = new(_typeRegistry);

        SourceFingerprint fp1 = await reader.GetFingerprintAsync(source);

        // Add second file
        string file2 = Path.Combine(_tempDir, "file2.are");
        await File.WriteAllTextAsync(file2, "content 2");

        SourceFingerprint fp2 = await reader.GetFingerprintAsync(source);

        fp1.Should().NotBe(fp2);
        fp1.Equals(fp2).Should().BeFalse();
    }

    [Test]
    public async Task GetFingerprintAsync_IdenticalInventory_ReturnsSameFingerprint()
    {
        string file1 = Path.Combine(_tempDir, "file1.nss");
        await File.WriteAllTextAsync(file1, "content 1");

        AssetSource source = AssetSource.CreateFolder(_tempDir);
        FolderAssetSourceReader reader = new(_typeRegistry);

        SourceFingerprint fp1 = await reader.GetFingerprintAsync(source);
        SourceFingerprint fp2 = await reader.GetFingerprintAsync(source);

        fp1.Should().Be(fp2);
        fp1.Equals(fp2).Should().BeTrue();
    }
}
