using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Schema;

namespace SRN.CC.Tests.Core.Schema;

[TestFixture]
public class SchemaMigrationPipelineTests
{
    private static JsonObject NewDocument()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["name"] = "sample",
            ["unknownFutureField"] = new JsonObject { ["keep"] = true },
            ["sources"] = new JsonArray { "a", "b" }
        };
    }

    private static IReadOnlyList<string> Markers(JsonObject document)
    {
        JsonArray? markers = document["markers"] as JsonArray;
        return markers is null
            ? Array.Empty<string>()
            : markers.Select(node => node!.GetValue<string>()).ToArray();
    }

    [Test]
    public void MigrationCount_AsShipped_IsZero()
    {
        SchemaMigrationPipeline pipeline = new(SchemaKind.Project, Array.Empty<IJsonSchemaMigration>());

        pipeline.MigrationCount.Should().Be(0, "the seam ships with no speculative migrations");
        pipeline.Kind.Should().Be(SchemaKind.Project);
        pipeline.TargetVersion.Should().Be(SchemaVersions.Project);
    }

    // Exit criterion 1: a 1 -> 2 -> 3 chain applies both migrations in order.
    [Test]
    public void TryUpgrade_ChainOfTwoSteps_AppliesBothInAscendingOrder()
    {
        // Registered out of order on purpose: the pipeline, not the caller, establishes the order.
        SchemaMigrationPipeline pipeline = new(
            SchemaKind.Project,
            new IJsonSchemaMigration[] { new FakeV2ToV3Migration(), new FakeV1ToV2Migration() });

        JsonObject document = NewDocument();
        string before = document.ToJsonString();

        bool ok = pipeline.TryUpgrade(
            document,
            fromVersion: 1,
            targetVersion: 3,
            out JsonObject upgraded,
            out IReadOnlyList<string> applied,
            out string? error);

        ok.Should().BeTrue(error ?? string.Empty);
        error.Should().BeNull();
        applied.Should().Equal("Project 1->2", "Project 2->3");
        Markers(upgraded).Should().Equal("1->2", "2->3");
        document.ToJsonString().Should().Be(before, "the caller's document is never touched");
    }

    // Exit criterion 2: a gap fails loudly instead of skipping a version.
    [Test]
    public void TryUpgrade_TargetThreeWithOnlyOneToTwoRegistered_FailsNamingTheMissingStep()
    {
        SchemaMigrationPipeline pipeline = new(
            SchemaKind.Project,
            new IJsonSchemaMigration[] { new FakeV1ToV2Migration() });

        JsonObject document = NewDocument();
        string before = document.ToJsonString();

        bool ok = pipeline.TryUpgrade(
            document,
            fromVersion: 1,
            targetVersion: 3,
            out JsonObject upgraded,
            out IReadOnlyList<string> applied,
            out string? error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
        error.Should().Contain("from schema version 2 to 3", "the error must name the missing step");
        error.Should().Contain("Project");
        applied.Should().BeEmpty("nothing is applied unless the whole chain resolves");
        Markers(upgraded).Should().BeEmpty("the resolvable 1 -> 2 step must not run either");
        document.ToJsonString().Should().Be(before);
    }

    // Exit criterion 3: an identity upgrade does not mutate the input, by reference and by text.
    [Test]
    public void TryUpgrade_IdentityWithNoMigrations_DoesNotMutateOrAliasTheInput()
    {
        SchemaMigrationPipeline pipeline = new(SchemaKind.Project, Array.Empty<IJsonSchemaMigration>());

        JsonObject document = NewDocument();
        string before = document.ToJsonString();

        bool ok = pipeline.TryUpgrade(
            document,
            fromVersion: SchemaVersions.Project,
            out JsonObject upgraded,
            out IReadOnlyList<string> applied,
            out string? error);

        ok.Should().BeTrue(error ?? string.Empty);
        error.Should().BeNull();
        applied.Should().BeEmpty("1 -> 1 is an identity no-op");

        // By reference: the result is a detached copy, so a later mutation of it cannot reach
        // ProjectPreferences.RawRootNode, which is how unknown fields survive a round trip.
        upgraded.Should().NotBeSameAs(document);

        // By serialized text: the copy is faithful and the input is byte-for-byte unchanged.
        upgraded.ToJsonString().Should().Be(before);
        document.ToJsonString().Should().Be(before);

        upgraded["addedLater"] = "mutation";
        document.ToJsonString().Should().Be(before, "mutating the result must not reach the input");
    }

    [Test]
    public void TryUpgrade_IdentityWithMigrationsRegistered_StillAppliesNothing()
    {
        SchemaMigrationPipeline pipeline = new(
            SchemaKind.Project,
            new IJsonSchemaMigration[] { new FakeV1ToV2Migration() });

        JsonObject document = NewDocument();

        bool ok = pipeline.TryUpgrade(
            document,
            fromVersion: 2,
            targetVersion: 2,
            out JsonObject upgraded,
            out IReadOnlyList<string> applied,
            out string? error);

        ok.Should().BeTrue(error ?? string.Empty);
        applied.Should().BeEmpty();
        Markers(upgraded).Should().BeEmpty();
    }

    [Test]
    public void TryUpgrade_ShippedDefaultPipeline_UpgradesVersionOneToCurrent()
    {
        SchemaMigrationPipeline pipeline = new(SchemaKind.Settings, Array.Empty<IJsonSchemaMigration>());

        bool ok = pipeline.TryUpgrade(
            NewDocument(),
            fromVersion: 1,
            out JsonObject upgraded,
            out IReadOnlyList<string> applied,
            out string? error);

        ok.Should().BeTrue(error ?? string.Empty);
        applied.Should().BeEmpty();
        upgraded.Should().NotBeNull();
    }

    [TestCase(0)]
    [TestCase(-3)]
    public void TryUpgrade_VersionBelowOne_FailsAsUnsupported(int fromVersion)
    {
        SchemaMigrationPipeline pipeline = new(SchemaKind.Project, Array.Empty<IJsonSchemaMigration>());

        bool ok = pipeline.TryUpgrade(
            NewDocument(),
            fromVersion,
            out _,
            out IReadOnlyList<string> applied,
            out string? error);

        ok.Should().BeFalse();
        error.Should().Contain("below the first released version 1");
        applied.Should().BeEmpty();
    }

    [Test]
    public void TryUpgrade_VersionNewerThanTarget_FailsWithoutMigrating()
    {
        SchemaMigrationPipeline pipeline = new(SchemaKind.Project, Array.Empty<IJsonSchemaMigration>());

        bool ok = pipeline.TryUpgrade(
            NewDocument(),
            fromVersion: SchemaVersions.Project + 1,
            out _,
            out IReadOnlyList<string> applied,
            out string? error);

        ok.Should().BeFalse();
        error.Should().Contain("newer than the target version");
        applied.Should().BeEmpty();
    }

    [Test]
    public void TryUpgrade_StepThatOvershootsTheTarget_FailsRatherThanApplying()
    {
        SchemaMigrationPipeline pipeline = new(
            SchemaKind.Project,
            new IJsonSchemaMigration[] { new FakeStepMigration(SchemaKind.Project, 1, 5) });

        bool ok = pipeline.TryUpgrade(
            NewDocument(),
            fromVersion: 1,
            targetVersion: 3,
            out JsonObject upgraded,
            out IReadOnlyList<string> applied,
            out string? error);

        ok.Should().BeFalse();
        error.Should().Contain("overshooting the target version 3");
        applied.Should().BeEmpty();
        Markers(upgraded).Should().BeEmpty();
    }

    [Test]
    public void TryUpgrade_MigrationReturningNoDocument_FailsWithAnExplicitError()
    {
        SchemaMigrationPipeline pipeline = new(
            SchemaKind.Project,
            new IJsonSchemaMigration[] { new FakeNullReturningMigration() });

        JsonObject document = NewDocument();

        bool ok = pipeline.TryUpgrade(
            document,
            fromVersion: 1,
            targetVersion: 2,
            out JsonObject upgraded,
            out IReadOnlyList<string> applied,
            out string? error);

        ok.Should().BeFalse();
        error.Should().Contain("returned no document");
        applied.Should().BeEmpty();
        upgraded.Should().BeSameAs(document);
    }

    [Test]
    public void TryUpgrade_NullDocument_Throws()
    {
        SchemaMigrationPipeline pipeline = new(SchemaKind.Project, Array.Empty<IJsonSchemaMigration>());

        Action act = () => pipeline.TryUpgrade(null!, 1, out _, out _, out _);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void TryUpgrade_TargetVersionBelowOne_Throws()
    {
        SchemaMigrationPipeline pipeline = new(SchemaKind.Project, Array.Empty<IJsonSchemaMigration>());

        Action act = () => pipeline.TryUpgrade(NewDocument(), 1, 0, out _, out _, out _);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Constructor_ManifestKind_ThrowsBecauseTheManifestHasNoIntegerVersion()
    {
        Action act = () => _ = new SchemaMigrationPipeline(SchemaKind.Manifest, Array.Empty<IJsonSchemaMigration>());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Constructor_NullMigrationSequence_Throws()
    {
        Action act = () => _ = new SchemaMigrationPipeline(SchemaKind.Project, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_MigrationForAnotherKind_Throws()
    {
        Action act = () => _ = new SchemaMigrationPipeline(
            SchemaKind.Project,
            new IJsonSchemaMigration[] { new FakeV1ToV2Migration(SchemaKind.Settings) });

        act.Should().Throw<ArgumentException>().WithMessage("*Settings*");
    }

    [Test]
    public void Constructor_TwoMigrationsFromTheSameVersion_Throws()
    {
        Action act = () => _ = new SchemaMigrationPipeline(
            SchemaKind.Project,
            new IJsonSchemaMigration[]
            {
                new FakeV1ToV2Migration(),
                new FakeStepMigration(SchemaKind.Project, 1, 3)
            });

        act.Should().Throw<ArgumentException>().WithMessage("*both start at schema version 1*");
    }

    [TestCase(1, 1)]
    [TestCase(3, 2)]
    public void Constructor_MigrationThatDoesNotAdvance_Throws(int fromVersion, int toVersion)
    {
        Action act = () => _ = new SchemaMigrationPipeline(
            SchemaKind.Project,
            new IJsonSchemaMigration[] { new FakeStepMigration(SchemaKind.Project, fromVersion, toVersion) });

        act.Should().Throw<ArgumentException>().WithMessage("*does not advance*");
    }

    [Test]
    public void Constructor_MigrationStartingBelowVersionOne_Throws()
    {
        Action act = () => _ = new SchemaMigrationPipeline(
            SchemaKind.Project,
            new IJsonSchemaMigration[] { new FakeStepMigration(SchemaKind.Project, 0, 1) });

        act.Should().Throw<ArgumentException>().WithMessage("*first released schema version is 1*");
    }

    /// <summary>
    /// Test-only. Records its own step in a <c>markers</c> array so a test can prove the order steps
    /// ran in, not merely which ones ran.
    /// </summary>
    private abstract class FakeMarkerMigration : IJsonSchemaMigration
    {
        protected FakeMarkerMigration(SchemaKind kind)
        {
            Kind = kind;
        }

        public SchemaKind Kind { get; }

        public abstract int FromVersion { get; }

        public abstract int ToVersion { get; }

        public JsonObject Apply(JsonObject document)
        {
            if (document["markers"] is not JsonArray markers)
            {
                markers = new JsonArray();
                document["markers"] = markers;
            }

            markers.Add($"{FromVersion}->{ToVersion}");
            document["schemaVersion"] = ToVersion;
            return document;
        }
    }

    private sealed class FakeV1ToV2Migration : FakeMarkerMigration
    {
        public FakeV1ToV2Migration(SchemaKind kind = SchemaKind.Project)
            : base(kind)
        {
        }

        public override int FromVersion => 1;

        public override int ToVersion => 2;
    }

    private sealed class FakeV2ToV3Migration : FakeMarkerMigration
    {
        public FakeV2ToV3Migration(SchemaKind kind = SchemaKind.Project)
            : base(kind)
        {
        }

        public override int FromVersion => 2;

        public override int ToVersion => 3;
    }

    private sealed class FakeStepMigration : FakeMarkerMigration
    {
        public FakeStepMigration(SchemaKind kind, int fromVersion, int toVersion)
            : base(kind)
        {
            FromVersion = fromVersion;
            ToVersion = toVersion;
        }

        public override int FromVersion { get; }

        public override int ToVersion { get; }
    }

    private sealed class FakeNullReturningMigration : IJsonSchemaMigration
    {
        public SchemaKind Kind => SchemaKind.Project;

        public int FromVersion => 1;

        public int ToVersion => 2;

        public JsonObject Apply(JsonObject document) => null!;
    }
}
