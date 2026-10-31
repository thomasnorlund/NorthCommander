namespace CommanderTom.Core.Transfers;

/// <summary>
/// Moves by renaming when possible (same volume, target absent), otherwise copies then deletes the source.
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

        if (TryRename(source, destination))
        {
            return;
        }

        await FileTransferEngine.CopyAsync(source, destination, context).ConfigureAwait(false);
        await context.CheckpointAsync().ConfigureAwait(false);
        context.SetCurrentItem(source);
        FileTransferEngine.Delete(source);
    }

    private static bool TryRename(string source, string destination)
    {
        if (!FileTransferPaths.IsSameVolume(source, destination) || Path.Exists(destination))
        {
            return false;
        }

        try
        {
            if (Path.GetDirectoryName(destination) is { Length: > 0 } parent)
            {
                Directory.CreateDirectory(parent);
            }

            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: false);
            }
            else
            {
                Directory.Move(source, destination);
            }

            return true;
        }
        catch (IOException) when (!Path.Exists(destination))
        {
            // Typically a cross-device move (e.g. different mount point): fall back to copy + delete.
            return false;
        }
    }
}
