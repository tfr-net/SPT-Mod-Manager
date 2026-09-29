using System.Diagnostics;
using System.IO.Compression;
using SptModManager.Core.IO;
using SptModManager.Core.Logging;
using SptModManager.Core.Mods;
using SptModManager.Core.Net;
using SptModManager.Core.Versioning;

namespace SptModManager.Core.Spt;

public enum SptUpdateState
{
    UpToDate,
    PatchAvailable,
    NewMinorAvailable,
    UnknownInstalledVersion,
    NoReleases,
}

public sealed record SptUpdateCheck(
    SptUpdateState State,
    string? InstalledVersion,
    SptRelease? LatestPatch,
    SptRelease? LatestOverall,
    bool ClientBuildMismatch,
    string Message)
{
    /// <summary>True when the patch can be applied in place by the manager.</summary>
    public bool CanUpdate => State == SptUpdateState.PatchAvailable && !ClientBuildMismatch && LatestPatch?.DownloadUrl is not null;
}

/// <summary>
/// Applies SPT patch releases (e.g. 4.1.5 to 4.1.6) in place, following the official guide: extract the release over
/// the existing install, overwriting files. Profiles and mods are not part of the release, and profiles are backed up
/// first anyway. Minor or major updates (4.1 to 4.2) need a fresh install and are only reported.
/// </summary>
public sealed class SptUpdater(
    SptInstallation install,
    ISptReleaseClient releases,
    IFileDownloader downloader,
    IActivityLog log,
    string downloadDirectory)
{
    private static readonly string[] SptProcessNames =
        ["SPT.Server", "SPT.Server.Linux", "SPT.Launcher", "SPT.Launcher.Linux", "EscapeFromTarkov"];

    public async Task<SptUpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var all = await releases.GetReleasesAsync(cancellationToken);
        return Evaluate(install.SptVersion, install.ClientBuild, all);
    }

    public static SptUpdateCheck Evaluate(string? installedVersion, string? installedClientBuild, IReadOnlyList<SptRelease> available)
    {
        var latest = available.FirstOrDefault();
        if (latest is null)
        {
            return new SptUpdateCheck(SptUpdateState.NoReleases, installedVersion, null, null, false, "No SPT releases were found.");
        }

        var installed = VersionUtil.TryParse(installedVersion);
        if (installed is null)
        {
            return new SptUpdateCheck(SptUpdateState.UnknownInstalledVersion, installedVersion, null, latest, false,
                $"Could not read your SPT version. The latest release is {latest.Version}.");
        }

        var installedMinor = $"{installed.Major}.{installed.Minor}";
        var latestPatch = available
            .Where(r => r.MajorMinor == installedMinor)
            .MaxBy(r => VersionUtil.TryParse(r.Version));

        var newerMinor = VersionUtil.TryParse(latest.Version) is { } latestParsed
                         && (latestParsed.Major > installed.Major || (latestParsed.Major == installed.Major && latestParsed.Minor > installed.Minor));

        if (latestPatch is not null && VersionUtil.Compare(latestPatch.Version, installedVersion) > 0)
        {
            var mismatch = installedClientBuild is not null
                           && latestPatch.ClientBuild is not null
                           && installedClientBuild != latestPatch.ClientBuild;

            var message = mismatch
                ? $"SPT {latestPatch.Version} needs EFT client build {latestPatch.ClientBuild}, but your install is on {installedClientBuild}. Use the official installer to update."
                : $"SPT {latestPatch.Version} is available (you have {installedVersion}).";

            if (newerMinor)
            {
                message += $" SPT {latest.Version} is also out, but moving to {latest.MajorMinor} needs a fresh install.";
            }

            return new SptUpdateCheck(SptUpdateState.PatchAvailable, installedVersion, latestPatch, latest, mismatch, message);
        }

        if (newerMinor)
        {
            return new SptUpdateCheck(SptUpdateState.NewMinorAvailable, installedVersion, latestPatch, latest, false,
                $"SPT {latest.Version} is out. Updating from {installedMinor} to {latest.MajorMinor} needs a fresh install with the official installer, and most mods will need new versions.");
        }

        return new SptUpdateCheck(SptUpdateState.UpToDate, installedVersion, latestPatch, latest, false, $"SPT {installedVersion} is up to date.");
    }

    public static IReadOnlyList<string> FindRunningSptProcesses()
    {
        var running = new List<string>();
        foreach (var name in SptProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    running.Add(name);
                }

                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
                // Process enumeration is best effort.
            }
        }

        return running;
    }

    public async Task UpdateAsync(SptRelease release, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release.DownloadUrl is null)
        {
            throw new InvalidOperationException($"SPT {release.Version} has no download link.");
        }

        if (VersionUtil.MajorMinor(release.Version) != VersionUtil.MajorMinor(install.SptVersion))
        {
            throw new InvalidOperationException($"SPT {release.Version} is a different minor version; it needs a fresh install.");
        }

        var running = FindRunningSptProcesses();
        if (running.Count > 0)
        {
            throw new InvalidOperationException($"Close {string.Join(", ", running)} before updating SPT.");
        }

        var previousVersion = install.SptVersion;
        log.Info($"Updating SPT {previousVersion} to {release.Version}...");

        progress?.Report(new OperationProgress($"Downloading SPT {release.Version}...", 0));
        var download = await downloader.DownloadAsync(
            release.DownloadUrl,
            downloadDirectory,
            new Progress<DownloadProgress>(p => progress?.Report(new OperationProgress($"Downloading SPT {release.Version}...", p.Fraction))),
            cancellationToken);

        try
        {
            if (release.Md5Base64 is not null && !string.Equals(release.Md5Base64, download.Md5Base64, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The SPT download failed its integrity check (expected md5 {release.Md5Base64}, got {download.Md5Base64}). Nothing was changed.");
            }

            if (release.Md5Base64 is not null)
            {
                log.Info("Download verified (md5 matches the release notes).");
            }

            progress?.Report(new OperationProgress("Backing up profiles...", null));
            BackupProfiles(previousVersion);

            progress?.Report(new OperationProgress($"Installing SPT {release.Version}...", 0));
            var profilesPrefix = PathUtil.NormalizeRelative(Path.GetRelativePath(install.RootPath, install.ProfilesPath)) + "/";

            ArchiveExtractor.Extract(
                download.FilePath,
                install.RootPath,
                key =>
                {
                    var normalized = PathUtil.NormalizeRelative(key);
                    return PathUtil.IsUnsafeRelative(normalized) || normalized.StartsWith(profilesPrefix, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : normalized;
                },
                new Progress<double>(f => progress?.Report(new OperationProgress($"Installing SPT {release.Version}...", f))),
                cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(download.FilePath);
            }
            catch (IOException)
            {
                // Temp file cleanup is best effort.
            }
        }

        install.Refresh();
        if (VersionUtil.AreEquivalent(install.SptVersion, release.Version))
        {
            log.Success($"SPT updated to {install.SptVersion}. Remember to update mods that have new versions for it.");
        }
        else
        {
            log.Warn($"SPT files were extracted, but the detected version is {install.SptVersion ?? "unknown"} (expected {release.Version}).");
        }

        progress?.Report(new OperationProgress("Done.", 1));
    }

    private void BackupProfiles(string? version)
    {
        if (!Directory.Exists(install.ProfilesPath) || !Directory.EnumerateFileSystemEntries(install.ProfilesPath).Any())
        {
            return;
        }

        var backups = Path.Combine(install.ManagerPath, "backups");
        Directory.CreateDirectory(backups);

        var target = Path.Combine(backups, $"profiles-{version ?? "unknown"}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        ZipFile.CreateFromDirectory(install.ProfilesPath, target, CompressionLevel.Optimal, includeBaseDirectory: false);
        log.Info($"Profiles backed up to {Path.GetRelativePath(install.RootPath, target)}.");
    }
}
