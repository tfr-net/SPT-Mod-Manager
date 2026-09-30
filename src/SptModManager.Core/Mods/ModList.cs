using System.Text.Json;
using System.Text.Json.Serialization;
using SptModManager.Core.Forge;
using SptModManager.Core.Versioning;

namespace SptModManager.Core.Mods;

public sealed class ModListEntry
{
    public int? ForgeModId { get; set; }

    public string? Guid { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Version { get; set; }

    public string? Url { get; set; }
}

/// <summary>A shareable list of mods ("*.sptmods.json") so friends can install the same set.</summary>
public sealed class ModListFile
{
    public const string FormatId = "spt-mod-manager/mod-list";

    /// <summary>Must be <see cref="FormatId"/>; left empty by default so foreign JSON is rejected on load.</summary>
    public string? Format { get; set; }

    public int FormatVersion { get; set; } = 1;

    public string? Name { get; set; }

    public string? SptVersion { get; set; }

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ModListEntry> Mods { get; set; } = [];
}

public sealed record ModListImportResult(IReadOnlyList<ModInstallRequest> Requests, IReadOnlyList<string> Notes);

public static class ModListService
{
    public const string FileExtension = ".sptmods.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds a mod list from installed mods. Only mods on The Forge are included, since anything else (side DLLs
    /// bundled with another mod, hand-made tweaks) has no download for an importer to fetch. Mods pulled in only as
    /// dependencies are left out too, because importing resolves dependencies again for the importer's SPT version.
    /// </summary>
    public static ModListFile Create(IEnumerable<InstalledMod> mods, string? sptVersion, string? name = null, bool includeDependencies = false)
    {
        return new ModListFile
        {
            Format = ModListFile.FormatId,
            Name = name,
            SptVersion = sptVersion,
            Mods = mods
                .Where(m => includeDependencies || !m.InstalledAsDependency)
                .Where(m => m.ForgeModId is not null)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new ModListEntry
                {
                    ForgeModId = m.ForgeModId,
                    Guid = m.Guid,
                    Name = m.Name,
                    Version = m.Version,
                    Url = m.DetailUrl,
                })
                .ToList(),
        };
    }

    public static async Task SaveAsync(ModListFile list, Stream stream, CancellationToken cancellationToken = default)
    {
        await JsonSerializer.SerializeAsync(stream, list, JsonOptions, cancellationToken);
    }

    public static async Task<ModListFile> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ModListFile? list;
        try
        {
            list = await JsonSerializer.DeserializeAsync<ModListFile>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"That file is not a valid mod list: {e.Message}", e);
        }

        if (list is null || !string.Equals(list.Format, ModListFile.FormatId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("That file is not a mod list exported by SPT Mod Manager.");
        }

        return list;
    }

    /// <summary>
    /// Turns a mod list into install requests for this install. The listed version is used when it exists and works
    /// with <paramref name="sptVersion"/>; otherwise the newest compatible version is picked. Mods already installed at
    /// the listed version are skipped.
    /// </summary>
    public static async Task<ModListImportResult> ResolveAsync(
        ModListFile list,
        ModManager manager,
        IForgeClient forge,
        string sptVersion,
        CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var requests = new List<ModInstallRequest>();

        if (list.SptVersion is not null && VersionUtil.MajorMinor(list.SptVersion) != VersionUtil.MajorMinor(sptVersion))
        {
            notes.Add($"This list was made for SPT {list.SptVersion} and you have {sptVersion}; newer compatible versions will be used where needed.");
        }

        // Look up entries that only carry a GUID so everything can be resolved by Forge ID.
        var guidOnly = list.Mods.Where(m => m.ForgeModId is null && m.Guid is not null).ToList();
        var byGuid = guidOnly.Count == 0
            ? []
            : (await forge.GetModsByGuidsAsync(guidOnly.Select(m => m.Guid!), cancellationToken))
                .Where(f => f.Guid is not null)
                .ToDictionary(f => f.Guid!, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in list.Mods)
        {
            var modId = entry.ForgeModId ?? (entry.Guid is not null && byGuid.TryGetValue(entry.Guid, out var found) ? found.Id : null);
            if (modId is null)
            {
                notes.Add($"{entry.Name} is not on The Forge and has to be installed manually.");
                continue;
            }

            var installed = manager.FindByForgeId(modId.Value) ?? manager.FindByGuid(entry.Guid);
            if (installed is not null && VersionUtil.AreEquivalent(installed.Version, entry.Version))
            {
                continue;
            }

            var compatible = await forge.GetModVersionsAsync(modId.Value, SptCompatibility.ForgeRangeConstraint(sptVersion) ?? sptVersion, cancellationToken);
            var pick = compatible.FirstOrDefault(v => VersionUtil.AreEquivalent(v.Version, entry.Version)) ?? compatible.FirstOrDefault();

            if (pick is null)
            {
                notes.Add($"{entry.Name} has no version compatible with SPT {sptVersion} and was skipped.");
                continue;
            }

            if (entry.Version is not null && !VersionUtil.AreEquivalent(pick.Version, entry.Version))
            {
                notes.Add($"{entry.Name}: listed {entry.Version} is not available for SPT {sptVersion}, using {pick.Version}.");
            }

            if (installed is not null && VersionUtil.AreEquivalent(installed.Version, pick.Version))
            {
                continue;
            }

            requests.Add(new ModInstallRequest(modId.Value, entry.Name, pick.Version, entry.Guid, VersionId: pick.Id, Link: pick.Link,
                ContentLength: pick.ContentLength));
        }

        return new ModListImportResult(requests, notes);
    }
}
