using Avalonia.Controls;
using Avalonia.Input;

namespace NorthCommander.UI.Views;

public partial class SyncDirectoriesView : UserControl
{
    public SyncDirectoriesView()
    {
        InitializeComponent();
    }

    public void FocusCompare() => CompareButton.Focus(NavigationMethod.Tab);
}
