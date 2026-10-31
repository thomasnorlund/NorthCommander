namespace CommanderTom.Core.Transfers;

/// <summary>Asks the user what to do when a transfer would overwrite an existing file.</summary>
public interface IFileConflictResolver
{
    /// <summary>
    /// Called on a worker thread; only the conflicting task waits for the answer. Conflicts of one
    /// <see cref="FileTransferBatch"/> are asked one at a time. Cancel the token to abandon the question.
    /// </summary>
    Task<FileConflictResolution> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken);
}
