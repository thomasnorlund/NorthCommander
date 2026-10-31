namespace NorthCommander.Core.Transfers;

public interface IBackgroundTransferQueue
{
    /// <summary>Raised on a background thread when a task is added, changes state, or reports progress.</summary>
    event EventHandler<FileTransferTask>? TaskChanged;

    /// <summary>Snapshot of queued, running and paused tasks (finished tasks are dropped).</summary>
    IReadOnlyList<FileTransferTask> Tasks { get; }

    FileTransferTask Enqueue(FileTransferType type, string sourcePath, string destinationPath);

    FileTransferTask Enqueue(FileTransferTask task);

    bool Pause(FileTransferTask task);

    bool Resume(FileTransferTask task);

    bool Cancel(FileTransferTask task);

    /// <summary>Pauses all queued and running tasks.</summary>
    void Pause();

    /// <summary>Resumes all paused tasks.</summary>
    void Resume();

    /// <summary>Cancels all unfinished tasks.</summary>
    void Cancel();
}
