using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SptModManager.App.ViewModels;
using SptModManager.App.Views;
using SptModManager.Core.Mods;

namespace SptModManager.App.Tests;

public class MainWindowTests
{
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }

    private static void Capture(MainWindow window, string name)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        foreach (var directory in new[] { Path.Combine(AppContext.BaseDirectory, "screenshots"), Environment.GetEnvironmentVariable("SPTMM_SCREENSHOT_DIR") })
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                frame.Save(Path.Combine(directory, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
    }

    private static async Task<(UiWorld World, MainViewModel Vm, MainWindow Window)> OpenAsync()
    {
        var world = new UiWorld();
        var vm = world.CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.True(await vm.OpenInstallAsync(world.Install.Root));
        await vm.Browse.SearchAsync();
        return (world, vm, window);
    }

    [AvaloniaFact]
    public async Task Walkthrough_RendersEveryPage()
    {
        var (world, vm, window) = await OpenAsync();
        using var _ = world;

        // Browse: select a mod with a dependency and let details, dependencies and thumbnails load.
        vm.SelectedNav = vm.NavItems[0];
        vm.Browse.SelectedMod = vm.Browse.Results.Single(r => r.Name == "Realistic Recoil");
        await WaitForAsync(() => vm.Browse.Details is { IsLoading: false, Dependencies.Count: > 0 });
        await WaitForAsync(() => vm.Browse.Results.Count(r => r.Thumbnail is not null) == 4);
        Assert.Equal(6, vm.Browse.Results.Count);

        // Mods without a picture get a colored tile with initials instead.
        var questTracker = vm.Browse.Results.Single(r => r.Name == "Quest Tracker");
        Assert.Null(questTracker.Thumbnail);
        Assert.Equal("QT", questTracker.Initials);

        // Tagged for SPT 4.1.4 only, which still works on the installed 4.1.6; 4.0 mods do not.
        Assert.True(vm.Browse.Results.Single(r => r.Name == "Loot Overhaul").IsCompatible);
        Assert.False(vm.Browse.Results.Single(r => r.Name == "Stash Expander").IsCompatible);
        Assert.Contains("Weapon Core Lib", vm.Browse.Details!.Dependencies[0].Text);
        Assert.True(vm.Browse.Results.Single(r => r.Name == "Better Bot Brains").IsInstalled);
        Capture(window, "browse.png");

        // My mods: scanned mods are matched to The Forge and one update is found.
        vm.SelectedNav = vm.NavItems[1];
        await vm.Installed.CheckUpdatesAsync();
        Assert.Equal(1, vm.Installed.UpdateCount);
        Assert.Contains(vm.Installed.Items, i => i.Name == "Better Bot Brains" && i.CanUpdate);
        Assert.Contains(vm.Installed.Items, i => i.Name == "Quest Tracker" && i.IsUpToDate);
        Assert.Contains(vm.Installed.Items, i => i.Name == "HelperLib" && i.IsUnrecognized);
        vm.Installed.Selected = vm.Installed.Items.First(i => i.Name == "Better Bot Brains");
        await WaitForAsync(() => vm.Installed.Items.Count(i => i.Thumbnail is not null) == 2);
        Capture(window, "my-mods.png");

        // SPT updates: a patch release for the same client build can be applied.
        vm.SelectedNav = vm.NavItems[2];
        await vm.Spt.CheckAsync();
        Assert.True(vm.Spt.CanUpdate);
        Capture(window, "spt-updates.png");

        vm.SelectedNav = vm.NavItems[3];
        Capture(window, "settings.png");

        vm.IsLogOpen = true;
        vm.SelectedNav = vm.NavItems[1];
        Capture(window, "activity-log.png");
    }

    [AvaloniaFact]
    public async Task UpdatingFromMyMods_InstallsNewVersion()
    {
        var (world, vm, _) = await OpenAsync();
        using var _world = world;

        await vm.Installed.CheckUpdatesAsync();
        var item = vm.Installed.Items.Single(i => i.Name == "Better Bot Brains");

        await vm.Installed.UpdateOneCommand.ExecuteAsync(item);

        Assert.True(File.Exists(world.Install.PathOf("BepInEx/plugins/BetterBots/BetterBots.Core.dll")));
        Assert.True(File.Exists(world.Install.PathOf("BepInEx/plugins/BetterBots/presets.json")));
        var updated = vm.Manager!.FindByForgeId(101)!;
        Assert.Equal("1.3.0", updated.Version);
        Assert.Equal(InstallSource.Manager, updated.Source);
        Assert.Equal(0, vm.Installed.UpdateCount);
        Assert.Contains(world.Dialogs.Shown, d => d.Message.Contains("Update Better Bot Brains 1.2.3 -> 1.3.0"));
    }

    [AvaloniaFact]
    public async Task InstallingFromBrowse_PullsInDependencies()
    {
        var (world, vm, _) = await OpenAsync();
        using var _world = world;

        vm.Browse.SelectedMod = vm.Browse.Results.Single(r => r.Name == "Realistic Recoil");
        await WaitForAsync(() => vm.Browse.Details is { IsLoading: false, SelectedVersion: not null });

        await vm.Browse.Details!.InstallCommand.ExecuteAsync(null);

        Assert.True(File.Exists(world.Install.PathOf("BepInEx/plugins/WeaponCore/WeaponCore.dll")));
        Assert.True(File.Exists(world.Install.PathOf("BepInEx/plugins/RealisticRecoil.dll")));
        Assert.True(File.Exists(world.Install.PathOf("SPT_Runtime/user/mods/RealisticRecoil/RealisticRecoil.dll")));
        Assert.True(vm.Manager!.FindByForgeId(105)!.InstalledAsDependency);
        Assert.Equal("Installed 2.2.0", vm.Browse.Details.Card.InstalledText);
        Assert.Contains(world.Dialogs.Shown, d => d.Message.Contains("Install Weapon Core Lib 1.4.0 (dependency)"));
    }
}
