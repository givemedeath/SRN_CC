using System.Diagnostics;
using System.Security.Cryptography;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Fingerprints;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Indexing;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Records;
using SRN.CC.Core.Services;
using SRN.CC.Core.Snapshots;
using SRN.CC.Core.Sources;
using SRN.CC.Formats.Hak;

namespace SRN.CC.Infrastructure.Sources;

public sealed class HakAssetSourceReader : IAssetSourceReader
{
    private const int AlgorithmVersion = 1;

    public async Task<SourceFingerprint> GetFingerprintAsync(AssetSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != AssetSourceKind.Hak)
        {
            throw new ArgumentException($"HakAssetSourceReader cannot index source kind '{source.Kind}'.", nameof(source));
        }

        FileInfo fileInfo = new(source.FullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException($"HAK source file not found at '{source.FullPath}'.", source.FullPath);
        }

        long fileSize = fileInfo.Length;
        long lastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;

        using FileStream stream = new(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        HakReader reader = new(stream);

        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] metaHeader = new byte[24];
        BitConverter.TryWriteBytes(metaHeader.AsSpan(0, 4), (int)AssetSourceKind.Hak);
        BitConverter.TryWriteBytes(metaHeader.AsSpan(4, 4), AlgorithmVersion);
        BitConverter.TryWriteBytes(metaHeader.AsSpan(8, 8), fileSize);
        BitConverter.TryWriteBytes(metaHeader.AsSpan(16, 8), lastWriteTicks);
        hasher.AppendData(metaHeader);

        // Append 160-byte fixed header
        stream.Position = 0;
        byte[] headerBytes = new byte[160];
        await stream.ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        hasher.AppendData(headerBytes);

        // Append localized string table if present
        if (reader.LocalizedStringSize > 0)
        {
            stream.Position = reader.OffsetToLocalizedString;
            byte[] locBytes = new byte[reader.LocalizedStringSize];
            await stream.ReadExactlyAsync(locBytes, cancellationToken).ConfigureAwait(false);
            hasher.AppendData(locBytes);
        }

        // Append KeyList table
        long keyListSize = checked((long)reader.EntryCount * 24);
        stream.Position = reader.OffsetToKeyList;
        byte[] keyBytes = new byte[keyListSize];
        await stream.ReadExactlyAsync(keyBytes, cancellationToken).ConfigureAwait(false);
        hasher.AppendData(keyBytes);

        // Append ResourceList table
        long resourceListSize = checked((long)reader.EntryCount * 8);
        stream.Position = reader.OffsetToResourceList;
        byte[] resBytes = new byte[resourceListSize];
        await stream.ReadExactlyAsync(resBytes, cancellationToken).ConfigureAwait(false);
        hasher.AppendData(resBytes);

        byte[] digest = hasher.GetHashAndReset();
        return new SourceFingerprint(AssetSourceKind.Hak, AlgorithmVersion, digest);
    }

    public async Task<SourceIndexSnapshot> IndexAsync(AssetSource source, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Stopwatch sw = Stopwatch.StartNew();

        SourceIndexSnapshot? snapshot = await ScanHakOnceAsync(source, progress, cancellationToken).ConfigureAwait(false);
        if (snapshot == null)
        {
            progress?.Report(new IndexProgress(source.Id, IndexPhase.Scanning, 0, null, "Retrying HAK scan after drift..."));
            snapshot = await ScanHakOnceAsync(source, progress, cancellationToken).ConfigureAwait(false);
        }

        if (snapshot == null)
        {
            AssetDiagnosticRecord diag = new(DiagnosticCode.ChangedSourceFailure, "HAK file changed repeatedly during indexing scan.", targetPath: source.FullPath);
            return CreateUnavailableSnapshot(source, diag, sw.Elapsed);
        }

        sw.Stop();
        return snapshot;
    }

    private async Task<SourceIndexSnapshot?> ScanHakOnceAsync(AssetSource source, IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();

        FileInfo fileInfoBefore = new(source.FullPath);
        if (!fileInfoBefore.Exists)
        {
            AssetDiagnosticRecord diag = new(DiagnosticCode.SourceNotFound, $"HAK file not found at '{source.FullPath}'.", targetPath: source.FullPath);
            return CreateUnavailableSnapshot(source, diag, sw.Elapsed);
        }

        SourceFingerprint fingerprint;
        try
        {
            fingerprint = await GetFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            AssetDiagnosticRecord diag = new(DiagnosticCode.InvalidResref, $"Invalid HAK resref: {ex.Message}", targetPath: source.FullPath);
            return CreateUnavailableSnapshot(source, diag, sw.Elapsed);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            AssetDiagnosticRecord diag = new(DiagnosticCode.InvalidHeader, $"Structurally invalid HAK file: {ex.Message}", targetPath: source.FullPath);
            return CreateUnavailableSnapshot(source, diag, sw.Elapsed);
        }

        List<IndexedAssetRecord> records = new();
        List<AssetDiagnosticRecord> diagnostics = new();
        HashSet<AssetIdentity> seenIdentities = new();

        HakReader hakReader;
        try
        {
            using FileStream stream = new(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            hakReader = new HakReader(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The source changed or became inaccessible between fingerprinting and parsing.
            return null;
        }

        long totalEntries = hakReader.Entries.Count;
        for (int i = 0; i < hakReader.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = hakReader.Entries[i];

            progress?.Report(new IndexProgress(source.Id, IndexPhase.Scanning, i + 1, totalEntries, entry.Key.ToString()));

            AssetIdentity identity;
            try
            {
                identity = new AssetIdentity(entry.Key.ResrefBytes.Span, entry.Key.ResourceType);
            }
            catch (Exception ex)
            {
                AssetDiagnosticRecord diag = new(DiagnosticCode.InvalidResref, $"Invalid HAK resref at index {i}: {ex.Message}", targetPath: source.FullPath, entryIndex: i);
                records.Add(new IndexedAssetRecord(diag));
                diagnostics.Add(diag);
                continue;
            }

            ValidationState vState = ValidationState.Valid;
            if (!seenIdentities.Add(identity))
            {
                vState = ValidationState.DuplicateIdentity;
            }

            HakEntryLocator locator = new(i);
            AssetOccurrence occurrence = new(
                identity: identity,
                sourceId: source.Id,
                locator: locator,
                originalName: identity.OriginalName,
                size: entry.ResourceSize,
                validationState: vState,
                extensionMetadata: null,
                sha256: null);

            records.Add(new IndexedAssetRecord(occurrence));
        }

        // Re-check metadata after scan to detect drift
        FileInfo fileInfoAfter = new(source.FullPath);
        if (!fileInfoAfter.Exists ||
            fileInfoAfter.Length != fileInfoBefore.Length ||
            fileInfoAfter.LastWriteTimeUtc != fileInfoBefore.LastWriteTimeUtc)
        {
            return null;
        }

        sw.Stop();
        SourceScanStatistics stats = new(totalEntries, fileInfoBefore.Length, sw.Elapsed);
        AssetSource sourceWithFingerprint = source with { Fingerprint = fingerprint, IsAvailable = true };

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

        if (occurrence.Locator is not HakEntryLocator hakLocator)
        {
            throw new ArgumentException($"Occurrence locator must be HakEntryLocator, got '{occurrence.Locator.GetType().Name}'.", nameof(occurrence));
        }

        HakReader hakReader;
        using (FileStream stream = new(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            hakReader = new HakReader(stream);
        }

        if (hakLocator.EntryIndex < 0 || hakLocator.EntryIndex >= hakReader.Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrence), $"HAK entry index {hakLocator.EntryIndex} out of bounds for HAK with {hakReader.Entries.Count} entries.");
        }

        var entry = hakReader.Entries[hakLocator.EntryIndex];
        if (entry.Key.ResourceType != occurrence.Identity.ResourceType ||
            !entry.Key.CanonicalResrefBytes.Span.SequenceEqual(occurrence.Identity.CanonicalResrefBytes.Span))
        {
            throw new InvalidDataException("HAK entry identity no longer matches the indexed occurrence.");
        }

        if (entry.ResourceSize != occurrence.Size)
        {
            throw new InvalidDataException($"HAK entry size mismatch. Expected {occurrence.Size}, found {entry.ResourceSize}.");
        }

        Stream payloadStream = HakReader.OpenPayloadStream(entry, source.FullPath);
        return Task.FromResult(payloadStream);
    }

    private static SourceIndexSnapshot CreateUnavailableSnapshot(AssetSource source, AssetDiagnosticRecord diagnostic, TimeSpan duration)
    {
        AssetSource unavailableSource = source with { IsAvailable = false };
        byte[] emptyDigest = new byte[32];
        SourceFingerprint emptyFingerprint = new(AssetSourceKind.Hak, AlgorithmVersion, emptyDigest);
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


