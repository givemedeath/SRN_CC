using System.Security.Cryptography;
using System.Text;
using SRN.CC.Core.Build;
using SRN.CC.Core.Services;
using SRN.CC.Formats.Hak;

namespace SRN.CC.Infrastructure.Build;

public sealed class BuildVerifier : IBuildVerifier
{
    private static readonly Encoding Cp1252 = Encoding.GetEncoding(1252);

    public async Task<BuildVerificationReport> VerifyAsync(
        BuildPlan plan,
        string tempHakPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempHakPath);

        List<string> errors = new();
        List<string> warnings = new();

        if (!File.Exists(tempHakPath))
        {
            errors.Add($"Temporary HAK file does not exist at {tempHakPath}");
            return new BuildVerificationReport(false, 0, 0, string.Empty, errors, warnings);
        }

        long verifiedBytes = 0;
        int verifiedEntries = 0;
        string computedHakHash = string.Empty;

        try
        {
            using (FileStream fs = new(tempHakPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hashBytes = await sha.ComputeHashAsync(fs, cancellationToken).ConfigureAwait(false);
                computedHakHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            HakReader reader;
            using (FileStream fs = File.OpenRead(tempHakPath))
            {
                reader = new HakReader(fs);
            }

            var entries = reader.Entries;
            if (entries.Count != plan.Items.Count)
            {
                errors.Add($"Entry count mismatch: HAK contains {entries.Count} entries, expected {plan.Items.Count}.");
            }

            List<HakFormatKey> expectedKeys = plan.Items
                .Select(item => new HakFormatKey(item.Identity.OriginalResrefBytes.Span, item.Identity.ResourceType))
                .ToList();
            expectedKeys.Sort(CompareHakFormatKey);

            if (expectedKeys.Count == entries.Count)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var expected = expectedKeys[i];
                    var actual = entries[i].Key;
                    if (!expected.Equals(actual))
                    {
                        errors.Add($"HAK key-table order mismatch at entry {i}: expected {DescribeKey(expected)} but found {DescribeKey(actual)}.");
                    }
                }
            }

            var expectedMap = plan.Items.ToDictionary(
                item => new HakFormatKey(item.Identity.OriginalResrefBytes.Span, item.Identity.ResourceType));

            using (FileStream fs = new(tempHakPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            {
                byte[] buffer = new byte[65536];

                for (int i = 0; i < entries.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = entries[i];
                    string resrefStr = Cp1252.GetString(entry.Key.ResrefBytes.Span);

                    fs.Seek(entry.OffsetToResource, SeekOrigin.Begin);
                    using SHA256 payloadSha = SHA256.Create();

                    long remaining = entry.ResourceSize;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = await fs.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            errors.Add($"Unexpected EOF verifying entry {resrefStr}.{entry.Key.ResourceType}");
                            break;
                        }
                        payloadSha.TransformBlock(buffer, 0, read, null, 0);
                        remaining -= read;
                    }
                    payloadSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                    string payloadHashHex = Convert.ToHexString(payloadSha.Hash!).ToLowerInvariant();

                    if (expectedMap.TryGetValue(entry.Key, out var expected))
                    {
                        if (entry.ResourceSize != expected.ExpectedSizeBytes)
                        {
                            errors.Add($"Size mismatch for entry {resrefStr}.{entry.Key.ResourceType}: expected {expected.ExpectedSizeBytes}, got {entry.ResourceSize}");
                        }

                        if (!string.IsNullOrEmpty(expected.ExpectedSha256Hex) &&
                            !string.Equals(expected.ExpectedSha256Hex, payloadHashHex, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"SHA-256 mismatch for entry {resrefStr}.{entry.Key.ResourceType}: expected {expected.ExpectedSha256Hex}, got {payloadHashHex}");
                        }
                    }
                    else
                    {
                        errors.Add($"Unexpected entry in built HAK: {resrefStr}.{entry.Key.ResourceType}");
                    }

                    verifiedBytes += entry.ResourceSize;
                    verifiedEntries++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"Verification exception: {ex.Message}");
        }

        bool isSuccess = errors.Count == 0;
        return new BuildVerificationReport(
            IsSuccess: isSuccess,
            VerifiedEntryCount: verifiedEntries,
            VerifiedTotalPayloadBytes: verifiedBytes,
            ComputedHakSha256Hex: computedHakHash,
            Errors: errors,
            Warnings: warnings
        );
    }

    private static int CompareHakFormatKey(HakFormatKey a, HakFormatKey b)
    {
        int typeComparison = a.ResourceType.CompareTo(b.ResourceType);
        if (typeComparison != 0)
        {
            return typeComparison;
        }

        return a.CanonicalResrefBytes.Span.SequenceCompareTo(b.CanonicalResrefBytes.Span);
    }

    private static string DescribeKey(HakFormatKey key)
    {
        return $"{Convert.ToHexString(key.CanonicalResrefBytes.Span).ToLowerInvariant()}.{key.ResourceType}";
    }
}
