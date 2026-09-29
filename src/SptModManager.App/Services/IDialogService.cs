namespace SptModManager.App.Services;

public interface IDialogService
{
    /// <summary>Shows a confirmation dialog. Returns true when the user confirms.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool danger = false);

    Task ShowMessageAsync(string title, string message);

    Task<string?> PickFolderAsync(string title);

    Task<Stream?> OpenFileAsync(string title, string patternName, string pattern);

    Task<Stream?> SaveFileAsync(string title, string suggestedName, string patternName, string pattern);

    Task OpenUrlAsync(string url);

    Task OpenFolderAsync(string path);
}
