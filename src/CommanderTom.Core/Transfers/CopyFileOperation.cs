namespace CommanderTom.Core.Transfers;

public sealed class CopyFileOperation : IFileOperation
{
    public FileTransferType Type => FileTransferType.Copy;

    public Task ExecuteAsync(FileTransferTask task, FileOperationContext context) =>
        FileTransferEngine.CopyAsync(task.SourcePath, task.DestinationPath, context);
}
