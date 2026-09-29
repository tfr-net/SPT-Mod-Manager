using System.Net;
using System.Reflection;

namespace SptModManager.Core.Net;

public static class HttpClientFactory
{
    public static string UserAgent { get; } =
        $"SptModManager/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"} (+https://github.com/tfr-net/SPT-Mod-Manager)";

    /// <summary>
    /// Creates the shared HttpClient. Redirects are not followed automatically: <see cref="FileDownloader"/> follows
    /// them itself so Forge download links (which redirect to the author's host) work across schemes.
    /// </summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };

        var client = new HttpClient(handler)
        {
            // Downloads can be large; per-request cancellation handles stalls instead.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
