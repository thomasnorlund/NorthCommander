using Avalonia.Controls;
using Avalonia.Input;

namespace NorthCommander.UI.Views;

public partial class MultiRenameView : UserControl
{
    public MultiRenameView()
    {
        InitializeComponent();
    }

    public void FocusMask()
    {
        NameMaskBox.Focus(NavigationMethod.Tab);
        NameMaskBox.CaretIndex = NameMaskBox.Text?.Length ?? 0;
    }
}
