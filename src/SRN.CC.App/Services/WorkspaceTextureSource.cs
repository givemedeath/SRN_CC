using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;

namespace SRN.CC.App.Services;

/// <summary>
/// Implements <see cref="ITextureSource"/> over this app's workspace/source-reading
/// infrastructure: curated <see cref="WorkspaceState"/> first, then an optional base-game KEY/BIF
/// catalog (per architecture decision A7). Mirrors <see cref="DependencyLocator"/>'s
/// workspace-first-then-catalog-fallback shape.
/// </summary>
/// <remarks>
/// <see cref="ITextureSource.OpenTextureAsync"/>'s <c>resref</c> is expected to carry an explicit
/// extension appended by <c>TextureResolver</c> (e.g. <c>"c_rat.dds"</c>, <c>"c_rat.mtr"</c>) - see
/// the resref convention documented on <c>SRN.CC.Preview.Render.TextureResolver</c>. This class
/// strips that extension, maps it to a resource type via <see cref="IResourceTypeRegistry"/>, and
/// only then builds the <see cref="AssetIdentity"/> used to look the resource up - <c>kind</c> is
/// carried through only as classification metadata, never used to derive the resource type. Never
/// throws: any failure to resolve, open, or classify a resref yields null (cancellation is the sole
/// exception permitted to propagate, matching <see cref="ITextureSource"/>'s contract).
/// </remarks>
public sealed class WorkspaceTextureSource : ITextureSource
{
    private readonly WorkspaceState _workspaceState;
    private readonly ISourceReaderDispatcher _readerDispatcher;
    private readonly IResourceTypeRegistry _typeRegistry;
    private readonly IBaseGameResourceCatalog? _baseGameCatalog;
    private readonly Dictionary<AssetIdentity, AssetOccurrence> _occurrenceCache;

    public WorkspaceTextureSource(
        WorkspaceState workspaceState,
        ISourceReaderDispatcher readerDispatcher,
        IResourceTypeRegistry typeRegistry,
        IBaseGameResourceCatalog? baseGameCatalog = null)
    {
        _workspaceState = workspaceState ?? throw new ArgumentNullException(nameof(workspaceState));
        _readerDispatcher = readerDispatcher ?? throw new ArgumentNullException(nameof(readerDispatcher));
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _baseGameCatalog = baseGameCatalog;

        // Build a fast identity -> occurrence lookup once, mirroring DependencyLocator's cache.
        _occurrenceCache = new Dictionary<AssetIdentity, AssetOccurrence>();
        foreach (CuratedAssetLike curated in EnumerateCuratedAssets(_workspaceState))
        {
            if (curated.Occurrence != null)
            {
                _occurrenceCache.TryAdd(curated.Identity, curated.Occurrence);
            }
        }
    }

    public async Task<TextureLookupResult?> OpenTextureAsync(
        string resref, TextureKind kind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resref))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveIdentity(resref, out AssetIdentity? identity, out ushort resourceType) || identity == null)
        {
            return null;
        }

        // Workspace first.
        if (_occurrenceCache.TryGetValue(identity, out AssetOccurrence? occurrence))
        {
            try
            {
                AssetSource? source = _workspaceState.Sources.FirstOrDefault(s => s.Id == occurrence.SourceId);
                if (source != null)
                {
                    Stream stream = await _readerDispatcher
                        .OpenOccurrenceAsync(source, occurrence, cancellationToken)
                        .ConfigureAwait(false);
                    return new TextureLookupResult(identity, resourceType, stream, TextureOrigin.Workspace);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall through to base-game; a broken workspace occurrence should not hide a
                // valid base-game texture.
            }
        }

        // Base-game KEY/BIF fallback, when a catalog is loaded (it may not be - see A7).
        if (_baseGameCatalog != null)
        {
            try
            {
                if (_baseGameCatalog.Contains(identity))
                {
                    Stream stream = await _baseGameCatalog.OpenAsync(identity, cancellationToken).ConfigureAwait(false);
                    return new TextureLookupResult(identity, resourceType, stream, TextureOrigin.BaseGame);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private bool TryResolveIdentity(string resref, out AssetIdentity? identity, out ushort resourceType)
    {
        identity = null;
        resourceType = 0;

        string extension = Path.GetExtension(resref).TrimStart('.');
        if (extension.Length == 0)
        {
            // TextureResolver always appends an extension; without one there is nothing to map
            // to a resource type, so this resref cannot be classified.
            return false;
        }

        if (!_typeRegistry.TryGetType(extension, out resourceType))
        {
            return false;
        }

        string bareName = resref[..^(extension.Length + 1)];
        if (bareName.Length == 0)
        {
            return false;
        }

        try
        {
            identity = new AssetIdentity(bareName, resourceType);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IEnumerable<CuratedAssetLike> EnumerateCuratedAssets(WorkspaceState workspaceState)
    {
        foreach (var curated in workspaceState.CuratedAssets)
        {
            AssetOccurrence? occurrence = curated.ResolvedOccurrence ?? curated.AllOccurrences.FirstOrDefault();
            yield return new CuratedAssetLike(curated.Identity, occurrence);
        }
    }

    private readonly record struct CuratedAssetLike(AssetIdentity Identity, AssetOccurrence? Occurrence);
}
