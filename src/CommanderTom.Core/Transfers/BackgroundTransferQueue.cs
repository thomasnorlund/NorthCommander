namespace CommanderTom.Core.Transfers;

/// <summary>
/// Thread-safe FIFO of <see cref="FileTransferTask"/>s processed by background workers
/// (<see cref="BackgroundTransferQueueOptions.MaxParallelTransfers"/> at a time).
/// </summary>
public sealed class BackgroundTransferQueue : IBackgroundTransferQueue, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Queue<FileTransferTask> _queue = new();
    private readonly List<FileTransferTask> _tasks = [];
    private readonly List<FileTransferTask> _parked = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<FileTransferType, IFileOperation> _operations;
    private readonly Task[] _workers;
    private bool _disposed;

    public BackgroundTransferQueue(IEnumerable<IFileOperation> operations, BackgroundTransferQueueOptions? options = null)
    {
        _operations = operations.ToDictionary(o => o.Type);
        var workerCount = Math.Max(1, options?.MaxParallelTransfers ?? 1);
        _workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            _workers[i] = Task.Run(WorkerLoopAsync);
        }
    }

    public event EventHandler<FileTransferTask>? TaskChanged;

    public IReadOnlyList<FileTransferTask> Tasks
    {
        get
        {
            lock (_lock)
            {
                return [.. _tasks];
            }
        }
    }

    public FileTransferTask Enqueue(FileTransferType type, string sourcePath, string destinationPath) =>
        Enqueue(new FileTransferTask(type, sourcePath, destinationPath));

    public FileTransferTask Enqueue(FileTransferTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_operations.ContainsKey(task.TaskType))
        {
            throw new NotSupportedException($"No {nameof(IFileOperation)} registered for {task.TaskType}.");
        }

        task.Changed += OnTaskChanged;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (task.Status != FileTransferStatus.Queued || _tasks.Contains(task))
            {
                throw new InvalidOperationException("Task has already been queued.");
            }

            _tasks.Add(task);
            _queue.Enqueue(task);
        }

        _signal.Release();
        task.NotifyChanged();
        return task;
    }

    public bool Pause(FileTransferTask task)
    {
        lock (_lock)
        {
            if (task.Status is not (FileTransferStatus.Queued or FileTransferStatus.Running))
            {
                return false;
            }

            task.Status = FileTransferStatus.Paused;
            task.PauseGate.Pause();
        }

        task.NotifyChanged();
        return true;
    }

    public bool Resume(FileTransferTask task)
    {
        var requeued = false;
        lock (_lock)
        {
            if (task.Status != FileTransferStatus.Paused)
            {
                return false;
            }

            task.Status = task.HasStarted ? FileTransferStatus.Running : FileTransferStatus.Queued;
            task.PauseGate.Resume();

            if (_parked.Remove(task))
            {
                _queue.Enqueue(task);
                requeued = true;
            }
        }

        if (requeued)
        {
            _signal.Release();
        }

        task.NotifyChanged();
        return true;
    }

    public bool Cancel(FileTransferTask task)
    {
        bool finishedNow;
        lock (_lock)
        {
            if (task.IsFinished)
            {
                return false;
            }

            // Not yet picked up by a worker: finish it right here. A stale entry left in
            // _queue is skipped by the worker because its status is no longer Queued.
            finishedNow = !task.HasStarted;
            if (finishedNow)
            {
                task.Status = FileTransferStatus.Canceled;
                _parked.Remove(task);
                _tasks.Remove(task);
            }
        }

        task.Cancellation.Cancel();
        task.PauseGate.Resume();

        if (finishedNow)
        {
            task.Changed -= OnTaskChanged;
            task.NotifyChanged();
            TaskChanged?.Invoke(this, task);
        }

        return true;
    }

    public void Pause()
    {
        foreach (var task in Tasks)
        {
            Pause(task);
        }
    }

    public void Resume()
    {
        foreach (var task in Tasks)
        {
            Resume(task);
        }
    }

    public void Cancel()
    {
        foreach (var task in Tasks)
        {
            Cancel(task);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Cancel();
        _shutdown.Cancel();

        // Give running operations a moment to clean up partially written files.
        Task.WaitAll(_workers, TimeSpan.FromSeconds(5));
        _signal.Dispose();
        _shutdown.Dispose();
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            FileTransferTask? task;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out task))
                {
                    continue;
                }

                if (task.Status == FileTransferStatus.Paused)
                {
                    _parked.Add(task);
                    continue;
                }

                if (task.Status != FileTransferStatus.Queued)
                {
                    continue;
                }

                task.HasStarted = true;
                task.Status = FileTransferStatus.Running;
            }

            task.NotifyChanged();
            await RunAsync(task).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(FileTransferTask task)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.Cancellation.Token, _shutdown.Token);
        var context = new FileOperationContext(task, linked.Token);

        FileTransferStatus finalStatus;
        try
        {
            await _operations[task.TaskType].ExecuteAsync(task, context).ConfigureAwait(false);
            finalStatus = FileTransferStatus.Completed;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            finalStatus = FileTransferStatus.Canceled;
        }
        catch (Exception ex)
        {
            task.ErrorMessage = ex.Message;
            finalStatus = FileTransferStatus.Failed;
        }

        lock (_lock)
        {
            task.Status = finalStatus;
            _tasks.Remove(task);
        }

        task.SetCurrentItem(null);
        task.Changed -= OnTaskChanged;
        task.NotifyChanged();
        TaskChanged?.Invoke(this, task);
    }

    private void OnTaskChanged(object? sender, EventArgs e)
    {
        if (sender is FileTransferTask task)
        {
            TaskChanged?.Invoke(this, task);
        }
    }
}
