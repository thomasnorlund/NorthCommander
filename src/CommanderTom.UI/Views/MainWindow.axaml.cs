using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommanderTom.UI.ViewModels;

namespace CommanderTom.UI.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Tunnel so Tab is intercepted before Avalonia's focus navigation consumes it.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        _viewModel?.TransferDialog.PropertyChanged -= OnTransferDialogPropertyChanged;
        _viewModel = DataContext as MainWindowViewModel;
        _viewModel?.TransferDialog.PropertyChanged += OnTransferDialogPropertyChanged;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_viewModel is { } viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || _viewModel.TransferDialog.IsOpen)
        {
            return;
        }

        if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
        {
            _viewModel.SwitchActivePanelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTransferDialogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TransferDialogViewModel.IsOpen) || _viewModel is null)
        {
            return;
        }

        var isOpen = _viewModel.TransferDialog.IsOpen;
        Dispatcher.UIThread.Post(() =>
        {
            if (isOpen)
            {
                TransferDialogView.FocusTarget();
            }
            else
            {
                ActivePanelView.FocusSelectedItem();
            }
        }, DispatcherPriority.Loaded);
    }

    private FilePanelView ActivePanelView =>
        ReferenceEquals(_viewModel?.ActivePanel, _viewModel?.RightPanel) ? RightPanelView : LeftPanelView;
}
