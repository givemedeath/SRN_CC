using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace SRN.CC.Tests.Architecture;

/// <summary>
/// Asserts that <c>NOTICES.md</c> actually covers every package the dependency policy marks as
/// shipping in the runtime distribution.
/// </summary>
/// <remarks>
/// <para>
/// <c>tools/AuditDependencies.ps1</c> checks each approved package's <c>mustShipNotice</c> flag, but
/// it never opens <c>NOTICES.md</c> — so a runtime package could be added to
/// <c>eng/dependency-policy.json</c>, flagged as requiring a notice, and still ship with no
/// attribution at all while every gate stayed green. That gap was recorded as an observation during
/// Milestone 6; this fixture turns it into an assertion.
/// </para>
/// <para>
/// <c>NOTICES.md</c> deliberately groups packages into component families (one row for the whole
/// Avalonia set, one for the whole NAudio set, and so on) rather than listing 43 near-identical
/// rows, so the mapping from package id to family row lives here in
/// <see cref="PackageIdToNoticeFamily"/>. The mapping is checked in both directions: an unmapped
/// runtime package fails, and a declared family that no runtime package resolves to also fails, so
/// the table cannot drift away from the policy in either direction.
/// </para>
/// <para>
/// Repo-root discovery mirrors <c>ArchitectureTests</c>/<c>ReleasePipelineTests</c>.
/// </para>
/// </remarks>
[TestFixture]
public class NoticesCompletenessTests
{
    private const string RuntimePackagesHeading = "## Resolved runtime packages";

    /// <summary>
    /// Ordered longest-prefix-first: <c>Avalonia.Angle.Windows.Natives</c> must be matched by its own
    /// entry before the generic <c>Avalonia</c> entry claims it, and likewise for the Microsoft ids.
    /// The family label is matched as a <i>prefix</i> of a row's "Component family" cell, so a row may
    /// append a parenthesised member list — as the NAudio row does — without breaking the mapping.
    /// </summary>
    private static readonly (string PackageIdPrefix, string NoticeFamily)[] PackageIdToNoticeFamily =
    [
        ("Avalonia.Angle.Windows.Natives", "Avalonia ANGLE Windows natives"),
        ("Avalonia", "Avalonia UI and platform backends"),
        ("CommunityToolkit.Mvvm", "CommunityToolkit.Mvvm"),
        ("HarfBuzzSharp", "HarfBuzzSharp and Win32 native assets"),
        ("MicroCom.Runtime", "MicroCom.Runtime"),
        ("Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite and Core"),
        ("Microsoft.DotNet.PlatformAbstractions", "Microsoft.DotNet.PlatformAbstractions"),
        ("Microsoft.Extensions.DependencyModel", "Microsoft.Extensions.DependencyModel"),
        ("NAudio", "NAudio and platform audio backends"),
        ("Pfim", "Pfim"),
        ("Silk.NET", "Silk.NET.Core, Silk.NET.Maths, Silk.NET.OpenGL"),
        ("SkiaSharp", "SkiaSharp and Win32 native assets"),
        ("SQLitePCLRaw", "SQLitePCLRaw family"),
        ("Tmds.DBus.Protocol", "Tmds.DBus.Protocol")
    ];

    /// <summary>
    /// Rows that describe something other than an <c>approvedPackages</c> entry and therefore have no
    /// package id to resolve back to. The shared-framework runtime pack is published by the SDK, not
    /// restored as a NuGet reference, so it appears in <c>NOTICES.md</c> without appearing in the
    /// policy's package list.
    /// </summary>
    private static readonly string[] NonPackageNoticeFamilies =
    [
        ".NET runtime pack, win-x64"
    ];

    private sealed record NoticeRow(string Family, string Versions, string License, string Attribution);

    private sealed record RuntimePackage(string Id, string Version, bool MustShipNotice);

    [Test]
    public void NoticesMd_HasARowForEveryRuntimeScopedPackageInTheDependencyPolicy()
    {
        IReadOnlyList<RuntimePackage> runtimePackages = ReadRuntimePackages();
        IReadOnlyList<NoticeRow> rows = ReadNoticeRows();

        runtimePackages.Should().NotBeEmpty("eng/dependency-policy.json must declare runtime packages");

        var uncovered = new List<string>();
        foreach (RuntimePackage package in runtimePackages)
        {
            string? family = ResolveFamily(package.Id);
            if (family == null)
            {
                uncovered.Add($"{package.Id}@{package.Version} (no NOTICES.md component family is mapped to this id)");
                continue;
            }

            if (FindRow(rows, family) == null)
            {
                uncovered.Add($"{package.Id}@{package.Version} (expected NOTICES.md row '{family}', which is absent)");
            }
        }

        uncovered.Should().BeEmpty(
            "every \"scope\": \"runtime\" package in eng/dependency-policy.json must have an attribution row in NOTICES.md");
    }

    [Test]
    public void NoticesMd_RowVersionsMatchTheResolvedRuntimePackageVersions()
    {
        IReadOnlyList<RuntimePackage> runtimePackages = ReadRuntimePackages();
        IReadOnlyList<NoticeRow> rows = ReadNoticeRows();

        var mismatches = new List<string>();
        foreach (RuntimePackage package in runtimePackages)
        {
            string? family = ResolveFamily(package.Id);
            NoticeRow? row = family == null ? null : FindRow(rows, family);
            if (row == null)
            {
                continue; // Reported by the completeness test above.
            }

            if (!row.Versions.Contains(package.Version, StringComparison.Ordinal))
            {
                mismatches.Add($"{package.Id}@{package.Version} vs NOTICES.md row '{row.Family}' listing '{row.Versions}'");
            }
        }

        mismatches.Should().BeEmpty(
            "a NOTICES.md row must list the version the dependency policy actually approved");
    }

    [Test]
    public void NoticesMd_DeclaresNoRuntimeFamilyThatThePolicyNoLongerShips()
    {
        IReadOnlyList<RuntimePackage> runtimePackages = ReadRuntimePackages();
        IReadOnlyList<NoticeRow> rows = ReadNoticeRows();

        var claimedRows = runtimePackages
            .Select(p => ResolveFamily(p.Id))
            .Where(f => f != null)
            .Select(f => FindRow(rows, f!))
            .Where(r => r != null)
            .Select(r => r!.Family)
            .ToHashSet(StringComparer.Ordinal);

        var orphaned = rows
            .Select(r => r.Family)
            .Where(f => !claimedRows.Contains(f) && !NonPackageNoticeFamilies.Contains(f, StringComparer.Ordinal))
            .ToList();

        orphaned.Should().BeEmpty(
            "a NOTICES.md row that no runtime-scoped package resolves to is stale attribution");
    }

    [Test]
    public void DependencyPolicy_MarksEveryRuntimeScopedPackageAsRequiringANotice()
    {
        IReadOnlyList<RuntimePackage> runtimePackages = ReadRuntimePackages();

        runtimePackages
            .Where(p => !p.MustShipNotice)
            .Select(p => p.Id)
            .Should()
            .BeEmpty("a package that ships in the distribution must carry mustShipNotice: true");
    }

    private static NoticeRow? FindRow(IReadOnlyList<NoticeRow> rows, string family) =>
        rows.FirstOrDefault(r => r.Family.StartsWith(family, StringComparison.Ordinal));

    private static string? ResolveFamily(string packageId)
    {
        foreach ((string prefix, string family) in PackageIdToNoticeFamily)
        {
            if (packageId.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                packageId.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                return family;
            }
        }

        return null;
    }

    private static IReadOnlyList<RuntimePackage> ReadRuntimePackages()
    {
        using JsonDocument document = JsonDocument.Parse(ReadRepoFile("eng", "dependency-policy.json"));
        document.RootElement.TryGetProperty("approvedPackages", out JsonElement packages).Should().BeTrue(
            "eng/dependency-policy.json must define 'approvedPackages'");

        var result = new List<RuntimePackage>();
        foreach (JsonElement package in packages.EnumerateArray())
        {
            string scope = package.TryGetProperty("scope", out JsonElement scopeElement)
                ? scopeElement.GetString() ?? string.Empty
                : string.Empty;

            if (!string.Equals(scope, "runtime", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new RuntimePackage(
                Id: package.GetProperty("id").GetString() ?? string.Empty,
                Version: package.GetProperty("version").GetString() ?? string.Empty,
                MustShipNotice: package.TryGetProperty("mustShipNotice", out JsonElement flag) && flag.GetBoolean()));
        }

        return result;
    }

    /// <summary>
    /// Parses the markdown table that follows the "Resolved runtime packages" heading. Stops at the
    /// first line after the table that is not a table row, so later sections (the license texts) are
    /// never mistaken for attribution rows.
    /// </summary>
    private static IReadOnlyList<NoticeRow> ReadNoticeRows()
    {
        string notices = ReadRepoFile("NOTICES.md");
        string[] lines = notices.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        int headingIndex = Array.FindIndex(lines, l => l.Trim() == RuntimePackagesHeading);
        headingIndex.Should().BeGreaterThanOrEqualTo(0, $"NOTICES.md must contain a '{RuntimePackagesHeading}' section");

        var rows = new List<NoticeRow>();
        bool insideTable = false;
        for (int i = headingIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                if (insideTable) break;
                continue;
            }

            if (!line.StartsWith('|'))
            {
                if (insideTable) break;
                continue;
            }

            insideTable = true;
            string[] cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 4)
            {
                continue;
            }

            if (cells[0] == "Component family" || cells[0].All(c => c is '-' or ':'))
            {
                continue; // header or separator
            }

            rows.Add(new NoticeRow(cells[0], cells[1], cells[2], cells[3]));
        }

        rows.Should().NotBeEmpty("the 'Resolved runtime packages' table in NOTICES.md must have rows");
        return rows;
    }

    private static string ReadRepoFile(params string[] relativeParts)
    {
        string path = Path.Combine(new[] { FindRepoRoot() }.Concat(relativeParts).ToArray());
        File.Exists(path).Should().BeTrue($"{string.Join("/", relativeParts)} must exist");
        return File.ReadAllText(path);
    }

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
}
