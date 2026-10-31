namespace CommanderTom.Core.Transfers;

/// <summary>
/// A single copy/move job tracked by <see cref="IBackgroundTransferQueue"/>.
/// All members are safe to read from any thread; <see cref="Changed"/> is raised on worker threads.
/// </summary>
public sealed class FileTransferTask
{
    private const long ProgressNotifyIntervalMs = 100;

    private int _status = (int)FileTransferStatus.Queued;
    private long _totalBytes;
    private long _transferredBytes;
    private long _lastNotifyTicks;
    private string? _currentItem;
    private string? _errorMessage;

    public FileTransferTask(FileTransferType taskType, string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        TaskType = taskType;
        SourcePath = Path.GetFullPath(sourcePath);
        DestinationPath = Path.GetFullPath(destinationPath);
    }

    /// <summary>Raised (throttled for progress) whenever status or progress changes.</summary>
    public event EventHandler? Changed;

    public Guid Id { get; } = Guid.NewGuid();

    public FileTransferType TaskType { get; }

    public string SourcePath { get; }

    /// <summary>Full path the source item will have after the transfer.</summary>
    public string DestinationPath { get; }

    public string DisplayName =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(SourcePath)) is { Length: > 0 } name ? name : SourcePath;

    public FileTransferStatus Status
    {
        get => (FileTransferStatus)Volatile.Read(ref _status);
        internal set => Volatile.Write(ref _status, (int)value);
    }

    public bool IsFinished => Status is FileTransferStatus.Completed or FileTransferStatus.Failed or FileTransferStatus.Canceled;

    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public long TransferredBytes => Interlocked.Read(ref _transferredBytes);

    public double ProgressPercentage
    {
        get
        {
            if (Status == FileTransferStatus.Completed)
            {
                return 100;
            }

            var total = TotalBytes;
            return total <= 0 ? 0 : Math.Min(100, TransferredBytes * 100d / total);
        }
    }

    /// <summary>Path of the file currently being transferred, if any.</summary>
    public string? CurrentItem => Volatile.Read(ref _currentItem);

    public string? ErrorMessage
    {
        get => Volatile.Read(ref _errorMessage);
        internal set => Volatile.Write(ref _errorMessage, value);
    }

    /// <summary>True once a worker has picked the task up (distinguishes paused-while-queued from paused-while-running).</summary>
    internal bool HasStarted { get; set; }

    internal CancellationTokenSource Cancellation { get; } = new();

    internal PauseGate PauseGate { get; } = new();

    internal void SetTotalBytes(long totalBytes)
    {
        Interlocked.Exchange(ref _totalBytes, totalBytes);
        NotifyChanged();
    }

    internal void AddTransferredBytes(long bytes)
    {
        Interlocked.Add(ref _transferredBytes, bytes);
        NotifyProgressThrottled();
    }

    internal void SetCurrentItem(string? path) => Volatile.Write(ref _currentItem, path);

    internal void NotifyChanged()
    {
        Volatile.Write(ref _lastNotifyTicks, Environment.TickCount64);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyProgressThrottled()
    {
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref _lastNotifyTicks);
        if (now - last >= ProgressNotifyIntervalMs &&
            Interlocked.CompareExchange(ref _lastNotifyTicks, now, last) == last)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
