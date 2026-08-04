using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Build;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Schema;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Formats.Hak;
using SRN.CC.Infrastructure.Build;
using SRN.CC.Infrastructure.Services;

namespace SRN.CC.Tests.Build;

/// <summary>
/// Covers the provenance manifest's version fields and the determinism guarantee documented in
/// <c>docs/operator/MANIFEST-FORMAT.md</c>: two builds of identical content produce manifests that
/// differ only in <c>GeneratedUtc</c>.
/// </summary>
[TestFixture]
public class ProvenanceManifestGeneratorTests
{
    /// <summary>Matches a Windows drive-qualified absolute path, e.g. <c>C:\</c>.</summary>
    private static readonly Regex AbsolutePathPattern = new(@"[A-Za-z]:\\", RegexOptions.Compiled);

    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "srncc_manifest_tests_" + Guid.NewGuid().ToString("N"));
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
    public async Task GenerateManifestAsync_IdenticalInputDifferentPlanTimestamp_DiffersOnlyInGeneratedUtc()
    {
        string hakPath = await WriteFixtureHakAsync();

        var first = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var second = first.AddHours(7);

        string manifestA = await GenerateAsync(hakPath, first, "a.srncc-manifest.json");
        string manifestB = await GenerateAsync(hakPath, second, "b.srncc-manifest.json");

        string[] linesA = SplitLines(manifestA);
        string[] linesB = SplitLines(manifestB);

        linesB.Length.Should().Be(linesA.Length, "a timestamp change must not change the manifest's shape");

        var differing = new List<string>();
        for (int i = 0; i < linesA.Length; i++)
        {
            if (!string.Equals(linesA[i], linesB[i], StringComparison.Ordinal))
            {
                differing.Add(linesA[i].Trim());
            }
        }

        differing.Should().ContainSingle("GeneratedUtc is the only field permitted to vary between "
            + "builds of identical content");
        differing[0].Should().StartWith("\"GeneratedUtc\":");

        using JsonDocument docA = JsonDocument.Parse(manifestA);
        using JsonDocument docB = JsonDocument.Parse(manifestB);
        docA.RootElement.GetProperty("GeneratedUtc").GetString()
            .Should().NotBe(docB.RootElement.GetProperty("GeneratedUtc").GetString());
    }

    [Test]
    public async Task GenerateManifestAsync_Always_WritesAppVersionFromRuntimeInformationalVersion()
    {
        string hakPath = await WriteFixtureHakAsync();
        string manifest = await GenerateAsync(hakPath, DateTime.UtcNow, "version.srncc-manifest.json");

        string expected = typeof(ProvenanceManifestGenerator).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        using JsonDocument doc = JsonDocument.Parse(manifest);
        string? appVersion = doc.RootElement.GetProperty("AppVersion").GetString();

        appVersion.Should().Be(expected);
        appVersion.Should().NotContain("+", "IncludeSourceRevisionInInformationalVersion=false keeps "
            + "the commit SHA out of the manifest, which is what makes the manifest deterministic");
        appVersion.Should().NotBe("0.0.0-unknown", "the assembly must actually carry a stamped version");
    }

    [Test]
    public async Task GenerateManifestAsync_Always_WritesSchemaVersionFromSchemaVersionsRegistry()
    {
        string hakPath = await WriteFixtureHakAsync();
        string manifest = await GenerateAsync(hakPath, DateTime.UtcNow, "schema.srncc-manifest.json");

        using JsonDocument doc = JsonDocument.Parse(manifest);
        doc.RootElement.GetProperty("SchemaVersion").GetString().Should().Be(SchemaVersions.Manifest);
    }

    [Test]
    public async Task GenerateManifestAsync_SourcesUnderAbsolutePaths_WritesNoDriveQualifiedPath()
    {
        string hakPath = await WriteFixtureHakAsync();
        string manifest = await GenerateAsync(hakPath, DateTime.UtcNow, "paths.srncc-manifest.json");

        // The fixture's sources genuinely live under an absolute temp path, so a generator that
        // leaked FullPath instead of the file name would be caught here.
        _tempDir.Should().MatchRegex(@"^[A-Za-z]:\\", "the fixture must exercise absolute source paths");
        AbsolutePathPattern.IsMatch(manifest).Should().BeFalse(
            "a manifest is distributed alongside the HAK and must not disclose local paths");
    }

    [Test]
    public async Task GenerateManifestAsync_Always_WritesPascalCaseFieldNames()
    {
        string hakPath = await WriteFixtureHakAsync();
        string manifest = await GenerateAsync(hakPath, DateTime.UtcNow, "casing.srncc-manifest.json");

        using JsonDocument doc = JsonDocument.Parse(manifest);
        var root = doc.RootElement;

        foreach (string name in new[]
                 {
                     "SchemaVersion", "AppVersion", "GeneratedUtc", "HakFileName",
                     "HakSha256Hex", "TotalEntries", "TotalSizeBytes", "Resources"
                 })
        {
            root.TryGetProperty(name, out _).Should().BeTrue($"'{name}' is documented in MANIFEST-FORMAT.md");
        }

        var resource = root.GetProperty("Resources").EnumerateArray().First();
        foreach (string name in new[]
                 {
                     "Resref", "ResourceType", "ResourceTypeName", "SourceLabel",
                     "OriginLocator", "SizeBytes", "Sha256Hex", "IsPinned"
                 })
        {
            resource.TryGetProperty(name, out _).Should().BeTrue($"'{name}' is documented in MANIFEST-FORMAT.md");
        }
    }

    [Test]
    public async Task GenerateManifestAsync_ItemWithoutExpectedHash_ComputesPayloadHashFromHak()
    {
        string hakPath = await WriteFixtureHakAsync();
        string manifest = await GenerateAsync(hakPath, DateTime.UtcNow, "hash.srncc-manifest.json");

        using JsonDocument doc = JsonDocument.Parse(manifest);
        var resources = doc.RootElement.GetProperty("Resources").EnumerateArray().ToList();

        resources.Should().HaveCount(2);
        doc.RootElement.GetProperty("TotalEntries").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("TotalSizeBytes").GetInt64()
            .Should().Be(PayloadOne.Length + PayloadTwo.Length);

        string unhashed = resources.Single(r => r.GetProperty("Resref").GetString() == "loosetex")
            .GetProperty("Sha256Hex").GetString()!;
        unhashed.Should().Be(Convert.ToHexString(SHA256.HashData(PayloadTwo)).ToLowerInvariant());
    }

    // --- fixture -----------------------------------------------------------------------------

    private static readonly byte[] PayloadOne = Encoding.ASCII.GetBytes("first payload bytes");
    private static readonly byte[] PayloadTwo = Encoding.ASCII.GetBytes("second payload bytes, a little longer");

    private static readonly AssetIdentity IdentityOne = new("hakmodel", 2002);
    private static readonly AssetIdentity IdentityTwo = new("loosetex", 2022);

    private async Task<string> WriteFixtureHakAsync()
    {
        string hakPath = Path.Combine(_tempDir, "fixture.hak");

        using FileStream fs = new(hakPath, FileMode.Create, FileAccess.Write);
        await HakWriter.WriteAsync(fs, new[]
        {
            new HakWriter.WriteItem(
                new HakFormatKey(IdentityOne.OriginalResrefBytes.Span, IdentityOne.ResourceType),
                new MemoryStream(PayloadOne),
                (uint)PayloadOne.Length),
            new HakWriter.WriteItem(
                new HakFormatKey(IdentityTwo.OriginalResrefBytes.Span, IdentityTwo.ResourceType),
                new MemoryStream(PayloadTwo),
                (uint)PayloadTwo.Length)
        });

        return hakPath;
    }

    /// <summary>
    /// Generates one manifest and returns its text. Every call builds the same plan apart from
    /// <paramref name="createdUtc"/>, which is the only value <c>GeneratedUtc</c> is derived from.
    /// </summary>
    private async Task<string> GenerateAsync(string hakPath, DateTime createdUtc, string manifestName)
    {
        // Fixed GUIDs: a random source id would leak into SourceLabel when a source fails to resolve
        // and would make the determinism comparison meaningless.
        AssetSource hakSource = AssetSource.CreateHak(
            Path.Combine(_tempDir, "base_override.hak"),
            0,
            new Guid("11111111-1111-1111-1111-111111111111"));

        AssetSource folderSource = AssetSource.CreateFolder(
            Path.Combine(_tempDir, "loose_textures"),
            1,
            new Guid("22222222-2222-2222-2222-222222222222"));

        var plan = new BuildPlan
        {
            DestinationHakPath = Path.Combine(_tempDir, "curated.hak"),
            DestinationManifestPath = Path.Combine(_tempDir, manifestName),
            FrozenSources = new[] { hakSource, folderSource },
            Items = new[]
            {
                new BuildItem(
                    IdentityOne,
                    hakSource.Id,
                    new HakEntryLocator(0),
                    PayloadOne.Length,
                    Convert.ToHexString(SHA256.HashData(PayloadOne)).ToLowerInvariant(),
                    IsPinned: true),
                // Null expected hash: forces the generator down the recompute-from-HAK path.
                new BuildItem(
                    IdentityTwo,
                    folderSource.Id,
                    new FolderFileLocator("textures/loosetex.txi"),
                    PayloadTwo.Length,
                    null,
                    IsPinned: false)
            },
            CreatedUtc = createdUtc
        };

        string manifestPath = Path.Combine(_tempDir, manifestName);
        IResourceTypeRegistry registry = new ResourceTypeRegistry();
        var generator = new ProvenanceManifestGenerator(registry);

        await generator.GenerateManifestAsync(
            plan,
            hakPath,
            manifestPath,
            computedHakSha256Hex: "deadbeef");

        return await File.ReadAllTextAsync(manifestPath);
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
