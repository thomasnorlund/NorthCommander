namespace CommanderTom.Core.Transfers;

/// <summary>Answer to a <see cref="FileConflict"/> (target file already exists).</summary>
public enum FileConflictResolution
{
    Overwrite,

    /// <summary>Overwrite this and every later conflict of the same <see cref="FileTransferBatch"/>.</summary>
    OverwriteAll,

    Skip,

    /// <summary>Skip this and every later conflict of the same <see cref="FileTransferBatch"/>.</summary>
    SkipAll,

    /// <summary>Keep both: the new file gets a free name such as "name (2).ext".</summary>
    Rename,

    /// <summary>Cancel the task and the rest of its <see cref="FileTransferBatch"/>.</summary>
    Cancel,
}
