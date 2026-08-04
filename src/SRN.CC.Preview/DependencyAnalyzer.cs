using System.Text;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Preview.Render;
using SWLOR.NWN.Formats;
using SWLOR.NWN.Formats.Mdl;

namespace SRN.CC.Preview;

/// <summary>
/// Analyzes asset payloads and extracts direct (non-transitive) dependencies.
/// </summary>
/// <remarks>
/// Implementations analyze specific family types (MDL, MTR, TXI, SET, WOK/PWK/DWK) and extract
/// asset references without recursive resolution. The caller is responsible for transitive closure.
/// </remarks>
public sealed class DependencyAnalyzer : IDependencyAnalyzer
{
    // Aurora resource type IDs relevant to companion-file dispatch below. These must match
    // SWLOR.NWN.Formats.Common.ResourceTypes exactly: 2016=wok, 2022=txi, 2029=dlg, 2030=itp,
    // 2052=dwk, 2053=pwk. A prior version of this dispatch used 2029/2030 (dlg/itp) where it
    // meant 2052/2053 (dwk/pwk), which misrouted .dlg/.itp into companion extraction while real
    // .dwk/.pwk walkmesh companions fell through as unsupported.
    private const ushort MdlResourceType = 2002;
    private const ushort MtrResourceType = 2072;
    private const ushort SetResourceType = 2013;
    private const ushort TxiResourceType = 2022;
    private const ushort WokResourceType = 2016;
    private const ushort DwkResourceType = 2052;
    private const ushort PwkResourceType = 2053;

    private readonly IResourceTypeRegistry _registry;

    public DependencyAnalyzer(IResourceTypeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// True for TXI (2022): a texture-info sidecar sharing a resref with a texture image, per
    /// <c>SWLOR.NWN.Formats.Common.ResourceTypes</c>.
    /// </summary>
    public static bool IsTxiCompanionResourceType(ushort resourceType) => resourceType == TxiResourceType;

    /// <summary>
    /// True for WOK (2016), DWK (2052), PWK (2053): walkmesh companions sharing a resref with the
    /// owning tile, door, or placeable model, per <c>SWLOR.NWN.Formats.Common.ResourceTypes</c>.
    /// DLG (2029) and ITP (2030) are deliberately excluded here - see the type-ID note above.
    /// </summary>
    public static bool IsWalkmeshCompanionResourceType(ushort resourceType) =>
        resourceType == WokResourceType || resourceType == DwkResourceType || resourceType == PwkResourceType;

    public async Task<IReadOnlySet<AssetIdentity>> AnalyzeDependenciesAsync(
        AssetOccurrence occurrence,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new InvalidOperationException("Dependency analyzer requires a readable stream.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Read payload into memory for parsing
        var read = await PreviewStreamHelpers
            .ReadStreamBoundedAsync(stream, PreviewStreamHelpers.TextPreviewBudgetBytes, cancellationToken)
            .ConfigureAwait(false);

        if (read.Bytes.Length == 0)
        {
            return new HashSet<AssetIdentity>();
        }

        try
        {
            var identity = occurrence.Identity;
            var resourceType = identity.ResourceType;

            // Dispatch to family-specific extractor
            if (resourceType == MdlResourceType)
            {
                return ExtractMdlDependencies(read.Bytes);
            }
            else if (resourceType == MtrResourceType) // MTR (Material)
            {
                return ExtractMtrDependencies(read.Bytes);
            }
            else if (resourceType == SetResourceType) // SET
            {
                return ExtractSetDependencies(read.Bytes);
            }
            else if (IsTxiCompanionResourceType(resourceType))
            {
                // TXI (2022) - texture-info sidecar; resolves against a same-resref texture image.
                return ExtractTxiCompanionDependencies(read.Bytes);
            }
            else if (IsWalkmeshCompanionResourceType(resourceType))
            {
                // WOK (2016) / DWK (2052) / PWK (2053) - walkmesh companions; resolve against a
                // same-resref tile/door/placeable model. Deliberately distinct from the TXI branch
                // above since a walkmesh companion resolves against a model, not a texture.
                return ExtractWalkmeshCompanionDependencies(read.Bytes);
            }

            // Unsupported family type - return empty set per interface contract
            return new HashSet<AssetIdentity>();
        }
        catch
        {
            // Parse failures return empty set rather than blocking
            return new HashSet<AssetIdentity>();
        }
    }

    private IReadOnlySet<AssetIdentity> ExtractMdlDependencies(byte[] bytes)
    {
        var dependencies = new HashSet<AssetIdentity>();

        try
        {
            bool isAscii = IsLikelyAsciiFormat(bytes);
            MdlModel model = new MdlReader().Parse(bytes);

            // Extract supermodel reference
            if (!string.IsNullOrWhiteSpace(model.SuperModel))
            {
                if (_registry.TryGetType("mdl", out var mdlType))
                {
                    dependencies.Add(new AssetIdentity(model.SuperModel, mdlType));
                }
            }

            // Extract texture references from mesh nodes
            foreach (var mesh in model.GetMeshNodes())
            {
                // Bitmap (diffuse texture)
                if (!string.IsNullOrWhiteSpace(mesh.Bitmap))
                {
                    if (TryGetTextureType(mesh.Bitmap, out var textureType))
                    {
                        dependencies.Add(new AssetIdentity(mesh.Bitmap, textureType));
                    }
                }

                // Lightmap
                if (!string.IsNullOrWhiteSpace(mesh.Lightmap))
                {
                    if (TryGetTextureType(mesh.Lightmap, out var lightmapType))
                    {
                        dependencies.Add(new AssetIdentity(mesh.Lightmap, lightmapType));
                    }
                }
            }
        }
        catch (NwnFormatException)
        {
            // Malformed MDL - attempt fallback extraction
            ExtractMdlFallbackDependencies(bytes, dependencies);
        }

        return dependencies;
    }

    private void ExtractMdlFallbackDependencies(byte[] bytes, HashSet<AssetIdentity> dependencies)
    {
        try
        {
            if (bytes.Length < 200 || !IsLikelyAsciiFormat(bytes))
            {
                return;
            }

            string text = Encoding.UTF8.GetString(bytes, 0, Math.Min(4096, bytes.Length));

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();

                // Extract supermodel from "setsupermodel" lines
                if (trimmed.StartsWith("setsupermodel", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && _registry.TryGetType("mdl", out var mdlType))
                    {
                        try
                        {
                            dependencies.Add(new AssetIdentity(parts[1], mdlType));
                        }
                        catch
                        {
                            // Invalid resref - skip
                        }
                    }
                }

                // Extract textures from "bitmap" lines
                if (trimmed.StartsWith("bitmap", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && TryGetTextureType(parts[1], out var textureType))
                    {
                        try
                        {
                            dependencies.Add(new AssetIdentity(parts[1], textureType));
                        }
                        catch
                        {
                            // Invalid resref - skip
                        }
                    }
                }

                // Extract lightmaps from "lightmap" lines
                if (trimmed.StartsWith("lightmap", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && TryGetTextureType(parts[1], out var textureType))
                    {
                        try
                        {
                            dependencies.Add(new AssetIdentity(parts[1], textureType));
                        }
                        catch
                        {
                            // Invalid resref - skip
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback failed - already populated set with what we found
        }
    }

    private IReadOnlySet<AssetIdentity> ExtractMtrDependencies(byte[] bytes)
    {
        var dependencies = new HashSet<AssetIdentity>();

        try
        {
            // Route through the shared MtrDocument parser (architecture decision A8) so this
            // analyzer and the future texture-resolution pipeline can never disagree about MTR
            // contents. MtrDocument.Parse never throws; malformed input yields a mostly-empty
            // document plus diagnostics, which simply means no dependencies get extracted here.
            var document = MtrDocument.Parse(bytes);

            AddMtrTextureDependency(dependencies, document.Texture0);
            AddMtrTextureDependency(dependencies, document.Texture1);
            AddMtrTextureDependency(dependencies, document.Texture2);
            AddMtrTextureDependency(dependencies, document.Texture3);
            AddMtrTextureDependency(dependencies, document.BumpMap);
            AddMtrTextureDependency(dependencies, document.EnvironmentMap);
        }
        catch
        {
            // Parse failure - return empty set
        }

        return dependencies;
    }

    private void AddMtrTextureDependency(HashSet<AssetIdentity> dependencies, string? textureName)
    {
        if (string.IsNullOrWhiteSpace(textureName))
        {
            return;
        }

        // Strip extension if present
        string textureResref = StripExtension(textureName);
        if (!TryGetTextureType(textureName, out var textureType))
        {
            return;
        }

        try
        {
            dependencies.Add(new AssetIdentity(textureResref, textureType));
        }
        catch
        {
            // Invalid resref - skip
        }
    }

    private IReadOnlySet<AssetIdentity> ExtractSetDependencies(byte[] bytes)
    {
        var dependencies = new HashSet<AssetIdentity>();

        try
        {
            // SET files are tokenized text files with model references
            string text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//"))
                {
                    continue;
                }

                // Tokenize line (SET files use whitespace-separated values)
                var tokens = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                // Look for model references (first token is often a command/type)
                for (int i = 0; i < tokens.Length; i++)
                {
                    string token = tokens[i];

                    // Heuristic: if token looks like a resref (alphanumeric underscore, 1-16 chars)
                    // and it's not a numeric value or known keyword, treat it as potential model reference
                    if (IsLikelyAssetReference(token) && !IsNumericOrKeyword(token))
                    {
                        if (_registry.TryGetType("mdl", out var mdlType))
                        {
                            // Check if this might be a model reference
                            if (token.Length <= 16)
                            {
                                try
                                {
                                    dependencies.Add(new AssetIdentity(token, mdlType));
                                }
                                catch
                                {
                                    // Invalid resref - skip
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Parse failure - return empty set
        }

        return dependencies;
    }

    private static IReadOnlySet<AssetIdentity> ExtractTxiCompanionDependencies(byte[] bytes)
    {
        // TXI (2022) is a texture-info sidecar: envmaptexture/blending/isbumpmap hints for the
        // same-resref texture image. It has no dependencies of its own here; the same-resref
        // relationship to its texture is a traversal-layer concern (per architecture decision A7),
        // not a direct-extraction concern.
        return new HashSet<AssetIdentity>();
    }

    private static IReadOnlySet<AssetIdentity> ExtractWalkmeshCompanionDependencies(byte[] bytes)
    {
        // WOK/DWK/PWK walkmesh companions carry per-face surface/collision data for the
        // same-resref tile, door, or placeable model. Like TXI, they have no dependencies of
        // their own here; the same-resref relationship to the owning model is resolved by the
        // traversal layer, not by this direct extractor.
        return new HashSet<AssetIdentity>();
    }

    private static bool IsLikelyAsciiFormat(byte[] bytes) =>
        bytes.Length >= 3 && bytes.AsSpan(0, 3).IndexOf((byte)0) == -1;

    private bool TryGetTextureType(string textureName, out ushort textureType)
    {
        textureType = 0;

        // Try to infer type from extension
        if (textureName.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
        {
            return _registry.TryGetType("tga", out textureType);
        }
        else if (textureName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return _registry.TryGetType("dds", out textureType);
        }
        else if (textureName.EndsWith(".plt", StringComparison.OrdinalIgnoreCase))
        {
            return _registry.TryGetType("plt", out textureType);
        }

        // Default to TGA if no extension given
        return _registry.TryGetType("tga", out textureType);
    }

    private static bool IsLikelyAssetReference(string token)
    {
        // Asset references are alphanumeric + underscore, typically 1-16 chars
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16)
        {
            return false;
        }

        foreach (char c in token)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNumericOrKeyword(string token)
    {
        // Known keywords that shouldn't be treated as asset references
        var keywords = new[]
        {
            "model", "node", "position", "orientation", "scale", "render", "shadow",
            "bitmap", "lightmap", "texture", "emitter", "animation", "loop", "random",
            "name", "type", "endnode", "newanim", "endanim", "node", "bone", "helper"
        };

        if (keywords.Contains(token, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if it's purely numeric
        return double.TryParse(token, out _) || int.TryParse(token, out _);
    }

    private static string StripExtension(string filename)
    {
        int lastDot = filename.LastIndexOf('.');
        if (lastDot > 0 && lastDot < filename.Length - 1)
        {
            return filename.Substring(0, lastDot);
        }
        return filename;
    }
}
