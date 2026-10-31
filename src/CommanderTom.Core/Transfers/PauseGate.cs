namespace CommanderTom.Core.Transfers;

/// <summary>
/// Async gate that lets a running operation park itself at checkpoints while paused.
/// </summary>
public sealed class PauseGate
{
    private readonly Lock _lock = new();
    private TaskCompletionSource? _resumed;

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _resumed is not null;
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? resumed;
        lock (_lock)
        {
            resumed = _resumed;
            _resumed = null;
        }

        resumed?.TrySetResult();
    }

    public Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _resumed is null ? Task.CompletedTask : _resumed.Task.WaitAsync(cancellationToken);
        }
    }
}
