using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommanderTom.UI.ViewModels;

/// <summary>Small overlay for confirmations (Delete) and single-line input (Rename).</summary>
public sealed partial class PromptDialogViewModel : ViewModelBase
{
    private Func<string, string?>? _onConfirm;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasInput { get; private set; }

    [ObservableProperty]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmText { get; private set; } = "OK";

    [ObservableProperty]
    public partial bool IsDanger { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <param name="input">Initial text of the input box, or null for a plain confirmation.</param>
    /// <param name="onConfirm">Receives the input; returns an error to keep the dialog open, or null on success.</param>
    public void Open(string title, string message, string? input, string confirmText, bool isDanger, Func<string, string?> onConfirm)
    {
        Title = title;
        Message = message;
        HasInput = input is not null;
        InputText = input ?? string.Empty;
        ConfirmText = confirmText;
        IsDanger = isDanger;
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

        if (HasInput && string.IsNullOrWhiteSpace(InputText))
        {
            ErrorMessage = "Please enter a name.";
            return;
        }

        ErrorMessage = _onConfirm(InputText);
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
