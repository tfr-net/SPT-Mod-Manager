using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SptModManager.Core.IO;

/// <summary>
/// Finds and runs a native 7-Zip executable. Native 7-Zip decompresses .7z archives several times faster than the
/// managed decoder (it uses every CPU core for LZMA2), which matters for multi-gigabyte mods and SPT releases.
/// </summary>
public static partial class SevenZipTool
{
    /// <summary>A 7-Zip executable shipped with the app (the Windows build bundles 7zr.exe), used as a last resort.</summary>
    public static string? BundledExecutable { get; set; }

    /// <summary>
    /// The best available 7-Zip: SPTMM_7ZIP, then an installed 7-Zip, then one on PATH, then the bundled copy.
    /// </summary>
    public static string? Locate()
    {
        if (Environment.GetEnvironmentVariable("SPTMM_7ZIP") is { Length: > 0 } overridden && File.Exists(overridden))
        {
            return overridden;
        }

        foreach (var candidate in InstalledCandidates().Concat(PathCandidates()))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return BundledExecutable is { } bundled && File.Exists(bundled) ? bundled : null;
    }

    private static IEnumerable<string> InstalledCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, "7-Zip", "7z.exe");
            }
        }

        // 7-Zip records its folder in the registry, which also covers custom install locations.
        string? registryPath = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\7-Zip");
            registryPath = key?.GetValue("Path64") as string ?? key?.GetValue("Path") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable registry just means one fewer place to look.
        }

        if (!string.IsNullOrWhiteSpace(registryPath))
        {
            yield return Path.Combine(registryPath, "7z.exe");
        }
    }

    private static IEnumerable<string> PathCandidates()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "7z.exe", "7za.exe", "7zz.exe" } : ["7zz", "7z", "7za"];
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                yield return candidate;
            }
        }
    }

    /// <summary>
    /// Extracts every file in <paramref name="archivePath"/> into <paramref name="outputDirectory"/>, reporting
    /// progress from 0 to 1. Throws <see cref="InvalidDataException"/> when 7-Zip reports an error.
    /// </summary>
    public static void Extract(string executable, string archivePath, string outputDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // x: keep paths, -y: never prompt, -aoa: overwrite, -bsp1: progress on stdout, -bse2: errors on stderr,
        // -bso0: no file listing, -p: a dummy password so encrypted archives fail instead of waiting for input.
        foreach (var argument in new[] { "x", "-y", "-aoa", "-bso0", "-bsp1", "-bse2", "-sccUTF-8", "-pSPTMM-no-password", $"-o{outputDirectory}", "--", archivePath })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start 7-Zip at {executable}.");
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var output = Task.Run(() => ReadProgress(process.StandardOutput, progress), CancellationToken.None);

        try
        {
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        output.GetAwaiter().GetResult();
        var errorText = errors.GetAwaiter().GetResult().Trim();

        // 0 = OK, 1 = warnings (files were still extracted), 2+ = fatal error.
        if (process.ExitCode > 1)
        {
            var reason = string.IsNullOrWhiteSpace(errorText) ? $"exit code {process.ExitCode}" : errorText.ReplaceLineEndings(" ");
            throw new InvalidDataException($"7-Zip could not extract the archive: {reason}");
        }

        progress?.Report(1);
    }

    /// <summary>7-Zip redraws its progress ("  42% 13 - file") with backspaces, so scan the raw characters.</summary>
    private static void ReadProgress(StreamReader output, IProgress<double>? progress)
    {
        var buffer = new char[4096];
        var window = new StringBuilder();
        var lastPercent = -1;

        int read;
        while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
        {
            window.Append(buffer, 0, read);
            if (window.Length > 256)
            {
                window.Remove(0, window.Length - 256);
            }

            var matches = PercentRegex().Matches(window.ToString());
            if (matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, out var percent) && percent != lastPercent && percent <= 100)
            {
                lastPercent = percent;
                progress?.Report(percent / 100.0);
            }
        }
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex PercentRegex();
}
