using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Infrastructure.Catalog;
using SRN.CC.Infrastructure.Install;

namespace SRN.CC.Tests.Catalog;

[TestFixture]
[SupportedOSPlatform("windows")]
public class BaseGameCatalogTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_CatalogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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
    public void NwnInstallLocator_InvalidExplicitOverride_ReturnsDiagnosticWithoutFallingBack()
    {
        string invalidPath = Path.Combine(_tempDir, "non_existent_nwn");

        NwnInstallLocator locator = new();
        NwnInstallLocation location = locator.Locate(explicitInstallRoot: invalidPath);

        location.IsExplicitOverride.Should().BeTrue();
        location.IsValid.Should().BeFalse();
        location.InstallRoot.Should().BeNull();
        location.Diagnostics.Should().ContainSingle(d => d.Code == SRN.CC.Core.Diagnostics.DiagnosticCode.InvalidInstallRoot);
    }

    [Test]
    public void BaseGameCatalog_PaddedKeyResref_IsIndexed()
    {
        string dataDir = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(Path.Combine(dataDir, "nwn_base.key"), BuildKey());

        BaseGameResourceCatalog catalog = new(_tempDir);

        catalog.Contains(new AssetIdentity("sample", 2002)).Should().BeTrue();
    }

    [Test]
    public void BaseGameCatalog_BifResourceTypeMismatch_RejectsPayload()
    {
        string dataDir = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(Path.Combine(dataDir, "nwn_base.key"), BuildKey());
        File.WriteAllBytes(Path.Combine(dataDir, "sample.bif"), BuildBif(resourceType: 2003));
        BaseGameResourceCatalog catalog = new(_tempDir);

        Action act = () => _ = catalog.OpenAsync(new AssetIdentity("sample", 2002));

        act.Should().Throw<InvalidDataException>().WithMessage("*KEY/BIF resource mismatch*");
    }

    private static byte[] BuildKey()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("KEY "u8);
        writer.Write("V1  "u8);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(64u);
        writer.Write(76u);
        writer.Write(126u);
        writer.Write(1u);
        writer.Write(new byte[32]);

        const string filename = @"data\sample.bif";
        writer.Write(40u);
        writer.Write(98u);
        writer.Write((ushort)filename.Length);
        writer.Write((ushort)1);
        writer.Write(Encoding.ASCII.GetBytes("sample"));
        writer.Write(new byte[10]);
        writer.Write((ushort)2002);
        writer.Write(0u);
        writer.Write(Encoding.ASCII.GetBytes(filename));
        return stream.ToArray();
    }

    private static byte[] BuildBif(uint resourceType)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("BIFF"u8);
        writer.Write("V1  "u8);
        writer.Write(1u);
        writer.Write(0u);
        writer.Write(20u);
        writer.Write(0u);
        writer.Write(36u);
        writer.Write(4u);
        writer.Write(resourceType);
        writer.Write(new byte[] { 1, 2, 3, 4 });
        return stream.ToArray();
    }
}


