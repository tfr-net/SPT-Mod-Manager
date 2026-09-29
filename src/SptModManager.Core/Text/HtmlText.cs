using System.Net;
using System.Text.RegularExpressions;

namespace SptModManager.Core.Text;

public static partial class HtmlText
{
    /// <summary>
    /// Converts Forge's HTML descriptions into readable plain text: block elements become line breaks, list items get
    /// bullets, tags are dropped and entities decoded.
    /// </summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = ScriptOrStyle().Replace(html, string.Empty);
        text = LineBreak().Replace(text, "\n");
        text = ListItem().Replace(text, "\n• ");
        text = BlockEnd().Replace(text, "\n\n");
        text = Tag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = SpacesBeforeNewline().Replace(text, "\n");
        text = ManyNewlines().Replace(text, "\n\n");

        return text.Trim();
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"</(p|div|h[1-6]|ul|ol|blockquote|pre|table|tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex SpacesBeforeNewline();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();
}
