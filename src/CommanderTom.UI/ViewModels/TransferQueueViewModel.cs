using CommanderTom.Core.Transfers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;

namespace CommanderTom.UI.ViewModels;

/// <summary>
/// UI-thread projection of <see cref="IBackgroundTransferQueue"/> for status bars.
/// </summary>
public sealed partial class TransferQueueViewModel : ViewModelBase
{
    private readonly IBackgroundTransferQueue _queue;
    private bool _updatePending;

    public TransferQueueViewModel(IBackgroundTransferQueue queue)
    {
        _queue = queue;
        _queue.TaskChanged += OnTaskChanged;
    }

    /// <summary>Raised on the UI thread when a task completes, fails or is canceled.</summary>
    public event EventHandler<FileTransferTask>? TransferFinished;

    [ObservableProperty]
    public partial bool HasActiveTransfers { get; private set; }

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    public partial double ProgressPercentage { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    public void Enqueue(FileTransferTask task) => _queue.Enqueue(task);

    [RelayCommand]
    private void TogglePause()
    {
        if (IsPaused)
        {
            _queue.Resume();
        }
        else
        {
            _queue.Pause();
        }
    }

    [RelayCommand]
    private void CancelAll() => _queue.Cancel();

    private void OnTaskChanged(object? sender, FileTransferTask task)
    {
        if (task.IsFinished)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Refresh();
                TransferFinished?.Invoke(this, task);
            });
            return;
        }

        // Coalesce bursts of progress notifications into a single UI update.
        lock (_queue)
        {
            if (_updatePending)
            {
                return;
            }

            _updatePending = true;
        }

        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        lock (_queue)
        {
            _updatePending = false;
        }

        var tasks = _queue.Tasks;
        HasActiveTransfers = tasks.Count > 0;
        if (tasks.Count == 0)
        {
            IsPaused = false;
            ProgressPercentage = 0;
            StatusText = string.Empty;
            return;
        }

        var current = tasks.FirstOrDefault(t => t.Status == FileTransferStatus.Running)
            ?? tasks.FirstOrDefault(t => t.Status == FileTransferStatus.Paused && t.TotalBytes > 0)
            ?? tasks[0];
        var waiting = tasks.Count - 1;

        IsPaused = tasks.All(t => t.Status == FileTransferStatus.Paused);
        ProgressPercentage = current.ProgressPercentage;

        var verb = current.TaskType == FileTransferType.Move ? "Moving" : "Copying";
        var state = current.PendingConflict is not null ? "Waiting for your decision: " : current.Status switch
        {
            FileTransferStatus.Paused => "Paused: ",
            FileTransferStatus.Queued => "Waiting: ",
            _ => string.Empty,
        };
        var queued = waiting > 0 ? $"  (+{waiting} queued)" : string.Empty;
        StatusText = $"{state}{verb} '{current.DisplayName}'  {current.ProgressPercentage:0}%{queued}";
    }
}
