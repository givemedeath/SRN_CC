using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Services;
using SRN.CC.Preview.Render;

namespace SRN.CC.Tests.Preview.Render;

[TestFixture]
public class TextureResolverTests
{
    private static readonly SceneBuildBudget GenerousBudget = new(
        CpuBytes: 48L * 1024 * 1024, TextureBytes: 32L * 1024 * 1024, MaxTextures: 64, MaxDrawCalls: 4096);

    [Test]
    public async Task ResolveAsync_MtrTexture0_OverridesBaseName()
    {
        var source = new FakeTextureSource();
        source.AddMtr("base.mtr", "texture0 override_name");
        source.Add("override_name.tga", TgaHeader(width: 5, height: 1));

        var resolver = new TextureResolver(source);
        TextureResolution resolution = await resolver.ResolveAsync("base", TextureKind.Diffuse, GenerousBudget);

        Assert.That(resolution.Image, Is.Not.Null);
        Assert.That(resolution.Image!.Width, Is.EqualTo(5));
        Assert.That(resolution.Image.Name, Is.EqualTo("override_name"));
        Assert.That(source.Requests, Does.Not.Contain("base.dds"));
        Assert.That(source.Requests, Does.Not.Contain("base.tga"));
    }

    [Test]
    public async Task ResolveAsync_WithoutMtr_UsesBaseNameDirectly()
    {
        var source = new FakeTextureSource();
        source.Add("plain.tga", TgaHeader(width: 3, height: 1));

        var resolver = new TextureResolver(source);
        TextureResolution resolution = await resolver.ResolveAsync("plain", TextureKind.Diffuse, GenerousBudget);

        Assert.That(resolution.Image, Is.Not.Null);
        Assert.That(resolution.Image!.Width, Is.EqualTo(3));
    }

    [Test]
    public async Task ResolveAsync_DdsBeatsTgaBeatsPlt_WhenAllThreePresent()
    {
        var source = new FakeTextureSource();
        source.Add("multi.dds", BuildRgb24Dds(width: 2, height: 1));
        source.Add("multi.tga", TgaHeader(width: 3, height: 1));
        source.Add("multi.plt", BuildPlt(width: 4, height: 1));

        var resolver = new TextureResolver(source);
        TextureResolution resolution = await resolver.ResolveAsync("multi", TextureKind.Diffuse, GenerousBudget);

        Assert.That(resolution.Image, Is.Not.Null);
        Assert.That(resolution.Image!.Width, Is.EqualTo(2), "DDS (width 2) must win over TGA/PLT.");
    }

    [Test]
    public async Task ResolveAsync_TgaBeatsPlt_WhenDdsAbsent()
    {
        var source = new FakeTextureSource();
        source.Add("dual.tga", TgaHeader(width: 3, height: 1));
        source.Add("dual.plt", BuildPlt(width: 4, height: 1));

        var resolver = new TextureResolver(source);
        TextureResolution resolution = await resolver.ResolveAsync("dual", TextureKind.Diffuse, GenerousBudget);

        Assert.That(resolution.Image, Is.Not.Null);
        Assert.That(resolution.Image!.Width, Is.EqualTo(3), "TGA (width 3) must win over PLT.");
    }

    [Test]
    public async Task ResolveAsync_PltUsedAsLastResort()
    {
        var source = new FakeTextureSource();
        source.Add("plonly.plt", BuildPlt(width: 4, height: 1));

        var resolver = new TextureResolver(source);
        TextureResolution resolution = await resolver.ResolveAsync("plonly", TextureKind.Diffuse, GenerousBudget);

        Assert.That(resolution.Image, Is.Not.Null);
        Assert.That(resolution.Image!.Width, Is.EqualTo(4));
    }

    [Test]
    public async Task ResolveAsync_UnknownTexture_ReturnsNullImageAndListsUnresolvedName_NeverThrows()
    {
        var source = new FakeTextureSource();
        var resolver = new TextureResolver(source);

        TextureResolution resolution = await resolver.ResolveAsync("missing", TextureKind.Diffuse, GenerousBudget);

        Assert.That(resolution.Image, Is.Null);
        Assert.That(resolution.UnresolvedTextures, Does.Contain("missing"));
    }

    [Test]
    public async Task ResolveAsync_NullOrWhitespaceTextureName_ResolvesToNullImage_NeverThrows()
    {
        var source = new FakeTextureSource();
        var resolver = new TextureResolver(source);

        TextureResolution nullResult = await resolver.ResolveAsync(null, TextureKind.Diffuse, GenerousBudget);
        TextureResolution blankResult = await resolver.ResolveAsync("   ", TextureKind.Lightmap, GenerousBudget);

        Assert.That(nullResult.Image, Is.Null);
        Assert.That(nullResult.UnresolvedTextures, Is.Empty);
        Assert.That(blankResult.Image, Is.Null);
        Assert.That(blankResult.UnresolvedTextures, Is.Empty);
    }

    [Test]
    public async Task ResolveAsync_MaxTexturesBudget_CapsCountAcrossCallsOnSameResolver()
    {
        var source = new FakeTextureSource();
        source.Add("first.tga", TgaHeader(width: 1, height: 1));
        source.Add("second.tga", TgaHeader(width: 1, height: 1));

        var budget = new SceneBuildBudget(CpuBytes: 1024, TextureBytes: 1024 * 1024, MaxTextures: 1, MaxDrawCalls: 10);
        var resolver = new TextureResolver(source);

        TextureResolution first = await resolver.ResolveAsync("first", TextureKind.Diffuse, budget);
        TextureResolution second = await resolver.ResolveAsync("second", TextureKind.Diffuse, budget);

        Assert.That(first.Image, Is.Not.Null);
        Assert.That(second.Image, Is.Null, "second texture must be refused once MaxTextures is reached.");
        Assert.That(second.UnresolvedTextures, Does.Contain("second"));
        Assert.That(second.Diagnostics, Has.Some.Contains("MaxTextures"));
        Assert.That(resolver.TexturesLoaded, Is.EqualTo(1));
    }

    [Test]
    public async Task ResolveAsync_TextureBytesBudget_RefusesOversizedTextureWithDiagnostic_NeverThrows()
    {
        var source = new FakeTextureSource();
        // 8x8 truecolor TGA decodes to 8*8*4 = 256 bytes, comfortably over a tiny budget.
        source.Add("big.tga", TgaHeader(width: 8, height: 8));

        var budget = new SceneBuildBudget(CpuBytes: 1024, TextureBytes: 32, MaxTextures: 64, MaxDrawCalls: 10);
        var resolver = new TextureResolver(source);

        TextureResolution resolution = await resolver.ResolveAsync("big", TextureKind.Diffuse, budget);

        Assert.That(resolution.Image, Is.Null);
        Assert.That(resolution.UnresolvedTextures, Does.Contain("big"));
        Assert.That(resolution.Diagnostics, Has.Some.Contains("budget"));
        Assert.That(resolver.TextureBytesUsed, Is.EqualTo(0));
    }

    [Test]
    public async Task ResolveAsync_EnvironmentMapFromMtr_IsReportedButNeverShaded()
    {
        var source = new FakeTextureSource();
        source.AddMtr("wall.mtr", "texture0 wall_diff\nenvmap wall_env");
        source.Add("wall_diff.tga", TgaHeader(width: 2, height: 1));
        source.Add("wall_env.tga", TgaHeader(width: 9, height: 9)); // exists, so it resolves cleanly

        var resolver = new TextureResolver(source);
        TextureResolution resolution = await resolver.ResolveAsync("wall", TextureKind.Diffuse, GenerousBudget);

        Assert.That(resolution.Image, Is.Not.Null);
        Assert.That(resolution.Image!.Width, Is.EqualTo(2), "the diffuse image must be wall_diff, never the env map.");
        Assert.That(resolution.EnvironmentMapName, Is.EqualTo("wall_env"));
        Assert.That(resolution.UnresolvedTextures, Does.Not.Contain("wall_env"), "a resolvable env map is not unresolved.");
    }

    [Test]
    public async Task ResolveAsync_UnresolvableEnvironmentMap_IsReportedAsUnresolvedWithDiagnostic()
    {
        var source = new FakeTextureSource();
        source.AddMtr("wall2.mtr", "texture0 wall2_diff\nenvmap missing_env");
        source.Add("wall2_diff.tga", TgaHeader(width: 2, height: 1));

        var resolver = new TextureResolver(source);
        TextureResolution resolution = await resolver.ResolveAsync("wall2", TextureKind.Diffuse, GenerousBudget);

        Assert.That(resolution.Image, Is.Not.Null);
        Assert.That(resolution.EnvironmentMapName, Is.EqualTo("missing_env"));
        Assert.That(resolution.UnresolvedTextures, Does.Contain("missing_env"));
        Assert.That(resolution.Diagnostics, Has.Some.Contains("missing_env"));
    }

    [Test]
    public async Task ResolveAsync_EnvironmentMapFromTxi_WhenNoMtrOverride()
    {
        var source = new FakeTextureSource();
        source.Add("floor.tga", TgaHeader(width: 2, height: 1));
        source.AddTxi("floor.txi", "envmaptexture floor_env");
        source.Add("floor_env.tga", TgaHeader(width: 1, height: 1));

        var resolver = new TextureResolver(source);
        TextureResolution resolution = await resolver.ResolveAsync("floor", TextureKind.Diffuse, GenerousBudget);

        Assert.That(resolution.Image, Is.Not.Null);
        Assert.That(resolution.EnvironmentMapName, Is.EqualTo("floor_env"));
    }

    [Test]
    public void ResolveAsync_PreCancelledToken_ThrowsOperationCanceled_RatherThanSwallowing()
    {
        var source = new FakeTextureSource();
        var resolver = new TextureResolver(source);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync("anything", TextureKind.Diffuse, GenerousBudget, cts.Token));
    }

    // -------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------

    private sealed class FakeTextureSource : ITextureSource
    {
        private readonly Dictionary<string, byte[]> _entries = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requests { get; } = new();

        public void Add(string resrefWithExtension, byte[] bytes) => _entries[resrefWithExtension] = bytes;

        public void AddMtr(string resrefWithExtension, string body) =>
            Add(resrefWithExtension, System.Text.Encoding.UTF8.GetBytes(body));

        public void AddTxi(string resrefWithExtension, string body) =>
            Add(resrefWithExtension, System.Text.Encoding.UTF8.GetBytes(body));

        public Task<TextureLookupResult?> OpenTextureAsync(
            string resref, TextureKind kind, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(resref);

            if (!_entries.TryGetValue(resref, out byte[]? bytes))
            {
                return Task.FromResult<TextureLookupResult?>(null);
            }

            var identity = new AssetIdentity(StripExtension(resref), 0);
            TextureLookupResult result = new(identity, 0, new MemoryStream(bytes), SRN.CC.Core.Services.TextureOrigin.Workspace);
            return Task.FromResult<TextureLookupResult?>(result);
        }

        private static string StripExtension(string name)
        {
            int lastDot = name.LastIndexOf('.');
            return lastDot > 0 ? name[..lastDot] : name;
        }
    }

    private static byte[] TgaHeader(ushort width, ushort height)
    {
        // Uncompressed truecolor, 24bpp, top-left origin; body filled with zeroed BGR triples.
        var header = new byte[18];
        header[2] = 2; // image type: uncompressed truecolor
        BitConverter.GetBytes(width).CopyTo(header, 12);
        BitConverter.GetBytes(height).CopyTo(header, 14);
        header[16] = 24;
        header[17] = 0x20;

        var body = new byte[width * height * 3];
        var bytes = new byte[header.Length + body.Length];
        header.CopyTo(bytes, 0);
        body.CopyTo(bytes, header.Length);
        return bytes;
    }

    /// <summary>Minimal uncompressed 24-bit RGB DDS payload (128-byte header, DDPF_RGB pixel format).</summary>
    private static byte[] BuildRgb24Dds(int width, int height)
    {
        byte[] bytes = new byte[128 + width * height * 3];
        "DDS "u8.CopyTo(bytes);
        BitConverter.GetBytes(124u).CopyTo(bytes, 4);
        BitConverter.GetBytes(0x1007u).CopyTo(bytes, 8);
        BitConverter.GetBytes((uint)height).CopyTo(bytes, 12);
        BitConverter.GetBytes((uint)width).CopyTo(bytes, 16);
        BitConverter.GetBytes((uint)(width * 3)).CopyTo(bytes, 20);
        BitConverter.GetBytes(32u).CopyTo(bytes, 76);
        BitConverter.GetBytes(0x40u).CopyTo(bytes, 80);
        BitConverter.GetBytes(24u).CopyTo(bytes, 88);
        BitConverter.GetBytes(0x00FF0000u).CopyTo(bytes, 92);
        BitConverter.GetBytes(0x0000FF00u).CopyTo(bytes, 96);
        BitConverter.GetBytes(0x000000FFu).CopyTo(bytes, 100);
        BitConverter.GetBytes(0x1000u).CopyTo(bytes, 108);
        return bytes;
    }

    private static byte[] BuildPlt(int width, int height)
    {
        int pixelCount = width * height;
        var bytes = new byte[24 + pixelCount * 2];
        "PLT "u8.CopyTo(bytes);
        "V1  "u8.CopyTo(bytes.AsSpan(4));
        BitConverter.GetBytes((uint)width).CopyTo(bytes, 16);
        BitConverter.GetBytes((uint)height).CopyTo(bytes, 20);
        for (int i = 0; i < pixelCount; i++)
        {
            bytes[24 + i * 2] = 128; // intensity
            bytes[24 + i * 2 + 1] = 0; // layer: Skin
        }

        return bytes;
    }
}
