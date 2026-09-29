using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SptModManager.Core.Forge;

namespace SptModManager.App.ViewModels;

public sealed record CategoryOption(string Name, string? Slug);

public sealed record SortOption(string Name, ModSort Sort);

public partial class BrowseViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailsCts;
    private bool _suppressSearch;

    public BrowseViewModel(MainViewModel main)
    {
        _main = main;
        Categories.Add(new CategoryOption("All categories", null));
        _suppressSearch = true;
        SelectedCategory = Categories[0];
        SelectedSort = SortOptions[0];
        OnlyCompatible = main.Services.Settings.ShowOnlyCompatibleMods;
        _suppressSearch = false;
    }

    public ObservableCollection<CategoryOption> Categories { get; } = [];

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new("Featured", ModSort.Featured),
        new("Most downloaded", ModSort.Downloads),
        new("Recently updated", ModSort.RecentlyUpdated),
        new("Newest", ModSort.Newest),
        new("Name", ModSort.Name),
    ];

    public ObservableCollection<ModCardViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CategoryOption? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial SortOption? SelectedSort { get; set; }

    [ObservableProperty]
    public partial bool OnlyCompatible { get; set; }

    [ObservableProperty]
    public partial bool FikaOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial int Page { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial int LastPage { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    public partial int Total { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial ModCardViewModel? SelectedMod { get; set; }

    [ObservableProperty]
    public partial ModDetailsViewModel? Details { get; set; }

    public string PageText => Total == 0 ? "No mods" : $"Page {Page} of {LastPage} · {Total:N0} mods";

    partial void OnSelectedCategoryChanged(CategoryOption? value) => TriggerSearch();

    partial void OnSelectedSortChanged(SortOption? value) => TriggerSearch();

    partial void OnOnlyCompatibleChanged(bool value)
    {
        _main.Services.Settings.ShowOnlyCompatibleMods = value;
        _main.Services.Settings.Save();
        TriggerSearch();
    }

    partial void OnFikaOnlyChanged(bool value) => TriggerSearch();

    partial void OnSelectedModChanged(ModCardViewModel? value)
    {
        _detailsCts?.Cancel();

        if (value is null)
        {
            Details = null;
            return;
        }

        _detailsCts = new CancellationTokenSource();
        Details = new ModDetailsViewModel(_main, value);
        _ = Details.LoadAsync(_detailsCts.Token);
    }

    private void TriggerSearch()
    {
        if (!_suppressSearch)
        {
            Page = 1;
            _ = SearchAsync();
        }
    }

    public async Task LoadCategoriesAsync()
    {
        try
        {
            var categories = await _main.Services.Forge.GetCategoriesAsync();
            foreach (var category in categories.OrderBy(c => c.DisplayName))
            {
                Categories.Add(new CategoryOption(category.DisplayName, category.Slug));
            }
        }
        catch (Exception e)
        {
            _main.Log.Warn($"Could not load mod categories: {e.Message}");
        }
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var query = new ModSearchQuery
            {
                Text = SearchText,
                CategorySlug = SelectedCategory?.Slug,
                Sort = SelectedSort?.Sort ?? ModSort.Featured,
                SptVersion = OnlyCompatible ? _main.SptVersion : null,
                FikaCompatibleOnly = FikaOnly,
                Page = Page,
                PerPage = 20,
            };

            var page = await _main.Services.Forge.SearchModsAsync(query, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Results.Clear();
            foreach (var mod in page.Items)
            {
                var card = new ModCardViewModel(mod, _main.SptVersion);
                card.UpdateInstalled(_main.Manager?.FindByForgeId(mod.Id) ?? _main.Manager?.FindByGuid(mod.Guid));
                Results.Add(card);
                _ = card.LoadThumbnailAsync(_main.Services.Images);
            }

            Page = page.CurrentPage;
            LastPage = Math.Max(1, page.LastPage);
            Total = page.Total;

            if (page.Items.Count == 0)
            {
                ErrorMessage = "No mods match those filters.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            ErrorMessage = $"Could not reach The Forge: {e.Message}";
            _main.Log.Error(ErrorMessage);
        }
        finally
        {
            if (cts == _searchCts)
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextPageAsync()
    {
        Page++;
        await SearchAsync();
    }

    private bool CanGoNext() => Page < LastPage;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousPageAsync()
    {
        Page--;
        await SearchAsync();
    }

    private bool CanGoPrevious() => Page > 1;

    /// <summary>Refreshes "installed" badges after installs, updates or removals.</summary>
    public void RefreshInstalledState()
    {
        foreach (var card in Results)
        {
            card.UpdateInstalled(_main.Manager?.FindByForgeId(card.Mod.Id) ?? _main.Manager?.FindByGuid(card.Mod.Guid));
        }

        Details?.RefreshInstalledState();
    }
}
