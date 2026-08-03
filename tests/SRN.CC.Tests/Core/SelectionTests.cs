using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Core.Identity;
using SRN.CC.Core.Selection;

namespace SRN.CC.Tests.Core;

[TestFixture]
public class SelectionTests
{
    [Test]
    public void SelectionDefaults_DefaultTrue_UnsetIdentitiesSelected()
    {
        SelectionState state = SelectionState.IncludeAll();
        AssetIdentity id1 = new AssetIdentity("res1", 2000);
        AssetIdentity id2 = new AssetIdentity("res2", 2000);

        state.DefaultSelected.Should().BeTrue();
        state.IsSelected(id1).Should().BeTrue();
        state.IsSelected(id2).Should().BeTrue();
        state.Overrides.Should().BeEmpty();
    }

    [Test]
    public void SelectionOverrides_SingleIdentityToggle_AppliesSparseOverride()
    {
        SelectionState state = SelectionState.IncludeAll();
        AssetIdentity id1 = new AssetIdentity("res1", 2000);
        AssetIdentity id2 = new AssetIdentity("res2", 2000);

        SelectionState updated = state.SetOverride(id1, false);

        updated.IsSelected(id1).Should().BeFalse();
        updated.IsSelected(id2).Should().BeTrue();
        updated.Overrides.Should().HaveCount(1);
    }

    [Test]
    public void SelectionCompaction_SettingOverrideToMatchingDefault_RemovesOverride()
    {
        SelectionState state = SelectionState.IncludeAll();
        AssetIdentity id1 = new AssetIdentity("res1", 2000);

        SelectionState toggledOff = state.SetOverride(id1, false);
        toggledOff.Overrides.Should().HaveCount(1);

        SelectionState toggledBack = toggledOff.SetOverride(id1, true);
        toggledBack.Overrides.Should().BeEmpty("Redundant override matching DefaultSelected true should be compacted");
    }

    [Test]
    public void IncludeAll_ClearsAllOverrides_SetsDefaultTrue()
    {
        SelectionState state = new SelectionState(false)
            .SetOverride(new AssetIdentity("a", 1000), true);

        SelectionState includeAll = SelectionState.IncludeAll();
        includeAll.DefaultSelected.Should().BeTrue();
        includeAll.Overrides.Should().BeEmpty();
    }

    [Test]
    public void ExcludeAll_ClearsAllOverrides_SetsDefaultFalse()
    {
        SelectionState state = new SelectionState(true)
            .SetOverride(new AssetIdentity("a", 1000), false);

        SelectionState excludeAll = SelectionState.ExcludeAll();
        excludeAll.DefaultSelected.Should().BeFalse();
        excludeAll.Overrides.Should().BeEmpty();
    }
}
