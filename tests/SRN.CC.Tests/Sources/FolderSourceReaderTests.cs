using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Records;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Services;
using SRN.CC.Infrastructure.Sources;

namespace SRN.CC.Tests.Sources;

[TestFixture]
public class FolderSourceReaderTests
{
    private string _tempDir = null!;
    private ResourceTypeRegistry _typeRegistry = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SRNCC_FolderTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task IndexAsync_CanonicalAndNumericExtensions_MapsCorrectly()
    {
        // Setup folder structure
        string file1 = Path.Combine(_tempDir, "script.nss");
        string file2 = Path.Combine(_tempDir, "area.are");
        string file3 = Path.Combine(_tempDir, "custom.2002");
        string file4 = Path.Combine(_tempDir, "unknown.xyz");

        await File.WriteAllTextAsync(file1, "void main(){}");
        await File.WriteAllTextAsync(file2, "area data");
        await File.WriteAllTextAsync(file3, "model data");
        await File.WriteAllTextAsync(file4, "unknown data");

        AssetSource source = AssetSource.CreateFolder(_tempDir);
        FolderAssetSourceReader reader = new(_typeRegistry);

        SourceIndexSnapshot snapshot = await reader.IndexAsync(source);

        snapshot.Records.Should().HaveCount(4);
        snapshot.Diagnostics.Should().ContainSingle(d => d.Code == DiagnosticCode.UnknownFolderExtension && d.TargetPath == "unknown.xyz");

        var validRecords = snapshot.Records.Where(r => r.IsValid).ToList();
        validRecords.Should().HaveCount(3);

        validRecords.Should().Contain(r => r.Occurrence!.Identity.OriginalName == "script" && r.Occurrence.Identity.ResourceType == 2009);
        validRecords.Should().Contain(r => r.Occurrence!.Identity.OriginalName == "area" && r.Occurrence.Identity.ResourceType == 2012);
        validRecords.Should().Contain(r => r.Occurrence!.Identity.OriginalName == "custom" && r.Occurrence.Identity.ResourceType == 2002);
    }

    [Test]
    public async Task IndexAsync_NestedDirectories_PreservesProvenanceAndOrder()
    {
        string subDir = Path.Combine(_tempDir, "sub", "deep");
        Directory.CreateDirectory(subDir);

        string file1 = Path.Combine(_tempDir, "b_root.2da");
        string file2 = Path.Combine(subDir, "a_nested.utc");

        await File.WriteAllTextAsync(file1, "2da data");
        await File.WriteAllTextAsync(file2, "utc data");

        AssetSource source = AssetSource.CreateFolder(_tempDir);
        FolderAssetSourceReader reader = new(_typeRegistry);

        SourceIndexSnapshot snapshot = await reader.IndexAsync(source);

        var locators = snapshot.Records
            .Where(r => r.IsValid)
            .Select(r => ((FolderFileLocator)r.Occurrence!.Locator).NormalizedRelativePath)
            .ToList();

        // Ordinal sorting: b_root.2da vs sub/deep/a_nested.utc -> 'b_root.2da' comes before 'sub/deep/a_nested.utc'
        locators[0].Should().Be("b_root.2da");
        locators[1].Should().Be("sub/deep/a_nested.utc");
    }

    [Test]
    public async Task OpenOccurrenceAsync_ValidFile_ReturnsReadableStream()
    {
        string filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "Hello World Content");

        AssetSource source = AssetSource.CreateFolder(_tempDir);
        FolderAssetSourceReader reader = new(_typeRegistry);

        SourceIndexSnapshot snapshot = await reader.IndexAsync(source);
        var record = snapshot.Records.First(r => r.IsValid);

        using Stream stream = await reader.OpenOccurrenceAsync(source, record.Occurrence!);
        using StreamReader sr = new(stream);
        string content = await sr.ReadToEndAsync();

        content.Should().Be("Hello World Content");
    }

    [Test]
    public async Task IndexAsync_NonAsciiCaseVariants_AreDistinctIdentities()
    {
        string firstDirectory = Path.Combine(_tempDir, "first");
        string secondDirectory = Path.Combine(_tempDir, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        await File.WriteAllTextAsync(Path.Combine(firstDirectory, "\u00C9.nss"), "first");
        await File.WriteAllTextAsync(Path.Combine(secondDirectory, "\u00E9.nss"), "second");

        SourceIndexSnapshot snapshot = await new FolderAssetSourceReader(_typeRegistry)
            .IndexAsync(AssetSource.CreateFolder(_tempDir));

        snapshot.Records.Select(record => record.Occurrence).Where(occurrence => occurrence != null)
            .Should().OnlyContain(occurrence => occurrence!.ValidationState == ValidationState.Valid);
    }
}


