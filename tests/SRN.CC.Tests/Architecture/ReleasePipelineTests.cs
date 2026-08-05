using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace SRN.CC.Tests.Architecture;

/// <summary>
/// Guards the release pipeline's wiring: that the master verifier still drives the release packer
/// and the release audit, that CI publishes the archive and puts it through a clean-machine smoke,
/// and that the two policy invariants the release layout depends on stay true.
/// </summary>
/// <remarks>
/// These assertions are deliberately made over the files on disk rather than by running the
/// scripts. A step silently deleted from <c>VerifyBuild.ps1</c>, or a <c>needs:</c> dropped from the
/// workflow, produces a passing pipeline that has stopped checking anything; only a test that reads
/// the pipeline definition catches that. Repo-root discovery mirrors <see cref="ArchitectureTests"/>.
/// </remarks>
[TestFixture]
public class ReleasePipelineTests
{
    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "SRN.CC.sln")))
        {
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        return dir;
    }

    private static string ReadRepoFile(params string[] relativeParts)
    {
        string path = Path.Combine(new[] { FindRepoRoot() }.Concat(relativeParts).ToArray());
        File.Exists(path).Should().BeTrue($"{string.Join("/", relativeParts)} must exist");
        return File.ReadAllText(path);
    }

    private static JsonElement ReadPublishPolicy()
    {
        using JsonDocument document = JsonDocument.Parse(ReadRepoFile("eng", "publish-policy.json"));
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement policy, string propertyName)
    {
        policy.TryGetProperty(propertyName, out JsonElement array).Should().BeTrue(
            $"eng/publish-policy.json must define '{propertyName}'");
        return array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
    }

    [Test]
    public void VerifyBuild_InvokesTheReleaseAuditAgainstThePublishedTreeAndTheExtractedArchive()
    {
        string script = ReadRepoFile("tools", "VerifyBuild.ps1");

        script.Should().Contain(
            "AuditRelease.ps1\" -Root $publishDir",
            "step [8b] must audit the tree step [6] published, without publishing a second time");
        script.Should().Contain(
            "AuditRelease.ps1\" -Root $extractRoot",
            "step [10] must re-audit the archive after extraction");
    }

    [Test]
    public void VerifyBuild_PacksTheReleaseTwiceAndComparesTheHashes()
    {
        string script = ReadRepoFile("tools", "VerifyBuild.ps1");

        int packInvocations = script.Split(new[] { "PackRelease.ps1\"" }, StringSplitOptions.None).Length - 1;
        packInvocations.Should().Be(
            2,
            "step [9] proves the packer is deterministic by packing the same tree twice");

        script.Should().Contain(
            "The release packer is not deterministic",
            "the two pack hashes must be compared, not merely computed");
    }

    [Test]
    public void VerifyBuild_ExtractsOutsideTheRepositoryAndCleansUp()
    {
        string script = ReadRepoFile("tools", "VerifyBuild.ps1");

        script.Should().Contain(
            "[System.IO.Path]::GetTempPath()",
            "step [10] must extract to a scratch directory outside the repository");
        script.Should().Contain(
            "is inside the repository",
            "the scratch directory being outside the repository must be asserted, not assumed");
        script.Should().Contain(
            "Remove-Item $extractRoot -Recurse -Force",
            "the scratch extraction directory must be cleaned up");
    }

    [Test]
    public void VerifyBuild_IsAnElevenStepLadderAndNoLongerClaimsToCoverMilestonesOneToThree()
    {
        string script = ReadRepoFile("tools", "VerifyBuild.ps1");

        script.Should().NotContain(
            "Milestones 1-3",
            "the banner must not name a milestone range the pass has outgrown");
        script.Should().NotContain("/9]", "no step may still be numbered out of nine");

        for (int step = 1; step <= 11; step++)
        {
            script.Should().Contain(
                $"[{step}/11]",
                $"step {step} of the eleven-step ladder must be announced");
        }

        script.Should().Contain("[8b/11]", "the release audit is inserted as step [8b]");
    }

    [Test]
    public void VerifyBuild_TakesLicensePackageVersionsFromDependencyPolicyRatherThanLiterals()
    {
        string script = ReadRepoFile("tools", "VerifyBuild.ps1");

        script.Should().Contain(
            "eng/dependency-policy.json",
            "the license-copy step must read approved package versions from the dependency policy");

        foreach (string duplicatedVersion in new[] { "3.119.4", "8.3.1.3", "8.4.2", "2.1.27548.20260419" })
        {
            script.Should().NotContain(
                duplicatedVersion,
                $"'{duplicatedVersion}' is already recorded in eng/dependency-policy.json; a second "
                + "copy in the verifier makes a version bump copy the wrong licence file");
        }
    }

    [Test]
    public void VerifyBuild_WritesTheReleaseFieldsIntoTheSummary()
    {
        string script = ReadRepoFile("tools", "VerifyBuild.ps1");

        foreach (string field in new[] { "version =", "releaseZipPath =", "releaseZipSha256 =" })
        {
            script.Should().Contain(
                field,
                "summary.json is the run's evidence and must name the archive it produced");
        }
    }

    [Test]
    public void VerifyWorkflow_UploadsTheReleaseArchiveUnderItsOwnArtifactName()
    {
        string workflow = ReadRepoFile(".github", "workflows", "verify.yml");

        workflow.Should().Contain(
            "name: release-archive",
            "the archive must be uploaded as its own artifact so the smoke job can download it alone");
        workflow.Should().Contain(
            "release/",
            "the release archive upload must point at the run's release directory");
        workflow.Should().NotContain(
            "milestones-1-2-verification",
            "the evidence artifact name must not still refer to milestones 1-2");
    }

    [Test]
    public void VerifyWorkflow_SetsContinuousIntegrationBuildSoLockedRestoreIsNotScriptOnly()
    {
        string workflow = ReadRepoFile(".github", "workflows", "verify.yml");

        workflow.Should().Contain(
            "ContinuousIntegrationBuild: true",
            "Directory.Build.props gates RestoreLockedMode on this property; without it, locked "
            + "restore in CI depends solely on VerifyBuild.ps1 passing --locked-mode");
    }

    [Test]
    public void VerifyWorkflow_HasACleanMachineSmokeJobThatDependsOnTheVerifyJob()
    {
        string workflow = ReadRepoFile(".github", "workflows", "verify.yml");

        workflow.Should().Contain(
            "clean-machine-smoke:",
            "the clean-machine smoke must be a job in the same workflow as the verify job");
        workflow.Should().Contain(
            "needs: verify-windows",
            "the smoke job consumes the verify job's artifact and must depend on it");
        workflow.Should().Contain(
            "SmokeCleanMachine.ps1",
            "the smoke job must run the clean-machine smoke script");
        workflow.Should().Contain(
            "sparse-checkout",
            "the smoke job checks out only tools/ and eng/ so it cannot depend on the repository");
    }

    [Test]
    public void PublishPolicy_ForbidsLogsDebugSymbolsAndProjectFilesFromTheReleaseTree()
    {
        IReadOnlyList<string> forbidden = ReadStringArray(ReadPublishPolicy(), "forbiddenExtensions");

        foreach (string extension in new[] { ".log", ".pdb", ".srnccproj" })
        {
            forbidden.Should().Contain(
                extension,
                $"'{extension}' files are user or build state and must never ship in the release tree");
        }
    }

    [Test]
    public void PublishPolicy_NeverTreatsTheProjectExtensionAsAForbiddenPathFragment()
    {
        IReadOnlyList<string> fragments = ReadStringArray(ReadPublishPolicy(), "forbiddenPathFragments");

        fragments.Should().NotContain(
            f => f.Contains("srnccproj", StringComparison.OrdinalIgnoreCase),
            "the application legitimately embeds the literal 'srnccproj' in its file-picker "
            + "patterns; forbidding it as a fragment would fail the audit on correct output. "
            + "Forbidding it as an extension is the correct and separate rule");
    }

    [Test]
    public void NoBuildFile_DeclaresPublishSingleFile()
    {
        string root = FindRepoRoot();
        string[] excludedSegments = { $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                      $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                      $"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}",
                                      $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}" };

        var buildFiles = new[] { "*.csproj", "*.props", "*.targets" }
            .SelectMany(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            .Where(path => !excludedSegments.Any(segment => path.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        buildFiles.Should().NotBeEmpty("the repository must contain build files to scan");

        var offenders = buildFiles
            .Where(path => File.ReadAllText(path).Contains("PublishSingleFile", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        offenders.Should().BeEmpty(
            "the release must stay a non-single-file layout: a single-file publish defeats the "
            + "per-file origin audit and the deterministic packer's per-file manifest");
    }

    [Test]
    public void SmokeCleanMachine_AssertsTheCleanMachinePreconditionAndTheFirstRunArtifacts()
    {
        string script = ReadRepoFile("tools", "SmokeCleanMachine.ps1");

        script.Should().Contain(
            "--srncc-preflight-only",
            "the deterministic half of the smoke is the preflight report");
        script.Should().Contain(
            "AuditRelease.ps1\" -Root $AppRoot",
            "the smoke must audit the extracted release tree it is about to run");
        script.Should().Contain(
            "Blocking",
            "no startup check may be Blocking on a clean machine");
        script.Should().Contain(
            "cache-v1.sqlite",
            "the first run must create the cache database");
        script.Should().Contain(
            "Logs/srncc.log",
            "the first run must create the JSON-line log");
    }
}
