using CommunityToolkit.Mvvm.ComponentModel;

namespace NorthCommander.UI.ViewModels;

/// <summary>One row of the multi-rename preview grid.</summary>
public sealed partial class MultiRenameItemViewModel(string sourcePath) : ObservableObject
{
    public string SourcePath { get; } = sourcePath;

    public string OldName { get; } = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));

    public string Folder { get; } = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sourcePath)) ?? string.Empty;

    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }
}
