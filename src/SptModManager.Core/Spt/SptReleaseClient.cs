using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SptModManager.Core.Versioning;

namespace SptModManager.Core.Spt;

/// <summary>An SPT release from the SP-Tushonka/build GitHub releases page.</summary>
public sealed record SptRelease(
    string Tag,
    string Title,
    string Version,
    DateTimeOffset? PublishedAt,
    string HtmlUrl,
    string? DownloadUrl,
    string? FileName,
    string? Md5Base64,
    string? ClientBuild,
    string? RequiredEftVersion,
    string Notes)
{
    public string MajorMinor => VersionUtil.MajorMinor(Version) ?? Version;
}

public interface ISptReleaseClient
{
    /// <summary>Stable releases, newest first.</summary>
    Task<IReadOnlyList<SptRelease>> GetReleasesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads SPT releases from GitHub. The build repo does not upload the .7z to GitHub; the release notes carry a
/// "Direct Download" mirror link and an "md5/b64" hash, which are parsed out here.
/// </summary>
public sealed partial class SptReleaseClient(HttpClient http, string repository = SptReleaseClient.DefaultRepository) : ISptReleaseClient
{
    public const string DefaultRepository = "SP-Tushonka/build";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<SptRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases?per_page=30");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub returned HTTP {(int)response.StatusCode} for the {repository} releases.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonOptions) ?? [];

        return ParseReleases(releases);
    }

    internal static IReadOnlyList<SptRelease> ParseReleases(IEnumerable<GitHubRelease> releases)
    {
        return releases
            .Where(r => !r.Draft && !r.Prerelease)
            .Select(Parse)
            .OfType<SptRelease>()
            .OrderByDescending(r => VersionUtil.TryParse(r.Version))
            .ToList();
    }

    internal static SptRelease? Parse(GitHubRelease release)
    {
        var version = VersionUtil.Normalize(release.TagName);

        // Only plain release tags ("4.1.6" / "v4.1.6"); bleeding edge and debug builds carry suffixes.
        if (version is null || version.Contains('-'))
        {
            return null;
        }

        var body = release.Body ?? string.Empty;
        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
        var bodyLink = DirectDownloadRegex().Match(body);
        var downloadUrl = asset?.BrowserDownloadUrl ?? (bodyLink.Success ? bodyLink.Value : null);

        var fileName = asset?.Name ?? (downloadUrl is null ? null : Uri.UnescapeDataString(Path.GetFileName(new Uri(downloadUrl).AbsolutePath)));
        var md5 = Md5Regex().Match(body) is { Success: true } hash ? hash.Groups[1].Value : null;
        var eft = RequiredEftRegex().Match(body) is { Success: true } e ? e.Groups[1].Value : null;

        // "SPT-4.1.6-40743-731d7a2.7z" -> client build 40743; fall back to the EFT version's last segment.
        var clientBuild = fileName is not null && FileNameRegex().Match(fileName) is { Success: true } f
            ? f.Groups["client"].Value
            : eft?.Split('.').LastOrDefault();

        return new SptRelease(
            release.TagName,
            string.IsNullOrWhiteSpace(release.Name) ? $"SPT {version}" : release.Name,
            version,
            release.PublishedAt,
            release.HtmlUrl,
            downloadUrl,
            fileName,
            md5,
            clientBuild,
            eft,
            body);
    }

    [GeneratedRegex(@"https?://[^\s<>()""'`]+?\.7z\b", RegexOptions.IgnoreCase)]
    private static partial Regex DirectDownloadRegex();

    [GeneratedRegex(@"md5\s*/\s*b64\s*:?\s*`?([A-Za-z0-9+/]{22}==)", RegexOptions.IgnoreCase)]
    private static partial Regex Md5Regex();

    [GeneratedRegex(@"Requires\s+EFT\s*`?\s*([0-9]+(?:\.[0-9]+){2,})", RegexOptions.IgnoreCase)]
    private static partial Regex RequiredEftRegex();

    [GeneratedRegex(@"^SPT-(?<spt>\d+\.\d+\.\d+)-(?<client>\d+)-(?<commit>[0-9a-f]+)\.7z$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        public string? Name { get; init; }

        public string? Body { get; init; }

        public bool Draft { get; init; }

        public bool Prerelease { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        public List<GitHubAsset> Assets { get; init; } = [];
    }

    internal sealed class GitHubAsset
    {
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;

        public long Size { get; init; }
    }
}
