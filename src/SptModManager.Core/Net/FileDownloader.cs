using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SptModManager.Core.Net;

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value : null;
}

public sealed record DownloadResult(string FilePath, string FileName, long Length, string Md5Base64);

public interface IFileDownloader
{
    Task<DownloadResult> DownloadAsync(
        string url,
        string destinationDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class FileDownloader(HttpClient http) : IFileDownloader
{
    private const int MaxRedirects = 10;

    /// <summary>Abort when no bytes arrive for this long.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    public async Task<DownloadResult> DownloadAsync(
        string url,
        string destinationDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);

        var current = new Uri(url);
        HttpResponseMessage? response = null;

        try
        {
            for (var hop = 0; ; hop++)
            {
                if (hop > MaxRedirects)
                {
                    throw new HttpRequestException($"Too many redirects while downloading {url}.");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    response.Dispose();
                    response = null;
                    continue;
                }

                break;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException("Download rate limit hit (The Forge allows 5 downloads per mod per minute). Try again shortly.");
            }

            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is "text/html")
            {
                throw new HttpRequestException(
                    $"The download link returned a web page instead of a file ({current.Host}). The mod may need to be downloaded manually.");
            }

            var fileName = GetFileName(response.Content.Headers.ContentDisposition, current);
            var finalPath = Path.Combine(destinationDirectory, fileName);
            var partPath = finalPath + ".part";
            var total = response.Content.Headers.ContentLength;

            using var md5 = MD5.Create();
            long received = 0;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stall.CancelAfter(StallTimeout);

                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, stall.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Download stalled for {StallTimeout.TotalSeconds:0}s: {current}");
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    md5.TransformBlock(buffer, 0, read, null, 0);
                    received += read;
                    progress?.Report(new DownloadProgress(received, total));
                }
            }

            md5.TransformFinalBlock([], 0, 0);

            if (total is > 0 && received != total)
            {
                File.Delete(partPath);
                throw new IOException($"Download was incomplete ({received} of {total} bytes).");
            }

            File.Move(partPath, finalPath, overwrite: true);
            return new DownloadResult(finalPath, fileName, received, Convert.ToBase64String(md5.Hash!));
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    internal static string GetFileName(ContentDispositionHeaderValue? disposition, Uri finalUri)
    {
        var name = disposition?.FileNameStar ?? disposition?.FileName;
        name = name?.Trim('"', ' ');

        if (string.IsNullOrWhiteSpace(name))
        {
            name = Uri.UnescapeDataString(Path.GetFileName(finalUri.AbsolutePath));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "download.bin";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars().Append('/').Append('\\'))
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
