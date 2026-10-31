namespace NorthCommander.Core.Models;

/// <summary>
/// Result of reading a directory: the normalized path and its sorted entries.
/// </summary>
public sealed record DirectoryListing(
    string Path,
    IReadOnlyList<FileSystemEntry> Entries,
    int DirectoryCount,
    int FileCount,
    long TotalFileSize,
    bool IsArchive = false);
