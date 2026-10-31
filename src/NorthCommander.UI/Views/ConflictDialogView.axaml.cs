using Avalonia.Controls;
using Avalonia.Input;

namespace NorthCommander.UI.Views;

public partial class ConflictDialogView : UserControl
{
    public ConflictDialogView()
    {
        InitializeComponent();
    }

    public void FocusDefaultButton() => OverwriteButton.Focus(NavigationMethod.Tab);
}
