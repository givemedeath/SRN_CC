using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace SRN.CC.Tests.Architecture;

/// <summary>
/// Cross-checks the build's single version source, <c>eng/Versions.props</c>, against the attributes
/// actually baked into the loaded production assemblies.
/// </summary>
/// <remarks>
/// The properties are read from the file on disk rather than from a constant in the test, so this is
/// a real cross-check: a version bump that fails to reach an assembly, or an assembly that acquires
/// its own version literal, breaks the test. Repo-root discovery mirrors
/// <see cref="ArchitectureTests"/>.
/// </remarks>
[TestFixture]
public class VersionStampingTests
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

    private static string ReadVersionsProperty(string name)
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, "eng", "Versions.props");
        File.Exists(path).Should().BeTrue("eng/Versions.props is the single source of the product version");

        XDocument doc = XDocument.Load(path);
        var element = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == name);

        element.Should().NotBeNull($"eng/Versions.props must define <{name}>");
        string value = element!.Value.Trim();
        value.Should().NotBeEmpty($"<{name}> must not be blank");
        return value;
    }

    /// <summary>
    /// The five shipped assemblies. Each is named by a type so the reference is checked at compile
    /// time and the assembly is guaranteed to be loaded when the test runs.
    /// </summary>
    private static IEnumerable<Assembly> ProductionAssemblies()
    {
        yield return typeof(SRN.CC.Core.Schema.SchemaVersions).Assembly;
        yield return typeof(SRN.CC.Formats.Hak.HakReader).Assembly;
        yield return typeof(SRN.CC.Infrastructure.Build.ProvenanceManifestGenerator).Assembly;
        yield return typeof(SRN.CC.Preview.Render.MdlSceneBuilder).Assembly;
        yield return typeof(SRN.CC.App.Services.DependencyLocator).Assembly;
    }

    [Test]
    public void ProductionAssemblies_AllFiveExpectedNamesAreCovered()
    {
        ProductionAssemblies()
            .Select(a => a.GetName().Name)
            .Should().BeEquivalentTo(new[]
            {
                "SRN.CC.Core",
                "SRN.CC.Formats",
                "SRN.CC.Infrastructure",
                "SRN.CC.Preview",
                "SRN.CC.App"
            });
    }

    [Test]
    public void InformationalVersion_ForEveryProductionAssembly_EqualsVersionsPropsVersionPrefix()
    {
        string expected = ReadVersionsProperty("SRNCCVersionPrefix");

        foreach (Assembly assembly in ProductionAssemblies())
        {
            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            attribute.Should().NotBeNull($"{assembly.GetName().Name} must be version stamped");

            attribute!.InformationalVersion.Should().Be(
                expected,
                $"{assembly.GetName().Name} must take its version from eng/Versions.props");
        }
    }

    [Test]
    public void InformationalVersion_ForEveryProductionAssembly_CarriesNoSourceRevisionSuffix()
    {
        foreach (Assembly assembly in ProductionAssemblies())
        {
            string informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion;

            informational.Should().NotContain(
                "+",
                $"{assembly.GetName().Name} must be built with "
                + "IncludeSourceRevisionInInformationalVersion=false; a '+<sha>' suffix reaches the "
                + "provenance manifest's AppVersion and destroys build-over-build determinism");
        }
    }

    [Test]
    public void AssemblyAndFileVersion_ForEveryProductionAssembly_EqualVersionPrefixWithFourthPartZero()
    {
        string expected = ReadVersionsProperty("SRNCCVersionPrefix") + ".0";

        foreach (Assembly assembly in ProductionAssemblies())
        {
            assembly.GetName().Version!.ToString().Should().Be(
                expected,
                $"{assembly.GetName().Name}'s AssemblyVersion is $(SRNCCVersionPrefix).0");

            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version.Should().Be(
                expected,
                $"{assembly.GetName().Name}'s FileVersion is $(SRNCCVersionPrefix).0");
        }
    }

    [Test]
    public void ProductAndCompany_ForEveryProductionAssembly_ComeFromVersionsProps()
    {
        string expectedProduct = ReadVersionsProperty("SRNCCProductName");
        string expectedCompany = ReadVersionsProperty("SRNCCCompany");

        foreach (Assembly assembly in ProductionAssemblies())
        {
            assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product
                .Should().Be(expectedProduct, $"{assembly.GetName().Name} carries the product name");

            assembly.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company
                .Should().Be(expectedCompany, $"{assembly.GetName().Name} carries the company name");
        }
    }

    [Test]
    public void ProductionProjects_DeclareNoVersionLiteralOfTheirOwn()
    {
        string root = FindRepoRoot();
        string[] csprojNames =
        {
            "SRN.CC.Core", "SRN.CC.Formats", "SRN.CC.Infrastructure", "SRN.CC.Preview", "SRN.CC.App"
        };

        string[] versionElements =
        {
            "Version", "VersionPrefix", "AssemblyVersion", "FileVersion", "InformationalVersion",
            "Product", "Company"
        };

        foreach (string name in csprojNames)
        {
            string path = Path.Combine(root, "src", name, name + ".csproj");
            File.Exists(path).Should().BeTrue($"{name}.csproj must exist");

            XDocument doc = XDocument.Load(path);
            var offenders = doc.Descendants()
                .Where(e => versionElements.Contains(e.Name.LocalName))
                .Select(e => e.Name.LocalName)
                .ToList();

            offenders.Should().BeEmpty(
                $"{name}.csproj must inherit its version from Directory.Build.props, not redeclare it");
        }
    }
}
