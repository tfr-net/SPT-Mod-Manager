using SptModManager.Core.Forge;
using SptModManager.Core.Mods;
using SptModManager.Core.Tests.Support;

namespace SptModManager.Core.Tests;

public class ModListServiceTests
{
    [Fact]
    public async Task CreateAndLoad_RoundTripsAndSkipsDependencies()
    {
        var list = ModListService.Create(
        [
            new InstalledMod { ForgeModId = 1, Guid = "com.a", Name = "Alpha", Version = "1.0.0" },
            new InstalledMod { ForgeModId = 2, Guid = "com.b", Name = "Beta", Version = "2.0.0", InstalledAsDependency = true },
            new InstalledMod { Name = "Mystery.dll" },
            new InstalledMod { Guid = "com.side.dlc", Name = "Side DLC bundled with Alpha", Version = "1.0.0" },
        ], "4.1.6", "My Setup");

        using var stream = new MemoryStream();
        await ModListService.SaveAsync(list, stream);
        stream.Position = 0;
        var loaded = await ModListService.LoadAsync(stream);

        Assert.Equal("My Setup", loaded.Name);
        Assert.Equal("4.1.6", loaded.SptVersion);
        Assert.Equal("Alpha", Assert.Single(loaded.Mods).Name);
    }

    [Fact]
    public async Task LoadAsync_RejectsOtherJson()
    {
        using var stream = new MemoryStream("""{ "hello": "world" }"""u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => ModListService.LoadAsync(stream));
    }

    [Fact]
    public async Task ResolveAsync_PicksListedOrNewestCompatibleVersions()
    {
        using var test = new TestInstall();
        new InstalledModsStore(test.Detect()).Save(new InstalledModsState
        {
            Mods = [new InstalledMod { ForgeModId = 3, Guid = "com.c", Name = "Gamma", Version = "3.0.0", Files = ["x"] }],
        });

        var forge = new FakeForge();
        forge.Mods.Add(new ForgeMod { Id = 4, Guid = "com.d", Name = "Delta" });
        forge.Versions[1] = [new ForgeModVersion { Id = 11, Version = "1.0.0", SptVersionConstraint = "~4.1.0", Link = "https://dl/1" }];
        forge.Versions[2] =
        [
            new ForgeModVersion { Id = 22, Version = "2.5.0", SptVersionConstraint = "~4.1.0", Link = "https://dl/2" },
            new ForgeModVersion { Id = 21, Version = "2.0.0", SptVersionConstraint = "~4.0.0", Link = "https://dl/2old" },
        ];
        forge.Versions[4] = [new ForgeModVersion { Id = 41, Version = "4.0.0", SptVersionConstraint = "~4.1.0", Link = "https://dl/4" }];
        forge.Versions[5] = [new ForgeModVersion { Id = 51, Version = "1.0.0", SptVersionConstraint = "~3.11.0" }];

        var list = new ModListFile
        {
            SptVersion = "4.1.5",
            Mods =
            [
                new ModListEntry { ForgeModId = 1, Name = "Alpha", Version = "1.0.0" },
                new ModListEntry { ForgeModId = 2, Name = "Beta", Version = "2.0.0" },
                new ModListEntry { ForgeModId = 3, Guid = "com.c", Name = "Gamma", Version = "3.0.0" },
                new ModListEntry { Guid = "com.d", Name = "Delta", Version = "4.0.0" },
                new ModListEntry { ForgeModId = 5, Name = "Ancient", Version = "1.0.0" },
                new ModListEntry { Guid = "com.nowhere", Name = "Nowhere" },
            ],
        };

        var manager = new ModManager(test.Detect(), forge, new FakeDownloader(), new RecordingLog(), test.Root);
        var result = await ModListService.ResolveAsync(list, manager, forge, "4.1.6");

        Assert.Equal(["Alpha 1.0.0", "Beta 2.5.0", "Delta 4.0.0"], result.Requests.Select(r => $"{r.Name} {r.Version}"));
        Assert.Contains(result.Notes, n => n.Contains("Beta") && n.Contains("2.5.0"));
        Assert.Contains(result.Notes, n => n.Contains("Ancient"));
        Assert.Contains(result.Notes, n => n.Contains("Nowhere"));
        Assert.Equal("https://dl/2", result.Requests[1].Link);
    }
}
