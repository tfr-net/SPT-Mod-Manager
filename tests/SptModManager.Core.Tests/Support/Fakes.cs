using SptModManager.Core.Forge;
using SptModManager.Core.Logging;
using SptModManager.Core.Net;

namespace SptModManager.Core.Tests.Support;

public sealed class RecordingLog : IActivityLog
{
    public List<(ActivityLevel Level, string Message)> Entries { get; } = [];

    public void Write(ActivityLevel level, string message) => Entries.Add((level, message));
}

/// <summary>Serves downloads from local files keyed by URL.</summary>
public sealed class FakeDownloader : IFileDownloader
{
    public Dictionary<string, string> Files { get; } = new();

    public List<string> Requested { get; } = [];

    public Task<DownloadResult> DownloadAsync(string url, string destinationDirectory, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Requested.Add(url);
        Directory.CreateDirectory(destinationDirectory);

        var source = Files[url];
        var target = Path.Combine(destinationDirectory, Guid.NewGuid().ToString("N") + Path.GetExtension(source));
        File.Copy(source, target);

        using var md5 = System.Security.Cryptography.MD5.Create();
        using var stream = File.OpenRead(target);
        var hash = Convert.ToBase64String(md5.ComputeHash(stream));

        return Task.FromResult(new DownloadResult(target, Path.GetFileName(source), new FileInfo(target).Length, hash));
    }
}

/// <summary>In-memory Forge with just enough behaviour for the manager tests.</summary>
public sealed class FakeForge : IForgeClient
{
    public List<ForgeMod> Mods { get; } = [];

    public Dictionary<int, List<ForgeModVersion>> Versions { get; } = new();

    public Dictionary<string, List<ForgeDependencyNode>> DependencyTrees { get; } = new();

    public Func<IReadOnlyList<ModVersionPair>, ForgeUpdateCheck>? UpdateHandler { get; set; }

    public Dictionary<(int, int), ForgeFileTree> FileTrees { get; } = new();

    public List<string> Calls { get; } = [];

    public Task<ForgePage<ForgeMod>> SearchModsAsync(ModSearchQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ForgePage<ForgeMod>(Mods, 1, 1, Mods.Count));

    public Task<ForgeMod?> GetModAsync(int modId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Mods.FirstOrDefault(m => m.Id == modId));

    public Task<IReadOnlyList<ForgeMod>> GetModsByGuidsAsync(IEnumerable<string> guids, CancellationToken cancellationToken = default)
    {
        var set = guids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Calls.Add("guids:" + string.Join(',', set.Order()));
        return Task.FromResult<IReadOnlyList<ForgeMod>>(Mods.Where(m => m.Guid is not null && set.Contains(m.Guid)).ToList());
    }

    public Task<IReadOnlyList<ForgeMod>> GetModsByIdsAsync(IEnumerable<int> modIds, CancellationToken cancellationToken = default)
    {
        var set = modIds.ToHashSet();
        return Task.FromResult<IReadOnlyList<ForgeMod>>(Mods.Where(m => set.Contains(m.Id)).ToList());
    }

    public Task<IReadOnlyList<ForgeModVersion>> GetModVersionsAsync(int modId, string? sptVersion = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"versions:{modId}:{sptVersion}");
        var versions = Versions.GetValueOrDefault(modId) ?? [];
        if (sptVersion is not null)
        {
            versions = versions.Where(v => Versioning.VersionUtil.Satisfies(sptVersion, v.SptVersionConstraint)).ToList();
        }

        return Task.FromResult<IReadOnlyList<ForgeModVersion>>(versions);
    }

    public Task<IReadOnlyDictionary<string, List<ForgeDependencyNode>>> ResolveDependenciesAsync(IEnumerable<ModVersionPair> mods, string sptVersion, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, List<ForgeDependencyNode>>();
        foreach (var pair in mods)
        {
            Calls.Add("deps:" + pair);
            result[pair.ToString()] = DependencyTrees.GetValueOrDefault(pair.ToString()) ?? [];
        }

        return Task.FromResult<IReadOnlyDictionary<string, List<ForgeDependencyNode>>>(result);
    }

    public Task<ForgeUpdateCheck> CheckUpdatesAsync(IEnumerable<ModVersionPair> installed, string sptVersion, CancellationToken cancellationToken = default)
    {
        var pairs = installed.ToList();
        Calls.Add("updates:" + string.Join(',', pairs));
        return Task.FromResult(UpdateHandler?.Invoke(pairs) ?? new ForgeUpdateCheck { SptVersion = sptVersion });
    }

    public Task<ForgeFileTree?> GetFileTreeAsync(int modId, int versionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(FileTrees.GetValueOrDefault((modId, versionId)));

    public Task<IReadOnlyList<ForgeSptVersion>> GetSptVersionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ForgeSptVersion>>([]);

    public Task<IReadOnlyList<ForgeCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ForgeCategory>>([]);
}

/// <summary>HttpMessageHandler that answers from a callback, recording each request URL.</summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }
}
