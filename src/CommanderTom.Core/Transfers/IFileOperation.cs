namespace CommanderTom.Core.Transfers;

/// <summary>
/// Executes one kind of <see cref="FileTransferTask"/> (copy, move, ...).
/// Implementations run on a background worker and must honor <see cref="FileOperationContext.CheckpointAsync"/>.
/// </summary>
public interface IFileOperation
{
    FileTransferType Type { get; }

    Task ExecuteAsync(FileTransferTask task, FileOperationContext context);
}
