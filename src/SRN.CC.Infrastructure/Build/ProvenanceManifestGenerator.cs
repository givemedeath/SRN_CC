using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using SRN.CC.Core.Build;
using SRN.CC.Core.Services;
using SRN.CC.Formats.Hak;

namespace SRN.CC.Infrastructure.Build;

public sealed class ProvenanceManifestGenerator
{
    private readonly IResourceTypeRegistry _registry;

    public ProvenanceManifestGenerator(IResourceTypeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task GenerateManifestAsync(
        BuildPlan plan,
        string tempHakPath,
        string tempManifestPath,
        string computedHakSha256Hex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempHakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(computedHakSha256Hex);

        var sourceMap = plan.FrozenSources.ToDictionary(s => s.Id);

        HakReader reader;
        using (FileStream fs = File.OpenRead(tempHakPath))
        {
            reader = new HakReader(fs);
        }

        var entryOffsetMap = reader.Entries.ToDictionary(
            e => new HakFormatKey(e.Key.ResrefBytes.Span, e.Key.ResourceType),
            e => e.OffsetToResource);

        List<ManifestResourceEntry> resourceEntries = new(plan.Items.Count);

        using (FileStream fs = new(tempHakPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        {
            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string sourceLabel = sourceMap.TryGetValue(item.SourceId, out var src)
                    ? Path.GetFileName(src.FullPath)
                    : item.SourceId.ToString();

                string typeName = _registry.TryGetExtension(item.Identity.ResourceType, out var ext)
                    ? ext.ToUpperInvariant()
                    : item.Identity.ResourceType.ToString();

                string itemHashHex = item.ExpectedSha256Hex ?? string.Empty;
                if (string.IsNullOrEmpty(itemHashHex))
                {
                    var key = new HakFormatKey(item.Identity.OriginalResrefBytes.Span, item.Identity.ResourceType);
                    if (entryOffsetMap.TryGetValue(key, out uint offset))
                    {
                        itemHashHex = await ComputePayloadHashAsync(fs, offset, item.ExpectedSizeBytes, cancellationToken).ConfigureAwait(false);
                    }
                }

                resourceEntries.Add(new ManifestResourceEntry(
                    Resref: item.Identity.Resref,
                    ResourceType: item.Identity.ResourceType,
                    ResourceTypeName: typeName,
                    SourceLabel: sourceLabel,
                    OriginLocator: item.Locator.ToString(),
                    SizeBytes: item.ExpectedSizeBytes,
                    Sha256Hex: itemHashHex,
                    IsPinned: item.IsPinned
                ));
            }
        }

        var manifestData = new ManifestData(
            SchemaVersion: "1.0",
            AppVersion: "1.0.0",
            GeneratedUtc: plan.CreatedUtc.ToString("o"),
            HakFileName: Path.GetFileName(plan.DestinationHakPath),
            HakSha256Hex: computedHakSha256Hex,
            TotalEntries: resourceEntries.Count,
            TotalSizeBytes: resourceEntries.Sum(e => e.SizeBytes),
            Resources: resourceEntries
        );

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(manifestData, options);
        await File.WriteAllBytesAsync(tempManifestPath, jsonBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputePayloadHashAsync(Stream fs, long offset, long sizeBytes, CancellationToken cancellationToken)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        using SHA256 sha = SHA256.Create();
        byte[] buffer = new byte[65536];
        long remaining = sizeBytes;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await fs.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            sha.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private sealed record ManifestData(
        string SchemaVersion,
        string AppVersion,
        string GeneratedUtc,
        string HakFileName,
        string HakSha256Hex,
        int TotalEntries,
        long TotalSizeBytes,
        IReadOnlyList<ManifestResourceEntry> Resources
    );

    private sealed record ManifestResourceEntry(
        string Resref,
        ushort ResourceType,
        string ResourceTypeName,
        string SourceLabel,
        string OriginLocator,
        long SizeBytes,
        string Sha256Hex,
        bool IsPinned
    );
}
