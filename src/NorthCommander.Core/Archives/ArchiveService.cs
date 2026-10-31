using System.IO.Compression;
using NorthCommander.Core.Models;
using NorthCommander.Core.Services;

namespace NorthCommander.Core.Archives;

/// <summary>
/// Virtual file system for archives (ZIP, 7z, RAR, TAR and compressed TARs). A path that continues past an archive file
/// names a folder or file inside it: <c>/data/backup.7z/docs/a.txt</c> → archive <c>/data/backup.7z</c>, inner path
/// <c>docs/a.txt</c>. Inner paths always use '/' and never start or end with one. Only ZIP archives can be written to.
/// </summary>
public static class ArchiveService
{
    private static readonly string[] CompressedTarExtensions = [".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tbz", ".tar.xz", ".txz"];
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".tar", .. CompressedTarExtensions];

    public static bool IsZipFile(string path) => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for file names NorthCommander opens like folders.</summary>
    public static bool IsArchiveFile(string path) => ArchiveExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Short type tag for display, e.g. "ZIP", "7Z", "TAR.GZ".</summary>
    public static string GetArchiveTag(string archivePath) =>
        ArchiveExtensions.FirstOrDefault(e => archivePath.EndsWith(e, StringComparison.OrdinalIgnoreCase))?.TrimStart('.').ToUpperInvariant() ?? "ARC";

    internal static bool IsCompressedTar(string path) => CompressedTarExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

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
                if (!IsArchiveFile(current))
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

        foreach (var item in archive.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = item.Name;
            if (name == innerPath && item.IsDirectory)
            {
                found = true;
                continue;
            }

            if (!name.StartsWith(prefix, StringComparison.Ordinal))
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
            if (slash >= 0 || item.IsDirectory)
            {
                var directoryName = slash >= 0 ? rest[..slash] : rest;
                var isExplicit = slash < 0;
                if (isExplicit || !directories.ContainsKey(directoryName))
                {
                    directories[directoryName] = new FileSystemEntry(
                        directoryName,
                        string.Empty,
                        ToVirtualPath(archivePath, prefix + directoryName),
                        null,
                        isExplicit ? item.LastWriteTime : archiveTime,
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
                    item.Length,
                    item.LastWriteTime,
                    FileAttributes.Normal,
                    FileSystemEntryKind.File));
                totalFileSize += item.Length;
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
            throw new FileNotFoundException($"Not a file inside an archive: {virtualPath}");
        }

        return Task.Run(
            async () =>
            {
                using var archive = OpenRead(archivePath);
                foreach (var (item, input) in archive.Read(i => i.Name == innerPath))
                {
                    var folder = Directory.CreateTempSubdirectory("NorthCommander_").FullName;
                    var target = Path.Combine(folder, Path.GetFileName(innerPath));
                    try
                    {
                        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                        {
                            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                        }

                        File.SetLastWriteTime(target, item.LastWriteTime);
                        return target;
                    }
                    catch
                    {
                        DeleteTemporaryFile(target);
                        throw;
                    }
                }

                throw new FileNotFoundException($"'{innerPath}' not found in {Path.GetFileName(archivePath)}.");
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

    internal static ArchiveReader OpenRead(string archivePath) => ArchiveReader.Open(archivePath);

    /// <summary>Opens a ZIP archive with System.IO.Compression (for rewriting it).</summary>
    internal static ZipArchive OpenZip(string archivePath)
    {
        if (!IsZipFile(archivePath))
        {
            throw new IOException($"{Path.GetFileName(archivePath)} is read-only: files can only be added to ZIP archives.");
        }

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

    /// <summary>The item named <paramref name="innerPath"/> (RelativePath "") or all items below it.</summary>
    internal static List<(ArchiveItem Item, string RelativePath)> SelectEntries(ArchiveReader archive, string innerPath)
    {
        var prefix = innerPath + "/";
        var result = new List<(ArchiveItem, string)>();
        foreach (var item in archive.Items)
        {
            if (item.Name == innerPath)
            {
                result.Add((item, string.Empty));
            }
            else if (item.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                result.Add((item, item.Name[prefix.Length..]));
            }
        }

        return result;
    }

    internal static bool IsDirectoryEntry(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
}
