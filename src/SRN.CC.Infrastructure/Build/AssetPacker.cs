using System.Security.Cryptography;
using SRN.CC.Core.Build;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Formats.Hak;

namespace SRN.CC.Infrastructure.Build;

public sealed class AssetPacker : IAssetPacker
{
    private readonly ISourceReaderDispatcher _dispatcher;

    public AssetPacker(ISourceReaderDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task<BuildArtifact> PackAsync(
        BuildPlan plan,
        string tempHakPath,
        string tempManifestPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempHakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempManifestPath);

        cancellationToken.ThrowIfCancellationRequested();

        string? dir = Path.GetDirectoryName(tempHakPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sourceMap = plan.FrozenSources.ToDictionary(s => s.Id);

        List<HakWriter.WriteItem> writeItems = new(plan.Items.Count);
        List<Stream> openedStreams = new(plan.Items.Count);

        try
        {
            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!sourceMap.TryGetValue(item.SourceId, out var source))
                {
                    throw new InvalidOperationException($"Source {item.SourceId} required for build item {item.Identity} is missing.");
                }

                var occ = new AssetOccurrence(item.Identity, item.SourceId, item.Locator, item.Identity.OriginalName, item.ExpectedSizeBytes);
                Stream payloadStream = await _dispatcher.OpenOccurrenceAsync(source, occ, cancellationToken).ConfigureAwait(false);
                openedStreams.Add(payloadStream);

                var hakKey = new HakFormatKey(item.Identity.OriginalResrefBytes.Span, item.Identity.ResourceType);
                writeItems.Add(new HakWriter.WriteItem(hakKey, payloadStream, checked((uint)item.ExpectedSizeBytes)));
            }

            await using (FileStream fs = new(tempHakPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await HakWriter.WriteAsync(fs, writeItems, progress, cancellationToken).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            long hakSizeBytes = new FileInfo(tempHakPath).Length;
            string hakSha256;

            using (FileStream fs = new(tempHakPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hashBytes = await sha.ComputeHashAsync(fs, cancellationToken).ConfigureAwait(false);
                hakSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            return new BuildArtifact(
                HakPath: plan.DestinationHakPath,
                ManifestPath: plan.DestinationManifestPath,
                HakSizeBytes: hakSizeBytes,
                HakSha256Hex: hakSha256,
                EntryCount: plan.Items.Count
            );
        }
        finally
        {
            foreach (var stream in openedStreams)
            {
                try { stream.Dispose(); } catch { }
            }
        }
    }
}
