using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace SptModManager.App.Services;

/// <summary>Loads and caches remote thumbnails, a few at a time.</summary>
public sealed class ImageLoader(HttpClient http)
{
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();
    private readonly SemaphoreSlim _gate = new(4);

    public Task<Bitmap?> LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        return _cache.GetOrAdd(url, FetchAsync);
    }

    private async Task<Bitmap?> FetchAsync(string url)
    {
        await _gate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var bytes = await http.GetByteArrayAsync(url, cts.Token);
            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, 160);
        }
        catch (Exception)
        {
            // Thumbnails are decoration; a broken one just shows the placeholder.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
