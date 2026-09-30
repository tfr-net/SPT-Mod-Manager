using System.Net;
using System.Reflection;

namespace SptModManager.Core.Net;

public static class HttpClientFactory
{
    /// <summary>
    /// The Forge sits behind Cloudflare, which turns away some requests that do not look like they come from a browser
    /// (its image host included). A Mozilla-style prefix, which is what browsers and most well-behaved tools send,
    /// keeps the manager on the right side of that while still naming itself.
    /// </summary>
    public static string UserAgent { get; } =
        $"Mozilla/5.0 (SPT Mod Manager {(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version?.ToString(3) ?? "1.0.0"}; desktop app)";

    /// <summary>Client for API calls and images: follows redirects and gives up after 30 seconds.</summary>
    public static HttpClient CreateApiClient() => Create(followRedirects: true, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Client for file downloads. Redirects are not followed automatically: <see cref="FileDownloader"/> follows them
    /// itself so Forge download links (which redirect to the author's host) work even across http/https. There is no
    /// overall timeout because archives can be large; the downloader aborts stalled transfers instead.
    /// </summary>
    public static HttpClient CreateDownloadClient() => Create(followRedirects: false, Timeout.InfiniteTimeSpan);

    private static HttpClient Create(bool followRedirects, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };

        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
