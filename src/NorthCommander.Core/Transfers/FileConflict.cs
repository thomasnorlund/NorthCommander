namespace NorthCommander.Core.Transfers;

/// <summary>Size and modification time of a file, for items that are not plain files on disk (e.g. inside a ZIP).</summary>
public readonly record struct FileStamp(long Size, DateTime Modified);

/// <summary>A file that cannot be written because <see cref="DestinationPath"/> already exists.</summary>
public sealed class FileConflict
{
    internal FileConflict(FileTransferTask task, string sourcePath, string destinationPath, FileStamp? sourceInfo = null, FileStamp? destinationInfo = null)
    {
        Task = task;
        SourcePath = sourcePath;
        DestinationPath = destinationPath;

        var source = sourceInfo ?? StampOf(sourcePath);
        var destination = destinationInfo ?? StampOf(destinationPath);
        SourceSize = source.Size;
        SourceModified = source.Modified;
        DestinationSize = destination.Size;
        DestinationModified = destination.Modified;
    }

    private static FileStamp StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(info.Length, info.LastWriteTime) : new FileStamp(0, DateTime.MinValue);
    }

    public FileTransferTask Task { get; }

    public string SourcePath { get; }

    public string DestinationPath { get; }

    public long SourceSize { get; }

    public DateTime SourceModified { get; }

    public long DestinationSize { get; }

    public DateTime DestinationModified { get; }
}
