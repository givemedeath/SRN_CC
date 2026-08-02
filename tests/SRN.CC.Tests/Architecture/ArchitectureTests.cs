using NUnit.Framework;
using FluentAssertions;
using System.Xml.Linq;

namespace SRN.CC.Tests.Architecture;

[TestFixture]
public class ArchitectureTests
{
    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "SRN.CC.sln")))
        {
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        return dir;
    }

    [Test]
    public void ProductionProjectDependencyRules_MustBeStrictlyEnforced()
    {
        string root = FindRepoRoot();
        File.Exists(Path.Combine(root, "SRN.CC.sln")).Should().BeTrue("SRN.CC.sln must exist at repo root");

        string coreCsproj = Path.Combine(root, "src", "SRN.CC.Core", "SRN.CC.Core.csproj");
        string formatsCsproj = Path.Combine(root, "src", "SRN.CC.Formats", "SRN.CC.Formats.csproj");
        string infraCsproj = Path.Combine(root, "src", "SRN.CC.Infrastructure", "SRN.CC.Infrastructure.csproj");
        string previewCsproj = Path.Combine(root, "src", "SRN.CC.Preview", "SRN.CC.Preview.csproj");
        string appCsproj = Path.Combine(root, "src", "SRN.CC.App", "SRN.CC.App.csproj");

        GetProjectReferences(coreCsproj).Should().BeEmpty("SRN.CC.Core must have no project references");
        GetProjectReferences(formatsCsproj).Should().BeEmpty("SRN.CC.Formats must have no project references");

        GetPackageReferences(coreCsproj).Should().NotContain(p => p.StartsWith("Avalonia"), "SRN.CC.Core must have no UI dependencies");
        GetPackageReferences(formatsCsproj).Should().NotContain(p => p.StartsWith("Avalonia"), "SRN.CC.Formats must have no UI dependencies");

        var infraRefs = GetProjectReferences(infraCsproj);
        infraRefs.Should().Contain(r => r.EndsWith("SRN.CC.Core.csproj"));
        infraRefs.Should().Contain(r => r.EndsWith("SRN.CC.Formats.csproj"));

        var previewRefs = GetProjectReferences(previewCsproj);
        previewRefs.Should().Contain(r => r.EndsWith("SRN.CC.Core.csproj"));
        previewRefs.Should().Contain(r => r.EndsWith("SRN.CC.Formats.csproj"));

        var appRefs = GetProjectReferences(appCsproj);
        appRefs.Should().Contain(r => r.EndsWith("SRN.CC.Core.csproj"));
        appRefs.Should().Contain(r => r.EndsWith("SRN.CC.Infrastructure.csproj"));
        appRefs.Should().Contain(r => r.EndsWith("SRN.CC.Preview.csproj"));
    }

    private static List<string> GetProjectReferences(string csprojPath)
    {
        XDocument doc = XDocument.Load(csprojPath);
        return doc.Descendants("ProjectReference")
                  .Select(e => e.Attribute("Include")?.Value ?? "")
                  .ToList();
    }

    private static List<string> GetPackageReferences(string csprojPath)
    {
        XDocument doc = XDocument.Load(csprojPath);
        return doc.Descendants("PackageReference")
                  .Select(e => e.Attribute("Include")?.Value ?? "")
                  .ToList();
    }
}
