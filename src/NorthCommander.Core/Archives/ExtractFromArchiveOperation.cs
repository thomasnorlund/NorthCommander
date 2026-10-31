using NorthCommander.Core.Transfers;

namespace NorthCommander.Core.Archives;

/// <summary>Copies a file or folder out of a ZIP archive (<see cref="FileTransferType.Extract"/>).</summary>
public sealed class ExtractFromArchiveOperation : IFileOperation
{
    public FileTransferType Type => FileTransferType.Extract;

    public async Task ExecuteAsync(FileTransferTask task, FileOperationContext context)
    {
        if (!ZipArchiveService.TryParse(task.SourcePath, out var archivePath, out var innerPath) || innerPath.Length == 0)
        {
            throw new FileNotFoundException($"Not an item inside a ZIP archive: {task.SourcePath}");
        }

        using var archive = ZipArchiveService.OpenRead(archivePath);
        var entries = ZipArchiveService.SelectEntries(archive, innerPath);
        if (entries.Count == 0)
        {
            throw new FileNotFoundException($"'{innerPath}' not found in {Path.GetFileName(archivePath)}.");
        }

        context.SetTotalBytes(entries.Sum(e => e.Entry.Length));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(task.DestinationPath));
        if (entries.Exists(e => e.RelativePath.Length > 0))
        {
            Directory.CreateDirectory(root);
        }

        foreach (var (entry, relativePath) in entries)
        {
            await context.CheckpointAsync().ConfigureAwait(false);
            var target = relativePath.Length == 0
                ? root
                : Path.GetFullPath(Path.Combine(root, relativePath.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(target, root))
            {
                throw new IOException($"Unsafe path in archive: {entry.FullName}");
            }

            if (ZipArchiveService.IsDirectoryEntry(entry))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var source = ZipArchiveService.ToVirtualPath(archivePath, ZipArchiveService.NormalizeEntryName(entry.FullName)!);
            var stamp = new FileStamp(entry.Length, entry.LastWriteTime.LocalDateTime);
            if (await FileTransferEngine.ResolveFileTargetAsync(source, target, context, stamp).ConfigureAwait(false) is not { } fileTarget)
            {
                context.ReportBytesTransferred(entry.Length);
                continue;
            }

            context.SetCurrentItem(source);
            await using var input = entry.Open();
            await FileTransferEngine.WriteStreamAsync(input, fileTarget.Path, fileTarget.Overwrite, entry.LastWriteTime.UtcDateTime, context).ConfigureAwait(false);
        }
    }

    private static bool IsInside(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
