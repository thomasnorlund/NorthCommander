using NorthCommander.Core.Archives;

namespace NorthCommander.Core.Services;

public readonly record struct FolderSize(long Bytes, int Files, int Folders);

/// <summary>
/// Recursive folder size (Space / F3 on a folder). Folders that can't be read are skipped, and links/junctions are
/// counted but not followed, so a link loop can't recurse forever. Works for folders inside archives too.
/// </summary>
public static class FolderSizeCalculator
{
    public static Task<FolderSize> CalculateAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Calculate(path, cancellationToken), cancellationToken);

    public static FolderSize Calculate(string path, CancellationToken cancellationToken = default)
    {
        if (ArchiveService.TryParse(path, out var archivePath, out var innerPath))
        {
            return CalculateInArchive(archivePath, innerPath, cancellationToken);
        }

        var root = new DirectoryInfo(path);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"Folder not found: {path}");
        }

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        long bytes = 0;
        int files = 0, folders = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
                {
                    if (entry is DirectoryInfo subdirectory)
                    {
                        folders++;
                        if (!subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            pending.Push(subdirectory);
                        }
                    }
                    else if (entry is FileInfo file)
                    {
                        files++;
                        bytes += file.Length;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleted or locked while scanning: skip it like an inaccessible folder.
            }
        }

        return new FolderSize(bytes, files, folders);
    }

    private static FolderSize CalculateInArchive(string archivePath, string innerPath, CancellationToken cancellationToken)
    {
        using var archive = ArchiveService.OpenRead(archivePath);
        var prefix = innerPath.Length == 0 ? string.Empty : innerPath + "/";
        var folders = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        var files = 0;
        foreach (var item in archive.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = item.Name[prefix.Length..];
            if (item.IsDirectory)
            {
                folders.Add(relative);
            }
            else
            {
                files++;
                bytes += item.Length;
            }

            // Folders that only exist as part of entry names count too.
            for (var slash = relative.LastIndexOf('/'); slash > 0; slash = relative.LastIndexOf('/', slash - 1))
            {
                folders.Add(relative[..slash]);
            }
        }

        folders.Remove(string.Empty);
        return new FolderSize(bytes, files, folders.Count);
    }
}
