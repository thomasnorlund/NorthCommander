using Avalonia.Controls;
using Avalonia.Input;

namespace NorthCommander.UI.Views;

public partial class TransferDialogView : UserControl
{
    public TransferDialogView()
    {
        InitializeComponent();
    }

    public void FocusTarget()
    {
        TargetBox.Focus(NavigationMethod.Tab);
        TargetBox.SelectAll();
    }
}
