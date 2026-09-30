using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SptModManager.Core.IO;

public sealed record ExtractOptions
{
    /// <summary>
    /// A specific 7-Zip executable for .7z archives, or null to use <see cref="SevenZipTool.Locate"/>.
    /// </summary>
    public string? SevenZipExecutable { get; init; }

    /// <summary>False forces the managed decoders (used by tests and as a fallback).</summary>
    public bool UseNativeSevenZip { get; init; } = true;

    /// <summary>
    /// Where native 7-Zip unpacks before files are moved into place. It should be on the same drive as the target
    /// so the moves are instant renames. Defaults to "&lt;root&gt;/.sptmm/staging".
    /// </summary>
    public string? StagingDirectory { get; init; }
}

public static class ArchiveExtractor
{
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    private enum ArchiveKind
    {
        SevenZip,
        Zip,
        Other,
    }

    /// <summary>Lists the keys of every file (not directory) entry in a .7z/.zip/.rar archive.</summary>
    public static IReadOnlyList<string> ListFiles(string archivePath)
    {
        if (Detect(archivePath) == ArchiveKind.Zip)
        {
            try
            {
                using var zip = ZipFile.OpenRead(archivePath);
                return zip.Entries.Where(e => !IsDirectoryEntry(e)).Select(e => e.FullName).ToList();
            }
            catch (InvalidDataException)
            {
                // Fall through to SharpCompress, which reads some zips .NET does not.
            }
        }

        using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions());
        return archive.Entries
            .Where(e => !e.IsDirectory && e.Key is not null)
            .Select(e => e.Key!)
            .ToList();
    }

    /// <summary>
    /// Extracts the archive into <paramref name="rootDirectory"/>. <paramref name="mapTarget"/> receives each file's
    /// normalized path inside the archive (forward slashes) and returns its target relative to the root, or null to
    /// skip it. Returns the relative paths written. Progress goes from 0 to 1.
    /// </summary>
    public static IReadOnlyList<string> Extract(
        string archivePath,
        string rootDirectory,
        Func<string, string?> mapTarget,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        ExtractOptions? options = null)
    {
        options ??= new ExtractOptions();

        switch (Detect(archivePath))
        {
            case ArchiveKind.SevenZip when options.UseNativeSevenZip && (options.SevenZipExecutable ?? SevenZipTool.Locate()) is { } executable:
                try
                {
                    return ExtractWithSevenZip(executable, archivePath, rootDirectory, mapTarget, progress, cancellationToken, options);
                }
                catch (Exception native) when (native is not OperationCanceledException)
                {
                    // 7-Zip could not run (blocked by antivirus, say) or failed; the managed decoder gets a turn.
                    try
                    {
                        return ExtractWithSharpCompress(archivePath, rootDirectory, mapTarget, progress, cancellationToken);
                    }
                    catch (Exception managed) when (managed is not OperationCanceledException)
                    {
                        throw new InvalidDataException($"{managed.Message} (7-Zip also failed: {native.Message})", managed);
                    }
                }

            case ArchiveKind.Zip:
                try
                {
                    return ExtractZip(archivePath, rootDirectory, mapTarget, progress, cancellationToken);
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException)
                {
                    // Unusual compression methods (e.g. LZMA inside a zip) are left to SharpCompress.
                    return ExtractWithSharpCompress(archivePath, rootDirectory, mapTarget, progress, cancellationToken);
                }

            default:
                return ExtractWithSharpCompress(archivePath, rootDirectory, mapTarget, progress, cancellationToken);
        }
    }

    /// <summary>
    /// Unpacks the whole archive with native 7-Zip into a staging folder next to the target, then moves the mapped
    /// files into place. Moves within a drive are renames, so this costs little beyond the decompression itself.
    /// </summary>
    private static IReadOnlyList<string> ExtractWithSevenZip(
        string executable,
        string archivePath,
        string rootDirectory,
        Func<string, string?> mapTarget,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        ExtractOptions options)
    {
        var stagingRoot = options.StagingDirectory ?? Path.Combine(rootDirectory, ".sptmm", "staging");
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            // Decompression is nearly all of the work; moving is the last few percent.
            var decompress = progress is null ? null : new InlineProgress<double>(f => progress.Report(f * 0.95));
            SevenZipTool.Extract(executable, archivePath, staging, decompress, cancellationToken);

            var written = new List<string>();
            var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).ToList();

            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = files[i];
                var target = mapTarget(PathUtil.ToRelative(staging, source));
                if (target is null)
                {
                    continue;
                }

                var normalized = PathUtil.NormalizeRelative(target);
                var destination = PathUtil.SafeCombine(rootDirectory, normalized);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                PrepareForOverwrite(destination);
                File.Move(source, destination, overwrite: true);

                written.Add(normalized);
                progress?.Report(0.95 + (0.05 * (i + 1) / files.Count));
            }

            progress?.Report(1.0);
            return written;
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteEmptyDirectory(stagingRoot);
        }
    }

    /// <summary>Zip via System.IO.Compression, which uses native zlib and is much faster than managed inflate.</summary>
    private static IReadOnlyList<string> ExtractZip(
        string archivePath,
        string rootDirectory,
        Func<string, string?> mapTarget,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var entries = zip.Entries.Where(e => !IsDirectoryEntry(e)).ToList();
        var totalBytes = Math.Max(1, entries.Sum(e => e.Length));
        long doneBytes = 0;
        var written = new List<string>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = mapTarget(PathUtil.NormalizeRelative(entry.FullName));
            if (target is not null)
            {
                var normalized = PathUtil.NormalizeRelative(target);
                var destination = PathUtil.SafeCombine(rootDirectory, normalized);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                PrepareForOverwrite(destination);
                entry.ExtractToFile(destination, overwrite: true);
                written.Add(normalized);
            }

            doneBytes += entry.Length;
            progress?.Report(Math.Min(1.0, (double)doneBytes / totalBytes));
        }

        progress?.Report(1.0);
        return written;
    }

    private static IReadOnlyList<string> ExtractWithSharpCompress(
        string archivePath,
        string rootDirectory,
        Func<string, string?> mapTarget,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
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

            doneBytes += entry.Size;
            var target = mapTarget(PathUtil.NormalizeRelative(entry.Key));
            if (target is null)
            {
                progress?.Report(Math.Min(1.0, (double)doneBytes / totalBytes));
                return;
            }

            var normalized = PathUtil.NormalizeRelative(target);
            var fullPath = PathUtil.SafeCombine(rootDirectory, normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            PrepareForOverwrite(fullPath);

            using (var output = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                copyTo(output);
            }

            if (entry.LastModifiedTime is { } modified)
            {
                TrySetLastWriteTime(fullPath, modified);
            }

            written.Add(normalized);
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

    private static ArchiveKind Detect(string archivePath)
    {
        Span<byte> header = stackalloc byte[6];
        using var stream = File.OpenRead(archivePath);
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

        if (read >= 6 && header.SequenceEqual(SevenZipSignature))
        {
            return ArchiveKind.SevenZip;
        }

        // "PK\x03\x04" (local file header) or "PK\x05\x06" (empty archive).
        if (read >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] is 0x03 or 0x05 && header[3] is 0x04 or 0x06)
        {
            return ArchiveKind.Zip;
        }

        return ArchiveKind.Other;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    /// <summary>Replace read-only files instead of failing on them.</summary>
    private static void PrepareForOverwrite(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leftover staging files are harmless and cleared by the next install.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
