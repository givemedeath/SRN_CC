using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Schema;

namespace SRN.CC.Tests.Core.Schema;

[TestFixture]
public class SchemaVersionsTests
{
    [Test]
    public void Constants_AsShipped_MatchThePerStoreLiteralsTheyReplace()
    {
        SchemaVersions.Project.Should().Be(1, "ProjectStore.CurrentSchemaVersion is 1");
        SchemaVersions.Settings.Should().Be(1, "SettingsStore.CurrentSchemaVersion is 1");
        SchemaVersions.Cache.Should().Be(1, "the cache database file is cache-v1.sqlite");
        SchemaVersions.Manifest.Should().Be("1.0", "ProvenanceManifestGenerator writes SchemaVersion \"1.0\"");
    }

    [TestCase(SchemaKind.Project, 1)]
    [TestCase(SchemaKind.Settings, 1)]
    [TestCase(SchemaKind.Cache, 1)]
    public void Current_IntegerVersionedKind_ReturnsTheShippedVersion(SchemaKind kind, int expected)
    {
        SchemaVersions.Current(kind).Should().Be(expected);
    }

    [Test]
    public void Current_ManifestKind_ThrowsAndPointsAtTheStringConstant()
    {
        Action act = () => SchemaVersions.Current(SchemaKind.Manifest);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.Message.Should().Contain("Manifest").And.Contain("1.0");
    }

    [Test]
    public void Current_UndefinedKind_Throws()
    {
        Action act = () => SchemaVersions.Current((SchemaKind)999);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.Message.Should().Contain("Unknown");
    }

    // Truth table: versions 0, 1 and 2 across all four SchemaKind values.
    // Kinds Project, Settings and Cache are integer-versioned at 1.
    [TestCase(SchemaKind.Project, 0, SchemaOpenMode.Unsupported)]
    [TestCase(SchemaKind.Project, 1, SchemaOpenMode.Current)]
    [TestCase(SchemaKind.Project, 2, SchemaOpenMode.ReadOnlyNewer)]
    [TestCase(SchemaKind.Settings, 0, SchemaOpenMode.Unsupported)]
    [TestCase(SchemaKind.Settings, 1, SchemaOpenMode.Current)]
    [TestCase(SchemaKind.Settings, 2, SchemaOpenMode.ReadOnlyNewer)]
    [TestCase(SchemaKind.Cache, 0, SchemaOpenMode.Unsupported)]
    [TestCase(SchemaKind.Cache, 1, SchemaOpenMode.Current)]
    [TestCase(SchemaKind.Cache, 2, SchemaOpenMode.ReadOnlyNewer)]
    public void Classify_IntegerVersionedKind_MatchesTheTruthTable(
        SchemaKind kind,
        int fileVersion,
        SchemaOpenMode expected)
    {
        SchemaVersions.Classify(kind, fileVersion).Should().Be(expected);
    }

    // The fourth kind completes the table by rejecting every integer: the manifest is string-versioned.
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Classify_ManifestKind_ThrowsForEveryIntegerVersion(int fileVersion)
    {
        Action act = () => SchemaVersions.Classify(SchemaKind.Manifest, fileVersion);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.Message.Should().Contain("Manifest").And.Contain("1.0");
    }

    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void Classify_NegativeVersion_IsUnsupported(int fileVersion)
    {
        SchemaVersions.Classify(SchemaKind.Project, fileVersion).Should().Be(SchemaOpenMode.Unsupported);
    }

    [Test]
    public void Classify_ProjectKind_AgreesWithProjectStoreBehaviour()
    {
        // ProjectStore.LoadAsync throws for schemaVersion < 1 and sets isReadOnly for
        // schemaVersion > CurrentSchemaVersion. The registry must not contradict it.
        SchemaVersions.Classify(SchemaKind.Project, 0).Should().Be(SchemaOpenMode.Unsupported);
        SchemaVersions.Classify(SchemaKind.Project, SchemaVersions.Project).Should().Be(SchemaOpenMode.Current);
        SchemaVersions.Classify(SchemaKind.Project, SchemaVersions.Project + 1).Should().Be(SchemaOpenMode.ReadOnlyNewer);
    }
}
