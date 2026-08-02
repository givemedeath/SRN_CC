using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Sources;

namespace SRN.CC.Tests.Sources;

[TestFixture]
public class HakSourceReaderTests
{
    [Test]
    public async Task IndexAsync_EmptyResref_ReturnsUnavailableSnapshot()
    {
        string hakPath = Path.Combine(Path.GetTempPath(), "SRNCC_InvalidResref_" + Guid.NewGuid().ToString("N") + ".hak");
        try
        {
            File.WriteAllBytes(hakPath, BuildHakWithEmptyResref());
            AssetSource source = AssetSource.CreateHak(hakPath);
            HakAssetSourceReader reader = new();

            var snapshot = await reader.IndexAsync(source);

            snapshot.Source.IsAvailable.Should().BeFalse();
            snapshot.Diagnostics.Should().ContainSingle(d => d.Code == DiagnosticCode.InvalidResref);
        }
        finally
        {
            if (File.Exists(hakPath)) File.Delete(hakPath);
        }
    }

    private static byte[] BuildHakWithEmptyResref()
    {
        byte[] bytes = new byte[192];
        "HAK "u8.CopyTo(bytes);
        "V1.0"u8.CopyTo(bytes.AsSpan(4));
        BitConverter.GetBytes(1u).CopyTo(bytes, 16);
        BitConverter.GetBytes(160u).CopyTo(bytes, 24);
        BitConverter.GetBytes(184u).CopyTo(bytes, 28);
        BitConverter.GetBytes((ushort)2009).CopyTo(bytes, 180);
        BitConverter.GetBytes(192u).CopyTo(bytes, 184);
        return bytes;
    }
}

