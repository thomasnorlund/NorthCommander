namespace NorthCommander.Core.Models;

/// <summary>
/// Immutable snapshot of a single item in a directory listing.
/// </summary>
public sealed record FileSystemEntry(
    string Name,
    string Extension,
    string FullPath,
    long? Size,
    DateTime LastWriteTime,
    FileAttributes Attributes,
    FileSystemEntryKind Kind)
{
    public bool IsDirectory => Kind != FileSystemEntryKind.File;

    public bool IsParentLink => Kind == FileSystemEntryKind.ParentLink;

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
}

public enum FileSystemEntryKind
{
    ParentLink,
    Directory,
    File,
}
