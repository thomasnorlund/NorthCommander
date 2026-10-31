using NorthCommander.Core.Archives;
using NorthCommander.Core.Models;

namespace NorthCommander.Core.Services;

public sealed class DirectoryService : IDirectoryService
{
    private const int CancellationCheckInterval = 256;

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        MatchType = MatchType.Simple,
    };

    public Task<DirectoryListing> GetDirectoryListingAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => ReadDirectory(path, cancellationToken), cancellationToken);
    }

    private static DirectoryListing ReadDirectory(string path, CancellationToken cancellationToken)
    {
        // "C:\data\backup.zip\docs" is a folder inside an archive.
        if (ArchiveService.TryParse(path, out var archivePath, out var innerPath))
        {
            return ArchiveService.ReadListing(archivePath, innerPath, cancellationToken);
        }

        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"Directory not found: {directory.FullName}");
        }

        var directories = new List<FileSystemEntry>();
        var files = new List<FileSystemEntry>();
        long totalFileSize = 0;
        var processed = 0;

        // EnumerateFileSystemInfos streams entries and populates FileInfo/DirectoryInfo
        // from the same OS call (FindNextFile / readdir+stat), so no extra I/O per item.
        foreach (var info in directory.EnumerateFileSystemInfos("*", EnumerationOptions))
        {
            if (++processed % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (info is FileInfo file)
            {
                var size = file.Length;
                totalFileSize += size;
                var (name, extension) = SplitFileName(file.Name);
                files.Add(new FileSystemEntry(
                    Name: name,
                    Extension: extension,
                    FullPath: file.FullName,
                    Size: size,
                    LastWriteTime: file.LastWriteTime,
                    Attributes: file.Attributes,
                    Kind: FileSystemEntryKind.File));
            }
            else
            {
                directories.Add(new FileSystemEntry(
                    Name: info.Name,
                    Extension: string.Empty,
                    FullPath: info.FullName,
                    Size: null,
                    LastWriteTime: info.LastWriteTime,
                    Attributes: info.Attributes,
                    Kind: FileSystemEntryKind.Directory));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        directories.Sort(NameComparer.Instance);
        files.Sort(NameComparer.Instance);

        var entries = new List<FileSystemEntry>(directories.Count + files.Count + 1);
        if (directory.Parent is { } parent)
        {
            entries.Add(new FileSystemEntry(
                Name: "..",
                Extension: string.Empty,
                FullPath: parent.FullName,
                Size: null,
                LastWriteTime: directory.LastWriteTime,
                Attributes: FileAttributes.Directory,
                Kind: FileSystemEntryKind.ParentLink));
        }

        entries.AddRange(directories);
        entries.AddRange(files);

        return new DirectoryListing(directory.FullName, entries, directories.Count, files.Count, totalFileSize);
    }

    /// <summary>
    /// Splits "report.final.pdf" into ("report.final", "pdf"). Dot-files such as
    /// ".gitignore" keep their full name and get no extension.
    /// </summary>
    internal static (string Name, string Extension) SplitFileName(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 || dot == fileName.Length - 1
            ? (fileName, string.Empty)
            : (fileName[..dot], fileName[(dot + 1)..]);
    }

    internal sealed class NameComparer : IComparer<FileSystemEntry>
    {
        public static readonly NameComparer Instance = new();

        public int Compare(FileSystemEntry? x, FileSystemEntry? y)
        {
            var result = StringComparer.OrdinalIgnoreCase.Compare(x?.Name, y?.Name);
            return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(x?.Extension, y?.Extension);
        }
    }
}
