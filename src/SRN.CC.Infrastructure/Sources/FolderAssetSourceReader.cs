using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Records;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;

namespace SRN.CC.Infrastructure.Sources;

public sealed class FolderAssetSourceReader : IAssetSourceReader
{
    private const int AlgorithmVersion = 1;
    private readonly IResourceTypeRegistry _typeRegistry;

    public FolderAssetSourceReader(IResourceTypeRegistry typeRegistry)
    {
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
    }

    public Task<SourceFingerprint> GetFingerprintAsync(AssetSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != AssetSourceKind.Folder)
        {
            throw new ArgumentException($"FolderAssetSourceReader cannot index source kind '{source.Kind}'.", nameof(source));
        }

        cancellationToken.ThrowIfCancellationRequested();

        DirectoryInfo rootDir = new(source.FullPath);
        if (!rootDir.Exists)
        {
            throw new DirectoryNotFoundException($"Folder source root not found at '{source.FullPath}'.");
        }

        List<FolderFileItem> files = EnumerateRegularFiles(rootDir, cancellationToken);
        files.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));

        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] metaHeader = new byte[8];
        BitConverter.TryWriteBytes(metaHeader.AsSpan(0, 4), (int)AssetSourceKind.Folder);
        BitConverter.TryWriteBytes(metaHeader.AsSpan(4, 4), AlgorithmVersion);
        hasher.AppendData(metaHeader);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] relPathBytes = Encoding.UTF8.GetBytes(file.RelativePath);
            byte[] lenBytes = BitConverter.GetBytes(relPathBytes.Length);
            hasher.AppendData(lenBytes);
            hasher.AppendData(relPathBytes);

            string ext = Path.GetExtension(file.RelativePath);
            bool isMapped = _typeRegistry.TryGetType(ext, out ushort typeId);

            byte[] recordHeader = new byte[19];
            recordHeader[0] = (byte)(isMapped ? 1 : 0);
            BitConverter.TryWriteBytes(recordHeader.AsSpan(1, 2), typeId);
            BitConverter.TryWriteBytes(recordHeader.AsSpan(3, 8), file.Length);
            BitConverter.TryWriteBytes(recordHeader.AsSpan(11, 8), file.LastWriteUtcTicks);
            hasher.AppendData(recordHeader);
        }

        byte[] digest = hasher.GetHashAndReset();
        return Task.FromResult(new SourceFingerprint(AssetSourceKind.Folder, AlgorithmVersion, digest));
    }

    public async Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Stopwatch sw = Stopwatch.StartNew();

        DirectoryInfo rootDir = new(source.FullPath);
        if (!rootDir.Exists)
        {
            AssetDiagnosticRecord diag = new(DiagnosticCode.RootInaccessible, $"Folder root not found at '{source.FullPath}'.", targetPath: source.FullPath);
            return CreateUnavailableSnapshot(source, diag, sw.Elapsed);
        }

        SourceIndexSnapshot? snapshot = await ScanFolderOnceAsync(source, rootDir, progress, cancellationToken).ConfigureAwait(false);
        if (snapshot == null)
        {
            // Retry scan once on detected drift
            progress?.Report(new IndexProgress(source.Id, IndexPhase.Scanning, 0, null, "Retrying folder scan after drift..."));
            snapshot = await ScanFolderOnceAsync(source, rootDir, progress, cancellationToken).ConfigureAwait(false);
        }

        if (snapshot == null)
        {
            AssetDiagnosticRecord diag = new(DiagnosticCode.ChangedSourceFailure, "Folder contents changed repeatedly during indexing scan.", targetPath: source.FullPath);
            return CreateUnavailableSnapshot(source, diag, sw.Elapsed);
        }

        sw.Stop();
        return snapshot;
    }

    private async Task<SourceIndexSnapshot?> ScanFolderOnceAsync(
        AssetSource source,
        DirectoryInfo rootDir,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SourceFingerprint fingerprint;
        try
        {
            fingerprint = await GetFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AssetDiagnosticRecord diag = new(DiagnosticCode.RootInaccessible, $"Inaccessible folder root: {ex.Message}", targetPath: source.FullPath);
            return CreateUnavailableSnapshot(source, diag, sw.Elapsed);
        }

        List<FolderFileItem> files;
        try
        {
            files = EnumerateRegularFiles(rootDir, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AssetDiagnosticRecord diag = new(DiagnosticCode.RootInaccessible, $"Inaccessible folder root: {ex.Message}", targetPath: source.FullPath);
            return CreateUnavailableSnapshot(source, diag, sw.Elapsed);
        }
        files.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));

        List<IndexedAssetRecord> records = new();
        List<AssetDiagnosticRecord> diagnostics = new();
        HashSet<AssetIdentity> seenIdentities = new();

        string rootPrefix = Path.GetFullPath(source.FullPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        long totalBytes = 0;

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            totalBytes += file.Length;

            progress?.Report(new IndexProgress(source.Id, IndexPhase.Scanning, i + 1, files.Count, file.RelativePath));

            string fullPath = Path.GetFullPath(file.FullPath);
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                AssetDiagnosticRecord diag = new(DiagnosticCode.FileReadError, $"File '{file.RelativePath}' escapes source root boundary.", targetPath: file.RelativePath);
                records.Add(new IndexedAssetRecord(diag));
                diagnostics.Add(diag);
                continue;
            }

            string filename = Path.GetFileNameWithoutExtension(file.RelativePath);
            string ext = Path.GetExtension(file.RelativePath);

            if (_typeRegistry.TryGetType(ext, out ushort typeId))
            {
                AssetIdentity identity;
                try
                {
                    identity = new AssetIdentity(filename, typeId);
                }
                catch (Exception ex)
                {
                    AssetDiagnosticRecord diag = new(DiagnosticCode.InvalidResref, $"Invalid folder resref for '{file.RelativePath}': {ex.Message}", targetPath: file.RelativePath);
                    records.Add(new IndexedAssetRecord(diag));
                    diagnostics.Add(diag);
                    continue;
                }

                ValidationState vState = ValidationState.Valid;
                if (!seenIdentities.Add(identity))
                {
                    vState = ValidationState.DuplicateIdentity;
                }

                FolderFileLocator locator = new(file.RelativePath);
                AssetOccurrence occurrence = new(
                    identity: identity,
                    sourceId: source.Id,
                    locator: locator,
                    originalName: Path.GetFileName(file.RelativePath),
                    size: file.Length,
                    validationState: vState,
                    extensionMetadata: null,
                    sha256: null);

                records.Add(new IndexedAssetRecord(occurrence));
            }
            else
            {
                AssetDiagnosticRecord diag = new(
                    DiagnosticCode.UnknownFolderExtension,
                    $"Unknown folder extension '{ext}' for file '{file.RelativePath}'.",
                    targetPath: file.RelativePath);
                records.Add(new IndexedAssetRecord(diag));
                diagnostics.Add(diag);
            }
        }

        // Verify folder inventory did not change during scan
        SourceFingerprint fingerprintAfter;
        try
        {
            fingerprintAfter = await GetFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AssetDiagnosticRecord diag = new(DiagnosticCode.RootInaccessible, $"Inaccessible folder root: {ex.Message}", targetPath: source.FullPath);
            return CreateUnavailableSnapshot(source, diag, sw.Elapsed);
        }
        if (!fingerprintAfter.Equals(fingerprint))
        {
            return null; // Drift detected
        }

        sw.Stop();
        SourceScanStatistics stats = new(files.Count, totalBytes, sw.Elapsed);
        AssetSource sourceWithFingerprint = source with { Fingerprint = fingerprint };

        return new SourceIndexSnapshot(
            source: sourceWithFingerprint,
            fingerprint: fingerprint,
            records: records,
            diagnostics: diagnostics,
            isCacheHit: false,
            scanStatistics: stats);
    }

    public Task<Stream> OpenOccurrenceAsync(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(occurrence);

        if (occurrence.Locator is not FolderFileLocator folderLocator)
        {
            throw new ArgumentException($"Occurrence locator must be FolderFileLocator, got '{occurrence.Locator.GetType().Name}'.", nameof(occurrence));
        }

        string rootPrefix = Path.GetFullPath(source.FullPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string combinedPath = Path.GetFullPath(Path.Combine(source.FullPath, folderLocator.NormalizedRelativePath));

        if (!combinedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Folder file '{folderLocator.NormalizedRelativePath}' escapes source root boundary.");
        }

        FileInfo fileInfo = new(combinedPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException($"Folder file not found at '{combinedPath}'.", combinedPath);
        }

        if (fileInfo.Length != occurrence.Size)
        {
            throw new InvalidDataException($"Folder file size mismatch. Expected {occurrence.Size}, found {fileInfo.Length}.");
        }

        Stream fileStream = new FileStream(combinedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(fileStream);
    }

    private sealed record FolderFileItem(string RelativePath, string FullPath, long Length, long LastWriteUtcTicks);

    private static List<FolderFileItem> EnumerateRegularFiles(DirectoryInfo rootDir, CancellationToken cancellationToken)
    {
        List<FolderFileItem> result = new();
        int rootLength = rootDir.FullName.TrimEnd('\\', '/').Length + 1;
        EnumerateDirectoryRecursive(rootDir, rootLength, result, cancellationToken);
        return result;
    }

    private static void EnumerateDirectoryRecursive(
        DirectoryInfo dir,
        int rootLength,
        List<FolderFileItem> result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return; // Skip reparse points / symlinks / junctions
        }

        foreach (FileInfo file in dir.GetFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            string relativePath = file.FullName[rootLength..].Replace('\\', '/');
            result.Add(new FolderFileItem(relativePath, file.FullName, file.Length, file.LastWriteTimeUtc.Ticks));
        }

        foreach (DirectoryInfo subDir in dir.GetDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }
            EnumerateDirectoryRecursive(subDir, rootLength, result, cancellationToken);
        }
    }

    private static SourceIndexSnapshot CreateUnavailableSnapshot(AssetSource source, AssetDiagnosticRecord diagnostic, TimeSpan duration)
    {
        AssetSource unavailableSource = source with { IsAvailable = false };
        byte[] emptyDigest = new byte[32];
        SourceFingerprint emptyFingerprint = new(AssetSourceKind.Folder, AlgorithmVersion, emptyDigest);
        SourceScanStatistics stats = new(0, 0, duration);

        return new SourceIndexSnapshot(
            source: unavailableSource,
            fingerprint: emptyFingerprint,
            records: Array.Empty<IndexedAssetRecord>(),
            diagnostics: new[] { diagnostic },
            isCacheHit: false,
            scanStatistics: stats);
    }
}

