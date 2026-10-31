using NorthCommander.Core.Transfers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NorthCommander.UI.ViewModels;

/// <summary>
/// Overlay that asks for the target of an F5 (copy) / F6 (move) operation.
/// </summary>
public sealed partial class TransferDialogViewModel : ViewModelBase
{
    private Func<string, string?>? _onConfirm;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Prompt { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmText { get; private set; } = "OK";

    [ObservableProperty]
    public partial string TargetPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <param name="onConfirm">Called with the entered target; returns an error message to keep the dialog open, or null on success.</param>
    public void Open(FileTransferType type, IReadOnlyList<string> sources, string targetPath, Func<string, string?> onConfirm)
    {
        var verb = type == FileTransferType.Move ? "Move" : "Copy";
        var what = sources.Count == 1
            ? $"'{Path.GetFileName(Path.TrimEndingDirectorySeparator(sources[0]))}'"
            : $"{sources.Count} items";

        Title = type == FileTransferType.Move ? "Move (F6)" : "Copy (F5)";
        Prompt = $"{verb} {what} to:";
        ConfirmText = verb;
        TargetPath = targetPath;
        ErrorMessage = null;
        _onConfirm = onConfirm;
        IsOpen = true;
    }

    [RelayCommand]
    private void Confirm()
    {
        if (!IsOpen || _onConfirm is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TargetPath))
        {
            ErrorMessage = "Please enter a target path.";
            return;
        }

        ErrorMessage = _onConfirm(TargetPath.Trim());
        if (ErrorMessage is null)
        {
            Close();
        }
    }

    [RelayCommand]
    private void Cancel() => Close();

    private void Close()
    {
        _onConfirm = null;
        IsOpen = false;
    }
}
