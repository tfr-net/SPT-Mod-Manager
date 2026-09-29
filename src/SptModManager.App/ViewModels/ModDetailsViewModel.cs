using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SptModManager.Core.Forge;
using SptModManager.Core.Mods;
using SptModManager.Core.Text;
using SptModManager.Core.Versioning;

namespace SptModManager.App.ViewModels;

public sealed record VersionOption(ForgeModVersion Version, bool IsCompatible)
{
    public string Label => $"v{Version.Version}" + (IsCompatible ? string.Empty : "  (not for your SPT)");

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Version.SptVersionConstraint is { } constraint)
            {
                parts.Add($"SPT {constraint}");
            }

            parts.Add(Format.Count(Version.Downloads) + " downloads");

            if (Version.FikaCompatibility is "compatible")
            {
                parts.Add("Fika compatible");
            }

            if (Version.PublishedAt is not null)
            {
                parts.Add(Format.Date(Version.PublishedAt));
            }

            if (Version.ContentLength is { } size)
            {
                parts.Add(Format.Bytes(size));
            }

            return string.Join(" · ", parts);
        }
    }
}

public sealed record DependencyLine(string Text, bool IsProblem);

public partial class ModDetailsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _dependencyCts;

    public ModDetailsViewModel(MainViewModel main, ModCardViewModel card)
    {
        _main = main;
        Card = card;
        Description = card.Teaser;
        RefreshInstalledState();
    }

    public ModCardViewModel Card { get; }

    public ForgeMod Mod => Card.Mod;

    public ObservableCollection<VersionOption> Versions { get; } = [];

    public ObservableCollection<DependencyLine> Dependencies { get; } = [];

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial string? License { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? DependencyStatus { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial VersionOption? SelectedVersion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    public partial InstalledMod? Installed { get; set; }

    public string InstallButtonText
    {
        get
        {
            if (SelectedVersion is null)
            {
                return "Install";
            }

            if (Installed is null)
            {
                return $"Install v{SelectedVersion.Version.Version}";
            }

            var comparison = VersionUtil.Compare(SelectedVersion.Version.Version, Installed.Version);
            return comparison == 0 ? "Reinstall" : comparison > 0 ? $"Update to v{SelectedVersion.Version.Version}" : $"Switch to v{SelectedVersion.Version.Version}";
        }
    }

    public void RefreshInstalledState()
    {
        Installed = _main.Manager?.FindByForgeId(Mod.Id) ?? _main.Manager?.FindByGuid(Mod.Guid);
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            var forge = _main.Services.Forge;
            var detailsTask = forge.GetModAsync(Mod.Id, cancellationToken);
            var versionsTask = forge.GetModVersionsAsync(Mod.Id, cancellationToken: cancellationToken);

            var details = await detailsTask;
            if (details is not null)
            {
                var text = HtmlText.ToPlainText(details.Description);
                Description = string.IsNullOrWhiteSpace(text) ? Card.Teaser : text;
                License = details.License?.Name;
            }

            var spt = _main.SptVersion;
            foreach (var version in await versionsTask)
            {
                Versions.Add(new VersionOption(version, spt is null || SptCompatibility.IsCompatible(spt, version.SptVersionConstraint)));
            }

            SelectedVersion = Versions.FirstOrDefault(v => v.IsCompatible) ?? Versions.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            DependencyStatus = $"Could not load details: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedVersionChanged(VersionOption? value)
    {
        _dependencyCts?.Cancel();
        Dependencies.Clear();

        if (value is null)
        {
            DependencyStatus = null;
            return;
        }

        _dependencyCts = new CancellationTokenSource();
        _ = LoadDependenciesAsync(value, _dependencyCts.Token);
    }

    private async Task LoadDependenciesAsync(VersionOption option, CancellationToken cancellationToken)
    {
        var spt = _main.SptVersion;
        if (spt is null)
        {
            DependencyStatus = "Pick your SPT folder to check dependencies.";
            return;
        }

        DependencyStatus = "Checking dependencies...";
        try
        {
            var key = $"{Mod.Id}:{option.Version.Version}";
            ModVersionPair[] pairs = [new ModVersionPair(Mod.Id.ToString(), option.Version.Version)];
            var trees = _main.Manager is { } manager
                ? await manager.ResolveDependenciesAsync(pairs, cancellationToken)
                : await _main.Services.Forge.ResolveDependenciesAsync(pairs, spt, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var seen = new HashSet<int>();
            void Walk(IEnumerable<ForgeDependencyNode> nodes, int depth)
            {
                foreach (var node in nodes)
                {
                    if (!seen.Add(node.Id))
                    {
                        continue;
                    }

                    var installed = _main.Manager?.FindByForgeId(node.Id);
                    var version = node.LatestCompatibleVersion?.Version;
                    var status = version is null
                        ? "no compatible version"
                        : installed is not null && VersionUtil.AreEquivalent(installed.Version, version) ? $"v{version}, installed" : $"v{version}";

                    Dependencies.Add(new DependencyLine($"{new string(' ', depth * 3)}{node.Name} ({status})", version is null));
                    Walk(node.Dependencies, depth + 1);
                }
            }

            Walk(trees.GetValueOrDefault(key) ?? [], 0);
            DependencyStatus = Dependencies.Count == 0 ? "No dependencies." : $"{Dependencies.Count} dependenc{(Dependencies.Count == 1 ? "y" : "ies")}, installed automatically:";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            DependencyStatus = $"Could not check dependencies: {e.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (SelectedVersion is not { } option)
        {
            return;
        }

        var version = option.Version;
        if (!option.IsCompatible)
        {
            var proceed = await _main.Services.Dialogs.ConfirmAsync(
                "Not marked compatible",
                $"{Mod.Name} v{version.Version} is not marked compatible with SPT {_main.SptVersion}. Install it anyway?",
                "Install anyway",
                danger: true);

            if (!proceed)
            {
                return;
            }
        }

        var request = new ModInstallRequest(Mod.Id, Mod.Name, version.Version, Mod.Guid, Mod.Slug, Mod.DetailUrl, version.Id, version.Link, version.ContentLength);
        await _main.InstallWithConfirmationAsync([request], $"Install {Mod.Name}");
        RefreshInstalledState();
    }

    private bool CanInstall() => SelectedVersion is not null;

    [RelayCommand]
    private Task OpenOnForgeAsync() => _main.Services.Dialogs.OpenUrlAsync(Mod.DetailUrl ?? $"{_main.Services.Settings.ForgeBaseUrl.TrimEnd('/')}/mods/{Mod.Id}/{Mod.Slug}");
}
