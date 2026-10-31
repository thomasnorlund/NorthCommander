namespace NorthCommander.Core.Services;

/// <summary>Immediate (non-queued) file system operations: delete and rename.</summary>
public static class FileSystemOperations
{
    /// <summary>Permanently deletes files and folders (recursively) on a background thread.</summary>
    /// <remarks>Read-only files are deleted too; symbolic links are removed without touching their targets.</remarks>
    public static Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                foreach (var path in paths)
                {
                    Delete(path, cancellationToken);
                }
            },
            cancellationToken);

    /// <summary>Renames a file or folder inside its current folder and returns the new full path.</summary>
    /// <exception cref="ArgumentException">The new name is empty or contains invalid characters.</exception>
    /// <exception cref="IOException">An item with the new name already exists.</exception>
    public static string Rename(string path, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName is "." or ".."
            || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || newName.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException($"'{newName}' is not a valid file name.", nameof(newName));
        }

        path = Path.TrimEndingDirectorySeparator(path);
        var parent = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("A root folder cannot be renamed.", nameof(path));
        var target = Path.Combine(parent, newName);
        if (string.Equals(target, path, StringComparison.Ordinal))
        {
            return path;
        }

        var caseOnly = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Path.Exists(target))
        {
            throw new IOException($"'{newName}' already exists.");
        }

        if (caseOnly)
        {
            // Case-insensitive file systems reject a direct case-only move of folders.
            var temporary = Transfers.FileTransferPaths.GetUniquePath($"{target}.ctrename");
            MoveEntry(path, temporary);
            MoveEntry(temporary, target);
        }
        else
        {
            MoveEntry(path, target);
        }

        return target;
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void Delete(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists && info.LinkTarget is null)
        {
            return;
        }

        if (info.LinkTarget is not null)
        {
            if (info is DirectoryInfo && OperatingSystem.IsWindows())
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if (info is DirectoryInfo directory)
        {
            foreach (var child in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                Delete(child.FullName, cancellationToken);
            }

            directory.Attributes &= ~FileAttributes.ReadOnly;
            directory.Delete();
            return;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            info.Attributes &= ~FileAttributes.ReadOnly;
        }

        info.Delete();
    }
}
