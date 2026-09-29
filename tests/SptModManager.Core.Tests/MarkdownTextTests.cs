using SptModManager.Core.Text;

namespace SptModManager.Core.Tests;

public class MarkdownTextTests
{
    [Fact]
    public void ToPlainText_CleansReleaseNotes()
    {
        const string markdown = "#### Requires EFT `0.16.9.5.40743`\n\n### FIXED\n* Server - Fixed **bad** things\n  - nested item\n\n\n\nSee [the wiki](https://wiki.example.com/page) for more. Keep snake_case_names.";

        var text = MarkdownText.ToPlainText(markdown);

        Assert.Equal(
            "Requires EFT 0.16.9.5.40743\n\nFIXED\n• Server - Fixed bad things\n  • nested item\n\nSee the wiki (https://wiki.example.com/page) for more. Keep snake_case_names.",
            text);
    }
}
