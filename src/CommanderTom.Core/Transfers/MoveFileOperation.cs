namespace CommanderTom.Core.Transfers;

/// <summary>
/// Moves by renaming when possible (same volume), otherwise copies then deletes the source.
/// Files skipped in the conflict dialog stay in the source.
/// </summary>
public sealed class MoveFileOperation : IFileOperation
{
    public FileTransferType Type => FileTransferType.Move;

    public async Task ExecuteAsync(FileTransferTask task, FileOperationContext context)
    {
        var source = task.SourcePath;
        var destination = task.DestinationPath;
        FileTransferEngine.EnsureValid(source, destination);
        await context.CheckpointAsync().ConfigureAwait(false);

        if (File.Exists(source))
        {
            await MoveFileAsync(source, destination, context).ConfigureAwait(false);
            return;
        }

        if (!Path.Exists(destination) && TryRenameDirectory(source, destination))
        {
            return;
        }

        // Target folder exists (merge) or another volume: copy, then remove what was copied.
        var result = await FileTransferEngine.CopyAsync(source, destination, context).ConfigureAwait(false);
        await context.CheckpointAsync().ConfigureAwait(false);
        context.SetCurrentItem(source);
        if (result.SkippedCount == 0)
        {
            FileTransferEngine.Delete(source);
        }
        else
        {
            FileTransferEngine.DeleteMoved(source, result.CopiedFiles);
        }
    }

    private static async Task MoveFileAsync(string source, string destination, FileOperationContext context)
    {
        var length = new FileInfo(source).Length;
        context.SetTotalBytes(length);
        context.SetCurrentItem(source);

        if (await FileTransferEngine.ResolveFileTargetAsync(source, destination, context).ConfigureAwait(false) is not { } target)
        {
            context.ReportBytesTransferred(length);
            return;
        }

        if (TryRenameFile(source, target))
        {
            context.ReportBytesTransferred(length);
            return;
        }

        await FileTransferEngine.CopyFileAsync(source, target.Path, target.Overwrite, context).ConfigureAwait(false);
        await context.CheckpointAsync().ConfigureAwait(false);
        FileTransferEngine.Delete(source);
    }

    private static bool TryRenameFile(string source, FileTarget target)
    {
        if (!FileTransferPaths.IsSameVolume(source, target.Path))
        {
            return false;
        }

        try
        {
            CreateParentDirectory(target.Path);
            if (target.Overwrite && OperatingSystem.IsWindows())
            {
                File.SetAttributes(target.Path, File.GetAttributes(target.Path) & ~FileAttributes.ReadOnly);
            }

            File.Move(source, target.Path, target.Overwrite);
            return true;
        }
        catch (IOException) when (File.Exists(source))
        {
            // Typically a cross-device move (e.g. different mount point): fall back to copy + delete.
            return false;
        }
    }

    private static bool TryRenameDirectory(string source, string destination)
    {
        if (!FileTransferPaths.IsSameVolume(source, destination))
        {
            return false;
        }

        try
        {
            CreateParentDirectory(destination);
            Directory.Move(source, destination);
            return true;
        }
        catch (IOException) when (!Path.Exists(destination))
        {
            return false;
        }
    }

    private static void CreateParentDirectory(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
        {
            Directory.CreateDirectory(parent);
        }
    }
}
