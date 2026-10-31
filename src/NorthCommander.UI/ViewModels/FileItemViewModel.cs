using System.Globalization;
using NorthCommander.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NorthCommander.UI.ViewModels;

/// <summary>
/// Row in a file panel. Display strings are computed on demand so that only
/// rows realized by the virtualizing list pay the formatting cost.
/// </summary>
public sealed partial class FileItemViewModel(FileSystemEntry entry) : ObservableObject
{
    public FileSystemEntry Entry { get; } = entry;

    /// <summary>Marked with Insert/Space (Total Commander's "selection"; distinct from the cursor row).</summary>
    [ObservableProperty]
    public partial bool IsMarked { get; set; }

    public string FullPath => Entry.FullPath;

    public bool IsDirectory => Entry.IsDirectory;

    public bool IsParentLink => Entry.IsParentLink;

    /// <summary>A .zip file: Enter opens it like a folder.</summary>
    public bool IsArchive => Entry.Kind == FileSystemEntryKind.File && string.Equals(Entry.Extension, "zip", StringComparison.OrdinalIgnoreCase);

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
