using SptModManager.Core.Forge;
using SptModManager.Core.Mods;
using SptModManager.Core.Tests.Support;

namespace SptModManager.Core.Tests;

public class ModManagerTests
{
    private static ModManager CreateManager(TestInstall test, FakeForge forge, FakeDownloader? downloader = null, RecordingLog? log = null) =>
        new(test.Detect(), forge, downloader ?? new FakeDownloader(), log ?? new RecordingLog(), Path.Combine(test.Root, "..", "dl-" + Path.GetFileName(test.Root)));

    private static void SeedHandInstalledMods(TestInstall test)
    {
        test.CopyFixture("FakeClientPlugin.dll", "BepInEx/plugins/ClientMod/ClientMod.dll");
        test.WriteFile("BepInEx/plugins/ClientMod/settings.json", "{}");
        test.CopyFixture("FakeServerMod.dll", "SPT_Runtime/user/mods/ServerMod/ServerMod.dll");
        test.WriteFile("SPT_Runtime/user/mods/ServerMod/config/config.json", "{}");
        test.CopyFixture("FakeClientPlugin.dll", "BepInEx/plugins/spt/spt-core.dll");
        test.WriteFile("BepInEx/plugins/Mystery.dll", "native-ish");
    }

    private static ForgeMod Mod(int id, string guid, string name, params (int Id, string Version)[] versions) => new()
    {
        Id = id,
        Guid = guid,
        Name = name,
        Slug = name.ToLowerInvariant().Replace(' ', '-'),
        DetailUrl = $"https://sp-mod.com/mods/{id}",
        Versions = versions.Select(v => new ForgeVersionSummary { Id = v.Id, Version = v.Version, SptVersionConstraint = "~4.1.0" }).ToList(),
    };

    private static PlanStep Step(int id, string name, string version, string link, bool dependency = false, params int[] dependsOn) =>
        new(PlanActionKind.Install, id, $"com.test.{id}", name, null, null, null, version, id * 10, link, null, dependency, dependsOn);

    [Fact]
    public void Scan_FindsClientAndServerModsButSkipsSptModules()
    {
        using var test = new TestInstall();
        SeedHandInstalledMods(test);

        var components = ModScanner.Scan(test.Detect());

        var client = Assert.Single(components, c => c.Guid == "com.test.client");
        Assert.Equal(ModComponentKind.ClientPlugin, client.Kind);
        Assert.Equal("1.2.3", client.Version);
        Assert.Equal(2, client.Files.Count);

        var server = Assert.Single(components, c => c.Guid == "com.test.server");
        Assert.Equal(ModComponentKind.ServerMod, server.Kind);
        Assert.Equal("2.0.1", server.Version);
        Assert.Equal("SPT_Runtime/user/mods/ServerMod", server.PrimaryPath);

        Assert.Single(components, c => c.Guid is null && c.Name == "Mystery");
        Assert.DoesNotContain(components, c => c.PrimaryPath.Contains("/spt/"));
    }

    [Fact]
    public async Task RefreshAsync_MatchesHandInstalledModsToForge()
    {
        using var test = new TestInstall();
        SeedHandInstalledMods(test);

        var forge = new FakeForge();
        forge.Mods.Add(Mod(10, "com.test.client", "Client Mod", (100, "1.2.3")));
        forge.Mods.Add(Mod(20, "com.test.server", "Server Mod", (200, "2.0.1")));

        var manager = CreateManager(test, forge);
        await manager.RefreshAsync(matchWithForge: true);

        Assert.Equal(3, manager.Mods.Count);

        var client = manager.FindByForgeId(10)!;
        Assert.Equal("Client Mod", client.Name);
        Assert.Equal(100, client.ForgeVersionId);
        Assert.Equal(InstallSource.Detected, client.Source);

        Assert.Equal(200, manager.FindByForgeId(20)!.ForgeVersionId);
        Assert.Contains(manager.Mods, m => m.ForgeModId is null && m.Name == "Mystery");

        // A second pass reuses the saved state instead of duplicating entries.
        var again = CreateManager(test, forge);
        await again.RefreshAsync(matchWithForge: true);
        Assert.Equal(3, again.Mods.Count);
    }

    [Fact]
    public async Task RefreshAsync_UsesFileTreeToGroupModPieces()
    {
        using var test = new TestInstall();
        SeedHandInstalledMods(test);

        var forge = new FakeForge();
        forge.Mods.Add(Mod(10, "com.test.client", "Combo Mod", (100, "1.2.3")));
        forge.FileTrees[(10, 100)] = new ForgeFileTree
        {
            Files = ["BepInEx/plugins/ClientMod/ClientMod.dll", "user/mods/ServerMod/ServerMod.dll", "user/mods/ServerMod/config/config.json"],
        };

        var manager = CreateManager(test, forge);
        await manager.RefreshAsync(matchWithForge: true);

        var combo = manager.FindByForgeId(10)!;
        Assert.Contains("com.test.server", combo.ComponentGuids);
        Assert.Contains("SPT_Runtime/user/mods/ServerMod/ServerMod.dll", combo.Files);
        Assert.Equal(2, manager.Mods.Count);
    }

    [Fact]
    public async Task PlanInstallAsync_OrdersDependenciesFirstAndFlagsProblems()
    {
        using var test = new TestInstall();
        var forge = new FakeForge();
        forge.DependencyTrees["1:1.0.0"] =
        [
            new ForgeDependencyNode
            {
                Id = 2, Guid = "com.dep.a", Name = "Dep A", Slug = "dep-a",
                LatestCompatibleVersion = new ForgeResolvedVersion { Id = 20, Version = "2.0.0", Link = "https://dl/a" },
                Dependencies =
                [
                    new ForgeDependencyNode { Id = 3, Guid = "com.dep.b", Name = "Dep B", Slug = "dep-b", LatestCompatibleVersion = new ForgeResolvedVersion { Id = 30, Version = "1.1.0", Link = "https://dl/b" } },
                ],
            },
            new ForgeDependencyNode { Id = 4, Guid = "com.dep.c", Name = "Dep C", Slug = "dep-c", LatestCompatibleVersion = null },
            new ForgeDependencyNode { Id = 5, Guid = "com.dep.d", Name = "Dep D", Slug = "dep-d", LatestCompatibleVersion = new ForgeResolvedVersion { Id = 50, Version = "1.0.0", Link = "https://dl/d" } },
        ];

        var store = new InstalledModsStore(test.Detect());
        store.Save(new InstalledModsState
        {
            Mods =
            [
                new InstalledMod { ForgeModId = 3, Guid = "com.dep.b", Name = "Dep B", Version = "1.0.0", Source = InstallSource.Manager, Files = ["x"] },
                new InstalledMod { ForgeModId = 5, Guid = "com.dep.d", Name = "Dep D", Version = "3.0.0", Source = InstallSource.Manager, Files = ["y"] },
            ],
        });

        var manager = CreateManager(test, forge);
        var plan = await manager.PlanInstallAsync([new ModInstallRequest(1, "Root", "1.0.0", "com.root", VersionId: 10, Link: "https://dl/root")]);

        Assert.Equal(["Dep B", "Dep A", "Dep D", "Root"], plan.Steps.Select(s => s.Name));
        Assert.Equal(PlanActionKind.Update, plan.Steps[0].Kind);
        Assert.Equal("1.0.0", plan.Steps[0].FromVersion);
        Assert.Equal(PlanActionKind.Install, plan.Steps[1].Kind);
        Assert.True(plan.Steps[1].IsDependency);
        Assert.Equal([3], plan.Steps[1].DependsOn);
        Assert.Equal(PlanActionKind.Keep, plan.Steps[2].Kind);
        Assert.Equal([2, 4, 5], plan.Steps[3].DependsOn);
        Assert.Contains(plan.Problems, p => p.Contains("Dep C"));
        Assert.Contains(plan.Warnings, w => w.Contains("newer 3.0.0"));
    }

    [Fact]
    public async Task InstallThenUpdate_ReplacesFilesAndKeepsUserConfigs()
    {
        using var test = new TestInstall();
        var forge = new FakeForge();
        var downloader = new FakeDownloader();
        downloader.Files["https://dl/v1"] = test.CreateZip("root-1.0.0.zip",
            ("Root-1.0.0/BepInEx/plugins/Root/Root.dll", "v1"),
            ("Root-1.0.0/BepInEx/plugins/Root/Old.dll", "old"),
            ("Root-1.0.0/README.md", "docs"));
        downloader.Files["https://dl/v2"] = test.CreateZip("root-1.1.0.zip",
            ("BepInEx/plugins/Root/Root.dll", "v2"));

        var manager = CreateManager(test, forge, downloader);

        var install = new InstallPlan();
        install.Steps.Add(Step(1, "Root", "1.0.0", "https://dl/v1"));
        await manager.ExecutePlanAsync(install);

        var mod = manager.FindByForgeId(1)!;
        Assert.Equal(InstallSource.Manager, mod.Source);
        Assert.Equal(["BepInEx/plugins/Root/Root.dll", "BepInEx/plugins/Root/Old.dll"], mod.Files);
        Assert.False(File.Exists(test.PathOf("README.md")));

        // The mod writes its own config at runtime; it must survive updates.
        test.WriteFile("BepInEx/plugins/Root/Root.cfg", "user settings");
        await manager.RefreshAsync(matchWithForge: false);
        Assert.DoesNotContain("BepInEx/plugins/Root/Root.cfg", manager.FindByForgeId(1)!.Files);

        var update = new InstallPlan();
        update.Steps.Add(Step(1, "Root", "1.1.0", "https://dl/v2") with { Kind = PlanActionKind.Update });
        await manager.ExecutePlanAsync(update);

        Assert.Equal("v2", File.ReadAllText(test.PathOf("BepInEx/plugins/Root/Root.dll")));
        Assert.False(File.Exists(test.PathOf("BepInEx/plugins/Root/Old.dll")));
        Assert.Equal("user settings", File.ReadAllText(test.PathOf("BepInEx/plugins/Root/Root.cfg")));
        Assert.Equal("1.1.0", manager.FindByForgeId(1)!.Version);
        Assert.Single(manager.Mods);
    }

    [Fact]
    public async Task Install_HandlesSolid7zArchives()
    {
        using var test = new TestInstall();
        var downloader = new FakeDownloader { Files = { ["https://dl/solid"] = TestInstall.TestData("solid-mod.7z") } };
        var manager = CreateManager(test, new FakeForge(), downloader);

        var plan = new InstallPlan();
        plan.Steps.Add(Step(7, "My Mod", "1.0.0", "https://dl/solid"));
        await manager.ExecutePlanAsync(plan);

        Assert.Equal("client-dll-bytes", File.ReadAllText(test.PathOf("BepInEx/plugins/MyMod/MyMod.dll")));
        Assert.Equal("server-dll-bytes", File.ReadAllText(test.PathOf("SPT_Runtime/user/mods/MyMod/MyMod.dll")));
        Assert.True(File.Exists(test.PathOf("SPT_Runtime/user/mods/MyMod/config/config.json")));
        Assert.Equal(3, manager.FindByForgeId(7)!.Files.Count);
    }

    [Fact]
    public async Task Install_ReplacesDetectedEntryForSameMod()
    {
        using var test = new TestInstall();
        SeedHandInstalledMods(test);

        var forge = new FakeForge();
        forge.Mods.Add(Mod(10, "com.test.client", "Client Mod", (100, "1.2.3")));

        var downloader = new FakeDownloader();
        downloader.Files["https://dl/client"] = test.CreateZip("client.zip", ("BepInEx/plugins/ClientMod/ClientMod.dll", "@FakeClientPlugin.dll"));

        var manager = CreateManager(test, forge, downloader);
        await manager.RefreshAsync(matchWithForge: true);

        var plan = new InstallPlan();
        plan.Steps.Add(new PlanStep(PlanActionKind.Update, 10, "com.test.client", "Client Mod", null, null, "1.2.3", "1.3.0", 101, "https://dl/client", null, false, []));
        await manager.ExecutePlanAsync(plan);

        var mod = Assert.Single(manager.Mods, m => m.ForgeModId == 10);
        Assert.Equal(InstallSource.Manager, mod.Source);
        Assert.Equal("1.3.0", mod.Version);

        // The hand-installed settings file is not a DLL, so it is left alone.
        Assert.True(File.Exists(test.PathOf("BepInEx/plugins/ClientMod/settings.json")));
    }

    [Fact]
    public async Task Uninstall_RemovesFilesAndReportsDependencies()
    {
        using var test = new TestInstall();
        var downloader = new FakeDownloader();
        downloader.Files["https://dl/lib"] = test.CreateZip("lib.zip", ("BepInEx/plugins/Lib/Lib.dll", "lib"));
        downloader.Files["https://dl/app"] = test.CreateZip("app.zip", ("SPT_Runtime/user/mods/App/App.dll", "app"), ("BepInEx/plugins/App.dll", "app"));

        var manager = CreateManager(test, new FakeForge(), downloader);
        var plan = new InstallPlan();
        plan.Steps.Add(Step(2, "Lib", "1.0.0", "https://dl/lib", dependency: true));
        plan.Steps.Add(Step(1, "App", "1.0.0", "https://dl/app", false, 2));
        await manager.ExecutePlanAsync(plan);

        var lib = manager.FindByForgeId(2)!;
        Assert.True(lib.InstalledAsDependency);
        Assert.Equal("App", Assert.Single(manager.GetDependents(lib)).Name);

        var result = manager.Uninstall(manager.FindByForgeId(1)!);

        Assert.Empty(result.BrokenDependents);
        Assert.Equal("Lib", Assert.Single(result.OrphanedDependencies).Name);
        Assert.False(Directory.Exists(test.PathOf("SPT_Runtime/user/mods/App")));
        Assert.False(File.Exists(test.PathOf("BepInEx/plugins/App.dll")));
        Assert.True(Directory.Exists(test.PathOf("SPT_Runtime/user/mods")));
        Assert.True(Directory.Exists(test.PathOf("BepInEx/plugins")));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_MapsResultsAndFixesCosmeticVersions()
    {
        using var test = new TestInstall();
        var store = new InstalledModsStore(test.Detect());
        store.Save(new InstalledModsState
        {
            Mods =
            [
                new InstalledMod { ForgeModId = 10, Name = "Cosmetic", Version = "1.2", Files = ["a"] },
                new InstalledMod { ForgeModId = 20, Name = "Outdated", Version = "2.0.1", Files = ["b"] },
                new InstalledMod { Guid = "local.only", Name = "Local", Version = "1.0.0", Files = ["c"] },
                new InstalledMod { ForgeModId = 30, Name = "Mystery Version", Version = "0.0.1", Files = ["d"] },
            ],
        });

        var forge = new FakeForge();
        forge.Versions[10] = [new ForgeModVersion { Id = 100, Version = "1.2.0", SptVersionConstraint = "~4.1.0" }];
        forge.Versions[30] = [new ForgeModVersion { Id = 300, Version = "5.0.0", SptVersionConstraint = "~4.1.0", Link = "https://dl/30" }];
        forge.UpdateHandler = pairs =>
        {
            var check = new ForgeUpdateCheck { SptVersion = "4.1.6" };
            foreach (var pair in pairs)
            {
                switch (pair.ToString())
                {
                    case "20:2.0.1":
                        check.Updates.Add(new ForgeUpdate
                        {
                            CurrentVersion = new ForgeUpdateModRef { ModId = 20, Version = "2.0.1" },
                            RecommendedVersion = new ForgeResolvedVersion { Id = 201, Version = "2.1.0", Link = "https://dl/20" },
                        });
                        break;
                    case "10:1.2.0":
                        check.UpToDate.Add(new ForgeUpdateModRef { Id = 100, ModId = 10, Version = "1.2.0" });
                        break;
                }
            }

            return check;
        };

        var manager = CreateManager(test, forge);
        var results = await manager.CheckForUpdatesAsync();

        Assert.Equal(ModUpdateStatus.UpdateAvailable, results["forge:20"].Status);
        Assert.Equal("2.1.0", results["forge:20"].LatestVersion);
        Assert.Equal(ModUpdateStatus.UpToDate, results["forge:10"].Status);
        Assert.Equal("1.2.0", manager.FindByForgeId(10)!.Version);
        Assert.Equal(ModUpdateStatus.NotOnForge, results["guid:local.only"].Status);
        Assert.Equal(ModUpdateStatus.UnknownVersion, results["forge:30"].Status);
        Assert.Equal("5.0.0", results["forge:30"].LatestVersion);
    }
}
