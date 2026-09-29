using System.Net;
using SptModManager.App.Services;
using SptModManager.App.ViewModels;
using SptModManager.Core;
using SptModManager.Core.Forge;
using SptModManager.Core.Spt;
using SptModManager.Core.Tests.Support;

namespace SptModManager.App.Tests;

/// <summary>
/// A fake SPT install plus fake Forge/GitHub backends with fictional mods, wired into the real view models.
/// </summary>
public sealed class UiWorld : IDisposable
{
    public UiWorld()
    {
        DataDirectory = Directory.CreateTempSubdirectory("sptmm-ui").FullName;
        Environment.SetEnvironmentVariable("SPTMM_DATA_DIR", DataDirectory);

        Install = new TestInstall();
        Install.CopyFixture("FakeClientPlugin.dll", "BepInEx/plugins/BetterBots/BetterBots.dll");
        Install.WriteFile("BepInEx/plugins/BetterBots/presets.json", "{}");
        Install.CopyFixture("FakeServerMod.dll", "SPT_Runtime/user/mods/LootOverhaul/LootOverhaul.dll");
        Install.WriteFile("SPT_Runtime/user/mods/LootOverhaul/config/config.json", "{}");
        Install.CopyFixture("FakeServerModGetters.dll", "SPT_Runtime/user/mods/QuestTracker/QuestTracker.dll");
        Install.CopyFixture("FakeClientPlugin.dll", "BepInEx/plugins/spt/spt-core.dll");
        Install.WriteFile("BepInEx/plugins/HelperLib.dll", "native helper");

        SeedForge();

        Downloader.Files["https://dl.test/better-bots-1.3.0.zip"] = Install.CreateZip("better-bots-1.3.0.zip",
            ("BepInEx/plugins/BetterBots/BetterBots.dll", "@FakeClientPlugin.dll"),
            ("BepInEx/plugins/BetterBots/BetterBots.Core.dll", "new helper"));
        Downloader.Files["https://dl.test/recoil-2.2.0.zip"] = Install.CreateZip("recoil-2.2.0.zip",
            ("Realistic Recoil/BepInEx/plugins/RealisticRecoil.dll", "recoil"),
            ("Realistic Recoil/SPT_Runtime/user/mods/RealisticRecoil/RealisticRecoil.dll", "recoil server"));
        Downloader.Files["https://dl.test/weapon-core-1.4.0.zip"] = Install.CreateZip("weapon-core-1.4.0.zip",
            ("BepInEx/plugins/WeaponCore/WeaponCore.dll", "core"));

        Http = new HttpClient(new StubHttpHandler(request =>
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", name);
            return File.Exists(path)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
    }

    public string DataDirectory { get; }

    public TestInstall Install { get; }

    public FakeForge Forge { get; } = new();

    public FakeDownloader Downloader { get; } = new();

    public RecordingDialogs Dialogs { get; } = new();

    public HttpClient Http { get; }

    public MainViewModel CreateViewModel()
    {
        var settings = new AppSettings { CheckForUpdatesOnStartup = false };
        var services = new AppServices(settings, Dialogs, Forge, new StubReleases(), Downloader, Http);
        return new MainViewModel(services);
    }

    private void SeedForge()
    {
        Forge.Mods.AddRange(
        [
            Mod(101, "com.test.client", "Better Bot Brains", "NeuralFox", "Smarter, sneakier AI that flanks, peeks and actually uses cover.", 2_450_000, "Bots", true, true, "thumb1.png",
                ("1.3.0", 1013, "~4.1.0"), ("1.2.3", 1012, "~4.1.0"), ("1.1.0", 1011, "~4.0.0")),
            Mod(102, "com.test.server", "Loot Overhaul", "CrateKeeper", "Rebalanced loot tables and container spawns across every map.", 890_000, "Loot", false, true, "thumb2.png",
                ("2.0.1", 1021, "4.1.4")),
            Mod(103, "com.test.getters", "Quest Tracker", "TaskMaster", "Pin quest objectives and see what items you still need in raid.", 1_120_000, "Quality of Life", true, false, null,
                ("3.4.5", 1031, "~4.1.0")),
            Mod(104, "com.test.recoil", "Realistic Recoil", "Muzzle", "Weapon handling that feels heavier and more deliberate.", 640_000, "Weapons", false, true, "thumb4.png",
                ("2.2.0", 1042, "~4.1.0"), ("2.1.0", 1041, "~4.0.0")),
            Mod(105, "com.test.weaponcore", "Weapon Core Lib", "Muzzle", "Shared library used by several weapon mods.", 1_600_000, "Libraries", false, true, "thumb5.png",
                ("1.4.0", 1051, "~4.1.0")),
            Mod(106, "com.test.stash", "Stash Expander", "HoarderHQ", "Bigger stash sizes for every edition.", 310_000, "Quality of Life", false, false, null,
                ("1.0.2", 1061, "~4.0.0")),
        ]);

        Forge.DependencyTrees["104:2.2.0"] =
        [
            new ForgeDependencyNode
            {
                Id = 105, Guid = "com.test.weaponcore", Name = "Weapon Core Lib", Slug = "weapon-core-lib",
                LatestCompatibleVersion = new ForgeResolvedVersion { Id = 1051, Version = "1.4.0", Link = "https://dl.test/weapon-core-1.4.0.zip", ContentLength = 48_000 },
            },
        ];

        Forge.Versions[104] = [Version("2.2.0", 1042, "~4.1.0", "https://dl.test/recoil-2.2.0.zip")];

        Forge.UpdateHandler = (pairs, spt) =>
        {
            var check = new ForgeUpdateCheck { SptVersion = spt };
            foreach (var pair in pairs)
            {
                var modId = int.Parse(pair.Identifier);
                var mod = Forge.Mods.First(m => m.Id == modId);

                if (modId == 101 && pair.Version == "1.2.3")
                {
                    check.Updates.Add(new ForgeUpdate
                    {
                        CurrentVersion = new ForgeUpdateModRef { ModId = 101, Name = mod.Name, Version = "1.2.3" },
                        RecommendedVersion = new ForgeResolvedVersion { Id = 1013, Version = "1.3.0", Link = "https://dl.test/better-bots-1.3.0.zip", ContentLength = 215_000 },
                    });
                }
                else
                {
                    check.UpToDate.Add(new ForgeUpdateModRef { ModId = modId, Name = mod.Name, Version = pair.Version });
                }
            }

            return check;
        };
    }

    private ForgeMod Mod(int id, string guid, string name, string author, string teaser, long downloads, string category, bool featured, bool fika, string? thumbnail,
        params (string Version, int Id, string Constraint)[] versions)
    {
        Forge.Versions[id] = versions.Select(v => Version(v.Version, v.Id, v.Constraint, $"https://dl.test/{id}-{v.Version}.zip")).ToList();

        return new ForgeMod
        {
            Id = id,
            Guid = guid,
            Name = name,
            Slug = name.ToLowerInvariant().Replace(' ', '-'),
            Teaser = teaser,
            Description = $"<p>{teaser}</p><h3>Features</h3><ul><li>Configurable in game</li><li>Works with existing saves</li></ul><p>Report issues on the mod page.</p>",
            Downloads = downloads,
            Owner = new ForgeUser { Id = id, Name = author },
            Category = new ForgeCategory { Id = id, Name = category, Slug = category.ToLowerInvariant() },
            Featured = featured,
            FikaCompatibility = fika,
            // The Forge sends an empty string for mods without a picture.
            Thumbnail = thumbnail is null ? string.Empty : $"https://img.test/{thumbnail}",
            DetailUrl = $"https://sp-mod.com/mods/{id}",
            License = new ForgeLicense { Name = "MIT" },
            Versions = versions.Select(v => new ForgeVersionSummary { Id = v.Id, Version = v.Version, SptVersionConstraint = v.Constraint }).ToList(),
        };
    }

    private static ForgeModVersion Version(string version, int id, string constraint, string link) => new()
    {
        Id = id,
        Version = version,
        SptVersionConstraint = constraint,
        Link = link,
        Downloads = 12_000 + id,
        ContentLength = 120_000,
        FikaCompatibility = "compatible",
        PublishedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
    };

    public void Dispose()
    {
        Install.Dispose();
        Environment.SetEnvironmentVariable("SPTMM_DATA_DIR", null);
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class StubReleases : ISptReleaseClient
    {
        public Task<IReadOnlyList<SptRelease>> GetReleasesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SptRelease>>(
            [
                new SptRelease("4.1.7", "SPT 4.1.7 (40743)", "4.1.7", new DateTimeOffset(2026, 9, 26, 15, 0, 0, TimeSpan.Zero),
                    "https://github.com/SP-Tushonka/build/releases/tag/4.1.7",
                    "https://mirror.sp-tushonka.com/builds/SPT-4.1.7-40743-1a2b3c4.7z", "SPT-4.1.7-40743-1a2b3c4.7z",
                    "q1w2e3r4t5y6u7i8o9p0aA==", "40743", "0.16.9.5.40743",
                    "#### Requires EFT `0.16.9.5.40743`\n\n### FIXED\n* Server - Fixed a rare crash when loading insurance returns\n* Launcher - Fixed the profile list not refreshing\n\n### Modding\n* All mods made for 4.1.X should keep working."),
                new SptRelease("4.1.6", "SPT 4.1.6 (40743)", "4.1.6", new DateTimeOffset(2026, 9, 18, 15, 45, 0, TimeSpan.Zero),
                    "https://github.com/SP-Tushonka/build/releases/tag/4.1.6", null, null, null, "40743", null, "Previous release"),
            ]);
    }
}

public sealed class RecordingDialogs : IDialogService
{
    public List<(string Title, string Message)> Shown { get; } = [];

    public bool ConfirmResult { get; set; } = true;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool danger = false)
    {
        Shown.Add((title, message));
        return Task.FromResult(ConfirmResult);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Shown.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<Stream?> OpenFileAsync(string title, string patternName, string pattern) => Task.FromResult<Stream?>(null);

    public Task<Stream?> SaveFileAsync(string title, string suggestedName, string patternName, string pattern) => Task.FromResult<Stream?>(null);

    public Task OpenUrlAsync(string url) => Task.CompletedTask;

    public Task OpenFolderAsync(string path) => Task.CompletedTask;
}
