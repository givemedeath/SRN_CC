using System.Security.Cryptography;
using SRN.CC.Core.Build;
using SRN.CC.Core.Resolution;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;
using SRN.CC.Core.Workspace;
using SRN.CC.Formats.Hak;

namespace SRN.CC.Infrastructure.Build;

public sealed class BuildOrchestrator : IBuildOrchestrator
{
    private readonly IAssetPacker _packer;
    private readonly IBuildVerifier _verifier;
    private readonly ProvenanceManifestGenerator _manifestGenerator;
    private readonly IArtifactPublisher _publisher;
    private readonly IResourceTypeRegistry _registry;
    private readonly ISourceReaderDispatcher _dispatcher;

    public BuildOrchestrator(
        IAssetPacker packer,
        IBuildVerifier verifier,
        ProvenanceManifestGenerator manifestGenerator,
        IArtifactPublisher publisher,
        IResourceTypeRegistry registry,
        ISourceReaderDispatcher dispatcher)
    {
        _packer = packer ?? throw new ArgumentNullException(nameof(packer));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _manifestGenerator = manifestGenerator ?? throw new ArgumentNullException(nameof(manifestGenerator));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task<PublicationResult> ExecuteBuildAsync(
        WorkspaceState workspace,
        string destinationHakPath,
        IProgress<(string message, double progressFraction)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationHakPath);

        progress?.Report(("Starting build preflight checks...", 0.05));

        // Preflight 1: Output path overlap with source files
        string destNorm = Path.GetFullPath(destinationHakPath);
        foreach (var source in workspace.Sources)
        {
            string srcNorm = Path.GetFullPath(source.FullPath);
            if (string.Equals(destNorm, srcNorm, StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"Output path '{destinationHakPath}' overlaps directly with source path '{source.FullPath}'.");
            }
            if (source.Kind == AssetSourceKind.Folder && IsPathInsideDirectory(destNorm, srcNorm))
            {
                return Fail($"Output path '{destinationHakPath}' is located inside source folder '{source.FullPath}'.");
            }
        }

        // Preflight 2: Selected assets validation
        var selectedAssets = workspace.CuratedAssets.Where(a => a.IsSelected).ToList();
        if (selectedAssets.Count == 0)
        {
            return Fail("No assets are selected for building.");
        }

        List<BuildItem> buildItems = new(selectedAssets.Count);
        var sourceMap = workspace.Sources.ToDictionary(s => s.Id);

        foreach (var asset in selectedAssets)
        {
            if (asset.Status != ResolutionStatus.Resolved)
            {
                return Fail($"Selected asset '{asset.Identity}' is not resolved (status: {asset.Status}). Clear invalid pins or resolve conflicts before building.");
            }

            if (asset.ResolvedOccurrence == null)
            {
                return Fail($"Selected asset '{asset.Identity}' has no resolved occurrence.");
            }

            var occ = asset.ResolvedOccurrence;
            if (!sourceMap.TryGetValue(occ.SourceId, out var src) || !src.IsAvailable)
            {
                return Fail($"Source for asset '{asset.Identity}' is unavailable.");
            }

            string? sha256Hex = occ.Sha256 != null ? Convert.ToHexString(occ.Sha256).ToLowerInvariant() : null;
            if (string.IsNullOrEmpty(sha256Hex))
            {
                await using var payloadStream = await _dispatcher.OpenOccurrenceAsync(src, occ, cancellationToken).ConfigureAwait(false);
                using var sha = SHA256.Create();
                byte[] hashBytes = await sha.ComputeHashAsync(payloadStream, cancellationToken).ConfigureAwait(false);
                sha256Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            buildItems.Add(new BuildItem(
                Identity: asset.Identity,
                SourceId: occ.SourceId,
                Locator: occ.Locator,
                ExpectedSizeBytes: occ.Size,
                ExpectedSha256Hex: sha256Hex,
                IsPinned: asset.Pin != null
            ));
        }

        // Preflight 3: Total estimated payload size < 2 GiB limit
        long totalPayloadSize = buildItems.Sum(i => i.ExpectedSizeBytes);
        if (totalPayloadSize >= HakWriter.LegacySingleHakLimit)
        {
            return Fail($"Total estimated payload size ({totalPayloadSize:N0} bytes) meets or exceeds the single-HAK limit ({HakWriter.LegacySingleHakLimit:N0} bytes).");
        }

        string destDir = Path.GetDirectoryName(destNorm)!;
        if (string.IsNullOrEmpty(destDir)) destDir = ".";
        Directory.CreateDirectory(destDir);

        string manifestName = Path.GetFileNameWithoutExtension(destNorm) + ".srncc-manifest.json";
        string destinationManifestPath = Path.Combine(destDir, manifestName);

        string tempHakPath = Path.Combine(destDir, $"{Guid.NewGuid():N}.tmp.hak");
        string tempManifestPath = Path.Combine(destDir, $"{Guid.NewGuid():N}.tmp.manifest.json");

        var plan = new BuildPlan
        {
            DestinationHakPath = destNorm,
            DestinationManifestPath = destinationManifestPath,
            FrozenSources = workspace.Sources,
            Items = buildItems,
            CreatedUtc = DateTime.UtcNow
        };

        try
        {
            // Step 1: Pack HAK
            progress?.Report(("Packing HAK payload...", 0.20));
            var artifact = await _packer.PackAsync(plan, tempHakPath, tempManifestPath, null, cancellationToken).ConfigureAwait(false);

            // Step 2: Verify HAK
            progress?.Report(("Verifying built HAK...", 0.60));
            var verification = await _verifier.VerifyAsync(plan, tempHakPath, cancellationToken).ConfigureAwait(false);
            if (!verification.IsSuccess)
            {
                string errStr = string.Join("; ", verification.Errors);
                return Fail($"HAK build verification failed: {errStr}");
            }

            // Step 3: Generate Provenance Manifest
            progress?.Report(("Generating provenance manifest...", 0.80));
            await _manifestGenerator.GenerateManifestAsync(plan, tempHakPath, tempManifestPath, verification.ComputedHakSha256Hex, cancellationToken).ConfigureAwait(false);

            // Step 4: Transactional Publication
            progress?.Report(("Publishing artifacts...", 0.90));
            var pubResult = await _publisher.PublishAsync(plan, tempHakPath, tempManifestPath, cancellationToken).ConfigureAwait(false);

            progress?.Report(("Build complete!", 1.0));
            return pubResult;
        }
        finally
        {
            if (File.Exists(tempHakPath)) { try { File.Delete(tempHakPath); } catch { } }
            if (File.Exists(tempManifestPath)) { try { File.Delete(tempManifestPath); } catch { } }
        }
    }

    private static PublicationResult Fail(string message) =>
        new PublicationResult(false, null, null, message, new[] { message });

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        string directoryWithSeparator = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return path.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
