using System.Net;
using System.Reflection;

namespace SptModManager.Core.Net;

public static class HttpClientFactory
{
    public static string UserAgent { get; } =
        $"SptModManager/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"} (+https://github.com/tfr-net/SPT-Mod-Manager)";

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
