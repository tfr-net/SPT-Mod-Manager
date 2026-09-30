using System.Net;
using Avalonia.Headless.XUnit;
using SptModManager.App.Services;
using SptModManager.Core.Net;

namespace SptModManager.App.Tests;

public class ImageLoaderTests
{
    private static readonly byte[] Png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "thumb1.png"));

    [Fact]
    public void CandidateUrls_AddsWebpCopyAndCurrentHostForLegacyUrls()
    {
        Assert.Equal(
            ["https://files.sp-mod.com/mods/76.png", "https://files.sp-mod.com/mods/76_192w.webp"],
            ImageLoader.CandidateUrls("https://files.sp-mod.com/mods/76.png"));

        Assert.Equal(
            [
                "https://forge-static.sp-tarkov.com/mods/379.jpg",
                "https://forge-static.sp-tarkov.com/mods/379_192w.webp",
                "https://files.sp-mod.com/mods/379.jpg",
                "https://files.sp-mod.com/mods/379_192w.webp",
            ],
            ImageLoader.CandidateUrls("https://forge-static.sp-tarkov.com/mods/379.jpg"));

        Assert.Single(ImageLoader.CandidateUrls("https://files.sp-mod.com/mods/9_192w.webp"));
    }

    [Fact]
    public void UserAgent_LooksLikeABrowserButNamesTheApp()
    {
        Assert.StartsWith("Mozilla/5.0 (SPT Mod Manager ", HttpClientFactory.UserAgent);
    }

    [AvaloniaFact]
    public async Task LoadAsync_SendsBrowserHeadersAndFallsBackToWebpCopy()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new RecordingHandler(request =>
        {
            requests.Add(request);
            return request.RequestUri!.AbsolutePath.EndsWith("_192w.webp")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var log = new UiActivityLog();
        var loader = new ImageLoader(new HttpClient(handler), log, () => "https://sp-mod.com");

        var bitmap = await loader.LoadAsync("https://files.sp-mod.com/mods/76.png");

        Assert.NotNull(bitmap);
        Assert.Equal(2, requests.Count);
        Assert.Equal("https://sp-mod.com/", requests[0].Headers.Referrer?.AbsoluteUri);
        Assert.Contains(requests[0].Headers.Accept, a => a.MediaType == "image/webp");
        Assert.Empty(log.Entries);
    }

    [AvaloniaFact]
    public async Task LoadAsync_LogsTheReasonOncePerHost()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "Forbidden" });
        var log = new UiActivityLog();
        var loader = new ImageLoader(new HttpClient(handler), log, () => "https://sp-mod.com");

        Assert.Null(await loader.LoadAsync("https://files.sp-mod.com/mods/1.png"));
        Assert.Null(await loader.LoadAsync("https://files.sp-mod.com/mods/2.png"));

        var entry = Assert.Single(log.Entries);
        Assert.Contains("HTTP 403 Forbidden", entry.Message);
        Assert.Contains("files.sp-mod.com", entry.Message);
    }

    [AvaloniaFact]
    public async Task LoadAsync_ReportsImagesThatCannotBeDecoded()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not an image</html>", System.Text.Encoding.UTF8, "text/html"),
        });
        var log = new UiActivityLog();
        var loader = new ImageLoader(new HttpClient(handler), log, () => "https://sp-mod.com");

        Assert.Null(await loader.LoadAsync("https://files.sp-mod.com/mods/3.png"));
        Assert.Contains("could not be decoded (text/html)", Assert.Single(log.Entries).Message);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
