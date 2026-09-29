using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SptModManager.Core;

namespace SptModManager.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        var settings = main.Services.Settings;
        GamePath = settings.GamePath ?? string.Empty;
        ForgeBaseUrl = settings.ForgeBaseUrl;
        ReleaseRepository = settings.ReleaseRepository;
        CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
    }

    [ObservableProperty]
    public partial string GamePath { get; set; }

    [ObservableProperty]
    public partial string? DetectedText { get; set; }

    [ObservableProperty]
    public partial string ForgeBaseUrl { get; set; }

    [ObservableProperty]
    public partial string ReleaseRepository { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdatesOnStartup { get; set; }

    public string SettingsFolder => AppSettings.AppDataDirectory;

    public string AppVersion => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";

    partial void OnCheckForUpdatesOnStartupChanged(bool value)
    {
        _main.Services.Settings.CheckForUpdatesOnStartup = value;
        _main.Services.Settings.Save();
    }

    public void SyncFromInstall()
    {
        var install = _main.Installation;
        if (install is null)
        {
            DetectedText = null;
            return;
        }

        GamePath = install.RootPath;
        DetectedText = $"Found SPT {install.SptVersion ?? "(unknown version)"} using the {install.DataFolderName} folder"
                       + (install.HasGameExecutable ? "." : ". EscapeFromTarkov.exe was not found next to it, double check this is the game folder.");
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _main.Services.Dialogs.PickFolderAsync("Pick your SPT game folder");
        if (path is not null)
        {
            GamePath = path;
            await UseFolderAsync();
        }
    }

    [RelayCommand]
    private async Task UseFolderAsync()
    {
        if (await _main.OpenInstallAsync(GamePath))
        {
            _main.SelectedNav = _main.NavItems[1];
        }
    }

    [RelayCommand]
    private async Task SaveEndpointsAsync()
    {
        if (!Uri.TryCreate(ForgeBaseUrl, UriKind.Absolute, out _))
        {
            await _main.Services.Dialogs.ShowMessageAsync("Invalid URL", "The Forge URL must be a full address like https://sp-mod.com.");
            return;
        }

        if (ReleaseRepository.Count(c => c == '/') != 1)
        {
            await _main.Services.Dialogs.ShowMessageAsync("Invalid repository", "Use the GitHub owner/name form, like SP-Tushonka/build.");
            return;
        }

        var settings = _main.Services.Settings;
        settings.ForgeBaseUrl = ForgeBaseUrl.Trim();
        settings.ReleaseRepository = ReleaseRepository.Trim();
        settings.Save();

        _main.Services.ApplyEndpointSettings();
        _main.RebuildManager();
        _main.Log.Success("Settings saved.");
    }

    [RelayCommand]
    private void ResetEndpoints()
    {
        ForgeBaseUrl = Core.Forge.ForgeClient.DefaultBaseUrl;
        ReleaseRepository = Core.Spt.SptReleaseClient.DefaultRepository;
    }

    [RelayCommand]
    private Task OpenSettingsFolderAsync() => _main.Services.Dialogs.OpenFolderAsync(SettingsFolder);
}
