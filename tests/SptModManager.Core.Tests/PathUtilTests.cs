using SptModManager.Core.IO;

namespace SptModManager.Core.Tests;

public class PathUtilTests
{
    [Theory]
    [InlineData(@"a\b\c.dll", "a/b/c.dll")]
    [InlineData("./a//b/./c", "a/b/c")]
    [InlineData("/a/b", "a/b")]
    public void NormalizeRelative_UsesForwardSlashes(string input, string expected)
    {
        Assert.Equal(expected, PathUtil.NormalizeRelative(input));
    }

    [Theory]
    [InlineData("../evil.dll", true)]
    [InlineData("a/../../evil.dll", true)]
    [InlineData("C:/Windows/evil.dll", true)]
    [InlineData("", true)]
    [InlineData("BepInEx/plugins/ok.dll", false)]
    [InlineData("BepInEx/plugins/..ok.dll", false)]
    public void IsUnsafeRelative_CatchesZipSlip(string path, bool unsafePath)
    {
        Assert.Equal(unsafePath, PathUtil.IsUnsafeRelative(path));
    }

    [Fact]
    public void SafeCombine_RefusesToEscapeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.Throws<InvalidOperationException>(() => PathUtil.SafeCombine(root, "../outside.txt"));
        Assert.Equal(Path.Combine(root, "a", "b.txt"), PathUtil.SafeCombine(root, "a/b.txt"));
    }

    [Fact]
    public void PruneEmptyDirectories_StopsAtRootAndNonEmptyFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "sptmm-prune-" + Guid.NewGuid().ToString("N"));
        var deep = Path.Combine(root, "plugins", "Mod", "sub");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(root, "plugins", "keep.txt"), "x");

        try
        {
            PathUtil.PruneEmptyDirectories(deep, Path.Combine(root, "plugins"));

            Assert.False(Directory.Exists(Path.Combine(root, "plugins", "Mod")));
            Assert.True(Directory.Exists(Path.Combine(root, "plugins")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
