using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace SptModManager.App.Services;

/// <summary>
/// Loads and caches mod thumbnails, a few at a time. The Forge serves them from files.sp-mod.com behind Cloudflare, so
/// requests look like a browser loading an image on the Forge (Accept and Referer headers, plus the Mozilla-style
/// User-Agent every client sends). When a picture still cannot be loaded, the smaller WebP copy the Forge generates
/// is tried, and the reason is logged once per host so problems are visible instead of silently showing tiles.
/// </summary>
public sealed class ImageLoader(HttpClient http, IActivityLog log, Func<string> forgeBaseUrl)
{
    public const string LegacyHost = "forge-static.sp-tarkov.com";
    public const string CurrentHost = "files.sp-mod.com";

    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();
    private readonly ConcurrentDictionary<string, bool> _reportedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(4);

    public Task<Bitmap?> LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        return _cache.GetOrAdd(url, FetchAsync);
    }

    /// <summary>
    /// The original URL first, then the Forge's 192px WebP copy that sits next to it ("mods/76.png" has
    /// "mods/76_192w.webp"), and for thumbnails still pointing at the retired forge-static host, the same paths on
    /// the current host.
    /// </summary>
    internal static IEnumerable<string> CandidateUrls(string url)
    {
        var uri = new Uri(url);
        var hosts = new List<string> { uri.Host };
        if (uri.Host.Equals(LegacyHost, StringComparison.OrdinalIgnoreCase))
        {
            hosts.Add(CurrentHost);
        }

        foreach (var host in hosts)
        {
            var builder = new UriBuilder(uri) { Host = host, Port = -1 };
            yield return builder.Uri.AbsoluteUri;

            var path = builder.Path;
            var slash = path.LastIndexOf('/');
            var dot = path.LastIndexOf('.');
            if (dot > slash && !path[slash..].Contains("_192w", StringComparison.Ordinal))
            {
                builder.Path = path[..dot] + "_192w.webp";
                yield return builder.Uri.AbsoluteUri;
            }
        }
    }

    private async Task<Bitmap?> FetchAsync(string url)
    {
        await _gate.WaitAsync();
        try
        {
            string? firstError = null;
            foreach (var candidate in CandidateUrls(url))
            {
                var (bitmap, error) = await TryFetchAsync(candidate);
                if (bitmap is not null)
                {
                    return bitmap;
                }

                firstError ??= error;
            }

            ReportOnce(url, firstError);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(Bitmap? Bitmap, string? Error)> TryFetchAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("image/webp,image/png,image/jpeg,image/gif;q=0.9,image/*;q=0.8");
            if (Uri.TryCreate(forgeBaseUrl().TrimEnd('/') + "/", UriKind.Absolute, out var referrer))
            {
                request.Headers.Referrer = referrer;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown type";
            return Decode(bytes) is { } bitmap ? (bitmap, null) : (null, $"the image could not be decoded ({contentType})");
        }
        catch (Exception e)
        {
            return (null, e is OperationCanceledException ? "timed out" : e.Message);
        }
    }

    private static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            // Enough pixels for the largest tile (96px) on high-DPI screens.
            return Bitmap.DecodeToWidth(stream, 256, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception)
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private void ReportOnce(string url, string? error)
    {
        var host = new Uri(url).Host;
        if (_reportedHosts.TryAdd(host, true))
        {
            log.Warn($"Mod pictures from {host} could not be loaded ({error ?? "unknown error"}), so colored tiles are shown instead. First failing picture: {url}");
        }
    }
}
