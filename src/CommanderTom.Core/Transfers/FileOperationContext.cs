namespace CommanderTom.Core.Transfers;

/// <summary>
/// Handed to an <see cref="IFileOperation"/> while it runs: cancellation, pause checkpoints and progress reporting.
/// </summary>
public sealed class FileOperationContext
{
    private readonly FileTransferTask _task;

    internal FileOperationContext(FileTransferTask task, CancellationToken cancellationToken)
    {
        _task = task;
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }

    /// <summary>Throws if canceled and waits while the task is paused. Call between chunks/items.</summary>
    public async ValueTask CheckpointAsync()
    {
        CancellationToken.ThrowIfCancellationRequested();
        await _task.PauseGate.WaitIfPausedAsync(CancellationToken).ConfigureAwait(false);
    }

    public void SetTotalBytes(long totalBytes) => _task.SetTotalBytes(totalBytes);

    public void ReportBytesTransferred(long bytes) => _task.AddTransferredBytes(bytes);

    public void SetCurrentItem(string? path) => _task.SetCurrentItem(path);
}
