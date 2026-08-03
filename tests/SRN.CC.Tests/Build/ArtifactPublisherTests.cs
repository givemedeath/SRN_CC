using System.Text;
using NUnit.Framework;
using SRN.CC.Core.Build;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Sources;
using SRN.CC.Infrastructure.Build;

namespace SRN.CC.Tests.Build;

[TestFixture]
public class ArtifactPublisherTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "srncc_publisher_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Test]
    public async Task PublishAsync_SuccessfulFlow_ReplacesTargetAndCleansBackups()
    {
        string targetHak = Path.Combine(_tempDir, "target.hak");
        string targetManifest = Path.Combine(_tempDir, "target.manifest.json");

        await File.WriteAllTextAsync(targetHak, "old hak content");
        await File.WriteAllTextAsync(targetManifest, "old manifest content");

        string tempHak = Path.Combine(_tempDir, "new.tmp.hak");
        string tempManifest = Path.Combine(_tempDir, "new.tmp.manifest.json");

        await File.WriteAllTextAsync(tempHak, "NEW hak content");
        await File.WriteAllTextAsync(tempManifest, "NEW manifest content");

        var plan = new BuildPlan
        {
            DestinationHakPath = targetHak,
            DestinationManifestPath = targetManifest,
            FrozenSources = Array.Empty<AssetSource>(),
            Items = Array.Empty<BuildItem>(),
            CreatedUtc = DateTime.UtcNow
        };

        var publisher = new ArtifactPublisher();
        var result = await publisher.PublishAsync(plan, tempHak, tempManifest);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(File.Exists(targetHak), Is.True);
        Assert.That(await File.ReadAllTextAsync(targetHak), Is.EqualTo("NEW hak content"));

        // Backups & temp files must be cleaned up
        Assert.That(File.Exists(targetHak + ".bak"), Is.False);
        Assert.That(File.Exists(targetManifest + ".bak"), Is.False);
        Assert.That(File.Exists(tempHak), Is.False);
        Assert.That(File.Exists(tempManifest), Is.False);
    }

    [Test]
    public async Task PublishAsync_FailureDuringReplacement_RestoresOriginalFilesFromBackup()
    {
        string targetHak = Path.Combine(_tempDir, "target_fail.hak");
        string targetManifest = Path.Combine(_tempDir, "target_fail.manifest.json");

        await File.WriteAllTextAsync(targetHak, "ORIGINAL HAK");
        await File.WriteAllTextAsync(targetManifest, "ORIGINAL MANIFEST");

        string nonExistentTempHak = Path.Combine(_tempDir, "missing.tmp.hak");
        string tempManifest = Path.Combine(_tempDir, "new.tmp.manifest.json");
        await File.WriteAllTextAsync(tempManifest, "NEW manifest content");

        var plan = new BuildPlan
        {
            DestinationHakPath = targetHak,
            DestinationManifestPath = targetManifest,
            FrozenSources = Array.Empty<AssetSource>(),
            Items = Array.Empty<BuildItem>(),
            CreatedUtc = DateTime.UtcNow
        };

        var publisher = new ArtifactPublisher();
        var result = await publisher.PublishAsync(plan, nonExistentTempHak, tempManifest);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(File.Exists(targetHak), Is.True);
        Assert.That(await File.ReadAllTextAsync(targetHak), Is.EqualTo("ORIGINAL HAK"));
    }

    [Test]
    public async Task PublishAsync_FailureWithNewTargets_RemovesNewlyCreatedTargetsOnRollback()
    {
        string newTargetHak = Path.Combine(_tempDir, "brand_new.hak");
        string newTargetManifest = Path.Combine(_tempDir, "brand_new.manifest.json");

        string tempHak = Path.Combine(_tempDir, "new.tmp.hak");
        string nonExistentTempManifest = Path.Combine(_tempDir, "missing.tmp.manifest.json");
        await File.WriteAllTextAsync(tempHak, "NEW HAK CONTENT");

        var plan = new BuildPlan
        {
            DestinationHakPath = newTargetHak,
            DestinationManifestPath = newTargetManifest,
            FrozenSources = Array.Empty<AssetSource>(),
            Items = Array.Empty<BuildItem>(),
            CreatedUtc = DateTime.UtcNow
        };

        var publisher = new ArtifactPublisher();
        var result = await publisher.PublishAsync(plan, tempHak, nonExistentTempManifest);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(File.Exists(newTargetHak), Is.False, "Newly created HAK target must be removed on rollback if it did not exist before.");
    }
}
