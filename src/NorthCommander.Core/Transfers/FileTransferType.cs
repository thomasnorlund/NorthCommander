namespace NorthCommander.Core.Transfers;

public enum FileTransferType
{
    Copy,
    Move,

    /// <summary>Copy items out of a ZIP archive to a folder.</summary>
    Extract,

    /// <summary>Copy files or folders into a ZIP archive.</summary>
    Pack,
}
