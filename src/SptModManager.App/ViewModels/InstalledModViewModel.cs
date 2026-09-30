using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SptModManager.Core.Mods;

namespace SptModManager.App.ViewModels;

public partial class InstalledModViewModel(InstalledMod mod) : ViewModelBase
{
    public InstalledMod Mod { get; } = mod;

    public string Name => Mod.Name;

    public string Initials { get; } = Avatar.Initials(mod.Name);

    public IBrush PlaceholderBrush { get; } = Avatar.Brush(mod.Guid ?? mod.Name);

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    public string VersionText => Mod.Version is { } v ? $"v{v}" : "unknown";

    public string Details
    {
        get
        {
            var parts = new List<string>();
            parts.Add(Mod.Source == InstallSource.Manager ? "Installed by the manager" : "Found on disk");

            if (Mod.InstalledAsDependency)
            {
                parts.Add("dependency");
            }

            parts.Add($"{Mod.Files.Count} file{(Mod.Files.Count == 1 ? string.Empty : "s")}");

            if (Mod.Guid is { } guid)
            {
                parts.Add(guid);
            }

            return string.Join(" · ", parts);
        }
    }

    public IEnumerable<string> Files => Mod.Files.Take(300);

    public string FilesHeader => $"Files ({Mod.Files.Count})";

    /// <summary>Loose files with no GUID and no Forge match (often helper DLLs).</summary>
    public bool IsUnrecognized => Mod.ForgeModId is null && Mod.Guid is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanUpdate), nameof(IsUpToDate), nameof(IsWarning), nameof(IsMuted), nameof(IsDanger), nameof(StatusDetail))]
    public partial ModUpdateInfo Update { get; set; } = new(ModUpdateStatus.NotChecked);

    public bool CanUpdate => Update.Status is ModUpdateStatus.UpdateAvailable
                             || (Update.Status is ModUpdateStatus.UnknownVersion or ModUpdateStatus.IncompatibleWithSpt && Update.Link is not null);

    public string StatusText => Update.Status switch
    {
        ModUpdateStatus.UpToDate => "Up to date",
        ModUpdateStatus.UpdateAvailable => $"Update: v{Update.LatestVersion}",
        ModUpdateStatus.Blocked => $"v{Update.LatestVersion} held back",
        ModUpdateStatus.IncompatibleWithSpt => Update.RequiredSptVersion is { } needed ? $"Needs SPT {needed}+" : "Not for your SPT",
        ModUpdateStatus.UnknownVersion => Update.LatestVersion is { } latest ? $"Latest: v{latest}" : "Unknown version",
        ModUpdateStatus.NotOnForge => IsUnrecognized ? "Unrecognized" : "Not on The Forge",
        _ => "Not checked",
    };

    public string? StatusDetail => Update.Reason;

    public bool IsUpToDate => Update.Status == ModUpdateStatus.UpToDate;

    public bool IsWarning => Update.Status is ModUpdateStatus.Blocked or ModUpdateStatus.UnknownVersion;

    public bool IsDanger => Update.Status == ModUpdateStatus.IncompatibleWithSpt;

    public bool IsMuted => Update.Status is ModUpdateStatus.NotChecked or ModUpdateStatus.NotOnForge;
}
