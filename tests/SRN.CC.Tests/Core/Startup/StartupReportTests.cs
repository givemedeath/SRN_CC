using NUnit.Framework;
using FluentAssertions;
using SRN.CC.Core.Diagnostics;
using SRN.CC.Core.Startup;

namespace SRN.CC.Tests.Core.Startup;

[TestFixture]
public class StartupReportTests
{
    private static StartupCheckResult Result(StartupCheckSeverity severity, string checkId = "check")
        => new(checkId, severity, $"{checkId} reported {severity}", Array.Empty<string>());

    [Test]
    public void Worst_EmptyResults_ReturnsOk()
    {
        var report = new StartupReport(Array.Empty<StartupCheckResult>());

        report.Worst.Should().Be(StartupCheckSeverity.Ok);
    }

    [Test]
    public void HasBlocking_EmptyResults_ReturnsFalse()
    {
        var report = new StartupReport(Array.Empty<StartupCheckResult>());

        report.HasBlocking.Should().BeFalse();
    }

    [Test]
    public void Worst_AllOk_ReturnsOk()
    {
        var report = new StartupReport([
            Result(StartupCheckSeverity.Ok, "a"),
            Result(StartupCheckSeverity.Ok, "b")
        ]);

        report.Worst.Should().Be(StartupCheckSeverity.Ok);
        report.HasBlocking.Should().BeFalse();
    }

    [Test]
    public void Worst_OkAndDegraded_ReturnsDegraded()
    {
        var report = new StartupReport([
            Result(StartupCheckSeverity.Ok, "a"),
            Result(StartupCheckSeverity.Degraded, "b"),
            Result(StartupCheckSeverity.Ok, "c")
        ]);

        report.Worst.Should().Be(StartupCheckSeverity.Degraded);
    }

    [Test]
    public void Worst_ContainsBlocking_ReturnsBlockingRegardlessOfOrder()
    {
        var blockingFirst = new StartupReport([
            Result(StartupCheckSeverity.Blocking, "a"),
            Result(StartupCheckSeverity.Degraded, "b"),
            Result(StartupCheckSeverity.Ok, "c")
        ]);
        var blockingLast = new StartupReport([
            Result(StartupCheckSeverity.Ok, "a"),
            Result(StartupCheckSeverity.Degraded, "b"),
            Result(StartupCheckSeverity.Blocking, "c")
        ]);

        blockingFirst.Worst.Should().Be(StartupCheckSeverity.Blocking);
        blockingLast.Worst.Should().Be(StartupCheckSeverity.Blocking);
    }

    [Test]
    public void Worst_SingleResult_ReturnsThatSeverity()
    {
        foreach (StartupCheckSeverity severity in Enum.GetValues<StartupCheckSeverity>())
        {
            new StartupReport([Result(severity)]).Worst.Should().Be(severity);
        }
    }

    [Test]
    public void HasBlocking_NoBlockingResult_ReturnsFalse()
    {
        var report = new StartupReport([
            Result(StartupCheckSeverity.Ok, "a"),
            Result(StartupCheckSeverity.Degraded, "b"),
            Result(StartupCheckSeverity.Degraded, "c")
        ]);

        report.HasBlocking.Should().BeFalse();
    }

    [Test]
    public void HasBlocking_AtLeastOneBlockingResult_ReturnsTrue()
    {
        var single = new StartupReport([Result(StartupCheckSeverity.Blocking)]);
        var mixed = new StartupReport([
            Result(StartupCheckSeverity.Ok, "a"),
            Result(StartupCheckSeverity.Blocking, "b")
        ]);

        single.HasBlocking.Should().BeTrue();
        mixed.HasBlocking.Should().BeTrue();
    }

    [Test]
    public void HasBlocking_AlwaysAgreesWithWorst()
    {
        StartupCheckSeverity[][] combinations =
        [
            [],
            [StartupCheckSeverity.Ok],
            [StartupCheckSeverity.Degraded],
            [StartupCheckSeverity.Blocking],
            [StartupCheckSeverity.Ok, StartupCheckSeverity.Degraded],
            [StartupCheckSeverity.Degraded, StartupCheckSeverity.Blocking],
            [StartupCheckSeverity.Blocking, StartupCheckSeverity.Ok, StartupCheckSeverity.Degraded]
        ];

        foreach (StartupCheckSeverity[] combination in combinations)
        {
            var report = new StartupReport(combination.Select(s => Result(s)).ToArray());

            report.HasBlocking.Should().Be(report.Worst == StartupCheckSeverity.Blocking);
        }
    }

    [Test]
    public void StartupCheckSeverity_OrdinalsAreLeastToMostSevere()
    {
        // Worst relies on the numeric ordering of the enum.
        ((int)StartupCheckSeverity.Ok).Should().BeLessThan((int)StartupCheckSeverity.Degraded);
        ((int)StartupCheckSeverity.Degraded).Should().BeLessThan((int)StartupCheckSeverity.Blocking);
    }

    [Test]
    public void StartupCheckResult_CodeIsOptionalAndDefaultsToNull()
    {
        var withoutCode = new StartupCheckResult("cache", StartupCheckSeverity.Ok, "fine", Array.Empty<string>());
        var withCode = new StartupCheckResult(
            "cache",
            StartupCheckSeverity.Degraded,
            "quarantined",
            ["cache-v1.sqlite moved aside"],
            DiagnosticCode.CorruptedCacheQuarantined);

        withoutCode.Code.Should().BeNull();
        withCode.Code.Should().Be(DiagnosticCode.CorruptedCacheQuarantined);
        withCode.Details.Should().ContainSingle();
    }
}
