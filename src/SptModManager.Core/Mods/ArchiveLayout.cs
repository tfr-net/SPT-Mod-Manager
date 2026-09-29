using SptModManager.Core.IO;
using SptModManager.Core.Spt;

namespace SptModManager.Core.Mods;

public sealed class ArchiveLayoutResult
{
    /// <summary>Archive entry key to target path relative to the game root (forward slashes).</summary>
    public Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);

    public List<string> Skipped { get; } = [];

    public List<string> Warnings { get; } = [];

    public string? Error { get; set; }

    public bool Success => Error is null && Map.Count > 0;
}

/// <summary>
/// Works out where each file in a mod archive belongs inside the game folder. Forge archives are meant to be
/// extracted straight into the game root (BepInEx/... and SPT_Runtime/user/mods/...), but real-world archives often
/// add a wrapper folder, use the SPT 4.0 "SPT/" root, or the old root-level "user/mods/" layout.
/// </summary>
public static class ArchiveLayout
{
    private static readonly HashSet<string> DocumentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".pdf", ".url", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".html", ".htm", ".rtf", ".docx",
    };

    private static readonly string[] BepInExKnownChildren = ["plugins", "patchers", "config", "core"];

    public static ArchiveLayoutResult Plan(IEnumerable<string> fileEntryKeys, string dataFolderName)
    {
        var result = new ArchiveLayoutResult();
        var entries = new List<(string Key, string[] Segments)>();

        foreach (var key in fileEntryKeys)
        {
            var normalized = PathUtil.NormalizeRelative(key);
            if (PathUtil.IsUnsafeRelative(normalized))
            {
                result.Skipped.Add(key);
                result.Warnings.Add($"Skipped unsafe path in archive: {key}");
                continue;
            }

            entries.Add((key, normalized.Split('/')));
        }

        if (entries.Count == 0)
        {
            result.Error = "The archive is empty.";
            return result;
        }

        var wrapper = FindWrapperPrefix(entries.Select(e => e.Segments));
        var unrecognized = new List<(string Key, string[] Segments)>();
        var remappedLegacyRoot = false;

        foreach (var (key, fullSegments) in entries)
        {
            var segments = StartsWith(fullSegments, wrapper) ? fullSegments[wrapper.Length..] : fullSegments;
            var target = MapKnownRoot(segments, dataFolderName, ref remappedLegacyRoot);

            if (target is not null)
            {
                result.Map[key] = target;
            }
            else if (IsDocumentation(segments))
            {
                result.Skipped.Add(key);
            }
            else
            {
                unrecognized.Add((key, segments));
            }
        }

        if (remappedLegacyRoot)
        {
            result.Warnings.Add($"The archive uses a different SPT folder layout; server files were placed in {dataFolderName}/user/mods.");
        }

        if (result.Map.Count == 0)
        {
            // Fallback: an archive of bare DLLs is almost always a BepInEx client plugin.
            if (unrecognized.Count > 0 && unrecognized.All(e => e.Segments[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                                                 || e.Segments[^1].EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var (key, segments) in unrecognized)
                {
                    result.Map[key] = "BepInEx/plugins/" + string.Join('/', segments);
                }

                result.Warnings.Add("The archive has no folder structure; its DLLs were treated as BepInEx plugins.");
                return result;
            }

            result.Error = "Could not work out where this archive's files belong (no BepInEx or user/mods folders found). Install it manually.";
            return result;
        }

        foreach (var (key, segments) in unrecognized)
        {
            result.Skipped.Add(key);
            result.Warnings.Add($"Skipped file outside BepInEx and user/mods: {string.Join('/', segments)}");
        }

        return result;
    }

    private static string? MapKnownRoot(string[] segments, string dataFolderName, ref bool remappedLegacyRoot)
    {
        if (segments.Length < 2)
        {
            return null;
        }

        var first = segments[0];

        if (first.Equals("BepInEx", StringComparison.OrdinalIgnoreCase))
        {
            // Canonical casing matters on case-sensitive filesystems (Linux/Proton installs).
            var rest = segments[1..].ToArray();
            var knownChild = BepInExKnownChildren.FirstOrDefault(c => c.Equals(rest[0], StringComparison.OrdinalIgnoreCase));
            if (knownChild is not null && rest.Length > 1)
            {
                rest[0] = knownChild;
            }

            return "BepInEx/" + string.Join('/', rest);
        }

        if (IsRuntimeRoot(first) && segments.Length >= 3)
        {
            if (!first.Equals(dataFolderName, StringComparison.OrdinalIgnoreCase))
            {
                remappedLegacyRoot = true;
            }

            return dataFolderName + "/" + CanonicalUserPath(segments[1..]);
        }

        if (first.Equals("user", StringComparison.OrdinalIgnoreCase) && segments.Length >= 3)
        {
            return dataFolderName + "/" + CanonicalUserPath(segments);
        }

        return null;
    }

    private static string CanonicalUserPath(string[] segments)
    {
        var copy = segments.ToArray();
        if (copy.Length >= 2 && copy[0].Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            copy[0] = "user";
            if (copy[1].Equals("mods", StringComparison.OrdinalIgnoreCase))
            {
                copy[1] = "mods";
            }
        }

        return string.Join('/', copy);
    }

    private static bool IsRuntimeRoot(string segment) =>
        segment.Equals(SptInstallation.RuntimeFolder41, StringComparison.OrdinalIgnoreCase)
        || segment.Equals(SptInstallation.RuntimeFolder40, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the folder segments that wrap the known roots (e.g. ["MyMod-1.2.0"] for "MyMod-1.2.0/BepInEx/...").
    /// </summary>
    internal static string[] FindWrapperPrefix(IEnumerable<string[]> allSegments)
    {
        var counts = new Dictionary<string, (string[] Prefix, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var segments in allSegments)
        {
            var rootIndex = FindKnownRootIndex(segments);
            if (rootIndex < 0)
            {
                continue;
            }

            var prefix = segments[..rootIndex];
            var key = string.Join('/', prefix);
            counts[key] = counts.TryGetValue(key, out var existing) ? (existing.Prefix, existing.Count + 1) : (prefix, 1);
        }

        if (counts.Count == 0)
        {
            return [];
        }

        var best = counts.Values.OrderByDescending(c => c.Count).ThenBy(c => c.Prefix.Length).First();
        return best.Prefix.Length <= 3 ? best.Prefix : [];
    }

    private static int FindKnownRootIndex(string[] segments)
    {
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            var next = segments[i + 1];

            if (segment.Equals("BepInEx", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            if (IsRuntimeRoot(segment) && (next.Equals("user", StringComparison.OrdinalIgnoreCase) || next.Equals("SPT_Data", StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }

            if (segment.Equals("user", StringComparison.OrdinalIgnoreCase) && next.Equals("mods", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool StartsWith(string[] segments, string[] prefix)
    {
        if (prefix.Length == 0 || segments.Length <= prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!segments[i].Equals(prefix[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDocumentation(string[] segments)
    {
        var name = segments[^1];
        return DocumentationExtensions.Contains(Path.GetExtension(name))
               || name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
               || name.Equals("README", StringComparison.OrdinalIgnoreCase);
    }
}
