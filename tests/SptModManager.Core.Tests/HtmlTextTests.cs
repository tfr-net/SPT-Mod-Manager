using SptModManager.Core.Text;

namespace SptModManager.Core.Tests;

public class HtmlTextTests
{
    [Fact]
    public void ToPlainText_KeepsStructureAndDecodesEntities()
    {
        const string html = "<h2>Features</h2><p>Makes raids &amp; loot better.<br>Really.</p><ul><li>One</li><li>Two</li></ul><script>alert(1)</script>";

        var text = HtmlText.ToPlainText(html);

        Assert.Equal("Features\n\nMakes raids & loot better.\nReally.\n\n• One\n• Two", text);
    }

    [Fact]
    public void ToPlainText_HandlesEmpty()
    {
        Assert.Equal(string.Empty, HtmlText.ToPlainText(null));
        Assert.Equal("plain", HtmlText.ToPlainText("plain"));
    }
}
