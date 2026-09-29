using System.Net;
using System.Text;
using SptModManager.Core.Spt;
using SptModManager.Core.Tests.Support;

namespace SptModManager.Core.Tests;

public class SptReleaseClientTests
{
    // Trimmed copy of the real 4.1.6 release notes.
    private const string Body416 = """
        #### Requires EFT `0.16.9.5.40743` (released 22nd October 2025)

        You NEED the following runtimes:
        NET Runtime 10.0.9 (https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-10.0.9-windows-x64-installer)

        ## Automatic install instructions
        This is the recommended way to install SPT
        https://sp-mod.com/installer

        ### Direct Download
        https://mirror.sp-tushonka.com/builds/SPT-4.1.6-40743-731d7a2.7z
        md5/b64: V0CKc6gSXCehdd5VQwsK3g==
        """;

    [Fact]
    public void Parse_ExtractsDownloadHashAndClientBuildFromNotes()
    {
        var release = SptReleaseClient.Parse(new SptReleaseClient.GitHubRelease
        {
            TagName = "4.1.6",
            Name = "SPT 4.1.6 (40743)",
            Body = Body416,
            HtmlUrl = "https://github.com/SP-Tushonka/build/releases/tag/4.1.6",
        });

        Assert.NotNull(release);
        Assert.Equal("4.1.6", release.Version);
        Assert.Equal("4.1", release.MajorMinor);
        Assert.Equal("https://mirror.sp-tushonka.com/builds/SPT-4.1.6-40743-731d7a2.7z", release.DownloadUrl);
        Assert.Equal("SPT-4.1.6-40743-731d7a2.7z", release.FileName);
        Assert.Equal("V0CKc6gSXCehdd5VQwsK3g==", release.Md5Base64);
        Assert.Equal("40743", release.ClientBuild);
        Assert.Equal("0.16.9.5.40743", release.RequiredEftVersion);
    }

    [Fact]
    public void Parse_PrefersUploadedAssetWhenPresent()
    {
        var release = SptReleaseClient.Parse(new SptReleaseClient.GitHubRelease
        {
            TagName = "v4.1.7",
            Body = "no links here",
            Assets = [new SptReleaseClient.GitHubAsset { Name = "SPT-4.1.7-40800-abcdef1.7z", BrowserDownloadUrl = "https://github.com/x/y/releases/download/4.1.7/SPT-4.1.7-40800-abcdef1.7z" }],
        });

        Assert.NotNull(release);
        Assert.Equal("4.1.7", release.Version);
        Assert.Equal("40800", release.ClientBuild);
        Assert.EndsWith("SPT-4.1.7-40800-abcdef1.7z", release.DownloadUrl);
        Assert.Null(release.Md5Base64);
    }

    [Fact]
    public void ParseReleases_SkipsDraftsPrereleasesAndBleedingEdgeTags()
    {
        var releases = SptReleaseClient.ParseReleases(
        [
            new() { TagName = "4.1.5", Body = Body416 },
            new() { TagName = "4.1.6", Body = Body416 },
            new() { TagName = "4.2.0", Draft = true },
            new() { TagName = "4.2.0-BE", Body = Body416 },
            new() { TagName = "4.1.7", Prerelease = true },
        ]);

        Assert.Equal(["4.1.6", "4.1.5"], releases.Select(r => r.Version));
    }

    [Fact]
    public async Task GetReleasesAsync_CallsGitHubReleasesApi()
    {
        const string json = """
            [{ "tag_name": "4.1.6", "name": "SPT 4.1.6 (40743)", "body": "https://mirror.sp-tushonka.com/builds/SPT-4.1.6-40743-731d7a2.7z", "draft": false, "prerelease": false, "html_url": "https://github.com/SP-Tushonka/build/releases/tag/4.1.6", "assets": [] }]
            """;

        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var client = new SptReleaseClient(new HttpClient(handler));

        var releases = await client.GetReleasesAsync();

        Assert.Single(releases);
        Assert.Equal("https://api.github.com/repos/SP-Tushonka/build/releases?per_page=30", handler.Requests.Single().ToString());
    }
}
