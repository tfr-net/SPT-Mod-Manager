using System.Text.Json;
using System.Text.Json.Serialization;
using SptModManager.Core.Spt;

namespace SptModManager.Core.Mods;

public enum InstallSource
{
    /// <summary>Installed or updated through the manager.</summary>
    Manager,

    /// <summary>Found on disk by a scan (installed by hand).</summary>
    Detected,
}

public sealed class InstalledMod
{
    public int? ForgeModId { get; set; }

    public int? ForgeVersionId { get; set; }

    public string? Guid { get; set; }

    /// <summary>GUIDs of the mod's other pieces (e.g. a server mod whose GUID differs from its client plugin).</summary>
    public List<string> ComponentGuids { get; set; } = [];

    public string Name { get; set; } = string.Empty;

    public string? Version { get; set; }

    public string? Slug { get; set; }

    public string? DetailUrl { get; set; }

    public InstallSource Source { get; set; }

    /// <summary>True when pulled in only to satisfy another mod's dependency.</summary>
    public bool InstalledAsDependency { get; set; }

    /// <summary>Forge mod IDs this mod depends on (direct dependencies).</summary>
    public List<int> DependsOn { get; set; } = [];

    /// <summary>Files owned by this mod, relative to the game root.</summary>
    public List<string> Files { get; set; } = [];

    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string Key => ForgeModId is { } id ? $"forge:{id}" : Guid is { } g ? $"guid:{g.ToLowerInvariant()}" : $"name:{Name}";

    public bool MatchesGuid(string? guid) =>
        guid is not null
        && (string.Equals(Guid, guid, StringComparison.OrdinalIgnoreCase)
            || ComponentGuids.Contains(guid, StringComparer.OrdinalIgnoreCase));
}

public sealed class InstalledModsState
{
    public int SchemaVersion { get; set; } = 1;

    public List<InstalledMod> Mods { get; set; } = [];
}

/// <summary>
/// Persists what the manager knows about installed mods in "&lt;game&gt;/.sptmm/installed.json", so the tracking travels
/// with the install.
/// </summary>
public sealed class InstalledModsStore(SptInstallation install)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath => Path.Combine(install.ManagerPath, "installed.json");

    public InstalledModsState Load()
    {
        if (!File.Exists(FilePath))
        {
            return new InstalledModsState();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<InstalledModsState>(json, JsonOptions) ?? new InstalledModsState();
        }
        catch (JsonException)
        {
            // Keep the broken file for inspection and start fresh; a rescan rebuilds most of it.
            File.Copy(FilePath, FilePath + ".broken", overwrite: true);
            return new InstalledModsState();
        }
    }

    public void Save(InstalledModsState state)
    {
        Directory.CreateDirectory(install.ManagerPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
