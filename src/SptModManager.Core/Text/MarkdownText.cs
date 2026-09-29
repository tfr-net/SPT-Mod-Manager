using System.Text.RegularExpressions;

namespace SptModManager.Core.Text;

public static partial class MarkdownText
{
    /// <summary>
    /// Makes GitHub release notes readable as plain text: headings lose their hashes, list markers become bullets,
    /// and emphasis/code markers and link syntax are dropped.
    /// </summary>
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            line = Heading().Replace(line, string.Empty);
            line = Bullet().Replace(line, "$1• ");
            line = Link().Replace(line, "$1 ($2)");
            line = StarEmphasis().Replace(line, "$2");
            line = UnderscoreEmphasis().Replace(line, "$2");
            line = line.Replace("`", string.Empty);
            return line.TrimEnd();
        });

        var text = string.Join('\n', lines);
        text = ManyNewlines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[*+-]\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(\*\*|\*)(\S(?:.*?\S)?)\1")]
    private static partial Regex StarEmphasis();

    // Underscores only count at word boundaries so identifiers like snake_case survive.
    [GeneratedRegex(@"(?<!\w)(__|_)(\S(?:.*?\S)?)\1(?!\w)")]
    private static partial Regex UnderscoreEmphasis();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();
}
