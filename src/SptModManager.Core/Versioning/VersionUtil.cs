using SemVersion = SemanticVersioning.Version;
using SemRange = SemanticVersioning.Range;

namespace SptModManager.Core.Versioning;

public static partial class VersionUtil
{
    /// <summary>
    /// Parses loosely-formatted version strings found in the wild ("v1.2", "1.2.0.0", "1.2.3-beta") into SemVer.
    /// Returns null when the text cannot be interpreted as a version.
    /// </summary>
    public static SemVersion? TryParse(string? text)
    {
        var normalized = Normalize(text);
        if (normalized is null)
        {
            return null;
        }

        return SemVersion.TryParse(normalized, loose: true, out var version) ? version : null;
    }

    /// <summary>
    /// Normalizes a version string to a three-part SemVer string. Four-part .NET versions drop a trailing ".0"
    /// revision ("1.2.3.0" becomes "1.2.3"), and short versions are padded ("1.2" becomes "1.2.0").
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        // Split off prerelease/build metadata so only the numeric core is reshaped.
        var suffixIndex = value.IndexOfAny(['-', '+']);
        var core = suffixIndex >= 0 ? value[..suffixIndex] : value;
        var suffix = suffixIndex >= 0 ? value[suffixIndex..] : string.Empty;

        var parts = core.Split('.');
        if (parts.Length == 0 || parts.Any(p => p.Length == 0 || !p.All(char.IsDigit)))
        {
            return null;
        }

        if (parts.Length == 4 && parts[3] == "0")
        {
            parts = parts[..3];
        }

        if (parts.Length > 3)
        {
            return null;
        }

        while (parts.Length < 3)
        {
            parts = [.. parts, "0"];
        }

        return string.Join('.', parts.Select(p => int.Parse(p).ToString())) + suffix;
    }

    /// <summary>
    /// Compares two version strings, falling back to ordinal comparison when either side is not SemVer.
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var l = TryParse(left);
        var r = TryParse(right);

        if (l is not null && r is not null)
        {
            return l.CompareTo(r);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static bool AreEquivalent(string? left, string? right)
    {
        var l = TryParse(left);
        var r = TryParse(right);

        return l is not null && r is not null
            ? l.Equals(r)
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when <paramref name="version"/> satisfies the npm-style constraint (e.g. "^4.1.0", "~4.1.0").
    /// A missing or unparsable constraint is not satisfied (Forge treats versions without one as legacy).
    /// </summary>
    public static bool Satisfies(string? version, string? constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint))
        {
            return false;
        }

        var parsed = TryParse(version);
        if (parsed is null)
        {
            return false;
        }

        return SemRange.TryParse(ToNpmRange(constraint), loose: true, out var range) && range.IsSatisfied(parsed, includePrerelease: true);
    }

    /// <summary>
    /// The Forge stores Composer-style constraints, which also allow "," for AND and a single "|" for OR. The SemVer
    /// library speaks the npm dialect, so those separators are rewritten.
    /// </summary>
    internal static string ToNpmRange(string constraint)
    {
        var text = CommaAnd().Replace(constraint.Trim(), " ");
        return SinglePipeOr().Replace(text, "||");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*,\s*")]
    private static partial System.Text.RegularExpressions.Regex CommaAnd();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\|)\|(?!\|)")]
    private static partial System.Text.RegularExpressions.Regex SinglePipeOr();

    /// <summary>
    /// "4.1.6" becomes "4.1". Returns null for unparsable input.
    /// </summary>
    public static string? MajorMinor(string? version)
    {
        var parsed = TryParse(version);
        return parsed is null ? null : $"{parsed.Major}.{parsed.Minor}";
    }
}
