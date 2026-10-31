using NorthCommander.Core.Transfers;

namespace NorthCommander.Core.Archives;

/// <summary>Copies a file or folder out of an archive (<see cref="FileTransferType.Extract"/>).</summary>
public sealed class ExtractFromArchiveOperation : IFileOperation
{
    public FileTransferType Type => FileTransferType.Extract;

    public async Task ExecuteAsync(FileTransferTask task, FileOperationContext context)
    {
        if (!ArchiveService.TryParse(task.SourcePath, out var archivePath, out var innerPath) || innerPath.Length == 0)
        {
            throw new FileNotFoundException($"Not an item inside an archive: {task.SourcePath}");
        }

        using var archive = ArchiveService.OpenRead(archivePath);
        var entries = ArchiveService.SelectEntries(archive, innerPath);
        if (entries.Count == 0)
        {
            throw new FileNotFoundException($"'{innerPath}' not found in {Path.GetFileName(archivePath)}.");
        }

        context.SetTotalBytes(entries.Sum(e => e.Item.Length));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(task.DestinationPath));
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (item, relativePath) in entries)
        {
            var target = relativePath.Length == 0
                ? root
                : Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(target, root))
            {
                throw new IOException($"Unsafe path in archive: {item.Name}");
            }

            if (item.IsDirectory)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                targets[item.Key] = target;
            }
        }

        // Files are read in archive order, so compressed TARs and solid archives are decompressed only once.
        foreach (var (item, input) in archive.Read(i => targets.ContainsKey(i.Key)))
        {
            await context.CheckpointAsync().ConfigureAwait(false);
            var target = targets[item.Key];
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var source = ArchiveService.ToVirtualPath(archivePath, item.Name);
            var stamp = new FileStamp(item.Length, item.LastWriteTime);
            if (await FileTransferEngine.ResolveFileTargetAsync(source, target, context, stamp).ConfigureAwait(false) is not { } fileTarget)
            {
                context.ReportBytesTransferred(item.Length);
                continue;
            }

            context.SetCurrentItem(source);
            await FileTransferEngine.WriteStreamAsync(input, fileTarget.Path, fileTarget.Overwrite, item.LastWriteTime.ToUniversalTime(), context).ConfigureAwait(false);
        }
    }

    private static bool IsInside(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
