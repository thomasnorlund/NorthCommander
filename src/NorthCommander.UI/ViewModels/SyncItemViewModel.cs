using System.Globalization;
using NorthCommander.Core.Sync;

namespace NorthCommander.UI.ViewModels;

/// <summary>One row of the directory synchronization grid.</summary>
public sealed class SyncItemViewModel(SyncItem item)
{
    public SyncItem Item { get; } = item;

    public string LeftName => Item.Left is null ? string.Empty : Item.RelativePath;

    public string RightName => Item.Right is null ? string.Empty : Item.RelativePath;

    public string LeftSize => Format(Item.Left);

    public string RightSize => Format(Item.Right);

    public string Arrow => Item.Direction switch
    {
        SyncDirection.LeftToRight => "->",
        SyncDirection.RightToLeft => "<-",
        SyncDirection.Conflict => "!=",
        _ => "=",
    };

    public bool IsCopy => Item.Direction is SyncDirection.LeftToRight or SyncDirection.RightToLeft;

    public bool IsConflict => Item.Direction == SyncDirection.Conflict;

    public bool IsEqual => Item.Direction == SyncDirection.Equal;

    public string ToolTip => $"{Item.RelativePath}\nLeft:  {Describe(Item.Left)}\nRight: {Describe(Item.Right)}";

    private static string Format(SyncFile? file) => file?.Size.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;

    private static string Describe(SyncFile? file) =>
        file is null ? "-" : string.Create(CultureInfo.CurrentCulture, $"{file.Size:N0} bytes, {file.LastWriteTimeUtc.ToLocalTime():g}");
}
