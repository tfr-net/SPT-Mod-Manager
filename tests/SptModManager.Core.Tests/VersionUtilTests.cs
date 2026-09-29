using SptModManager.Core.Versioning;

namespace SptModManager.Core.Tests;

public class VersionUtilTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1", "1.0.0")]
    [InlineData("1.2.3.0", "1.2.3")]
    [InlineData("01.02.03", "1.2.3")]
    [InlineData("1.2.3-beta.1", "1.2.3-beta.1")]
    [InlineData("4.1.6-RELEASE+abc", "4.1.6-RELEASE+abc")]
    [InlineData("1.2.3.4", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_ReshapesVersions(string? input, string? expected)
    {
        Assert.Equal(expected, VersionUtil.Normalize(input));
    }

    [Theory]
    [InlineData("1.2", "1.2.0", true)]
    [InlineData("1.2.0.0", "1.2.0", true)]
    [InlineData("1.2.1", "1.2.0", false)]
    public void AreEquivalent_IgnoresCosmeticDifferences(string left, string right, bool expected)
    {
        Assert.Equal(expected, VersionUtil.AreEquivalent(left, right));
    }

    [Theory]
    [InlineData("4.1.6", "~4.1.0", true)]
    [InlineData("4.2.0", "~4.1.0", false)]
    [InlineData("4.1.6", "^4.0.0", true)]
    [InlineData("4.1.6", ">=4.1.0 <4.2.0", true)]
    [InlineData("4.1.6", null, false)]
    [InlineData("4.1.6", "", false)]
    public void Satisfies_UsesNpmStyleRanges(string version, string? constraint, bool expected)
    {
        Assert.Equal(expected, VersionUtil.Satisfies(version, constraint));
    }

    [Fact]
    public void Compare_OrdersSemantically()
    {
        Assert.True(VersionUtil.Compare("1.10.0", "1.9.0") > 0);
        Assert.True(VersionUtil.Compare("1.0.0-beta", "1.0.0") < 0);
        Assert.Equal(0, VersionUtil.Compare("1.2", "1.2.0"));
    }

    [Fact]
    public void MajorMinor_TakesFirstTwoParts()
    {
        Assert.Equal("4.1", VersionUtil.MajorMinor("4.1.6"));
        Assert.Null(VersionUtil.MajorMinor("nope"));
    }
}
