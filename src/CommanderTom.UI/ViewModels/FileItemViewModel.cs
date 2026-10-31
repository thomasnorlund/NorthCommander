using System.Globalization;
using CommanderTom.Core.Models;

namespace CommanderTom.UI.ViewModels;

/// <summary>
/// Read-only row in a file panel. Display strings are computed on demand so that only
/// rows realized by the virtualizing list pay the formatting cost.
/// </summary>
public sealed class FileItemViewModel(FileSystemEntry entry)
{
    public FileSystemEntry Entry { get; } = entry;

    public string FullPath => Entry.FullPath;

    public bool IsDirectory => Entry.IsDirectory;

    public bool IsParentLink => Entry.IsParentLink;

    public double Opacity => Entry.IsHidden ? 0.55 : 1.0;

    public string DisplayName => Entry.IsDirectory ? $"[{Entry.Name}]" : Entry.Name;

    public string Extension => Entry.Extension;

    public string SizeText => Entry.Kind switch
    {
        FileSystemEntryKind.ParentLink => "<UP>",
        FileSystemEntryKind.Directory => "<DIR>",
        _ => Entry.Size?.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty,
    };

    public string DateText => Entry.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
}
