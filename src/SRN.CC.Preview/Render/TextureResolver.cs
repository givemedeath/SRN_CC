using SRN.CC.Core.Services;

namespace SRN.CC.Preview.Render;

/// <summary>
/// Implements the texture resolution ladder from architecture decision A7: for a texture name,
/// try (in order) an overriding <c>&lt;name&gt;.mtr</c> material (resource type 2072, whose
/// <c>Texture0</c>/<c>EnvironmentMap</c> replace the base name/report an environment map when
/// present), then <c>&lt;name&gt;.dds</c>, then <c>&lt;name&gt;.tga</c>, then <c>&lt;name&gt;.plt</c>
/// (first hit wins), then a <c>&lt;name&gt;.txi</c> (2022) sidecar for best-effort
/// <c>envmaptexture</c> discovery. Decoding is delegated to <see cref="TextureDecoder"/> so this
/// class never duplicates a codec path.
/// </summary>
/// <remarks>
/// Sees only <see cref="ITextureSource"/> (per A7) — never <c>WorkspaceState</c> or any App-layer
/// type — so it is testable with a plain in-memory fake and no application wiring.
/// <para>
/// <b>Resref convention:</b> <see cref="ITextureSource.OpenTextureAsync"/> is called with the
/// candidate extension appended to the resref (e.g. <c>"c_rat.dds"</c>, <c>"c_rat.mtr"</c>) so
/// that this resolver — not the source — owns and can be unit-tested for the ladder's precedence
/// (MTR override, then DDS &gt; TGA &gt; PLT). Implementations of <see cref="ITextureSource"/> are
/// expected to strip the extension back off before constructing an
/// <see cref="SRN.CC.Core.Identity.AssetIdentity"/> and to derive the resource type to look up from
/// that extension (see <c>SRN.CC.App.Services.WorkspaceTextureSource</c>).
/// </para>
/// <para>
/// <b>Environment maps:</b> per the milestone's explicitly-deferred scope, an environment map name
/// discovered via MTR or TXI is resolved only far enough to know whether it exists (a cheap
/// extension-ladder probe, no decode, no budget consumption) and is reported back as a name plus an
/// unresolved-diagnostic when absent. It is never returned as a shaded/decoded
/// <see cref="TextureImage"/> — callers must not use it for rendering.
/// </para>
/// <para>
/// <b>Budgets:</b> a single <see cref="TextureResolver"/> instance accumulates
/// <see cref="SceneBuildBudget.TextureBytes"/> and <see cref="SceneBuildBudget.MaxTextures"/> usage
/// across every <see cref="ResolveAsync"/> call made on it, so constructing one resolver per scene
/// build (as <c>MdlSceneBuilder</c> is expected to) enforces the budget scene-wide, not per-material.
/// Exceeding either budget degrades the offending texture to unresolved plus a diagnostic — it never
/// throws.
/// </para>
/// </remarks>
public sealed class TextureResolver
{
    private static readonly string[] BitmapExtensionLadder = { "dds", "tga", "plt" };
    private const int MaximumDiagnostics = 64;

    private readonly ITextureSource _textureSource;
    private int _texturesLoaded;
    private long _textureBytesUsed;

    public TextureResolver(ITextureSource textureSource)
    {
        _textureSource = textureSource ?? throw new ArgumentNullException(nameof(textureSource));
    }

    /// <summary>Total decoded texture bytes charged against <see cref="SceneBuildBudget.TextureBytes"/> so far.</summary>
    public long TextureBytesUsed => _textureBytesUsed;

    /// <summary>Total textures successfully decoded and charged against <see cref="SceneBuildBudget.MaxTextures"/> so far.</summary>
    public int TexturesLoaded => _texturesLoaded;

    /// <summary>
    /// Resolves <paramref name="textureName"/> through the ladder described on the type. Never
    /// throws: any failure (missing file, malformed payload, exhausted budget) is reported through
    /// <see cref="TextureResolution.UnresolvedTextures"/>/<see cref="TextureResolution.Diagnostics"/>
    /// instead, with <see cref="TextureResolution.Image"/> left null.
    /// </summary>
    public async Task<TextureResolution> ResolveAsync(
        string? textureName,
        TextureKind kind,
        SceneBuildBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var diagnostics = new List<string>();
        var unresolved = new List<string>();

        if (string.IsNullOrWhiteSpace(textureName))
        {
            return new TextureResolution(null, null, unresolved, diagnostics);
        }

        string baseName = StripExtension(textureName).ToLowerInvariant();
        string effectiveName = baseName;
        string? environmentMapName = null;

        MtrDocument? mtr = await TryReadMtrAsync(baseName, cancellationToken).ConfigureAwait(false);
        if (mtr != null)
        {
            foreach (string mtrDiagnostic in mtr.Diagnostics)
            {
                AddDiagnostic(diagnostics, $"MTR '{baseName}.mtr': {mtrDiagnostic}");
            }

            if (!string.IsNullOrWhiteSpace(mtr.Texture0))
            {
                effectiveName = StripExtension(mtr.Texture0).ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(mtr.EnvironmentMap))
            {
                environmentMapName = StripExtension(mtr.EnvironmentMap).ToLowerInvariant();
            }
        }

        TextureImage? image = await TryLoadBitmapAsync(effectiveName, kind, budget, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        if (image == null)
        {
            unresolved.Add(effectiveName);
        }

        // TXI is a best-effort sidecar: only consulted for an envmap hint when MTR didn't already
        // supply one. Flags (blending/isbumpmap) are discovered but not otherwise acted on here -
        // that classification is MdlSceneBuilder's job (slice S6b).
        if (environmentMapName == null)
        {
            string? txiEnvironmentMap = await TryReadTxiEnvironmentMapAsync(effectiveName, cancellationToken)
                .ConfigureAwait(false);
            if (txiEnvironmentMap != null)
            {
                environmentMapName = StripExtension(txiEnvironmentMap).ToLowerInvariant();
            }
        }

        if (environmentMapName != null)
        {
            bool envMapExists = await ProbeBitmapExistsAsync(environmentMapName, cancellationToken)
                .ConfigureAwait(false);
            if (!envMapExists)
            {
                unresolved.Add(environmentMapName);
                AddDiagnostic(
                    diagnostics,
                    $"Environment map '{environmentMapName}' referenced but not resolved " +
                    "(discover-and-report only; never shaded per architecture decision A7).");
            }
        }

        return new TextureResolution(image, environmentMapName, unresolved, diagnostics);
    }

    private async Task<MtrDocument?> TryReadMtrAsync(string baseName, CancellationToken cancellationToken)
    {
        TextureLookupResult? lookup = await _textureSource
            .OpenTextureAsync($"{baseName}.mtr", TextureKind.Material, cancellationToken)
            .ConfigureAwait(false);
        if (lookup == null)
        {
            return null;
        }

        try
        {
            var read = await PreviewStreamHelpers
                .ReadStreamBoundedAsync(lookup.Payload, PreviewStreamHelpers.TextPreviewBudgetBytes, cancellationToken)
                .ConfigureAwait(false);
            return MtrDocument.Parse(read.Bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            await DisposePayloadAsync(lookup.Payload).ConfigureAwait(false);
        }
    }

    private async Task<TextureImage?> TryLoadBitmapAsync(
        string name,
        TextureKind kind,
        SceneBuildBudget budget,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (_texturesLoaded >= budget.MaxTextures)
        {
            AddDiagnostic(diagnostics, $"Texture '{name}' skipped: MaxTextures budget ({budget.MaxTextures}) reached.");
            return null;
        }

        long remainingBudget = budget.TextureBytes - _textureBytesUsed;
        if (remainingBudget <= 0)
        {
            AddDiagnostic(diagnostics, $"Texture '{name}' skipped: TextureBytes budget ({budget.TextureBytes:N0}) exhausted.");
            return null;
        }

        foreach (string extension in BitmapExtensionLadder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TextureLookupResult? lookup = await _textureSource
                .OpenTextureAsync($"{name}.{extension}", kind, cancellationToken)
                .ConfigureAwait(false);
            if (lookup == null)
            {
                continue;
            }

            byte[] rawBytes;
            try
            {
                var read = await PreviewStreamHelpers
                    .ReadStreamBoundedAsync(lookup.Payload, PreviewStreamHelpers.ImageInputBudgetBytes, cancellationToken)
                    .ConfigureAwait(false);
                rawBytes = read.Bytes;
            }
            finally
            {
                await DisposePayloadAsync(lookup.Payload).ConfigureAwait(false);
            }

            if (!TextureDecoder.TryDecodeToBgra(rawBytes, extension, out var decoded, out string? decodeError))
            {
                AddDiagnostic(diagnostics, $"Texture '{name}.{extension}' failed to decode: {decodeError}");
                continue;
            }

            if (decoded.Bgra.LongLength > remainingBudget)
            {
                AddDiagnostic(
                    diagnostics,
                    $"Texture '{name}.{extension}' ({decoded.Bgra.LongLength:N0} bytes) exceeds remaining " +
                    $"TextureBytes budget ({remainingBudget:N0}); left untextured.");
                return null;
            }

            _textureBytesUsed += decoded.Bgra.LongLength;
            _texturesLoaded++;

            return new TextureImage(
                name,
                decoded.Width,
                decoded.Height,
                decoded.Bgra,
                decoded.HasAlpha,
                lookup.Origin);
        }

        return null;
    }

    private async Task<string?> TryReadTxiEnvironmentMapAsync(string name, CancellationToken cancellationToken)
    {
        TextureLookupResult? lookup = await _textureSource
            .OpenTextureAsync($"{name}.txi", TextureKind.TextureInfo, cancellationToken)
            .ConfigureAwait(false);
        if (lookup == null)
        {
            return null;
        }

        try
        {
            var read = await PreviewStreamHelpers
                .ReadStreamBoundedAsync(lookup.Payload, PreviewStreamHelpers.TextPreviewBudgetBytes, cancellationToken)
                .ConfigureAwait(false);
            return ParseTxiEnvironmentMap(read.Bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            await DisposePayloadAsync(lookup.Payload).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Minimal, best-effort TXI scan for the <c>envmaptexture &lt;name&gt;</c> directive. TXI is a
    /// loosely-specified, whitespace-tokenized sidecar format; this deliberately does not attempt to
    /// fully parse <c>blending</c>/<c>isbumpmap</c> and similar flags (out of scope for S7), only the
    /// one directive needed to complete the environment-map discovery ladder. Never throws.
    /// </summary>
    private static string? ParseTxiEnvironmentMap(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        string text;
        try
        {
            text = PreviewStreamHelpers.DecodeTextPayload(bytes, out _);
        }
        catch
        {
            return null;
        }

        foreach (string rawLine in PreviewStreamHelpers.NormalizeLineEndings(text).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2 && string.Equals(tokens[0], "envmaptexture", StringComparison.OrdinalIgnoreCase))
            {
                return tokens[1];
            }
        }

        return null;
    }

    private async Task<bool> ProbeBitmapExistsAsync(string name, CancellationToken cancellationToken)
    {
        foreach (string extension in BitmapExtensionLadder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TextureLookupResult? lookup = await _textureSource
                .OpenTextureAsync($"{name}.{extension}", TextureKind.EnvironmentMap, cancellationToken)
                .ConfigureAwait(false);
            if (lookup == null)
            {
                continue;
            }

            await DisposePayloadAsync(lookup.Payload).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static async ValueTask DisposePayloadAsync(Stream payload)
    {
        try
        {
            await payload.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Disposal failures must never surface as a resolution failure.
        }
    }

    private static string StripExtension(string name)
    {
        int lastDot = name.LastIndexOf('.');
        return lastDot > 0 && lastDot < name.Length - 1 ? name[..lastDot] : name;
    }

    private static void AddDiagnostic(List<string> diagnostics, string message)
    {
        if (diagnostics.Count < MaximumDiagnostics)
        {
            diagnostics.Add(message);
        }
    }
}

/// <summary>
/// Result of resolving one logical texture slot: the decoded, GPU-ready image when found (never a
/// shaded environment map — see the type-level remarks on <see cref="TextureResolver"/>), the
/// discovered environment map name (report-only), and diagnostics/unresolved names for materials
/// that end up untextured. Designed to be trivially foldable into a
/// <see cref="RenderMaterial"/> by <c>MdlSceneBuilder</c>/<c>MdlPreviewProvider</c> (slice S9).
/// </summary>
public sealed record TextureResolution(
    TextureImage? Image,
    string? EnvironmentMapName,
    IReadOnlyList<string> UnresolvedTextures,
    IReadOnlyList<string> Diagnostics);
