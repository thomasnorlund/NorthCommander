using Avalonia.Controls;
using Avalonia.Input;

namespace CommanderTom.UI.Views;

public partial class ConflictDialogView : UserControl
{
    public ConflictDialogView()
    {
        InitializeComponent();
    }

    public void FocusDefaultButton() => OverwriteButton.Focus(NavigationMethod.Tab);
}
