using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SptModManager.App.Services;
using SptModManager.Core.Forge;
using SptModManager.Core.Mods;
using SptModManager.Core.Versioning;

namespace SptModManager.App.ViewModels;

public partial class ModCardViewModel(ForgeMod mod, string? sptVersion) : ViewModelBase
{
    public ForgeMod Mod { get; } = mod;

    public string Name => Mod.Name;

    public string Author => Mod.Owner?.Name ?? "Unknown author";

    public string Teaser => Mod.Teaser ?? string.Empty;

    public string Downloads => Format.Count(Mod.Downloads) + " downloads";

    public string? Category => Mod.Category?.DisplayName;

    public bool IsFeatured => Mod.Featured;

    public bool IsFikaCompatible => Mod.FikaCompatibility == true;

    public string Initial => string.IsNullOrEmpty(Mod.Name) ? "?" : Mod.Name[..1].ToUpperInvariant();

    public ForgeVersionSummary? LatestVersion => Mod.Versions?.OrderByDescending(v => VersionUtil.TryParse(v.Version)).FirstOrDefault();

    public string LatestVersionText => LatestVersion is { } v ? $"v{v.Version}" : string.Empty;

    /// <summary>True when the newest listed version works with the installed SPT.</summary>
    public bool IsCompatible => sptVersion is null
                                || (Mod.Versions?.Any(v => VersionUtil.Satisfies(sptVersion, v.SptVersionConstraint)) ?? false);

    public string? SptConstraint => LatestVersion?.SptVersionConstraint is { } c ? $"SPT {c}" : null;

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(InstalledText))]
    public partial string? InstalledVersion { get; set; }

    public bool IsInstalled => InstalledVersion is not null;

    public string InstalledText => InstalledVersion is null ? string.Empty : $"Installed {InstalledVersion}";

    public void UpdateInstalled(InstalledMod? installed) => InstalledVersion = installed is null ? null : installed.Version ?? "?";

    public async Task LoadThumbnailAsync(ImageLoader images)
    {
        Thumbnail = await images.LoadAsync(Mod.Thumbnail);
    }
}
