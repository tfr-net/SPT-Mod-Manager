namespace SptModManager.Core.Versioning;

/// <summary>
/// SPT patch releases keep mod compatibility within a minor version: a mod made for 4.1.4 works on 4.1.4, 4.1.5 and
/// every later 4.1.x. The Forge links a mod version only to the exact SPT versions its author's constraint names, so
/// the manager widens every compatibility question to "any patch of this minor up to the installed one".
/// </summary>
public static class SptCompatibility
{
    /// <summary>
    /// True when <paramref name="constraint"/> accepts the installed SPT version or any earlier patch of the same
    /// minor version (so "4.1.4" is compatible with an installed 4.1.6, but "4.1.6" is not compatible with 4.1.4).
    /// </summary>
    public static bool IsCompatible(string? installedSptVersion, string? constraint)
    {
        var installed = VersionUtil.TryParse(installedSptVersion);
        if (installed is null || string.IsNullOrWhiteSpace(constraint))
        {
            return false;
        }

        for (var patch = installed.Patch; patch >= 0; patch--)
        {
            if (VersionUtil.Satisfies($"{installed.Major}.{installed.Minor}.{patch}", constraint))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first later patch of the installed minor version that <paramref name="constraint"/> accepts, e.g. "4.1.5"
    /// for a mod tagged 4.1.5 on an installed 4.1.3. Null when no patch of this minor fits.
    /// </summary>
    public static string? EarliestLaterPatch(string? installedSptVersion, string? constraint, int maxPatchesAhead = 50)
    {
        var installed = VersionUtil.TryParse(installedSptVersion);
        if (installed is null || string.IsNullOrWhiteSpace(constraint))
        {
            return null;
        }

        for (var patch = installed.Patch + 1; patch <= installed.Patch + maxPatchesAhead; patch++)
        {
            var candidate = $"{installed.Major}.{installed.Minor}.{patch}";
            if (VersionUtil.Satisfies(candidate, constraint))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// A Forge filter constraint covering every patch of the installed minor up to the installed version,
    /// e.g. "&gt;=4.1.0 &lt;=4.1.6". Returns null when the version cannot be read.
    /// </summary>
    public static string? ForgeRangeConstraint(string? installedSptVersion)
    {
        var installed = VersionUtil.TryParse(installedSptVersion);
        return installed is null
            ? null
            : $">={installed.Major}.{installed.Minor}.0 <={installed.Major}.{installed.Minor}.{installed.Patch}";
    }

    /// <summary>
    /// Earlier patch releases of the installed minor version from <paramref name="publishedVersions"/>, newest first.
    /// These are the versions to retry Forge lookups against when nothing matches the installed version exactly.
    /// </summary>
    public static IReadOnlyList<string> EarlierPatches(string? installedSptVersion, IEnumerable<string> publishedVersions)
    {
        var installed = VersionUtil.TryParse(installedSptVersion);
        if (installed is null)
        {
            return [];
        }

        return publishedVersions
            .Select(v => (Text: v, Parsed: VersionUtil.TryParse(v)))
            .Where(v => v.Parsed is { IsPreRelease: false }
                        && v.Parsed.Major == installed.Major
                        && v.Parsed.Minor == installed.Minor
                        && v.Parsed.Patch < installed.Patch)
            .OrderByDescending(v => v.Parsed)
            .Select(v => v.Text)
            .Distinct()
            .ToList();
    }
}
