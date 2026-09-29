using System.Text.Json;
using SptModManager.Core.Mods;
using SptModManager.Core.Versioning;

namespace SptModManager.Core.Spt;

/// <summary>
/// A detected SPT install. The game root holds EscapeFromTarkov.exe and BepInEx; SPT's server, launcher and user data
/// live in "SPT_Runtime" for SPT 4.1+ or "SPT" for SPT 4.0.
/// </summary>
public sealed class SptInstallation
{
    public const string RuntimeFolder41 = "SPT_Runtime";
    public const string RuntimeFolder40 = "SPT";

    private static readonly string[] RuntimeFolderCandidates = [RuntimeFolder41, RuntimeFolder40];

    private SptInstallation(string rootPath, string dataFolderName)
    {
        RootPath = rootPath;
        DataFolderName = dataFolderName;
    }

    public string RootPath { get; }

    /// <summary>"SPT_Runtime" (4.1+) or "SPT" (4.0).</summary>
    public string DataFolderName { get; }

    public string DataPath => Path.Combine(RootPath, DataFolderName);

    public string UserPath => Path.Combine(DataPath, "user");

    public string ServerModsPath => Path.Combine(UserPath, "mods");

    public string ProfilesPath => Path.Combine(UserPath, "profiles");

    public string BepInExPath => Path.Combine(RootPath, "BepInEx");

    public string PluginsPath => Path.Combine(BepInExPath, "plugins");

    public string PatchersPath => Path.Combine(BepInExPath, "patchers");

    /// <summary>Folder that holds the SPT client modules, which are part of SPT rather than mods.</summary>
    public string SptPluginsPath => Path.Combine(PluginsPath, "spt");

    /// <summary>The manager's own bookkeeping folder inside the install.</summary>
    public string ManagerPath => Path.Combine(RootPath, ".sptmm");

    /// <summary>SPT version such as "4.1.6", or null if it could not be read.</summary>
    public string? SptVersion { get; private set; }

    /// <summary>Full informational version, e.g. "4.1.6-RELEASE+731d7a2.20260918".</summary>
    public string? SptBuildInfo { get; private set; }

    /// <summary>The EFT client version this SPT install targets, e.g. "0.16.9.40743".</summary>
    public string? CompatibleTarkovVersion { get; private set; }

    /// <summary>The EFT client build number, e.g. "40743".</summary>
    public string? ClientBuild => CompatibleTarkovVersion?.Split('.').LastOrDefault();

    public bool HasGameExecutable => File.Exists(Path.Combine(RootPath, "EscapeFromTarkov.exe"));

    /// <summary>
    /// Detects an SPT install at <paramref name="path"/>. The path may be the game root or the runtime folder itself.
    /// </summary>
    public static SptInstallation? TryDetect(string? path, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            error = "That folder does not exist.";
            return null;
        }

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var root in new[] { full, Path.GetDirectoryName(full) })
        {
            if (root is null)
            {
                continue;
            }

            foreach (var folder in RuntimeFolderCandidates)
            {
                var dataPath = Path.Combine(root, folder);
                if (LooksLikeRuntimeFolder(dataPath))
                {
                    var install = new SptInstallation(root, folder);
                    install.Refresh();
                    return install;
                }
            }
        }

        error = "No SPT install found. Pick the folder that contains EscapeFromTarkov.exe and the SPT_Runtime (or SPT) folder.";
        return null;
    }

    /// <summary>Re-reads version information from disk (e.g. after an SPT update).</summary>
    public void Refresh()
    {
        SptVersion = null;
        SptBuildInfo = null;

        foreach (var candidate in new[] { "SPTarkov.Server.Core.dll", "SPT.Server.dll", "SPT.Server.Linux.dll" })
        {
            var info = AssemblyInspector.ReadAssemblyInfo(Path.Combine(DataPath, candidate));
            if (info is null)
            {
                continue;
            }

            SptBuildInfo = info.InformationalVersion;
            SptVersion = VersionFromInformational(info.InformationalVersion)
                         ?? VersionUtil.Normalize(info.FileVersion)
                         ?? (info.Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : null);

            if (SptVersion is not null)
            {
                break;
            }
        }

        CompatibleTarkovVersion = ReadCompatibleTarkovVersion();
    }

    internal static string? VersionFromInformational(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return null;
        }

        // "4.1.6-RELEASE+731d7a2.20260918" -> "4.1.6"
        var core = informational.Split('-', '+')[0];
        return VersionUtil.Normalize(core);
    }

    private static bool LooksLikeRuntimeFolder(string dataPath)
    {
        if (!Directory.Exists(dataPath))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(dataPath, "SPT_Data"))
               || File.Exists(Path.Combine(dataPath, "SPTarkov.Server.Core.dll"))
               || File.Exists(Path.Combine(dataPath, "SPT.Server.exe"))
               || File.Exists(Path.Combine(dataPath, "SPT.Server.Linux"));
    }

    private string? ReadCompatibleTarkovVersion()
    {
        // The config location moved between releases, so check both known spots.
        var candidates = new[]
        {
            Path.Combine(DataPath, "SPT_Data", "configs", "core.json"),
            Path.Combine(DataPath, "SPT_Data", "Server", "configs", "core.json"),
        };

        foreach (var file in candidates.Where(File.Exists))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                if (document.RootElement.TryGetProperty("compatibleTarkovVersion", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // Unreadable config; version info is optional.
            }
        }

        return null;
    }
}
