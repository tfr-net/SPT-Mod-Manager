using SptModManager.Core.Mods;
using SptModManager.Core.Tests.Support;

namespace SptModManager.Core.Tests;

public class AssemblyInspectorTests
{
    [Fact]
    public void ReadBepInPlugins_ReadsAttributeArguments()
    {
        var plugins = AssemblyInspector.ReadBepInPlugins(TestInstall.Fixture("FakeClientPlugin.dll"));

        var plugin = Assert.Single(plugins);
        Assert.Equal("com.test.client", plugin.Guid);
        Assert.Equal("Test Client Plugin", plugin.Name);
        Assert.Equal("1.2.3", plugin.Version);
    }

    [Fact]
    public void ReadServerModMetadata_ReadsPropertyInitializers()
    {
        var metadata = AssemblyInspector.ReadServerModMetadata(TestInstall.Fixture("FakeServerMod.dll"));

        Assert.NotNull(metadata);
        Assert.Equal("com.test.server", metadata.Guid);
        Assert.Equal("Test Server Mod", metadata.Name);
        Assert.Equal("Tester", metadata.Author);
        Assert.Equal("2.0.1", metadata.Version);
        Assert.Equal("~4.1.0", metadata.SptVersion);
    }

    [Fact]
    public void ReadServerModMetadata_ReadsExpressionBodiedGetters()
    {
        var metadata = AssemblyInspector.ReadServerModMetadata(TestInstall.Fixture("FakeServerModGetters.dll"));

        Assert.NotNull(metadata);
        Assert.Equal("com.test.getters", metadata.Guid);
        Assert.Equal("Getter Style Mod", metadata.Name);
        Assert.Equal("3.4.5", metadata.Version);
    }

    [Fact]
    public void ReadServerModMetadata_ReturnsNullForClientPlugin()
    {
        Assert.Null(AssemblyInspector.ReadServerModMetadata(TestInstall.Fixture("FakeClientPlugin.dll")));
        Assert.Empty(AssemblyInspector.ReadBepInPlugins(TestInstall.Fixture("FakeServerMod.dll")));
    }

    [Fact]
    public void ReadAssemblyInfo_ReadsInformationalVersion()
    {
        var info = AssemblyInspector.ReadAssemblyInfo(TestInstall.Fixture("SPTarkov.Server.Core.dll"));

        Assert.NotNull(info);
        Assert.Equal("SPTarkov.Server.Core", info.Name);
        Assert.Equal("4.1.6-RELEASE+731d7a2.20260918", info.InformationalVersion);
    }

    [Fact]
    public void Inspector_ToleratesNonAssemblies()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "definitely not a PE file");

        try
        {
            Assert.Null(AssemblyInspector.ReadAssemblyInfo(path));
            Assert.Empty(AssemblyInspector.ReadBepInPlugins(path));
            Assert.Null(AssemblyInspector.ReadServerModMetadata(path));
            Assert.Null(AssemblyInspector.ReadAssemblyInfo(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
