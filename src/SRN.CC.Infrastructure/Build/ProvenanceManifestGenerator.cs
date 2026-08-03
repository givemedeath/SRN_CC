using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using SRN.CC.Core.Build;
using SRN.CC.Core.Services;

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
        string hakSha256Hex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempHakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempManifestPath);

        var sourceMap = plan.FrozenSources.ToDictionary(s => s.Id);

        var resourcesList = new List<object>(plan.Items.Count);

        using (FileStream hakStream = new(tempHakPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        {
            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string typeName = _registry.TryGetExtension(item.Identity.ResourceType, out var ext) ? ext.ToUpperInvariant() : "UNKNOWN";

                string sourceLabel = sourceMap.TryGetValue(item.SourceId, out var src)
                    ? Path.GetFileName(src.FullPath)
                    : item.SourceId.ToString();

                string payloadHash = item.ExpectedSha256Hex ?? string.Empty;
                if (string.IsNullOrEmpty(payloadHash))
                {
                    payloadHash = await ComputeItemHashAsync(hakStream, item, plan, cancellationToken).ConfigureAwait(false);
                }

                resourcesList.Add(new
                {
                    resref = item.Identity.Resref,
                    resourceType = item.Identity.ResourceType,
                    resourceTypeName = typeName,
                    sizeBytes = item.ExpectedSizeBytes,
                    sha256Hex = payloadHash,
                    sourceLabel = sourceLabel,
                    locator = item.Locator.ToString(),
                    isPinned = item.IsPinned
                });
            }
        }

        var manifestData = new
        {
            schemaVersion = 1,
            appVersion = "1.0.0",
            generatedUtc = plan.CreatedUtc.ToString("o"),
            hakFileName = Path.GetFileName(plan.DestinationHakPath),
            hakSha256Hex = hakSha256Hex,
            totalEntries = plan.Items.Count,
            totalSizeBytes = plan.TotalEstimatedPayloadSize,
            resources = resourcesList
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(manifestData, jsonOptions);

        string? dir = Path.GetDirectoryName(tempManifestPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(tempManifestPath, jsonBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeItemHashAsync(Stream stream, BuildItem item, BuildPlan plan, CancellationToken cancellationToken)
    {
        using SHA256 sha = SHA256.Create();
        byte[] buffer = new byte[8192];
        long remaining = item.ExpectedSizeBytes;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            sha.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
