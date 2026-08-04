using System.Text;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
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
    private readonly IResourceTypeRegistry _registry;

    public DependencyAnalyzer(IResourceTypeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

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
            if (resourceType == 2002) // MDL
            {
                return ExtractMdlDependencies(read.Bytes);
            }
            else if (resourceType == 2072) // MTR (Material)
            {
                return ExtractMtrDependencies(read.Bytes);
            }
            else if (resourceType == 2013) // SET
            {
                return ExtractSetDependencies(read.Bytes);
            }
            else if (resourceType == 2022 || resourceType == 2016 || resourceType == 2029 || resourceType == 2030)
            {
                // TXI (2022), WOK (2016), PWK (2029), DWK (2030) - extract companion references
                return ExtractCompanionDependencies(occurrence.Identity.Resref, read.Bytes);
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
            // MTR files are text-based key=value pairs
            string text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("texture0", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("texture1", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string textureName = parts[1].Trim();
                        if (!string.IsNullOrWhiteSpace(textureName))
                        {
                            // Strip extension if present
                            string textureResref = StripExtension(textureName);
                            if (TryGetTextureType(textureName, out var textureType))
                            {
                                dependencies.Add(new AssetIdentity(textureResref, textureType));
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

    private IReadOnlySet<AssetIdentity> ExtractCompanionDependencies(string resref, byte[] bytes)
    {
        var dependencies = new HashSet<AssetIdentity>();

        // Companion files (TXI, WOK, PWK, DWK) don't have dependencies within themselves
        // but they have shared-resref relationships defined in the PLAN
        // These are handled by the traversal layer, not direct extraction

        return dependencies;
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
