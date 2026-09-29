using SptModManager.Core.IO;
using SptModManager.Core.Spt;
using SptModManager.Core.Versioning;

namespace SptModManager.Core.Mods;

public enum ModComponentKind
{
    ClientPlugin,
    ClientPatcher,
    ServerMod,
}

/// <summary>A mod piece found on disk: one BepInEx plugin/patcher or one server mod folder.</summary>
public sealed record LocalModComponent(
    ModComponentKind Kind,
    string? Guid,
    string Name,
    string? Version,
    string PrimaryPath,
    IReadOnlyList<string> Files);

/// <summary>
/// Finds installed mods by reading their DLLs. Paths in results are relative to the game root with forward slashes.
/// </summary>
public static class ModScanner
{
    public static IReadOnlyList<LocalModComponent> Scan(SptInstallation install)
    {
        var components = new List<LocalModComponent>();
        components.AddRange(ScanBepInExFolder(install, install.PluginsPath, ModComponentKind.ClientPlugin));
        components.AddRange(ScanBepInExFolder(install, install.PatchersPath, ModComponentKind.ClientPatcher));
        components.AddRange(ScanServerMods(install));
        return components;
    }

    private static IEnumerable<LocalModComponent> ScanBepInExFolder(SptInstallation install, string folder, ModComponentKind kind)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        var sptModules = Path.GetFullPath(install.SptPluginsPath);

        // Loose DLLs at the folder root: each DLL is its own component.
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var component = ReadClientDll(install, dll, kind, [dll]);
            if (component is not null)
            {
                yield return component;
            }
        }

        // Subfolders: the whole folder belongs to the mod whose plugin DLL lives in it.
        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            if (Path.GetFullPath(directory).Equals(sptModules, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var files = SafeEnumerateFiles(directory).ToList();
            var dlls = files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();

            LocalModComponent? best = null;
            foreach (var dll in dlls)
            {
                var component = ReadClientDll(install, dll, kind, files);
                if (component?.Guid is not null)
                {
                    best = component;
                    break;
                }

                best ??= component;
            }

            if (best is not null)
            {
                yield return best;
            }
            else if (files.Count > 0)
            {
                yield return new LocalModComponent(
                    kind,
                    null,
                    Path.GetFileName(directory),
                    null,
                    PathUtil.ToRelative(install.RootPath, directory),
                    files.Select(f => PathUtil.ToRelative(install.RootPath, f)).ToList());
            }
        }
    }

    private static LocalModComponent? ReadClientDll(SptInstallation install, string dll, ModComponentKind kind, IReadOnlyList<string> files)
    {
        var plugin = AssemblyInspector.ReadBepInPlugins(dll).FirstOrDefault();
        var relativeFiles = files.Select(f => PathUtil.ToRelative(install.RootPath, f)).ToList();

        if (plugin is not null)
        {
            return new LocalModComponent(kind, plugin.Guid, plugin.Name, VersionUtil.Normalize(plugin.Version) ?? plugin.Version,
                PathUtil.ToRelative(install.RootPath, dll), relativeFiles);
        }

        var info = AssemblyInspector.ReadAssemblyInfo(dll);
        if (info is null)
        {
            // Not a .NET assembly (native helper or corrupt); only report it when it is the sole file.
            return files.Count == 1
                ? new LocalModComponent(kind, null, Path.GetFileNameWithoutExtension(dll), null, PathUtil.ToRelative(install.RootPath, dll), relativeFiles)
                : null;
        }

        return new LocalModComponent(
            kind,
            null,
            info.Name,
            VersionUtil.Normalize(info.InformationalVersion?.Split('+')[0]) ?? (info.Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : null),
            PathUtil.ToRelative(install.RootPath, dll),
            relativeFiles);
    }

    private static IEnumerable<LocalModComponent> ScanServerMods(SptInstallation install)
    {
        if (!Directory.Exists(install.ServerModsPath))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(install.ServerModsPath))
        {
            var files = SafeEnumerateFiles(directory).ToList();
            var relativeFiles = files.Select(f => PathUtil.ToRelative(install.RootPath, f)).ToList();

            // Prefer DLLs at the mod root; libraries usually sit in subfolders.
            var dlls = files
                .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetDirectoryName(f)!.Length)
                .ToList();

            ServerModMetadata? metadata = null;
            foreach (var dll in dlls)
            {
                metadata = AssemblyInspector.ReadServerModMetadata(dll);
                if (metadata is not null)
                {
                    break;
                }
            }

            yield return new LocalModComponent(
                ModComponentKind.ServerMod,
                metadata?.Guid,
                metadata?.Name ?? Path.GetFileName(directory),
                VersionUtil.Normalize(metadata?.Version) ?? metadata?.Version,
                PathUtil.ToRelative(install.RootPath, directory),
                relativeFiles);
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
