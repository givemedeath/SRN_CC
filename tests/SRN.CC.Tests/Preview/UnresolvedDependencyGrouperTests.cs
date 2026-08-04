using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Services;
using SRN.CC.Preview;

namespace SRN.CC.Tests.Preview;

[TestFixture]
public class UnresolvedDependencyGrouperTests
{
    private StubRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        _registry = new StubRegistry();
    }

    [Test]
    public void GroupUnresolved_WithEmptyUnresolved_ReturnsEmptyList()
    {
        // Arrange
        var unresolved = new Dictionary<AssetIdentity, string>();

        // Act
        var groups = UnresolvedDependencyGrouper.GroupUnresolved(unresolved, _registry);

        // Assert
        Assert.That(groups, Is.Empty);
    }

    [Test]
    public void GroupUnresolved_WithSingleFamily_CreatesOneGroup()
    {
        // Arrange
        var mdlType = (ushort)2002;
        var id1 = new AssetIdentity("model_01", mdlType);
        var id2 = new AssetIdentity("model_02", mdlType);

        var unresolved = new Dictionary<AssetIdentity, string>
        {
            { id1, "Occurrence not found in workspace" },
            { id2, "Occurrence not found in workspace" }
        };

        // Act
        var groups = UnresolvedDependencyGrouper.GroupUnresolved(unresolved, _registry);

        // Assert
        Assert.That(groups, Has.Count.EqualTo(1));
        Assert.That(groups[0].Family, Is.EqualTo("mdl"));
        Assert.That(groups[0].Count, Is.EqualTo(2));
    }

    [Test]
    public void GroupUnresolved_WithMultipleFamilies_CreateMultipleGroups()
    {
        // Arrange
        var mdlType = (ushort)2002;
        var mtrType = (ushort)2072;

        var unresolved = new Dictionary<AssetIdentity, string>
        {
            { new AssetIdentity("model_01", mdlType), "Not found" },
            { new AssetIdentity("material_01", mtrType), "Not found" }
        };

        // Act
        var groups = UnresolvedDependencyGrouper.GroupUnresolved(unresolved, _registry);

        // Assert
        Assert.That(groups, Has.Count.EqualTo(2));

        // Verify families are sorted alphabetically
        var families = groups.Select(g => g.Family).ToList();
        Assert.That(families, Is.Ordered);
    }

    [Test]
    public void GroupUnresolved_GroupsReasonsByType()
    {
        // Arrange
        var mdlType = (ushort)2002;
        var id1 = new AssetIdentity("model_01", mdlType);
        var id2 = new AssetIdentity("model_02", mdlType);
        var id3 = new AssetIdentity("model_03", mdlType);

        var unresolved = new Dictionary<AssetIdentity, string>
        {
            { id1, "Occurrence not found in workspace" },
            { id2, "Occurrence not found in workspace" },
            { id3, "Cyclic dependency detected" }
        };

        // Act
        var groups = UnresolvedDependencyGrouper.GroupUnresolved(unresolved, _registry);

        // Assert
        Assert.That(groups, Has.Count.EqualTo(1));
        var group = groups[0];
        Assert.That(group.ReasonGroups, Has.Count.EqualTo(2));
        Assert.That(group.ReasonGroups.ContainsKey("Occurrence not found in workspace"), Is.True);
        Assert.That(group.ReasonGroups.ContainsKey("Cyclic dependency detected"), Is.True);

        // Verify counts
        Assert.That(group.ReasonGroups["Occurrence not found in workspace"].Count, Is.EqualTo(2));
        Assert.That(group.ReasonGroups["Cyclic dependency detected"].Count, Is.EqualTo(1));
    }

    [Test]
    public void GroupUnresolved_DeterministicOrdering()
    {
        // Arrange
        var unresolved = new Dictionary<AssetIdentity, string>
        {
            { new AssetIdentity("tex_01", 3), "Not found" },    // Unsupported type
            { new AssetIdentity("model_01", 2002), "Not found" },
            { new AssetIdentity("material_01", 2072), "Not found" }
        };

        // Act - call multiple times
        var groups1 = UnresolvedDependencyGrouper.GroupUnresolved(unresolved, _registry);
        var groups2 = UnresolvedDependencyGrouper.GroupUnresolved(unresolved, _registry);

        // Assert - results should be identical
        Assert.That(groups1.Count, Is.EqualTo(groups2.Count));
        for (int i = 0; i < groups1.Count; i++)
        {
            Assert.That(groups1[i].Family, Is.EqualTo(groups2[i].Family));
            Assert.That(groups1[i].Count, Is.EqualTo(groups2[i].Count));
        }
    }

    private sealed class StubRegistry : IResourceTypeRegistry
    {
        public bool TryGetType(string extension, out ushort typeId)
        {
            typeId = extension.ToLowerInvariant() switch
            {
                "mdl" => 2002,
                "mtr" => 2072,
                "txi" => 2022,
                "tga" => 3,
                "dds" => 2033,
                "plt" => 6,
                "wok" => 2016,
                "pwk" => 2029,
                "dwk" => 2030,
                "set" => 2013,
                _ => 0
            };
            return typeId != 0;
        }

        public bool TryGetExtension(ushort typeId, out string extension)
        {
            extension = typeId switch
            {
                2002 => "mdl",
                2072 => "mtr",
                2022 => "txi",
                3 => "tga",
                2033 => "dds",
                6 => "plt",
                2016 => "wok",
                2029 => "pwk",
                2030 => "dwk",
                2013 => "set",
                _ => ""
            };
            return !string.IsNullOrEmpty(extension);
        }
    }
}
