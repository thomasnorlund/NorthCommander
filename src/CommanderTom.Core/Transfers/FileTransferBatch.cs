namespace CommanderTom.Core.Transfers;

/// <summary>
/// Groups the tasks created by one user action (e.g. F5 on several marked items) so that
/// "Overwrite All", "Skip All" and "Cancel" in the conflict dialog apply to all of them.
/// </summary>
public sealed class FileTransferBatch
{
    private int _stickyResolution = -1;
    private int _canceled;

    /// <summary>Overwrite or Skip once the user chose "... All"; null while every conflict is asked.</summary>
    public FileConflictResolution? StickyResolution
    {
        get => Volatile.Read(ref _stickyResolution) is var value and >= 0 ? (FileConflictResolution)value : null;
        internal set => Volatile.Write(ref _stickyResolution, value is { } resolution ? (int)resolution : -1);
    }

    public bool IsCanceled => Volatile.Read(ref _canceled) != 0;

    /// <summary>Serializes conflict questions so an "... All" answer is seen by the next conflict.</summary>
    internal SemaphoreSlim ConflictGate { get; } = new(1, 1);

    /// <summary>Running tasks of the batch stop at their next checkpoint; queued ones are canceled when they start.</summary>
    public void Cancel() => Volatile.Write(ref _canceled, 1);
}
