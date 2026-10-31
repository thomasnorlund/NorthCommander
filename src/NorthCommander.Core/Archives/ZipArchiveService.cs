using System.IO.Compression;
using NorthCommander.Core.Models;
using NorthCommander.Core.Services;

namespace NorthCommander.Core.Archives;

/// <summary>
/// Virtual file system for ZIP archives. A path that continues past a <c>.zip</c> file names a folder or file
/// inside it: <c>/data/backup.zip/docs/a.txt</c> → archive <c>/data/backup.zip</c>, inner path <c>docs/a.txt</c>.
/// Inner paths always use '/' and never start or end with one.
/// </summary>
public static class ZipArchiveService
{
    public static bool IsZipFile(string path) => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits a virtual path into the archive file on disk and the inner path ("" for the archive root).</summary>
    public static bool TryParse(string path, out string archivePath, out string innerPath)
    {
        archivePath = innerPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string? current;
        try
        {
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var inner = new List<string>();
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                return false;
            }

            if (File.Exists(current))
            {
                if (!IsZipFile(current))
                {
                    return false;
                }

                inner.Reverse();
                archivePath = current;
                innerPath = string.Join('/', inner);
                return true;
            }

            inner.Add(Path.GetFileName(current));
            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    public static bool IsArchivePath(string path) => TryParse(path, out _, out _);

    public static string ToVirtualPath(string archivePath, string innerPath) =>
        innerPath.Length == 0 ? archivePath : Path.Combine(archivePath, innerPath.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Lists one folder of an archive. Folders that only exist implicitly (as part of entry names) are included.</summary>
    public static DirectoryListing ReadListing(string archivePath, string innerPath, CancellationToken cancellationToken = default)
    {
        using var archive = OpenRead(archivePath);
        var prefix = innerPath.Length == 0 ? string.Empty : innerPath + "/";
        var archiveTime = File.GetLastWriteTime(archivePath);
        var directories = new Dictionary<string, FileSystemEntry>(StringComparer.Ordinal);
        var files = new List<FileSystemEntry>();
        long totalFileSize = 0;
        var found = innerPath.Length == 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NormalizeEntryName(entry.FullName) is not { } name || !name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            found = true;
            var rest = name[prefix.Length..];
            if (rest.Length == 0)
            {
                continue;
            }

            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                var directoryName = rest[..slash];
                var isExplicit = slash == rest.Length - 1;
                if (isExplicit || !directories.ContainsKey(directoryName))
                {
                    directories[directoryName] = new FileSystemEntry(
                        directoryName,
                        string.Empty,
                        ToVirtualPath(archivePath, prefix + directoryName),
                        null,
                        isExplicit ? entry.LastWriteTime.LocalDateTime : archiveTime,
                        FileAttributes.Directory,
                        FileSystemEntryKind.Directory);
                }
            }
            else
            {
                var (fileName, extension) = DirectoryService.SplitFileName(rest);
                files.Add(new FileSystemEntry(
                    fileName,
                    extension,
                    ToVirtualPath(archivePath, name),
                    entry.Length,
                    entry.LastWriteTime.LocalDateTime,
                    FileAttributes.Normal,
                    FileSystemEntryKind.File));
                totalFileSize += entry.Length;
            }
        }

        if (!found)
        {
            throw new DirectoryNotFoundException($"Folder not found in {Path.GetFileName(archivePath)}: {innerPath}");
        }

        var sortedDirectories = directories.Values.ToList();
        sortedDirectories.Sort(DirectoryService.NameComparer.Instance);
        files.Sort(DirectoryService.NameComparer.Instance);

        var slashIndex = innerPath.LastIndexOf('/');
        var parent = innerPath.Length == 0
            ? Path.GetDirectoryName(archivePath)
            : ToVirtualPath(archivePath, slashIndex < 0 ? string.Empty : innerPath[..slashIndex]);

        var entries = new List<FileSystemEntry>(sortedDirectories.Count + files.Count + 1);
        if (parent is not null)
        {
            entries.Add(new FileSystemEntry("..", string.Empty, parent, null, archiveTime, FileAttributes.Directory, FileSystemEntryKind.ParentLink));
        }

        entries.AddRange(sortedDirectories);
        entries.AddRange(files);
        return new DirectoryListing(ToVirtualPath(archivePath, innerPath), entries, sortedDirectories.Count, files.Count, totalFileSize, IsArchive: true);
    }

    /// <summary>Extracts one file of an archive to a new temporary folder and returns its path (for viewing/opening).</summary>
    public static Task<string> ExtractToTempAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        if (!TryParse(virtualPath, out var archivePath, out var innerPath) || innerPath.Length == 0)
        {
            throw new FileNotFoundException($"Not a file inside a ZIP archive: {virtualPath}");
        }

        return Task.Run(
            async () =>
            {
                using var archive = OpenRead(archivePath);
                var entry = archive.Entries.FirstOrDefault(e => NormalizeEntryName(e.FullName) == innerPath)
                    ?? throw new FileNotFoundException($"'{innerPath}' not found in {Path.GetFileName(archivePath)}.");
                var folder = Directory.CreateTempSubdirectory("NorthCommander_").FullName;
                var target = Path.Combine(folder, Path.GetFileName(innerPath));
                try
                {
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    {
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }

                    File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
                    return target;
                }
                catch
                {
                    DeleteTemporaryFile(target);
                    throw;
                }
            },
            cancellationToken);
    }

    /// <summary>Removes a file created by <see cref="ExtractToTempAsync"/> together with its temporary folder.</summary>
    public static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
            if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the OS cleans the temp folder eventually.
        }
    }

    internal static ZipArchive OpenRead(string archivePath)
    {
        try
        {
            return ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException ex)
        {
            throw new IOException($"{Path.GetFileName(archivePath)} is not a valid ZIP archive: {ex.Message}", ex);
        }
    }

    /// <summary>Entry name with '/' separators, or null for names that would escape the archive ("..", absolute, empty).</summary>
    internal static string? NormalizeEntryName(string fullName)
    {
        var name = fullName.Replace('\\', '/');
        if (name.Length == 0 || name.StartsWith('/') || (name.Length > 1 && name[1] == ':'))
        {
            return null;
        }

        var segments = name.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i] is ".." or "." || (segments[i].Length == 0 && i < segments.Length - 1))
            {
                return null;
            }
        }

        return name;
    }

    /// <summary>The entry named <paramref name="innerPath"/> (RelativePath "") or all entries below it.</summary>
    internal static List<(ZipArchiveEntry Entry, string RelativePath)> SelectEntries(ZipArchive archive, string innerPath)
    {
        var prefix = innerPath + "/";
        var result = new List<(ZipArchiveEntry, string)>();
        foreach (var entry in archive.Entries)
        {
            if (NormalizeEntryName(entry.FullName) is not { } name)
            {
                continue;
            }

            if (name == innerPath)
            {
                result.Add((entry, string.Empty));
            }
            else if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                result.Add((entry, name[prefix.Length..]));
            }
        }

        return result;
    }

    internal static bool IsDirectoryEntry(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
}
