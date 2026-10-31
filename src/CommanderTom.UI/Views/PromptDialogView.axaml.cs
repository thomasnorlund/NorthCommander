using Avalonia.Controls;
using Avalonia.Input;
using CommanderTom.UI.ViewModels;

namespace CommanderTom.UI.Views;

public partial class PromptDialogView : UserControl
{
    public PromptDialogView()
    {
        InitializeComponent();
    }

    public void FocusInput()
    {
        if (DataContext is not PromptDialogViewModel { HasInput: true } viewModel)
        {
            ConfirmButton.Focus(NavigationMethod.Tab);
            return;
        }

        // Like Total Commander's rename: select the name without its extension.
        InputBox.Focus(NavigationMethod.Tab);
        var text = viewModel.InputText;
        var dot = text.LastIndexOf('.');
        InputBox.SelectionStart = 0;
        InputBox.SelectionEnd = dot > 0 ? dot : text.Length;
    }
}
