namespace NorthCommander.Core.Transfers;

/// <summary>A file that cannot be written because <see cref="DestinationPath"/> already exists.</summary>
public sealed class FileConflict
{
    internal FileConflict(FileTransferTask task, string sourcePath, string destinationPath)
    {
        Task = task;
        SourcePath = sourcePath;
        DestinationPath = destinationPath;

        var source = new FileInfo(sourcePath);
        var destination = new FileInfo(destinationPath);
        SourceSize = source.Length;
        SourceModified = source.LastWriteTime;
        DestinationSize = destination.Length;
        DestinationModified = destination.LastWriteTime;
    }

    public FileTransferTask Task { get; }

    public string SourcePath { get; }

    public string DestinationPath { get; }

    public long SourceSize { get; }

    public DateTime SourceModified { get; }

    public long DestinationSize { get; }

    public DateTime DestinationModified { get; }
}
