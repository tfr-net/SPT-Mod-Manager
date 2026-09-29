using System.Net;
using System.Text;
using SptModManager.Core.Forge;
using SptModManager.Core.Tests.Support;

namespace SptModManager.Core.Tests;

public class ForgeClientTests
{
    private static (ForgeClient Client, StubHttpHandler Handler) Create(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        return (new ForgeClient(new HttpClient(handler), "https://sp-mod.com/"), handler);
    }

    [Fact]
    public async Task SearchModsAsync_BuildsFilterQueryAndParsesPage()
    {
        const string json = """
            {
              "success": true,
              "data": [{
                "id": 12, "guid": "com.example.mod", "name": "Example", "slug": "example", "teaser": "Does things",
                "thumbnail": "https://img/1.png", "downloads": 1234, "detail_url": "https://sp-mod.com/mods/12/example",
                "fika_compatibility": true, "featured": true,
                "owner": { "id": 1, "name": "Author" },
                "category": { "id": 3, "name": "Gameplay", "slug": "gameplay", "color_class": "blue" },
                "versions": [{ "id": 99, "version": "1.2.0", "spt_version_constraint": "~4.1.0", "downloads": 10 }]
              }],
              "meta": { "current_page": 2, "last_page": 5, "per_page": 20, "total": 90 }
            }
            """;

        var (client, handler) = Create(json);
        var page = await client.SearchModsAsync(new ModSearchQuery { SptVersion = "4.1.6", CategorySlug = "gameplay", FikaCompatibleOnly = true, Page = 2, Sort = ModSort.Downloads });

        var mod = Assert.Single(page.Items);
        Assert.Equal("com.example.mod", mod.Guid);
        Assert.Equal("Author", mod.Owner!.Name);
        Assert.Equal("Gameplay", mod.Category!.DisplayName);
        Assert.Equal("~4.1.0", mod.Versions![0].SptVersionConstraint);
        Assert.True(page.HasNext);
        Assert.True(page.HasPrevious);
        Assert.Equal(90, page.Total);

        var url = handler.Requests.Single().ToString();
        Assert.StartsWith("https://sp-mod.com/api/v0/mods?", url);
        Assert.Contains("filter[spt_version]=4.1.6", url);
        Assert.Contains("filter[category_slug]=gameplay", url);
        Assert.Contains("filter[fika_compatibility]=true", url);
        Assert.Contains("sort=-downloads", url);
        Assert.Contains("include=versions,category", url);
    }

    [Fact]
    public async Task SearchModsAsync_UsesQueryInsteadOfSortForText()
    {
        var (client, handler) = Create("""{ "success": true, "data": [], "meta": { "current_page": 1, "last_page": 1, "total": 0 } }""");

        await client.SearchModsAsync(new ModSearchQuery { Text = "raid time" });

        var url = handler.Requests.Single().AbsoluteUri;
        Assert.Contains("query=raid%20time", url);
        Assert.DoesNotContain("sort=", url);
    }

    [Fact]
    public async Task ResolveDependenciesAsync_ParsesNestedTrees()
    {
        const string json = """
            {
              "success": true,
              "data": {
                "com.example.mod:2.0.5": [{
                  "id": 5, "guid": "com.example.dependency", "name": "Dependency Mod", "slug": "dependency-mod",
                  "latest_compatible_version": { "id": 42, "version": "2.1.0", "link": "https://sp-mod.com/mod/download/5/dependency-mod/2.1.0", "content_length": 1048576, "fika_compatibility": "compatible" },
                  "conflict": false,
                  "dependencies": [{ "id": 6, "guid": "com.example.lib", "name": "Lib", "slug": "lib", "latest_compatible_version": null, "conflict": true, "dependencies": [] }]
                }]
              }
            }
            """;

        var (client, handler) = Create(json);
        var trees = await client.ResolveDependenciesAsync([new ModVersionPair("com.example.mod", "2.0.5")], "4.1.6");

        var node = Assert.Single(trees["com.example.mod:2.0.5"]);
        Assert.Equal(42, node.LatestCompatibleVersion!.Id);
        Assert.Equal(1048576, node.LatestCompatibleVersion.ContentLength);
        var child = Assert.Single(node.Dependencies);
        Assert.Null(child.LatestCompatibleVersion);
        Assert.True(child.Conflict);
        Assert.Contains("mods=com.example.mod:2.0.5&spt_version=4.1.6", handler.Requests.Single().ToString());
    }

    [Fact]
    public async Task CheckUpdatesAsync_ParsesAllBuckets()
    {
        const string json = """
            {
              "success": true,
              "data": {
                "spt_version": "4.1.6",
                "updates": [{ "current_version": { "id": 42, "mod_id": 5, "guid": "com.example.mod", "name": "Example", "slug": "example", "version": "1.0.0" },
                              "recommended_version": { "id": 58, "version": "1.5.0", "link": "https://x/1.5.0", "content_length": 100, "fika_compatibility": "compatible", "spt_versions": ["4.1.6"] },
                              "update_reason": "newer_version_available" }],
                "blocked_updates": [{ "current_version": { "id": 99, "mod_id": 20, "guid": "g", "name": "Blocked", "version": "2.0.0" },
                                      "latest_version": { "id": 105, "version": "3.0.0", "spt_versions": ["4.1.6"] },
                                      "block_reason": "dependency_constraint_violation",
                                      "blocking_mods": [{ "mod_id": 15, "mod_guid": "d", "mod_name": "Dependent", "current_version": "1.0.0", "constraint": "^2.0.0", "incompatible_with": "3.0.0" }] }],
                "up_to_date": [{ "id": 125, "mod_id": 25, "guid": "c", "name": "Current", "version": "1.8.0", "spt_versions": ["4.1.6"] }],
                "incompatible_with_spt": [{ "id": 150, "mod_id": 30, "guid": "o", "name": "Old", "version": "1.0.0", "reason": "no_version_for_spt", "latest_compatible_version": null }]
              }
            }
            """;

        var (client, _) = Create(json);
        var result = await client.CheckUpdatesAsync([new ModVersionPair("5", "1.0.0")], "4.1.6");

        Assert.Equal("1.5.0", Assert.Single(result.Updates).RecommendedVersion.Version);
        Assert.Equal("^2.0.0", Assert.Single(Assert.Single(result.BlockedUpdates).BlockingMods).Constraint);
        Assert.Equal(25, Assert.Single(result.UpToDate).ModId);
        Assert.Equal("no_version_for_spt", Assert.Single(result.IncompatibleWithSpt).Reason);
    }

    [Fact]
    public async Task ErrorsSurfaceAsForgeApiException()
    {
        var (client, _) = Create("""{ "success": false, "code": "VALIDATION_FAILED", "message": "SPT version not found or not published." }""", HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<ForgeApiException>(() => client.CheckUpdatesAsync([new ModVersionPair("1", "1.0.0")], "9.9.9"));

        Assert.Equal("SPT version not found or not published.", error.Message);
        Assert.Equal("VALIDATION_FAILED", error.Code);
    }

    [Fact]
    public async Task RateLimitGetsAFriendlyMessage()
    {
        var (client, _) = Create("<html>slow down</html>", HttpStatusCode.TooManyRequests);

        var error = await Assert.ThrowsAsync<ForgeApiException>(() => client.GetCategoriesAsync());

        Assert.Contains("rate limit", error.Message);
    }

    [Fact]
    public async Task GetModAsync_ReturnsNullOn404()
    {
        var (client, _) = Create("""{ "success": false, "code": "NOT_FOUND", "message": "Resource not found." }""", HttpStatusCode.NotFound);

        Assert.Null(await client.GetModAsync(123));
    }

    [Fact]
    public async Task GetModsByGuidsAsync_BatchesByFifty()
    {
        var (client, handler) = Create("""{ "success": true, "data": [] }""");

        await client.GetModsByGuidsAsync(Enumerable.Range(0, 120).Select(i => $"com.mod.{i}"));

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("filter[guid]=", r.ToString()));
    }

    [Fact]
    public async Task GetModVersionsAsync_SortsNewestFirst()
    {
        const string json = """
            { "success": true, "data": [ { "id": 1, "version": "1.9.0" }, { "id": 2, "version": "1.10.0" }, { "id": 3, "version": "1.2.0" } ],
              "meta": { "current_page": 1, "last_page": 1 } }
            """;
        var (client, _) = Create(json);

        var versions = await client.GetModVersionsAsync(7);

        Assert.Equal(["1.10.0", "1.9.0", "1.2.0"], versions.Select(v => v.Version));
    }
}
