using SptModManager.Core.Mods;

namespace SptModManager.Core.Tests;

public class ArchiveLayoutTests
{
    [Fact]
    public void Plan_KeepsForgeStandardLayout()
    {
        var result = ArchiveLayout.Plan(
            ["BepInEx/plugins/MyMod/MyMod.dll", "SPT_Runtime/user/mods/MyMod/MyMod.dll"],
            "SPT_Runtime");

        Assert.True(result.Success);
        Assert.Equal("BepInEx/plugins/MyMod/MyMod.dll", result.Map["BepInEx/plugins/MyMod/MyMod.dll"]);
        Assert.Equal("SPT_Runtime/user/mods/MyMod/MyMod.dll", result.Map["SPT_Runtime/user/mods/MyMod/MyMod.dll"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Plan_StripsWrapperFolderAndSkipsDocs()
    {
        var result = ArchiveLayout.Plan(
            ["MyMod-1.2.0/BepInEx/plugins/MyMod.dll", "MyMod-1.2.0/README.md", @"MyMod-1.2.0\SPT_Runtime\user\mods\MyMod\MyMod.dll"],
            "SPT_Runtime");

        Assert.True(result.Success);
        Assert.Equal("BepInEx/plugins/MyMod.dll", result.Map["MyMod-1.2.0/BepInEx/plugins/MyMod.dll"]);
        Assert.Equal("SPT_Runtime/user/mods/MyMod/MyMod.dll", result.Map[@"MyMod-1.2.0\SPT_Runtime\user\mods\MyMod\MyMod.dll"]);
        Assert.Contains("MyMod-1.2.0/README.md", result.Skipped);
    }

    [Fact]
    public void Plan_RemapsOtherRuntimeFolderWithWarning()
    {
        var result = ArchiveLayout.Plan(["SPT/user/mods/Old/Old.dll"], "SPT_Runtime");

        Assert.Equal("SPT_Runtime/user/mods/Old/Old.dll", result.Map["SPT/user/mods/Old/Old.dll"]);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Plan_MapsRootLevelUserModsIntoRuntimeFolder()
    {
        var result = ArchiveLayout.Plan(["user/mods/Legacy/package.json", "User/Mods/Legacy/src/mod.js"], "SPT");

        Assert.Equal("SPT/user/mods/Legacy/package.json", result.Map["user/mods/Legacy/package.json"]);
        Assert.Equal("SPT/user/mods/Legacy/src/mod.js", result.Map["User/Mods/Legacy/src/mod.js"]);
    }

    [Fact]
    public void Plan_CanonicalizesBepInExCasing()
    {
        var result = ArchiveLayout.Plan(["bepinex/Plugins/Thing/Thing.dll"], "SPT_Runtime");

        Assert.Equal("BepInEx/plugins/Thing/Thing.dll", result.Map["bepinex/Plugins/Thing/Thing.dll"]);
    }

    [Fact]
    public void Plan_TreatsWrapperNamedSptAsWrapper()
    {
        var result = ArchiveLayout.Plan(["SPT/BepInEx/plugins/x.dll"], "SPT_Runtime");

        Assert.Equal("BepInEx/plugins/x.dll", result.Map["SPT/BepInEx/plugins/x.dll"]);
    }

    [Fact]
    public void Plan_FallsBackToPluginsForBareDlls()
    {
        var result = ArchiveLayout.Plan(["Thing.dll", "Thing.pdb", "readme.txt"], "SPT_Runtime");

        Assert.True(result.Success);
        Assert.Equal("BepInEx/plugins/Thing.dll", result.Map["Thing.dll"]);
        Assert.Contains(result.Warnings, w => w.Contains("no folder structure"));
    }

    [Fact]
    public void Plan_SkipsUnsafeAndStrayFiles()
    {
        var result = ArchiveLayout.Plan(["../../evil.dll", "BepInEx/plugins/ok.dll", "installer.exe"], "SPT_Runtime");

        Assert.True(result.Success);
        Assert.Single(result.Map);
        Assert.Contains("../../evil.dll", result.Skipped);
        Assert.Contains("installer.exe", result.Skipped);
    }

    [Fact]
    public void Plan_FailsForUnrecognizedLayout()
    {
        var result = ArchiveLayout.Plan(["SomeFolder/config.json", "SomeFolder/mod.js"], "SPT_Runtime");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void FindWrapperPrefix_PicksMostCommonPrefix()
    {
        var prefix = ArchiveLayout.FindWrapperPrefix(
        [
            ["Wrap", "BepInEx", "plugins", "a.dll"],
            ["Wrap", "SPT_Runtime", "user", "mods", "a", "a.dll"],
            ["Other", "BepInEx", "plugins", "b.dll"],
        ]);

        Assert.Equal(["Wrap"], prefix);
    }
}
