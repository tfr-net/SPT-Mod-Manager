using SptModManager.Core.Spt;
using SptModManager.Core.Tests.Support;

namespace SptModManager.Core.Tests;

public class SptUpdaterTests
{
    private static SptRelease Release(string version, string clientBuild = "40743", string? md5 = null, string? url = null) =>
        new($"{version}", $"SPT {version}", version, DateTimeOffset.UtcNow, "https://github.com", url ?? $"https://mirror/SPT-{version}-{clientBuild}-abc.7z",
            $"SPT-{version}-{clientBuild}-abc.7z", md5, clientBuild, null, "notes");

    [Fact]
    public void Evaluate_FindsPatchUpdate()
    {
        var check = SptUpdater.Evaluate("4.1.5", "40743", [Release("4.1.6"), Release("4.1.5")]);

        Assert.Equal(SptUpdateState.PatchAvailable, check.State);
        Assert.True(check.CanUpdate);
        Assert.Equal("4.1.6", check.LatestPatch!.Version);
    }

    [Fact]
    public void Evaluate_BlocksPatchThatNeedsDifferentClient()
    {
        var check = SptUpdater.Evaluate("4.1.5", "40743", [Release("4.1.6", clientBuild: "40999")]);

        Assert.Equal(SptUpdateState.PatchAvailable, check.State);
        Assert.True(check.ClientBuildMismatch);
        Assert.False(check.CanUpdate);
        Assert.Contains("40999", check.Message);
    }

    [Fact]
    public void Evaluate_ReportsNewMinorAsFreshInstall()
    {
        var check = SptUpdater.Evaluate("4.1.6", "40743", [Release("4.2.0", "41000"), Release("4.1.6")]);

        Assert.Equal(SptUpdateState.NewMinorAvailable, check.State);
        Assert.False(check.CanUpdate);
        Assert.Contains("fresh install", check.Message);
    }

    [Fact]
    public void Evaluate_PatchTakesPriorityButMentionsNewMinor()
    {
        var check = SptUpdater.Evaluate("4.1.5", "40743", [Release("4.2.0", "41000"), Release("4.1.6")]);

        Assert.Equal(SptUpdateState.PatchAvailable, check.State);
        Assert.Contains("4.2.0", check.Message);
    }

    [Fact]
    public void Evaluate_UpToDateAndUnknown()
    {
        Assert.Equal(SptUpdateState.UpToDate, SptUpdater.Evaluate("4.1.6", null, [Release("4.1.6")]).State);
        Assert.Equal(SptUpdateState.UnknownInstalledVersion, SptUpdater.Evaluate(null, null, [Release("4.1.6")]).State);
        Assert.Equal(SptUpdateState.NoReleases, SptUpdater.Evaluate("4.1.6", null, []).State);
    }

    [Fact]
    public async Task UpdateAsync_ExtractsOverInstallAndBacksUpProfiles()
    {
        using var test = new TestInstall();
        test.WriteFile("SPT_Runtime/user/profiles/abc.json", "{\"profile\":1}");
        test.WriteFile("SPT_Runtime/SPT.Server.exe", "old server");

        var archive = test.CreateZip("SPT-4.1.6-40743-abc.zip",
            ("SPT_Runtime/SPT.Server.exe", "new server"),
            ("SPT_Runtime/user/profiles/abc.json", "{\"profile\":\"clobbered\"}"),
            ("BepInEx/core/BepInEx.dll", "bepinex"));

        var release = Release("4.1.6", url: "https://mirror/spt.7z");
        var downloader = new FakeDownloader { Files = { ["https://mirror/spt.7z"] = archive } };
        var install = test.Detect();
        var updater = new SptUpdater(install, new StubReleases([release]), downloader, new RecordingLog(), Path.Combine(test.Root, "..", "dl-" + Guid.NewGuid()));

        await updater.UpdateAsync(release);

        Assert.Equal("new server", File.ReadAllText(test.PathOf("SPT_Runtime/SPT.Server.exe")));
        Assert.Equal("bepinex", File.ReadAllText(test.PathOf("BepInEx/core/BepInEx.dll")));
        Assert.Equal("{\"profile\":1}", File.ReadAllText(test.PathOf("SPT_Runtime/user/profiles/abc.json")));
        Assert.Single(Directory.GetFiles(test.PathOf(".sptmm/backups"), "profiles-4.1.6-*.zip"));
    }

    [Fact]
    public async Task UpdateAsync_RejectsHashMismatchWithoutTouchingFiles()
    {
        using var test = new TestInstall();
        test.WriteFile("SPT_Runtime/SPT.Server.exe", "old server");

        var archive = test.CreateZip("spt.zip", ("SPT_Runtime/SPT.Server.exe", "new server"));
        var release = Release("4.1.6", md5: "AAAAAAAAAAAAAAAAAAAAAA==", url: "https://mirror/spt.7z");
        var downloader = new FakeDownloader { Files = { ["https://mirror/spt.7z"] = archive } };
        var updater = new SptUpdater(test.Detect(), new StubReleases([release]), downloader, new RecordingLog(), Path.Combine(test.Root, "..", "dl-" + Guid.NewGuid()));

        await Assert.ThrowsAsync<InvalidDataException>(() => updater.UpdateAsync(release));
        Assert.Equal("old server", File.ReadAllText(test.PathOf("SPT_Runtime/SPT.Server.exe")));
    }

    [Fact]
    public async Task UpdateAsync_RefusesDifferentMinorVersion()
    {
        using var test = new TestInstall();
        var release = Release("4.2.0");
        var updater = new SptUpdater(test.Detect(), new StubReleases([release]), new FakeDownloader(), new RecordingLog(), test.Root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.UpdateAsync(release));
    }

    private sealed class StubReleases(IReadOnlyList<SptRelease> releases) : ISptReleaseClient
    {
        public Task<IReadOnlyList<SptRelease>> GetReleasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(releases);
    }
}
