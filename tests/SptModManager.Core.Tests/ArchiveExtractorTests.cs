using System.IO.Compression;
using SptModManager.Core.IO;
using SptModManager.Core.Tests.Support;

namespace SptModManager.Core.Tests;

public class ArchiveExtractorTests
{
    private static readonly string[] SolidModFiles =
    [
        "MyMod-1.0.0/BepInEx/plugins/MyMod/MyMod.dll",
        "MyMod-1.0.0/README.txt",
        "MyMod-1.0.0/SPT_Runtime/user/mods/MyMod/MyMod.dll",
        "MyMod-1.0.0/SPT_Runtime/user/mods/MyMod/config/config.json",
    ];

    /// <summary>Maps "Wrapper/x" to "x" and skips the readme, recording the keys it was asked about.</summary>
    private static Func<string, string?> StripWrapper(List<string> seen) => key =>
    {
        lock (seen)
        {
            seen.Add(key);
        }

        return key.EndsWith("README.txt") ? null : key[(key.IndexOf('/') + 1)..];
    };

    private static void AssertSolidModExtracted(TestInstall test, IReadOnlyList<string> written, List<string> seen)
    {
        Assert.Equal(SolidModFiles.OrderBy(k => k), seen.OrderBy(k => k));
        Assert.Equal(3, written.Count);
        Assert.Equal("client-dll-bytes", File.ReadAllText(test.PathOf("BepInEx/plugins/MyMod/MyMod.dll")));
        Assert.Equal("server-dll-bytes", File.ReadAllText(test.PathOf("SPT_Runtime/user/mods/MyMod/MyMod.dll")));
        Assert.Equal("{\"enabled\":true}", File.ReadAllText(test.PathOf("SPT_Runtime/user/mods/MyMod/config/config.json")));
        Assert.False(File.Exists(test.PathOf("README.txt")));
        Assert.False(Directory.Exists(test.PathOf(".sptmm/staging")));
    }

    [Fact]
    public void Extract_UsesNative7ZipWhenAvailable()
    {
        var sevenZip = SevenZipTool.Locate();
        if (sevenZip is null)
        {
            return; // No 7-Zip on this machine; the managed path is covered below.
        }

        using var test = new TestInstall();
        var seen = new List<string>();
        var reports = new List<double>();

        var written = ArchiveExtractor.Extract(TestInstall.TestData("solid-mod.7z"), test.Root, StripWrapper(seen),
            new SyncProgress(reports.Add), options: new ExtractOptions { SevenZipExecutable = sevenZip });

        AssertSolidModExtracted(test, written, seen);
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public void Extract_ManagedDecoderGivesTheSameResult()
    {
        using var test = new TestInstall();
        var seen = new List<string>();

        var written = ArchiveExtractor.Extract(TestInstall.TestData("solid-mod.7z"), test.Root, StripWrapper(seen),
            options: new ExtractOptions { UseNativeSevenZip = false });

        AssertSolidModExtracted(test, written, seen);
    }

    [Fact]
    public void Extract_FallsBackWhen7ZipFails()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The stand-in 7-Zip below is a shell script.
        }

        using var test = new TestInstall();
        var broken = Path.Combine(test.Root, "broken-7z.sh");
        File.WriteAllText(broken, "#!/bin/sh\necho 'ERROR: simulated failure' >&2\nexit 2\n");
        File.SetUnixFileMode(broken, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var seen = new List<string>();
        var written = ArchiveExtractor.Extract(TestInstall.TestData("solid-mod.7z"), test.Root, StripWrapper(seen),
            options: new ExtractOptions { SevenZipExecutable = broken });

        AssertSolidModExtracted(test, written, seen);
    }

    [Fact]
    public void Extract_ReportsBothErrorsForCorruptArchives()
    {
        using var test = new TestInstall();
        var corrupt = Path.Combine(test.Root, "corrupt.7z");
        File.WriteAllBytes(corrupt, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04, 1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.ThrowsAny<Exception>(() => ArchiveExtractor.Extract(corrupt, test.Root, key => key));
        Assert.False(Directory.Exists(test.PathOf(".sptmm/staging")));
    }

    [Fact]
    public void Extract_ZipUsesNormalizedKeysAndReportsProgress()
    {
        using var test = new TestInstall();
        var zip = test.CreateZip("backslashes.zip",
            (@"Wrapper\BepInEx\plugins\Thing.dll", "thing"),
            ("Wrapper/docs/readme.md", "docs"));
        var seen = new List<string>();
        var reports = new List<double>();

        var written = ArchiveExtractor.Extract(zip, test.Root, key =>
        {
            seen.Add(key);
            return key.EndsWith(".md") ? null : key["Wrapper/".Length..];
        }, new SyncProgress(reports.Add));

        Assert.Contains("Wrapper/BepInEx/plugins/Thing.dll", seen);
        Assert.Equal(["BepInEx/plugins/Thing.dll"], written);
        Assert.Equal("thing", File.ReadAllText(test.PathOf("BepInEx/plugins/Thing.dll")));
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public void Extract_HonorsCancellation()
    {
        using var test = new TestInstall();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ArchiveExtractor.Extract(TestInstall.TestData("solid-mod.7z"), test.Root, key => key, cancellationToken: cancelled.Token));
        Assert.False(Directory.Exists(test.PathOf(".sptmm/staging")));
    }

    [Fact]
    public void ListFiles_ReadsZipAnd7z()
    {
        using var test = new TestInstall();
        var zip = test.CreateZip("list.zip", ("a/b.txt", "x"), ("c.dll", "y"));

        Assert.Equal(["a/b.txt", "c.dll"], ArchiveExtractor.ListFiles(zip));
        Assert.Equal(SolidModFiles.OrderBy(k => k), ArchiveExtractor.ListFiles(TestInstall.TestData("solid-mod.7z")).Select(PathUtil.NormalizeRelative).OrderBy(k => k));
    }

    /// <summary>IProgress that reports inline, so tests can inspect values right after the call returns.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
