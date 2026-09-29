using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SptModManager.App.Views;

namespace SptModManager.App.Services;

public sealed class DialogService(Func<Window?> owner) : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool danger = false)
    {
        var window = owner();
        if (window is null)
        {
            return false;
        }

        var dialog = new MessageDialog(title, message, confirmText, cancelText, danger);
        return await dialog.ShowDialog<bool>(window);
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var window = owner();
        if (window is null)
        {
            return;
        }

        var dialog = new MessageDialog(title, message, "OK", cancelText: null, danger: false);
        await dialog.ShowDialog<bool>(window);
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var storage = owner()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<Stream?> OpenFileAsync(string title, string patternName, string pattern)
    {
        var storage = owner()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(patternName) { Patterns = [pattern] }, FilePickerFileTypes.All],
        });

        return files.Count == 0 ? null : await files[0].OpenReadAsync();
    }

    public async Task<Stream?> SaveFileAsync(string title, string suggestedName, string patternName, string pattern)
    {
        var storage = owner()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(patternName) { Patterns = [pattern] }],
        });

        return file is null ? null : await file.OpenWriteAsync();
    }

    public async Task OpenUrlAsync(string url)
    {
        var launcher = owner()?.Launcher;
        if (launcher is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await launcher.LaunchUriAsync(uri);
        }
    }

    public async Task OpenFolderAsync(string path)
    {
        var launcher = owner()?.Launcher;
        if (launcher is not null && Directory.Exists(path))
        {
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
    }
}
