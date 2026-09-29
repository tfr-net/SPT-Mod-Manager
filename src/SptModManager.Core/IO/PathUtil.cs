namespace SptModManager.Core.IO;

public static class PathUtil
{
    /// <summary>
    /// Normalizes an archive or relative path to forward slashes with no leading "./" or "/" and no empty segments.
    /// </summary>
    public static string NormalizeRelative(string path)
    {
        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".");

        return string.Join('/', segments);
    }

    /// <summary>
    /// Returns true when a normalized relative path would escape its root (zip-slip) or is rooted.
    /// </summary>
    public static bool IsUnsafeRelative(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return true;
        }

        if (Path.IsPathRooted(normalizedPath) || normalizedPath.Contains(':'))
        {
            return true;
        }

        return normalizedPath.Split('/').Any(s => s == "..");
    }

    /// <summary>
    /// Combines a root directory with a normalized relative path and guarantees the result stays inside the root.
    /// </summary>
    public static string SafeCombine(string rootDirectory, string normalizedRelativePath)
    {
        if (IsUnsafeRelative(normalizedRelativePath))
        {
            throw new InvalidOperationException($"Refusing to use unsafe path '{normalizedRelativePath}'.");
        }

        var root = Path.GetFullPath(rootDirectory);
        var full = Path.GetFullPath(Path.Combine(root, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to write outside of '{root}': '{normalizedRelativePath}'.");
        }

        return full;
    }

    /// <summary>
    /// Returns a normalized path of <paramref name="fullPath"/> relative to <paramref name="rootDirectory"/>.
    /// </summary>
    public static string ToRelative(string rootDirectory, string fullPath)
    {
        return NormalizeRelative(Path.GetRelativePath(rootDirectory, fullPath));
    }

    /// <summary>
    /// Deletes empty directories walking up from <paramref name="startDirectory"/>, stopping at (and never deleting)
    /// <paramref name="stopAt"/>.
    /// </summary>
    public static void PruneEmptyDirectories(string startDirectory, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (current is not null
               && current.Exists
               && !string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase)
               && current.FullName.StartsWith(stop, StringComparison.OrdinalIgnoreCase)
               && !current.EnumerateFileSystemInfos().Any())
        {
            var parent = current.Parent;
            current.Delete();
            current = parent;
        }
    }
}
