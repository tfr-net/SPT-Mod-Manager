using System.Text.Json;
using SptModManager.Core.Forge;
using SptModManager.Core.Spt;

namespace SptModManager.Core;

public sealed class AppSettings
{
    public string? GamePath { get; set; }

    public string ForgeBaseUrl { get; set; } = ForgeClient.DefaultBaseUrl;

    /// <summary>GitHub "owner/repo" that publishes SPT releases.</summary>
    public string ReleaseRepository { get; set; } = SptReleaseClient.DefaultRepository;

    public bool CheckForUpdatesOnStartup { get; set; } = true;

    public bool ShowOnlyCompatibleMods { get; set; } = true;

    /// <summary>Where settings live. SPTMM_DATA_DIR overrides it (used by tests and portable setups).</summary>
    public static string AppDataDirectory =>
        Environment.GetEnvironmentVariable("SPTMM_DATA_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "SptModManager");

    public static string DownloadDirectory => Path.Combine(Path.GetTempPath(), "SptModManager", "downloads");

    private static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to defaults; the file is rewritten on the next save.
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
