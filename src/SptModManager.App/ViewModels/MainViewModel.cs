using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SptModManager.App.Services;
using SptModManager.Core;
using SptModManager.Core.Logging;
using SptModManager.Core.Mods;
using SptModManager.Core.Spt;

namespace SptModManager.App.ViewModels;

public sealed record NavItem(string Title, Geometry Icon, ViewModelBase Page);

public partial class MainViewModel : ViewModelBase
{
    private CancellationTokenSource? _operationCts;

    public MainViewModel(AppServices services)
    {
        Services = services;
        Browse = new BrowseViewModel(this);
        Installed = new InstalledViewModel(this);
        Spt = new SptViewModel(this);
        Settings = new SettingsViewModel(this);

        NavItems =
        [
            new NavItem("Browse mods", Icons.Browse, Browse),
            new NavItem("My mods", Icons.Installed, Installed),
            new NavItem("SPT updates", Icons.Spt, Spt),
            new NavItem("Settings", Icons.Settings, Settings),
        ];
        SelectedNav = NavItems[0];

        services.Log.EntryAdded += entry => LastLogMessage = entry.Message;
    }

    public AppServices Services { get; }

    public BrowseViewModel Browse { get; }

    public InstalledViewModel Installed { get; }

    public SptViewModel Spt { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    public ObservableCollection<LogEntry> LogEntries => Services.Log.Entries;

    public IActivityLog Log => Services.Log;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstall), nameof(InstallTitle), nameof(InstallSubtitle))]
    public partial SptInstallation? Installation { get; set; }

    [ObservableProperty]
    public partial ModManager? Manager { get; set; }

    [ObservableProperty]
    public partial NavItem? SelectedNav { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial string? LastLogMessage { get; set; } = "Ready.";

    [ObservableProperty]
    public partial bool IsLogOpen { get; set; }

    public bool HasInstall => Installation is not null;

    /// <summary>Forge thumbnail URLs by mod ID, shared by the Browse and My mods pages.</summary>
    public Dictionary<int, string?> KnownThumbnails { get; } = new();

    public void RememberThumbnail(Core.Forge.ForgeMod mod) => KnownThumbnails[mod.Id] = ResolveImageUrl(mod.Thumbnail);

    /// <summary>Makes a Forge image URL absolute (it normally already is); empty means the mod has no picture.</summary>
    public string? ResolveImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return absolute.AbsoluteUri;
        }

        return Uri.TryCreate(new Uri(Services.Settings.ForgeBaseUrl.TrimEnd('/') + "/"), url.TrimStart('/'), out var combined)
            ? combined.AbsoluteUri
            : null;
    }

    public string InstallTitle => Installation is null
        ? "No SPT folder"
        : Installation.SptVersion is { } version ? $"SPT {version}" : "SPT (unknown version)";

    public string InstallSubtitle => Installation?.RootPath ?? "Pick it in Settings";

    public string? SptVersion => Installation?.SptVersion;

    public async Task InitializeAsync()
    {
        Log.Info("Welcome to SPT Mod Manager.");

        await Task.Run(() => BundledTools.Install(Log));
        Log.Info(Core.IO.SevenZipTool.Locate() is { } sevenZip
            ? $"Using 7-Zip for fast .7z extraction ({sevenZip})."
            : "7-Zip was not found, so .7z archives use the slower built-in extractor.");

        if (!string.IsNullOrWhiteSpace(Services.Settings.GamePath))
        {
            await OpenInstallAsync(Services.Settings.GamePath, showErrors: false);
        }

        if (!HasInstall)
        {
            SelectedNav = NavItems[3];
            Log.Warn("Pick your SPT folder in Settings to get started.");
        }

        _ = Browse.LoadCategoriesAsync();
        await Browse.SearchAsync();

        if (HasInstall && Services.Settings.CheckForUpdatesOnStartup)
        {
            // Background checks at startup only log problems (e.g. offline) instead of popping dialogs.
            await Installed.CheckUpdatesCoreAsync(showErrors: false);
            await Spt.CheckCoreAsync(showErrors: false);
        }
    }

    /// <summary>Points the app at an SPT install and loads its mods.</summary>
    public async Task<bool> OpenInstallAsync(string path, bool showErrors = true)
    {
        var install = SptInstallation.TryDetect(path, out var error);
        if (install is null)
        {
            Log.Error(error ?? "That folder is not an SPT install.");
            if (showErrors)
            {
                await Services.Dialogs.ShowMessageAsync("Not an SPT folder", error ?? "That folder is not an SPT install.");
            }

            return false;
        }

        Installation = install;
        Manager = new ModManager(install, Services.Forge, Services.Downloader, Services.Log, AppSettings.DownloadDirectory);
        Services.Settings.GamePath = install.RootPath;
        Services.Settings.Save();

        OnPropertyChanged(nameof(SptVersion));
        Log.Info($"Using {InstallTitle} at {install.RootPath}.");
        if (install.SptVersion is null)
        {
            Log.Warn("Could not read the SPT version; dependency resolution and update checks need it.");
        }

        Settings.SyncFromInstall();
        Spt.SyncFromInstall();
        await Installed.ReloadAsync(matchWithForge: true);
        Browse.RefreshInstalledState();
        return true;
    }

    /// <summary>Refreshes the sidebar after the install changes on disk (e.g. an SPT update).</summary>
    public void RefreshInstallInfo()
    {
        OnPropertyChanged(nameof(InstallTitle));
        OnPropertyChanged(nameof(InstallSubtitle));
        OnPropertyChanged(nameof(SptVersion));
    }

    /// <summary>Recreates the manager after its API clients change.</summary>
    public void RebuildManager()
    {
        if (Installation is not null)
        {
            Manager = new ModManager(Installation, Services.Forge, Services.Downloader, Services.Log, AppSettings.DownloadDirectory);
        }
    }

    /// <summary>
    /// Runs a long operation with the status bar showing progress. Errors are logged and shown. Returns true on
    /// success.
    /// </summary>
    public async Task<bool> RunAsync(string message, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool showErrors = true)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        BusyMessage = message;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        _operationCts = new CancellationTokenSource();

        var progress = new Progress<OperationProgress>(p =>
        {
            BusyMessage = p.Message;
            if (p.Fraction is { } fraction)
            {
                IsProgressIndeterminate = false;
                ProgressValue = fraction * 100;
            }
            else
            {
                IsProgressIndeterminate = true;
            }
        });

        try
        {
            await Task.Run(() => work(progress, _operationCts.Token));
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Cancelled.");
            return false;
        }
        catch (PlanExecutionException e) when (showErrors)
        {
            // The individual failures were already logged as they happened.
            await Services.Dialogs.ShowMessageAsync("Some mods could not be installed", e.Message);
            return false;
        }
        catch (Exception e)
        {
            if (showErrors)
            {
                Log.Error(e.Message);
                await Services.Dialogs.ShowMessageAsync("Something went wrong", e.Message);
            }
            else
            {
                Log.Warn(e.Message);
            }

            return false;
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            _operationCts.Dispose();
            _operationCts = null;
        }
    }

    [RelayCommand]
    private void CancelOperation() => _operationCts?.Cancel();

    [RelayCommand]
    private void ToggleLog() => IsLogOpen = !IsLogOpen;

    [RelayCommand]
    private void ClearLog() => Services.Log.Entries.Clear();

    /// <summary>
    /// The shared install flow: resolve dependencies, show the plan, and install after the user confirms.
    /// </summary>
    public async Task InstallWithConfirmationAsync(IReadOnlyList<ModInstallRequest> requests, string title, IReadOnlyList<string>? notes = null)
    {
        var manager = Manager;
        if (manager is null)
        {
            await Services.Dialogs.ShowMessageAsync("No SPT folder", "Pick your SPT folder in Settings first.");
            return;
        }

        if (requests.Count == 0)
        {
            var text = notes is { Count: > 0 } ? string.Join('\n', notes) : "There is nothing to install.";
            await Services.Dialogs.ShowMessageAsync(title, text);
            return;
        }

        InstallPlan? plan = null;
        var planned = await RunAsync("Resolving dependencies...", async (_, ct) => plan = await manager.PlanInstallAsync(requests, ct));
        if (!planned || plan is null)
        {
            return;
        }

        if (!plan.HasWork)
        {
            await Services.Dialogs.ShowMessageAsync(title, "Everything is already installed and up to date.");
            return;
        }

        var confirmed = await Services.Dialogs.ConfirmAsync(
            title,
            DescribePlan(plan, notes),
            plan.Problems.Count > 0 ? "Install anyway" : "Install",
            danger: plan.Problems.Count > 0);

        if (!confirmed)
        {
            return;
        }

        await RunAsync("Installing...", (progress, ct) => manager.ExecutePlanAsync(plan, progress, ct));
        await Installed.ReloadAsync(matchWithForge: false);
        Browse.RefreshInstalledState();
    }

    internal static string DescribePlan(InstallPlan plan, IReadOnlyList<string>? notes)
    {
        var text = new StringBuilder();

        foreach (var step in plan.ActionableSteps)
        {
            text.Append("• ").AppendLine(step.Describe());
        }

        var kept = plan.Steps.Where(s => s.Kind == PlanActionKind.Keep).ToList();
        if (kept.Count > 0)
        {
            text.AppendLine().AppendLine("Already installed:");
            foreach (var step in kept)
            {
                text.Append("• ").Append(step.Name).Append(' ').AppendLine(step.FromVersion);
            }
        }

        if (plan.TotalDownloadBytes is { } bytes)
        {
            text.AppendLine().Append("Download size: ").AppendLine(Format.Bytes(bytes));
        }

        if (plan.Problems.Count > 0)
        {
            text.AppendLine().AppendLine("Problems:");
            foreach (var problem in plan.Problems)
            {
                text.Append("⚠ ").AppendLine(problem);
            }
        }

        foreach (var line in plan.Warnings.Concat(notes ?? []))
        {
            text.AppendLine().Append("Note: ").Append(line);
        }

        return text.ToString().TrimEnd();
    }
}
