using SptModManager.Core.Versioning;

namespace SptModManager.Core.Tests;

public class SptCompatibilityTests
{
    [Theory]
    [InlineData("4.1.6", "4.1.4", true)]    // made for an earlier patch
    [InlineData("4.1.4", "4.1.4", true)]
    [InlineData("4.1.6", "~4.1.4", true)]
    [InlineData("4.1.6", "^4.1.0", true)]
    [InlineData("4.1.6", ">=4.1.0, <4.1.3", true)]
    [InlineData("4.1.3", "4.1.4", false)]   // made for a later patch
    [InlineData("4.1.6", "~4.0.0", false)]  // different minor
    [InlineData("4.1.6", "4.2.0", false)]
    [InlineData("4.1.6", "", false)]
    [InlineData(null, "4.1.4", false)]
    public void IsCompatible_AllowsEarlierPatchesOfSameMinor(string? installed, string constraint, bool expected)
    {
        Assert.Equal(expected, SptCompatibility.IsCompatible(installed, constraint));
    }

    [Fact]
    public void ForgeRangeConstraint_CoversMinorUpToInstalled()
    {
        Assert.Equal(">=4.1.0 <=4.1.6", SptCompatibility.ForgeRangeConstraint("4.1.6"));
        Assert.Null(SptCompatibility.ForgeRangeConstraint("nope"));
    }

    [Fact]
    public void EarlierPatches_ListsOlderPatchesNewestFirst()
    {
        var published = new[] { "4.0.13", "4.1.0", "4.1.2", "4.1.4", "4.1.5", "4.1.6", "4.1.7", "4.2.0", "4.1.3-BE" };

        Assert.Equal(["4.1.5", "4.1.4", "4.1.2", "4.1.0"], SptCompatibility.EarlierPatches("4.1.6", published));
        Assert.Empty(SptCompatibility.EarlierPatches("4.1.0", published));
    }

    [Theory]
    [InlineData("4.1.5", ">=4.1.0, <4.2.0", true)]
    [InlineData("4.1.5", "4.1.4 | 4.1.5", true)]
    [InlineData("4.1.6", "4.1.4 || 4.1.5", false)]
    public void Satisfies_UnderstandsComposerSeparators(string version, string constraint, bool expected)
    {
        Assert.Equal(expected, VersionUtil.Satisfies(version, constraint));
    }
}
