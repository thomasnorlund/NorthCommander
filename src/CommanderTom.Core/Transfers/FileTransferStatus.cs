namespace CommanderTom.Core.Transfers;

public enum FileTransferStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Canceled,
}
