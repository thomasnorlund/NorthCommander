namespace NorthCommander.Core.Transfers;

public sealed class BackgroundTransferQueueOptions
{
    /// <summary>Number of tasks processed concurrently. 1 = strictly sequential (default).</summary>
    public int MaxParallelTransfers { get; init; } = 1;
}
