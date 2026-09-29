using SptModManager.Core.Spt;
using SptModManager.Core.Tests.Support;

namespace SptModManager.Core.Tests;

public class SptInstallationTests
{
    [Fact]
    public void TryDetect_FindsSpt41InstallFromGameRoot()
    {
        using var test = new TestInstall();

        var install = SptInstallation.TryDetect(test.Root, out var error);

        Assert.NotNull(install);
        Assert.Null(error);
        Assert.Equal("SPT_Runtime", install.DataFolderName);
        Assert.Equal("4.1.6", install.SptVersion);
        Assert.Equal("4.1.6-RELEASE+731d7a2.20260918", install.SptBuildInfo);
        Assert.Equal("0.16.9.40743", install.CompatibleTarkovVersion);
        Assert.Equal("40743", install.ClientBuild);
        Assert.True(install.HasGameExecutable);
        Assert.Equal(Path.Combine(test.Root, "SPT_Runtime", "user", "mods"), install.ServerModsPath);
    }

    [Fact]
    public void TryDetect_AcceptsTheRuntimeFolderItself()
    {
        using var test = new TestInstall();

        var install = SptInstallation.TryDetect(Path.Combine(test.Root, "SPT_Runtime"), out _);

        Assert.NotNull(install);
        Assert.Equal(Path.GetFullPath(test.Root), install.RootPath);
    }

    [Fact]
    public void TryDetect_SupportsSpt40Layout()
    {
        using var test = new TestInstall(dataFolder: SptInstallation.RuntimeFolder40);

        var install = SptInstallation.TryDetect(test.Root, out _);

        Assert.NotNull(install);
        Assert.Equal("SPT", install.DataFolderName);
    }

    [Fact]
    public void TryDetect_ReportsMissingInstall()
    {
        var empty = Directory.CreateTempSubdirectory("sptmm-empty");
        try
        {
            Assert.Null(SptInstallation.TryDetect(empty.FullName, out var error));
            Assert.NotNull(error);
            Assert.Null(SptInstallation.TryDetect(Path.Combine(empty.FullName, "nope"), out _));
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("4.1.6-RELEASE+731d7a2.20260918", "4.1.6")]
    [InlineData("4.0.13+abc", "4.0.13")]
    [InlineData(null, null)]
    public void VersionFromInformational_StripsBuildMetadata(string? informational, string? expected)
    {
        Assert.Equal(expected, SptInstallation.VersionFromInformational(informational));
    }
}
