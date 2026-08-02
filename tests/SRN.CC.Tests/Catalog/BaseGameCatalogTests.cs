using System.Runtime.Versioning;
using FluentAssertions;
using NUnit.Framework;
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
}
