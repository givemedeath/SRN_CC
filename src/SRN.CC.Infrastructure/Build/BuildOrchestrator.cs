using System.Security.Cryptography;
using SRN.CC.Core.Build;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Logging;
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
    private readonly IAppLogger _logger;

    /// <param name="logger">
    /// Optional and last so every existing call site keeps compiling unchanged; defaults to
    /// <see cref="NullAppLogger.Instance"/>.
    /// </param>
    public BuildOrchestrator(
        IAssetPacker packer,
        IBuildVerifier verifier,
        ProvenanceManifestGenerator manifestGenerator,
        IArtifactPublisher publisher,
        IResourceTypeRegistry registry,
        ISourceReaderDispatcher dispatcher,
        IAppLogger? logger = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
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

            // Defensive guard: the resolver never produces a winner from a Hidden source, so a
            // Hidden winning source here means the plan was built from stale state. Fail rather
            // than silently packaging content the user has switched off.
            if (src.Mode == SourceMode.Hidden)
            {
                return Fail($"Source for asset '{asset.Identity}' is hidden and cannot contribute to a build. Rescan or re-resolve before building.");
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

        // Preflight 2b: source drift. Recompute the fingerprint of every source that actually
        // contributes a payload and compare it to the fingerprint recorded at its last scan. A
        // mismatch means the source changed on disk since it was indexed, so the frozen locators,
        // sizes, and hashes can no longer be trusted (PLAN.md:143). Scoped to contributing sources:
        // drift in a source that ships nothing cannot corrupt the output, and folder fingerprints
        // require a directory walk.
        foreach (Guid contributingSourceId in buildItems.Select(i => i.SourceId).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceMap.TryGetValue(contributingSourceId, out var contributingSource))
            {
                continue;
            }

            // No recorded baseline means there is nothing to compare against; skip rather than block.
            // A real workspace always records a fingerprint during indexing, so drift is still caught
            // in production — this only spares hand-built workspaces that never carried one.
            if (contributingSource.Fingerprint is null)
            {
                continue;
            }

            SourceFingerprint current;
            try
            {
                current = await _dispatcher.GetFingerprintAsync(contributingSource, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDrift(contributingSource.FullPath, ex);
                return Fail($"Source drift detected ({DiagnosticCode.SourceDriftDetected}): '{contributingSource.FullPath}' could not be re-read to confirm it is unchanged. Rescan sources and retry the build.");
            }

            if (!contributingSource.Fingerprint.Equals(current))
            {
                LogDrift(contributingSource.FullPath, exception: null);
                return Fail($"Source drift detected ({DiagnosticCode.SourceDriftDetected}): '{contributingSource.FullPath}' changed since its last scan. Rescan sources and retry the build.");
            }
        }

        // Preflight 3: Total estimated payload size < 2 GiB limit
        long totalPayloadSize = buildItems.Sum(i => i.ExpectedSizeBytes);
        long estimatedKeyListBytes = checked(buildItems.Count * 24L);
        long estimatedResourceListBytes = checked(buildItems.Count * 8L);
        long estimatedHeaderBytes = 160L;
        long estimatedHakSize = checked(estimatedHeaderBytes + estimatedKeyListBytes + estimatedResourceListBytes + totalPayloadSize);
        if (estimatedHakSize >= HakWriter.LegacySingleHakLimit)
        {
            return Fail(
                $"Estimated output HAK size ({estimatedHakSize:N0} bytes) with format overhead meets or exceeds the single-HAK limit ({HakWriter.LegacySingleHakLimit:N0} bytes).");
        }

        string destDir = Path.GetDirectoryName(destNorm)!;
        if (string.IsNullOrEmpty(destDir)) destDir = ".";
        Directory.CreateDirectory(destDir);

        string manifestName = Path.GetFileNameWithoutExtension(destNorm) + ".srncc-manifest.json";
        string destinationManifestPath = Path.Combine(destDir, manifestName);

        string tempHakPath = Path.Combine(destDir, $"{Guid.NewGuid():N}.tmp.hak");
        string tempManifestPath = Path.Combine(destDir, $"{Guid.NewGuid():N}.tmp.manifest.json");

        long estimatedManifestSize = Math.Max(1, checked(selectedAssets.Count * 512L));
        long estimatedExistingPayloadBytes = 0;
        if (File.Exists(destNorm)) { estimatedExistingPayloadBytes += new FileInfo(destNorm).Length; }
        if (File.Exists(destinationManifestPath)) { estimatedExistingPayloadBytes += new FileInfo(destinationManifestPath).Length; }

        long requiredDiskBytes = checked(estimatedHakSize + estimatedManifestSize + estimatedExistingPayloadBytes);
        if (!TryGetAvailableBytes(destDir, out var freeBytes, out var diskError))
        {
            return Fail(diskError);
        }

        if (requiredDiskBytes > freeBytes)
        {
            return Fail(
                $"Insufficient disk space for build artifacts. Destination volume has approximately {freeBytes:N0} bytes available, but estimated requirement is {requiredDiskBytes:N0} bytes.");
        }

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
            TryDeleteTempFile(tempHakPath);
            TryDeleteTempFile(tempManifestPath);
        }
    }

    /// <summary>
    /// Removes one build temp file, reporting rather than swallowing a failure.
    /// </summary>
    /// <remarks>
    /// Deletion must never displace the outcome of the build itself — a successful publication that
    /// leaked a temp file is still a successful publication — so this cannot rethrow. It logs
    /// instead: an undeleted temp file left the destination directory dirty, and before this the
    /// only evidence was the file.
    /// </remarks>
    private void TryDeleteTempFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            // Deliberately as broad as the bare `catch { }` this replaced: narrowing it here would be
            // a behaviour change smuggled in behind a logging change.
            _logger.Log(
                LogLevel.Warn,
                nameof(BuildOrchestrator),
                $"Failed to delete build temp file '{path}'.",
                ex);
        }
    }

    private void LogDrift(string sourcePath, Exception? exception)
    {
        _logger.Log(
            LogLevel.Error,
            nameof(BuildOrchestrator),
            $"Source drift detected for '{sourcePath}'.",
            exception,
            new Dictionary<string, string> { ["code"] = nameof(DiagnosticCode.SourceDriftDetected) });
    }

    private static PublicationResult Fail(string message) =>
        new PublicationResult(false, null, null, message, new[] { message });

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        string directoryWithSeparator = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return path.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetAvailableBytes(string directory, out long availableBytes, out string error)
    {
        availableBytes = 0;
        error = string.Empty;

        try
        {
            string normalizedDirectory = Path.GetFullPath(directory);
            var drive = DriveInfo.GetDrives()
                .Where(d =>
                    d.IsReady &&
                    IsPathWithinRoot(normalizedDirectory, d.RootDirectory.FullName))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive is null)
            {
                error = $"Could not locate destination volume for '{directory}'.";
                return false;
            }

            availableBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to check destination free space: {ex.Message}";
            return false;
        }
    }

    private static bool IsPathWithinRoot(string fullPath, string rootPath)
    {
        string normalizedPath = Path.GetFullPath(fullPath);
        string normalizedRoot = Path.GetFullPath(rootPath);

        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(normalizedRoot, Path.GetPathRoot(normalizedPath), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.Length > normalizedRoot.Length &&
            (normalizedPath[normalizedRoot.Length] == Path.DirectorySeparatorChar || normalizedPath[normalizedRoot.Length] == Path.AltDirectorySeparatorChar);
    }
}
