using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SptModManager.Core.IO;

public static class ArchiveExtractor
{
    /// <summary>Lists the keys of every file (not directory) entry in a .7z/.zip/.rar archive.</summary>
    public static IReadOnlyList<string> ListFiles(string archivePath)
    {
        using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions());
        return archive.Entries
            .Where(e => !e.IsDirectory && e.Key is not null)
            .Select(e => e.Key!)
            .ToList();
    }

    /// <summary>
    /// Extracts the archive into <paramref name="rootDirectory"/>. <paramref name="mapTarget"/> turns an entry key into
    /// a target path relative to the root, or null to skip the entry. Returns the relative paths written.
    /// </summary>
    public static IReadOnlyList<string> Extract(
        string archivePath,
        string rootDirectory,
        Func<string, string?> mapTarget,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var written = new List<string>();

        using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions());
        var totalBytes = Math.Max(1, archive.TotalUncompressSize);
        long doneBytes = 0;

        void WriteEntry(IEntry entry, Action<Stream> copyTo)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory || entry.Key is null)
            {
                return;
            }

            var target = mapTarget(entry.Key);
            if (target is null)
            {
                return;
            }

            var normalized = PathUtil.NormalizeRelative(target);
            var fullPath = PathUtil.SafeCombine(rootDirectory, normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            // Replace read-only files instead of failing on them.
            if (File.Exists(fullPath))
            {
                File.SetAttributes(fullPath, FileAttributes.Normal);
            }

            using (var output = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                copyTo(output);
            }

            if (entry.LastModifiedTime is { } modified)
            {
                TrySetLastWriteTime(fullPath, modified);
            }

            written.Add(normalized);
            doneBytes += entry.Size;
            progress?.Report(Math.Min(1.0, (double)doneBytes / totalBytes));
        }

        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            // Solid archives must be decompressed sequentially; per-entry access re-decompresses from the start.
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                WriteEntry(reader.Entry, reader.WriteEntryTo);
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                WriteEntry(entry, output => entry.WriteTo(output));
            }
        }

        progress?.Report(1.0);
        return written;
    }

    private static void TrySetLastWriteTime(string path, DateTime modified)
    {
        try
        {
            File.SetLastWriteTime(path, modified);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // Timestamps are cosmetic.
        }
    }
}
