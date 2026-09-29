using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SptModManager.Core.Net;
using SptModManager.Core.Tests.Support;

namespace SptModManager.Core.Tests;

public class FileDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_FollowsRedirectsAndHashes()
    {
        var payload = "archive-bytes"u8.ToArray();
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "sp-mod.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("http://files.example.com/dl/My%20Mod-1.0.0.7z");
                return redirect;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-7z-compressed");
            return response;
        });

        var directory = Directory.CreateTempSubdirectory("sptmm-dl");
        try
        {
            var downloader = new FileDownloader(new HttpClient(handler));
            var result = await downloader.DownloadAsync("https://sp-mod.com/mod/download/1/my-mod/1.0.0", directory.FullName);

            Assert.Equal("My Mod-1.0.0.7z", result.FileName);
            Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
            Assert.Equal(Convert.ToBase64String(MD5.HashData(payload)), result.Md5Base64);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsHtmlPages()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>click here to download</html>", System.Text.Encoding.UTF8, "text/html"),
        });

        var downloader = new FileDownloader(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync("https://drive.example.com/file", Path.GetTempPath()));
    }

    [Fact]
    public void GetFileName_PrefersContentDisposition()
    {
        var disposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"Cool Mod.zip\"" };

        Assert.Equal("Cool Mod.zip", FileDownloader.GetFileName(disposition, new Uri("https://x/y/download")));
        Assert.Equal("download.bin", FileDownloader.GetFileName(null, new Uri("https://x/")));
    }
}
