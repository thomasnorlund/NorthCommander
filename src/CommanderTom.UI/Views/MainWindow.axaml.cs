using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommanderTom.UI.ViewModels;

namespace CommanderTom.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Tunnel so Tab is intercepted before Avalonia's focus navigation consumes it.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (ViewModel is { } viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
        {
            ViewModel?.SwitchActivePanelCommand.Execute(null);
            e.Handled = true;
        }
    }
}
