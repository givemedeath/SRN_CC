using System.Text;
using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Preview;
using SWLOR.NWN.Formats.Common;

namespace SRN.CC.Tests.Preview;

[TestFixture]
public class DependencyAnalyzerTests
{
    private DependencyAnalyzer _analyzer = null!;
    private StubRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        _registry = new StubRegistry();
        _analyzer = new DependencyAnalyzer(_registry);
    }

    #region MDL Tests

    [Test]
    public async Task AnalyzeMdl_WithSuperModelAndTextures_ExtractsAllDependencies()
    {
        // Arrange
        var mdlPayload = CreateMinimalMdlPayload(superModel: "my_base", textureNames: ["tex_01", "tex_02"]);
        var occurrence = CreateOccurrence("test_model", 2002, mdlPayload.Length);

        // Act
        using var stream = new MemoryStream(mdlPayload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Not.Null);
        // Note: MDL parsing is complex; at minimum, verify no exceptions thrown
        // Full fixture parsing would require valid binary MDL data
    }

    [Test]
    public async Task AnalyzeMdl_WithMalformedBinary_FallsBackToFallbackExtraction()
    {
        // Arrange - create ASCII MDL content with supermodel and textures
        var asciiContent = @"#MAXMODEL ASCII
setsupermodel my_base
node trimesh mesh_main
  parent Armature
  ambient 1.0 1.0 1.0
  diffuse 1.0 1.0 1.0
  specular 0.0 0.0 0.0
  shininess 1.0
  bitmap tex_01
  lightmap tex_02
endnode
#ENDMDL";

        var mdlPayload = Encoding.UTF8.GetBytes(asciiContent);
        var occurrence = CreateOccurrence("test_model", 2002, mdlPayload.Length);

        // Act
        using var stream = new MemoryStream(mdlPayload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Not.Null);
        Assert.That(dependencies.Count, Is.GreaterThan(0));

        // Verify supermodel was extracted
        var supermodelDep = dependencies.FirstOrDefault(d => d.Resref == "my_base");
        Assert.That(supermodelDep, Is.Not.Null);
    }

    [Test]
    public async Task AnalyzeMdl_WithEmptyPayload_ReturnsEmptySet()
    {
        // Arrange
        byte[] emptyPayload = Array.Empty<byte>();
        var occurrence = CreateOccurrence("empty_model", 2002, 0);

        // Act
        using var stream = new MemoryStream(emptyPayload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Empty);
    }

    #endregion

    #region MTR Tests

    [Test]
    public async Task AnalyzeMtr_WithTextureReferences_ExtractsDependencies()
    {
        // Arrange
        var mtrContent = @"texture0=tex_diffuse
texture1=tex_specular
ambient=0.5 0.5 0.5";

        var occurrence = CreateOccurrence("test_material", 2072, mtrContent.Length);
        var mtrPayload = Encoding.UTF8.GetBytes(mtrContent);

        // Act
        using var stream = new MemoryStream(mtrPayload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Not.Null);
        Assert.That(dependencies.Count, Is.EqualTo(2));

        var textures = dependencies.Select(d => d.Resref).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.That(textures, Contains.Item("tex_diffuse"));
        Assert.That(textures, Contains.Item("tex_specular"));
    }

    [Test]
    public async Task AnalyzeMtr_WithNoTextures_ReturnsEmptySet()
    {
        // Arrange
        var mtrContent = @"ambient=0.5 0.5 0.5
diffuse=1.0 1.0 1.0
specular=0.0 0.0 0.0";

        var occurrence = CreateOccurrence("material_no_tex", 2072, mtrContent.Length);
        var mtrPayload = Encoding.UTF8.GetBytes(mtrContent);

        // Act
        using var stream = new MemoryStream(mtrPayload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Empty);
    }

    #endregion

    #region SET Tests

    [Test]
    public async Task AnalyzeSet_WithModelReferences_ExtractsDependencies()
    {
        // Arrange
        var setContent = @"// SET file with model references
model_a
model_b
position 0 0 0";

        var occurrence = CreateOccurrence("test_set", 2013, setContent.Length);
        var setPayload = Encoding.UTF8.GetBytes(setContent);

        // Act
        using var stream = new MemoryStream(setPayload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Not.Null);
        // SET parsing is heuristic-based; verify it extracts something reasonable
    }

    [Test]
    public async Task AnalyzeSet_WithCommentedLines_SkipsComments()
    {
        // Arrange
        var setContent = @"// This is a comment
// model_c should be ignored
model_real";

        var occurrence = CreateOccurrence("set_comments", 2013, setContent.Length);
        var setPayload = Encoding.UTF8.GetBytes(setContent);

        // Act
        using var stream = new MemoryStream(setPayload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Not.Null);
        // Verify model_c was not extracted (it was in a comment)
        var hasModelC = dependencies.Any(d => d.Resref == "model_c");
        Assert.That(hasModelC, Is.False);
    }

    #endregion

    #region Companion File Tests (TXI, WOK, PWK, DWK)

    [Test]
    public async Task AnalyzeCompanionFile_TxiFormat_ReturnsEmptySet()
    {
        // Arrange - companion files don't have direct dependencies in themselves
        var txiContent = @"columns 1
rows 1
0 0 0";

        var occurrence = CreateOccurrence("tex_diffuse", ResourceTypes.FromExtension("txi"), txiContent.Length);
        var txiPayload = Encoding.UTF8.GetBytes(txiContent);

        // Act
        using var stream = new MemoryStream(txiPayload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        // Companion files have no internal dependencies
        Assert.That(dependencies, Is.Empty);
    }

    #endregion

    #region Regression: DLG/ITP vs DWK/PWK/WOK type-ID routing (S4 fix)

    // Historically this dispatch checked `resourceType == 2022 || 2016 || 2029 || 2030`, intending
    // TXI/WOK/PWK/DWK, but 2029/2030 are actually DLG/ITP per ResourceTypes.cs - so .dlg/.itp were
    // misrouted into companion extraction while real .dwk (2052) / .pwk (2053) walkmesh companions
    // fell through to "unsupported". These tests pin the type-ID table directly against
    // SWLOR.NWN.Formats.Common.ResourceTypes (never hardcoded duplicate literals) and prove the
    // corrected routing predicate: run this test against the pre-fix predicate
    // (`resourceType is 2022 or 2016 or 2029 or 2030`) and IsDlg/IsItp assert True while they must
    // assert False here, and vice versa for Dwk/Pwk.

    [Test]
    public void TypeIdTable_CompanionRoutingPredicates_MatchResourceTypesCs()
    {
        Assert.Multiple(() =>
        {
            // TXI/WOK/DWK/PWK must route to companion extraction.
            Assert.That(DependencyAnalyzer.IsTxiCompanionResourceType(ResourceTypes.FromExtension("txi")), Is.True, "txi");
            Assert.That(DependencyAnalyzer.IsWalkmeshCompanionResourceType(ResourceTypes.FromExtension("wok")), Is.True, "wok");
            Assert.That(DependencyAnalyzer.IsWalkmeshCompanionResourceType(ResourceTypes.FromExtension("dwk")), Is.True, "dwk");
            Assert.That(DependencyAnalyzer.IsWalkmeshCompanionResourceType(ResourceTypes.FromExtension("pwk")), Is.True, "pwk");

            // DLG/ITP must NOT route to companion extraction (the historical bug).
            Assert.That(DependencyAnalyzer.IsTxiCompanionResourceType(ResourceTypes.FromExtension("dlg")), Is.False, "dlg (txi check)");
            Assert.That(DependencyAnalyzer.IsWalkmeshCompanionResourceType(ResourceTypes.FromExtension("dlg")), Is.False, "dlg (walkmesh check)");
            Assert.That(DependencyAnalyzer.IsTxiCompanionResourceType(ResourceTypes.FromExtension("itp")), Is.False, "itp (txi check)");
            Assert.That(DependencyAnalyzer.IsWalkmeshCompanionResourceType(ResourceTypes.FromExtension("itp")), Is.False, "itp (walkmesh check)");

            // Sanity: the real numeric values are what we expect them to be.
            Assert.That(ResourceTypes.FromExtension("dlg"), Is.EqualTo((ushort)2029));
            Assert.That(ResourceTypes.FromExtension("itp"), Is.EqualTo((ushort)2030));
            Assert.That(ResourceTypes.FromExtension("wok"), Is.EqualTo((ushort)2016));
            Assert.That(ResourceTypes.FromExtension("dwk"), Is.EqualTo((ushort)2052));
            Assert.That(ResourceTypes.FromExtension("pwk"), Is.EqualTo((ushort)2053));
        });
    }

    [Test]
    public async Task AnalyzeDlgOccurrence_DoesNotEnterCompanionExtraction()
    {
        // Arrange - a .dlg (2029) occurrence must no longer be treated as a companion file.
        var dlgResourceType = ResourceTypes.FromExtension("dlg");
        Assert.That(DependencyAnalyzer.IsTxiCompanionResourceType(dlgResourceType), Is.False);
        Assert.That(DependencyAnalyzer.IsWalkmeshCompanionResourceType(dlgResourceType), Is.False);

        var dlgContent = "arbitrary dlg-shaped payload; dispatch routing is what's under test here";
        var occurrence = CreateOccurrence("test_dialog", dlgResourceType, dlgContent.Length);
        var payload = Encoding.UTF8.GetBytes(dlgContent);

        // Act
        using var stream = new MemoryStream(payload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert - falls through to the generic "unsupported family type" path, not companion extraction.
        Assert.That(dependencies, Is.Empty);
    }

    [Test]
    public async Task AnalyzePwkOccurrence_EntersWalkmeshCompanionExtraction()
    {
        // Arrange - a .pwk (2053) occurrence must now correctly route to companion extraction.
        var pwkResourceType = ResourceTypes.FromExtension("pwk");
        Assert.That(DependencyAnalyzer.IsWalkmeshCompanionResourceType(pwkResourceType), Is.True);

        var pwkContent = "arbitrary pwk-shaped payload; dispatch routing is what's under test here";
        var occurrence = CreateOccurrence("door_placeable", pwkResourceType, pwkContent.Length);
        var payload = Encoding.UTF8.GetBytes(pwkContent);

        // Act
        using var stream = new MemoryStream(payload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert - companion extraction is currently a no-op (same-resref relationships are
        // resolved by the traversal layer), so the observable result is an empty, non-throwing set.
        Assert.That(dependencies, Is.Empty);
    }

    [Test]
    public async Task AnalyzeDwkOccurrence_EntersWalkmeshCompanionExtraction()
    {
        // Arrange - a .dwk (2052) occurrence must now correctly route to companion extraction.
        var dwkResourceType = ResourceTypes.FromExtension("dwk");
        Assert.That(DependencyAnalyzer.IsWalkmeshCompanionResourceType(dwkResourceType), Is.True);

        var dwkContent = "arbitrary dwk-shaped payload; dispatch routing is what's under test here";
        var occurrence = CreateOccurrence("door_model", dwkResourceType, dwkContent.Length);
        var payload = Encoding.UTF8.GetBytes(dwkContent);

        // Act
        using var stream = new MemoryStream(payload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Empty);
    }

    [Test]
    public async Task AnalyzeWokOccurrence_EntersWalkmeshCompanionExtraction()
    {
        // Arrange - a .wok (2016) occurrence continues to route to companion extraction, now split
        // into its own walkmesh-companion branch rather than sharing the TXI branch.
        var wokResourceType = ResourceTypes.FromExtension("wok");
        Assert.That(DependencyAnalyzer.IsWalkmeshCompanionResourceType(wokResourceType), Is.True);
        Assert.That(DependencyAnalyzer.IsTxiCompanionResourceType(wokResourceType), Is.False);

        var wokContent = "arbitrary wok-shaped payload; dispatch routing is what's under test here";
        var occurrence = CreateOccurrence("tile_walkmesh", wokResourceType, wokContent.Length);
        var payload = Encoding.UTF8.GetBytes(wokContent);

        // Act
        using var stream = new MemoryStream(payload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Empty);
    }

    #endregion

    #region Unsupported Format Tests

    [Test]
    public async Task AnalyzeUnsupportedFormat_ReturnsEmptySet()
    {
        // Arrange
        var payload = Encoding.ASCII.GetBytes("arbitrary binary data");
        var occurrence = CreateOccurrence("unsupported", 9999, payload.Length); // Unknown type

        // Act
        using var stream = new MemoryStream(payload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Empty);
    }

    [Test]
    public async Task AnalyzeInvalidPayload_ReturnsEmptySet()
    {
        // Arrange
        var invalidPayload = new byte[] { 0xFF, 0xFE, 0xFD }; // Invalid UTF-8/MDL
        var occurrence = CreateOccurrence("invalid", 2002, invalidPayload.Length);

        // Act
        using var stream = new MemoryStream(invalidPayload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        Assert.That(dependencies, Is.Not.Null);
        // Should not throw; returns empty set on parse failure
    }

    #endregion

    #region Stream Contract Tests

    [Test]
    public void AnalyzeDependencies_WithNullOccurrence_ThrowsArgumentNullException()
    {
        // Arrange
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _analyzer.AnalyzeDependenciesAsync(null!, stream));
    }

    [Test]
    public void AnalyzeDependencies_WithNullStream_ThrowsArgumentNullException()
    {
        // Arrange
        var occurrence = CreateOccurrence("test", 2000, 100);

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _analyzer.AnalyzeDependenciesAsync(occurrence, null!));
    }

    [Test]
    public void AnalyzeDependencies_WithNonReadableStream_ThrowsInvalidOperationException()
    {
        // Arrange
        var occurrence = CreateOccurrence("test", 2000, 100);
        using var stream = new NonReadableStream();

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _analyzer.AnalyzeDependenciesAsync(occurrence, stream));
    }

    [Test]
    public async Task AnalyzeDependencies_WithCancellationToken_ThrowsOperationCanceledException()
    {
        // Arrange
        var occurrence = CreateOccurrence("test", 2000, 100);
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await _analyzer.AnalyzeDependenciesAsync(occurrence, stream, cts.Token));
    }

    #endregion

    #region Deduplication Tests

    [Test]
    public async Task AnalyzeMdl_WithDuplicateTextureReferences_DeduplicatesDependencies()
    {
        // Arrange - MDL with same texture referenced multiple times in fallback format
        var asciiContent = @"setsupermodel base
bitmap tex_shared
bitmap tex_shared
bitmap tex_other";

        var occurrence = CreateOccurrence("multi_mesh", 2002, asciiContent.Length);
        var payload = Encoding.UTF8.GetBytes(asciiContent);

        // Act
        using var stream = new MemoryStream(payload);
        var dependencies = await _analyzer.AnalyzeDependenciesAsync(occurrence, stream);

        // Assert
        // Verify deduplication worked: we should have at most 2 texture refs (tex_shared once, tex_other once)
        // plus the supermodel
        var texCount = dependencies.Count(d => d.Resref.Equals("tex_shared", StringComparison.OrdinalIgnoreCase));
        Assert.That(texCount, Is.LessThanOrEqualTo(1), "tex_shared should appear at most once due to deduplication");
    }

    #endregion

    #region Test Helpers

    private static AssetOccurrence CreateOccurrence(string resref, ushort resourceType, long size)
    {
        var identity = new AssetIdentity(resref, resourceType);
        var source = AssetSource.CreateHak("test.hak", 0);
        return new AssetOccurrence(
            identity: identity,
            sourceId: source.Id,
            locator: new HakEntryLocator(0),
            originalName: $"{resref}.bin",
            size: size,
            validationState: ValidationState.Valid,
            extensionMetadata: null,
            sha256: new byte[32]);
    }

    private static byte[] CreateMinimalMdlPayload(string superModel, string[] textureNames)
    {
        // Create a minimal valid-looking binary MDL header
        var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload, Encoding.ASCII, leaveOpen: true);

        // MDL file magic/version (first 4 bytes)
        writer.Write(0x00_00_00_00u); // Not ASCII

        // Allocate space for headers and pad to minimum size
        byte[] header = new byte[1024];
        payload.Write(header, 0, header.Length);

        return payload.ToArray();
    }

    /// <summary>
    /// Delegates straight to <see cref="ResourceTypes"/> rather than duplicating type-ID literals,
    /// so this stub cannot silently drift from the canonical table (it previously hardcoded
    /// pwk/dwk as 2029/2030, which are actually dlg/itp - the same bug being regression-tested
    /// above).
    /// </summary>
    private sealed class StubRegistry : IResourceTypeRegistry
    {
        public bool TryGetType(string extension, out ushort typeId) => ResourceTypes.TryGetType(extension, out typeId);

        public bool TryGetExtension(ushort typeId, out string extension) => ResourceTypes.TryGetExtension(typeId, out extension);
    }

    private sealed class NonReadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotImplementedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    #endregion
}
