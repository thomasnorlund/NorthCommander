namespace CommanderTom.Core.Transfers;

/// <summary>
/// Handed to an <see cref="IFileOperation"/> while it runs: cancellation, pause checkpoints, progress reporting
/// and conflict resolution.
/// </summary>
public sealed class FileOperationContext
{
    private readonly FileTransferTask _task;
    private readonly IFileConflictResolver? _conflictResolver;

    internal FileOperationContext(FileTransferTask task, IFileConflictResolver? conflictResolver, CancellationToken cancellationToken)
    {
        _task = task;
        _conflictResolver = conflictResolver;
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }

    /// <summary>Throws if canceled and waits while the task is paused. Call between chunks/items.</summary>
    public async ValueTask CheckpointAsync()
    {
        ThrowIfCanceled();
        await _task.PauseGate.WaitIfPausedAsync(CancellationToken).ConfigureAwait(false);
    }

    public void SetTotalBytes(long totalBytes) => _task.SetTotalBytes(totalBytes);

    public void ReportBytesTransferred(long bytes) => _task.AddTransferredBytes(bytes);

    public void SetCurrentItem(string? path) => _task.SetCurrentItem(path);

    /// <summary>
    /// Decides what to do with an existing <paramref name="destinationPath"/>: returns
    /// <see cref="FileConflictResolution.Overwrite"/>, <see cref="FileConflictResolution.Skip"/> or
    /// <see cref="FileConflictResolution.Rename"/>. Without a resolver the transfer fails (never overwrites).
    /// </summary>
    public async Task<FileConflictResolution> ResolveConflictAsync(string sourcePath, string destinationPath)
    {
        var batch = _task.Batch;
        await batch.ConflictGate.WaitAsync(CancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfCanceled();
            if (batch.StickyResolution is { } sticky)
            {
                return sticky;
            }

            if (_conflictResolver is null)
            {
                throw new IOException($"Destination already exists: {destinationPath}");
            }

            FileConflictResolution choice;
            _task.PendingConflict = new FileConflict(_task, sourcePath, destinationPath);
            _task.NotifyChanged();
            try
            {
                choice = await _conflictResolver.ResolveAsync(_task.PendingConflict, CancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _task.PendingConflict = null;
                _task.NotifyChanged();
            }

            switch (choice)
            {
                case FileConflictResolution.OverwriteAll:
                    batch.StickyResolution = FileConflictResolution.Overwrite;
                    return FileConflictResolution.Overwrite;
                case FileConflictResolution.SkipAll:
                    batch.StickyResolution = FileConflictResolution.Skip;
                    return FileConflictResolution.Skip;
                case FileConflictResolution.Cancel:
                    batch.Cancel();
                    ThrowIfCanceled();
                    throw new OperationCanceledException(CancellationToken);
                default:
                    return choice;
            }
        }
        finally
        {
            batch.ConflictGate.Release();
        }
    }

    private void ThrowIfCanceled()
    {
        if (_task.Batch.IsCanceled)
        {
            _task.Cancellation.Cancel();
        }

        CancellationToken.ThrowIfCancellationRequested();
    }
}
