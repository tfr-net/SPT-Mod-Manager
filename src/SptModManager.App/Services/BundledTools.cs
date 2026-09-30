using System.Reflection;
using System.Security.Cryptography;
using SptModManager.Core;
using SptModManager.Core.IO;

namespace SptModManager.App.Services;

/// <summary>
/// Puts the embedded 7zr.exe (the official 7-Zip standalone extractor) on disk so large .7z archives can be unpacked
/// natively on machines without 7-Zip installed. The copy lives in the app data folder under a name derived from its
/// hash and is verified before use, so a damaged or replaced file is rewritten.
/// </summary>
public static class BundledTools
{
    private const string SevenZipResource = "SptModManager.Tools.7zr.exe";

    public static void Install(IActivityLog log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(SevenZipResource);
            if (resource is null)
            {
                return;
            }

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            var directory = Path.Combine(AppSettings.AppDataDirectory, "tools");
            var path = Path.Combine(directory, $"7zr-{hash[..12]}.exe");

            if (!File.Exists(path) || !HashMatches(path, hash))
            {
                Directory.CreateDirectory(directory);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }

            SevenZipTool.BundledExecutable = path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not set up the bundled 7-Zip ({e.Message}); large .7z mods will unpack more slowly.");
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
