using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SptModManager.Core.Mods;

namespace SptModManager.App.ViewModels;

public partial class InstalledViewModel(MainViewModel main) : ViewModelBase
{
    private readonly List<InstalledModViewModel> _all = [];
    private IReadOnlyDictionary<string, ModUpdateInfo> _updates = new Dictionary<string, ModUpdateInfo>();

    public ObservableCollection<InstalledModViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowUnrecognized { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand), nameof(OpenOnForgeCommand))]
    public partial InstalledModViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "No SPT folder selected.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    public partial int UpdateCount { get; set; }

    [ObservableProperty]
    public partial string? LastChecked { get; set; }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnShowUnrecognizedChanged(bool value) => ApplyFilter();

    /// <summary>Rescans the install and rebuilds the list.</summary>
    public async Task ReloadAsync(bool matchWithForge)
    {
        var manager = main.Manager;
        if (manager is null)
        {
            return;
        }

        await main.RunAsync(matchWithForge ? "Scanning mods and matching them to The Forge..." : "Scanning mods...", async (_, ct) =>
        {
            try
            {
                await manager.RefreshAsync(matchWithForge, ct);
            }
            catch (Exception e) when (matchWithForge && e is not OperationCanceledException)
            {
                // Offline or Forge trouble should not hide what is on disk.
                main.Log.Warn($"Could not match mods with The Forge: {e.Message}");
                await manager.RefreshAsync(matchWithForge: false, ct);
            }
        });

        Rebuild();
    }

    private void Rebuild()
    {
        var selectedKey = Selected?.Mod.Key;
        _all.Clear();

        foreach (var mod in main.Manager?.Mods ?? [])
        {
            var item = new InstalledModViewModel(mod);
            item.Update = _updates.GetValueOrDefault(mod.Key) ?? new ModUpdateInfo(mod.ForgeModId is null ? ModUpdateStatus.NotOnForge : ModUpdateStatus.NotChecked);
            _all.Add(item);
        }

        ApplyFilter();
        Selected = Items.FirstOrDefault(i => i.Mod.Key == selectedKey);
        UpdateSummary();
        _ = LoadThumbnailsAsync(_all.ToList());
    }

    /// <summary>Shows each Forge mod's picture, looking up any thumbnails the Browse page has not seen yet.</summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<InstalledModViewModel> items)
    {
        var missing = items
            .Select(i => i.Mod.ForgeModId)
            .OfType<int>()
            .Where(id => !main.KnownThumbnails.ContainsKey(id))
            .Distinct()
            .ToList();

        if (missing.Count > 0)
        {
            try
            {
                foreach (var mod in await main.Services.Forge.GetModsByIdsAsync(missing))
                {
                    main.RememberThumbnail(mod);
                }

                // Mods The Forge returned nothing for have no picture; do not ask again this session.
                foreach (var id in missing)
                {
                    main.KnownThumbnails.TryAdd(id, null);
                }
            }
            catch (Exception e)
            {
                // Pictures are decoration; the colored tiles cover for them when offline.
                main.Log.Warn($"Could not load mod pictures: {e.Message}");
            }
        }

        foreach (var item in items)
        {
            if (item.Mod.ForgeModId is { } id && main.KnownThumbnails.GetValueOrDefault(id) is { } url)
            {
                item.Thumbnail = await main.Services.Images.LoadAsync(url);
            }
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var item in _all.Where(Matches).OrderByDescending(i => i.CanUpdate).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            Items.Add(item);
        }
    }

    private bool Matches(InstalledModViewModel item)
    {
        if (!ShowUnrecognized && item.IsUnrecognized)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(FilterText)
               || item.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
               || (item.Mod.Guid?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void UpdateSummary()
    {
        if (main.Manager is null)
        {
            Summary = "No SPT folder selected.";
            UpdateCount = 0;
            return;
        }

        UpdateCount = _all.Count(i => i.CanUpdate);
        var onForge = _all.Count(i => i.Mod.ForgeModId is not null);
        var parts = new List<string> { $"{_all.Count} installed", $"{onForge} on The Forge" };

        if (UpdateCount > 0)
        {
            parts.Add($"{UpdateCount} update{(UpdateCount == 1 ? string.Empty : "s")} available");
        }

        var incompatible = _all.Count(i => i.Update.Status == ModUpdateStatus.IncompatibleWithSpt);
        if (incompatible > 0)
        {
            parts.Add($"{incompatible} not marked for your SPT");
        }

        Summary = string.Join(" · ", parts);
    }

    [RelayCommand]
    private Task RescanAsync() => ReloadAsync(matchWithForge: true);

    [RelayCommand]
    public Task CheckUpdatesAsync() => CheckUpdatesCoreAsync(showErrors: true);

    public async Task CheckUpdatesCoreAsync(bool showErrors)
    {
        var manager = main.Manager;
        if (manager is null)
        {
            return;
        }

        IReadOnlyDictionary<string, ModUpdateInfo>? updates = null;
        var ok = await main.RunAsync("Checking The Forge for mod updates...", async (_, ct) => updates = await manager.CheckForUpdatesAsync(ct), showErrors);
        if (!ok || updates is null)
        {
            return;
        }

        _updates = updates;
        LastChecked = $"Checked {DateTime.Now:HH:mm}";
        Rebuild();

        main.Log.Info(UpdateCount == 0 ? "All mods are up to date." : $"{UpdateCount} mod update{(UpdateCount == 1 ? " is" : "s are")} available.");
    }

    [RelayCommand(CanExecute = nameof(HasUpdates))]
    private Task UpdateAllAsync() => UpdateModsAsync(_all.Where(i => i.CanUpdate).ToList());

    private bool HasUpdates() => UpdateCount > 0;

    [RelayCommand]
    private Task UpdateOneAsync(InstalledModViewModel? item) => item is null ? Task.CompletedTask : UpdateModsAsync([item]);

    private async Task UpdateModsAsync(IReadOnlyList<InstalledModViewModel> items)
    {
        var requests = items
            .Where(i => i.CanUpdate && i.Mod.ForgeModId is not null && i.Update.LatestVersion is not null)
            .Select(i => new ModInstallRequest(
                i.Mod.ForgeModId!.Value,
                i.Mod.Name,
                i.Update.LatestVersion!,
                i.Mod.Guid,
                i.Mod.Slug,
                i.Mod.DetailUrl,
                i.Update.LatestVersionId,
                i.Update.Link,
                i.Update.ContentLength))
            .ToList();

        await main.InstallWithConfirmationAsync(requests, requests.Count == 1 ? $"Update {requests[0].Name}" : $"Update {requests.Count} mods");

        // Updated mods are now current; recheck so statuses reflect the new versions.
        if (requests.Count > 0)
        {
            await CheckUpdatesAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task UninstallAsync()
    {
        var manager = main.Manager;
        if (Selected is not { } item || manager is null)
        {
            return;
        }

        var dependents = manager.GetDependents(item.Mod);
        var message = $"Remove {item.Name} and its {item.Mod.Files.Count} file(s)?";
        if (dependents.Count > 0)
        {
            message += $"\n\nThese mods depend on it and may stop working:\n• {string.Join("\n• ", dependents.Select(d => d.Name))}";
        }

        if (!await main.Services.Dialogs.ConfirmAsync($"Uninstall {item.Name}", message, "Uninstall", danger: true))
        {
            return;
        }

        UninstallResult? result = null;
        await main.RunAsync($"Removing {item.Name}...", (_, _) =>
        {
            result = manager.Uninstall(item.Mod);
            return Task.CompletedTask;
        });

        if (result is { OrphanedDependencies.Count: > 0 } && await main.Services.Dialogs.ConfirmAsync(
                "Remove unused dependencies?",
                $"These were installed for {item.Name} and nothing else needs them now:\n• {string.Join("\n• ", result.OrphanedDependencies.Select(o => o.Name))}",
                "Remove them",
                "Keep"))
        {
            await main.RunAsync("Removing unused dependencies...", (_, _) =>
            {
                foreach (var orphan in result.OrphanedDependencies)
                {
                    manager.Uninstall(orphan);
                }

                return Task.CompletedTask;
            });
        }

        Rebuild();
        main.Browse.RefreshInstalledState();
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(SelectedIsOnForge))]
    private Task OpenOnForgeAsync() =>
        Selected?.Mod.DetailUrl is { } url ? main.Services.Dialogs.OpenUrlAsync(url) : Task.CompletedTask;

    private bool SelectedIsOnForge() => Selected?.Mod.DetailUrl is not null;

    [RelayCommand]
    private Task OpenModsFolderAsync() =>
        main.Installation is { } install ? main.Services.Dialogs.OpenFolderAsync(install.RootPath) : Task.CompletedTask;

    [RelayCommand]
    private async Task ExportAsync()
    {
        var manager = main.Manager;
        if (manager is null)
        {
            return;
        }

        var list = ModListService.Create(manager.Mods, main.SptVersion, $"Mods for SPT {main.SptVersion}");
        if (list.Mods.Count == 0)
        {
            await main.Services.Dialogs.ShowMessageAsync("Nothing to export", "No installed mods could be matched to The Forge yet. Try Rescan first.");
            return;
        }

        await using var stream = await main.Services.Dialogs.SaveFileAsync("Export mod list", $"spt-{main.SptVersion}-mods{ModListService.FileExtension}", "SPT mod list", "*.json");
        if (stream is null)
        {
            return;
        }

        await ModListService.SaveAsync(list, stream);
        var skipped = manager.Mods.Count(m => m.ForgeModId is null && m.Guid is null);
        main.Log.Success($"Exported {list.Mods.Count} mod(s).{(skipped > 0 ? $" {skipped} unrecognized item(s) were left out." : string.Empty)}");
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var manager = main.Manager;
        if (manager is null || main.SptVersion is not { } spt)
        {
            await main.Services.Dialogs.ShowMessageAsync("No SPT folder", "Pick your SPT folder in Settings first.");
            return;
        }

        ModListFile list;
        await using (var stream = await main.Services.Dialogs.OpenFileAsync("Import mod list", "SPT mod list", "*.json"))
        {
            if (stream is null)
            {
                return;
            }

            try
            {
                list = await ModListService.LoadAsync(stream);
            }
            catch (InvalidDataException e)
            {
                await main.Services.Dialogs.ShowMessageAsync("Can't import that file", e.Message);
                return;
            }
        }

        ModListImportResult? result = null;
        var ok = await main.RunAsync($"Looking up {list.Mods.Count} mods...", async (_, ct) => result = await ModListService.ResolveAsync(list, manager, main.Services.Forge, spt, ct));
        if (!ok || result is null)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(list.Name) ? "Import mod list" : $"Import \"{list.Name}\"";
        if (result.Requests.Count == 0)
        {
            var message = "You already have everything from this list." + (result.Notes.Count > 0 ? "\n\n" + string.Join('\n', result.Notes) : string.Empty);
            await main.Services.Dialogs.ShowMessageAsync(title, message);
            return;
        }

        await main.InstallWithConfirmationAsync(result.Requests, title, result.Notes);
    }
}
