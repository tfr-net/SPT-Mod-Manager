using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SptModManager.Core.Spt;
using SptModManager.Core.Text;

namespace SptModManager.App.ViewModels;

public partial class SptViewModel(MainViewModel main) : ViewModelBase
{
    public const string InstallerUrl = "https://sp-mod.com/installer";

    [ObservableProperty]
    public partial string InstalledVersion { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string BuildInfo { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClientVersion { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string Location { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheck), nameof(StatusText), nameof(CanUpdate), nameof(IsUpToDate), nameof(ShowInstallerHint))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    public partial SptUpdateCheck? Check { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReleaseNotes))]
    public partial SptRelease? DisplayedRelease { get; set; }

    public string ReleaseNotes => MarkdownText.ToPlainText(DisplayedRelease?.Notes);

    public bool HasCheck => Check is not null;

    public string StatusText => Check?.Message ?? "Check GitHub for new SPT releases.";

    public bool CanUpdate => Check?.CanUpdate ?? false;

    public bool IsUpToDate => Check?.State == SptUpdateState.UpToDate;

    public bool ShowInstallerHint => Check is { State: SptUpdateState.NewMinorAvailable } or { ClientBuildMismatch: true };

    public void SyncFromInstall()
    {
        var install = main.Installation;
        InstalledVersion = install?.SptVersion ?? "Unknown";
        BuildInfo = install?.SptBuildInfo ?? string.Empty;
        ClientVersion = install?.CompatibleTarkovVersion ?? "Unknown";
        Location = install is null ? string.Empty : Path.Combine(install.RootPath, install.DataFolderName);
    }

    [RelayCommand]
    public Task CheckAsync() => CheckCoreAsync(showErrors: true);

    public async Task CheckCoreAsync(bool showErrors)
    {
        var install = main.Installation;
        if (install is null)
        {
            return;
        }

        var updater = CreateUpdater(install);
        SptUpdateCheck? check = null;
        var ok = await main.RunAsync("Checking GitHub for SPT releases...", async (_, ct) => check = await updater.CheckAsync(ct), showErrors);
        if (!ok || check is null)
        {
            return;
        }

        Check = check;
        DisplayedRelease = check.State == SptUpdateState.PatchAvailable ? check.LatestPatch : check.LatestOverall;
        main.Log.Info(check.Message);
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        var install = main.Installation;
        if (install is null || Check?.LatestPatch is not { } release)
        {
            return;
        }

        var confirmed = await main.Services.Dialogs.ConfirmAsync(
            $"Update to SPT {release.Version}",
            $"This downloads {release.FileName ?? "the release"} and extracts it over your install at\n{install.RootPath}\n\n" +
            "Your profiles are backed up first, and your mods and mod configs are left alone. " +
            "Close the SPT server, launcher and game before continuing.",
            "Update SPT");

        if (!confirmed)
        {
            return;
        }

        var updater = CreateUpdater(install);
        var ok = await main.RunAsync($"Updating SPT to {release.Version}...", (progress, ct) => updater.UpdateAsync(release, progress, ct));

        SyncFromInstall();
        main.RefreshInstallInfo();

        if (ok)
        {
            await CheckAsync();
            await main.Installed.CheckUpdatesAsync();
        }
    }

    [RelayCommand]
    private Task OpenReleaseAsync() =>
        DisplayedRelease is { } release ? main.Services.Dialogs.OpenUrlAsync(release.HtmlUrl) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenInstallerAsync() => main.Services.Dialogs.OpenUrlAsync(InstallerUrl);

    [RelayCommand]
    private Task OpenFolderAsync() =>
        main.Installation is { } install ? main.Services.Dialogs.OpenFolderAsync(install.RootPath) : Task.CompletedTask;

    private SptUpdater CreateUpdater(SptInstallation install) =>
        new(install, main.Services.Releases, main.Services.Downloader, main.Log, Core.AppSettings.DownloadDirectory);
}
