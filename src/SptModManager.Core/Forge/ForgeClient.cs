using System.Net;
using System.Text;
using System.Text.Json;

namespace SptModManager.Core.Forge;

public enum ModSort
{
    Featured,
    Downloads,
    RecentlyUpdated,
    Newest,
    Name,
}

public sealed record ModSearchQuery
{
    public string? Text { get; init; }

    public string? CategorySlug { get; init; }

    /// <summary>
    /// SPT version constraint (e.g. "&gt;=4.1.0 &lt;=4.1.6") to only show mods that support a matching SPT version.
    /// </summary>
    public string? SptVersionConstraint { get; init; }

    public bool FikaCompatibleOnly { get; init; }

    public ModSort Sort { get; init; } = ModSort.Featured;

    public int Page { get; init; } = 1;

    public int PerPage { get; init; } = 20;
}

/// <summary>An "identifier:version" pair, where the identifier is a Forge mod ID or GUID.</summary>
public sealed record ModVersionPair(string Identifier, string Version)
{
    public override string ToString() => $"{Identifier}:{Version}";
}

public interface IForgeClient
{
    Task<ForgePage<ForgeMod>> SearchModsAsync(ModSearchQuery query, CancellationToken cancellationToken = default);

    Task<ForgeMod?> GetModAsync(int modId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ForgeMod>> GetModsByGuidsAsync(IEnumerable<string> guids, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ForgeMod>> GetModsByIdsAsync(IEnumerable<int> modIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// All published versions of a mod, newest first, optionally limited to versions supporting an SPT version that
    /// matches <paramref name="sptVersionConstraint"/> (an exact version or a range such as "&gt;=4.1.0 &lt;=4.1.6").
    /// </summary>
    Task<IReadOnlyList<ForgeModVersion>> GetModVersionsAsync(int modId, string? sptVersionConstraint = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, List<ForgeDependencyNode>>> ResolveDependenciesAsync(
        IEnumerable<ModVersionPair> mods,
        string sptVersion,
        CancellationToken cancellationToken = default);

    Task<ForgeUpdateCheck> CheckUpdatesAsync(IEnumerable<ModVersionPair> installed, string sptVersion, CancellationToken cancellationToken = default);

    Task<ForgeFileTree?> GetFileTreeAsync(int modId, int versionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ForgeSptVersion>> GetSptVersionsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ForgeCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default);
}

public sealed class ForgeApiException(string message, HttpStatusCode? statusCode = null, string? code = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    public string? Code { get; } = code;
}

/// <summary>
/// Client for The Forge API v0. The API is open and read-only (no key), rate limited per IP.
/// </summary>
public sealed class ForgeClient : IForgeClient
{
    public const string DefaultBaseUrl = "https://sp-mod.com";

    /// <summary>The API caps per_page at 50.</summary>
    private const int MaxPerPage = 50;

    /// <summary>Keep query strings comfortably below common URL length limits.</summary>
    private const int MaxQueryLength = 6000;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _apiBase;

    public ForgeClient(HttpClient http, string baseUrl = DefaultBaseUrl)
    {
        _http = http;
        _apiBase = baseUrl.TrimEnd('/') + "/api/v0";
    }

    public async Task<ForgePage<ForgeMod>> SearchModsAsync(ModSearchQuery query, CancellationToken cancellationToken = default)
    {
        var args = new List<KeyValuePair<string, string>>
        {
            new("include", "versions,category"),
            new("page", Math.Max(1, query.Page).ToString()),
            new("per_page", Math.Clamp(query.PerPage, 1, MaxPerPage).ToString()),
        };

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            args.Add(new("query", query.Text.Trim()));
        }
        else
        {
            // Relevance ordering applies to text searches, so explicit sorting is only sent without one.
            args.Add(new("sort", query.Sort switch
            {
                ModSort.Downloads => "-downloads",
                ModSort.RecentlyUpdated => "-updated_at",
                ModSort.Newest => "-published_at",
                ModSort.Name => "name",
                _ => "-featured,-downloads",
            }));
        }

        if (!string.IsNullOrWhiteSpace(query.CategorySlug))
        {
            args.Add(new("filter[category_slug]", query.CategorySlug));
        }

        if (!string.IsNullOrWhiteSpace(query.SptVersionConstraint))
        {
            args.Add(new("filter[spt_version]", query.SptVersionConstraint));
        }

        if (query.FikaCompatibleOnly)
        {
            args.Add(new("filter[fika_compatibility]", "true"));
        }

        var envelope = await GetAsync<List<ForgeMod>>("/mods", args, cancellationToken);
        return ToPage(envelope);
    }

    public async Task<ForgeMod?> GetModAsync(int modId, CancellationToken cancellationToken = default)
    {
        try
        {
            var envelope = await GetAsync<ForgeMod>(
                $"/mod/{modId}",
                [new("include", "versions,category,license,source_code_links")],
                cancellationToken);

            return envelope.Data;
        }
        catch (ForgeApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<ForgeMod>> GetModsByGuidsAsync(IEnumerable<string> guids, CancellationToken cancellationToken = default)
    {
        var distinct = guids.Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return GetModsByFilterAsync("filter[guid]", distinct, cancellationToken);
    }

    public Task<IReadOnlyList<ForgeMod>> GetModsByIdsAsync(IEnumerable<int> modIds, CancellationToken cancellationToken = default)
    {
        var distinct = modIds.Distinct().Select(i => i.ToString()).ToList();
        return GetModsByFilterAsync("filter[id]", distinct, cancellationToken);
    }

    public async Task<IReadOnlyList<ForgeModVersion>> GetModVersionsAsync(int modId, string? sptVersionConstraint = null, CancellationToken cancellationToken = default)
    {
        var results = new List<ForgeModVersion>();
        var page = 1;

        while (true)
        {
            var args = new List<KeyValuePair<string, string>>
            {
                new("include", "virus_total_links"),
                new("page", page.ToString()),
                new("per_page", MaxPerPage.ToString()),
            };

            if (!string.IsNullOrWhiteSpace(sptVersionConstraint))
            {
                args.Add(new("filter[spt_version]", sptVersionConstraint));
            }

            var envelope = await GetAsync<List<ForgeModVersion>>($"/mod/{modId}/versions", args, cancellationToken);
            results.AddRange(envelope.Data ?? []);

            if (envelope.Meta is null || envelope.Meta.CurrentPage >= envelope.Meta.LastPage || page >= 20)
            {
                break;
            }

            page++;
        }

        results.Sort((a, b) => Versioning.VersionUtil.Compare(b.Version, a.Version));
        return results;
    }

    public async Task<IReadOnlyDictionary<string, List<ForgeDependencyNode>>> ResolveDependenciesAsync(
        IEnumerable<ModVersionPair> mods,
        string sptVersion,
        CancellationToken cancellationToken = default)
    {
        var pairs = mods.Select(m => m.ToString()).Distinct().ToList();
        if (pairs.Count == 0)
        {
            return new Dictionary<string, List<ForgeDependencyNode>>();
        }

        var envelope = await GetAsync<Dictionary<string, List<ForgeDependencyNode>>>(
            "/mods/dependencies",
            [new("mods", string.Join(',', pairs)), new("spt_version", sptVersion)],
            cancellationToken);

        return envelope.Data ?? new Dictionary<string, List<ForgeDependencyNode>>();
    }

    public async Task<ForgeUpdateCheck> CheckUpdatesAsync(IEnumerable<ModVersionPair> installed, string sptVersion, CancellationToken cancellationToken = default)
    {
        var pairs = installed.Select(m => m.ToString()).Distinct().ToList();
        var combined = new ForgeUpdateCheck { SptVersion = sptVersion };

        if (pairs.Count == 0)
        {
            return combined;
        }

        // The whole installed set is sent at once so Forge can check cross-mod constraints; chunk only if huge.
        foreach (var chunk in ChunkByLength(pairs, MaxQueryLength))
        {
            var envelope = await GetAsync<ForgeUpdateCheck>(
                "/mods/updates",
                [new("mods", string.Join(',', chunk)), new("spt_version", sptVersion)],
                cancellationToken);

            var data = envelope.Data;
            if (data is null)
            {
                continue;
            }

            combined.Updates.AddRange(data.Updates);
            combined.BlockedUpdates.AddRange(data.BlockedUpdates);
            combined.UpToDate.AddRange(data.UpToDate);
            combined.IncompatibleWithSpt.AddRange(data.IncompatibleWithSpt);
        }

        return combined;
    }

    public async Task<ForgeFileTree?> GetFileTreeAsync(int modId, int versionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var envelope = await GetAsync<ForgeFileTree>($"/mod/{modId}/versions/{versionId}/file-tree", [], cancellationToken);
            return envelope.Data;
        }
        catch (ForgeApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ForgeSptVersion>> GetSptVersionsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ForgeSptVersion>();
        for (var page = 1; page <= 10; page++)
        {
            var envelope = await GetAsync<List<ForgeSptVersion>>(
                "/spt/versions",
                [new("page", page.ToString()), new("per_page", MaxPerPage.ToString())],
                cancellationToken);

            results.AddRange(envelope.Data ?? []);
            if (envelope.Meta is null || envelope.Meta.CurrentPage >= envelope.Meta.LastPage)
            {
                break;
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<ForgeCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await GetAsync<List<ForgeCategory>>("/mod-categories", [new("per_page", "100"), new("sort", "title")], cancellationToken);
        return envelope.Data ?? [];
    }

    private async Task<IReadOnlyList<ForgeMod>> GetModsByFilterAsync(string filterName, List<string> values, CancellationToken cancellationToken)
    {
        var results = new List<ForgeMod>();

        // Batch values so each request stays well under per_page and URL length limits.
        foreach (var batch in values.Chunk(MaxPerPage))
        {
            var envelope = await GetAsync<List<ForgeMod>>(
                "/mods",
                [
                    new(filterName, string.Join(',', batch)),
                    new("include", "versions,category"),
                    new("filter[include_legacy]", "true"),
                    new("per_page", MaxPerPage.ToString()),
                ],
                cancellationToken);

            results.AddRange(envelope.Data ?? []);
        }

        return results;
    }

    private async Task<ForgeEnvelope<T>> GetAsync<T>(string path, IEnumerable<KeyValuePair<string, string>> query, CancellationToken cancellationToken)
    {
        var url = BuildUrl(path, query);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        ForgeEnvelope<T>? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<ForgeEnvelope<T>>(body, JsonOptions);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            // Error pages (e.g. Cloudflare) are not JSON; fall through to the status code error below.
        }
        catch (JsonException e)
        {
            throw new ForgeApiException($"The Forge returned an unexpected response for {path}: {e.Message}", response.StatusCode);
        }

        if (!response.IsSuccessStatusCode || envelope is null || !envelope.Success)
        {
            var message = response.StatusCode == HttpStatusCode.TooManyRequests
                ? "The Forge rate limit was hit. Wait a minute and try again."
                : envelope?.Message ?? $"The Forge request to {path} failed with HTTP {(int)response.StatusCode}.";

            throw new ForgeApiException(message, response.StatusCode, envelope?.Code);
        }

        return envelope;
    }

    internal string BuildUrl(string path, IEnumerable<KeyValuePair<string, string>> query)
    {
        var builder = new StringBuilder(_apiBase).Append(path);
        var first = true;

        foreach (var (key, value) in query)
        {
            builder.Append(first ? '?' : '&');
            first = false;

            // Keep filter brackets readable; Laravel accepts both raw and encoded forms.
            builder.Append(Uri.EscapeDataString(key).Replace("%5B", "[").Replace("%5D", "]"));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value).Replace("%2C", ",").Replace("%3A", ":"));
        }

        return builder.ToString();
    }

    private static ForgePage<T> ToPage<T>(ForgeEnvelope<List<T>> envelope)
    {
        var items = envelope.Data ?? [];
        var meta = envelope.Meta;

        return new ForgePage<T>(items, meta?.CurrentPage ?? 1, meta?.LastPage ?? 1, meta?.Total ?? items.Count);
    }

    private static IEnumerable<List<string>> ChunkByLength(List<string> items, int maxLength)
    {
        var current = new List<string>();
        var length = 0;

        foreach (var item in items)
        {
            if (current.Count > 0 && length + item.Length + 1 > maxLength)
            {
                yield return current;
                current = [];
                length = 0;
            }

            current.Add(item);
            length += item.Length + 1;
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }
}
