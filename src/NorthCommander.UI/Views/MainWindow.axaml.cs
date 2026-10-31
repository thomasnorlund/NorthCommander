using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NorthCommander.Core.Settings;
using NorthCommander.UI.ViewModels;

namespace NorthCommander.UI.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Tunnel so Tab is intercepted before Avalonia's focus navigation consumes it.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
    }

    /// <summary>Restores size/position (only if still on a connected screen) and maximized state.</summary>
    public void ApplySettings(WindowSettings settings)
    {
        if (settings.Width is double width && settings.Height is double height && width >= MinWidth && height >= MinHeight)
        {
            Width = width;
            Height = height;
        }

        if (settings.X is int x && settings.Y is int y
            && Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(x + 40, y + 20))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }

        if (settings.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    public void CaptureSettings(WindowSettings settings)
    {
        settings.IsMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            settings.Width = Width;
            settings.Height = Height;
            settings.X = Position.X;
            settings.Y = Position.Y;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        _viewModel?.TransferDialog.PropertyChanged -= OnDialogPropertyChanged;
        _viewModel?.ConflictDialog.PropertyChanged -= OnDialogPropertyChanged;
        _viewModel?.PromptDialog.PropertyChanged -= OnDialogPropertyChanged;
        _viewModel?.Lister.PropertyChanged -= OnDialogPropertyChanged;
        _viewModel?.MultiRename.PropertyChanged -= OnDialogPropertyChanged;
        _viewModel?.SyncDirectories.PropertyChanged -= OnDialogPropertyChanged;
        _viewModel?.QuitRequested -= OnQuitRequested;
        _viewModel = DataContext as MainWindowViewModel;
        _viewModel?.QuitRequested += OnQuitRequested;
        _viewModel?.SyncDirectories.PropertyChanged += OnDialogPropertyChanged;
        _viewModel?.MultiRename.PropertyChanged += OnDialogPropertyChanged;
        _viewModel?.Lister.PropertyChanged += OnDialogPropertyChanged;
        _viewModel?.PromptDialog.PropertyChanged += OnDialogPropertyChanged;
        _viewModel?.TransferDialog.PropertyChanged += OnDialogPropertyChanged;
        _viewModel?.ConflictDialog.PropertyChanged += OnDialogPropertyChanged;
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
        if (_viewModel is null || _viewModel.IsModalOpen)
        {
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && !string.IsNullOrWhiteSpace(_viewModel.CommandText))
        {
            // Typed command wins over "open the selected item".
            _viewModel.ExecuteCommandLineCommand.Execute(null);
            ActivePanelView.FocusSelectedItem();
            e.Handled = true;
        }
        else if (e.Key is Key.Escape or Key.Enter && CommandBox.IsFocused)
        {
            _viewModel.CommandText = string.Empty;
            ActivePanelView.FocusSelectedItem();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _viewModel.PreviousTabCommand : _viewModel.NextTabCommand;
            command.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
        {
            _viewModel.SwitchActivePanelCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Like NC, typing anywhere in the panels goes to the command line.</summary>
    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (_viewModel is null || _viewModel.IsModalOpen || e.Source is TextBox || string.IsNullOrEmpty(e.Text)
            || char.IsControl(e.Text[0]) || (e.Text == " " && _viewModel.CommandText.Length == 0))
        {
            return;
        }

        _viewModel.CommandText += e.Text;
        e.Handled = true;
        CommandBox.Focus();
        CommandBox.CaretIndex = _viewModel.CommandText.Length;
    }

    private void OnQuitRequested(object? sender, EventArgs e) => Close();

    private void OnDialogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TransferDialogViewModel.IsOpen) || _viewModel is null)
        {
            return;
        }

        // Topmost open overlay gets the keyboard: conflict prompt, then F5/F6 dialog, then the active panel.
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.ConflictDialog.IsOpen)
            {
                ConflictDialogView.FocusDefaultButton();
            }
            else if (_viewModel.PromptDialog.IsOpen)
            {
                PromptDialogView.FocusInput();
            }
            else if (_viewModel.SyncDirectories.IsOpen)
            {
                SyncDirectoriesView.FocusCompare();
            }
            else if (_viewModel.MultiRename.IsOpen)
            {
                MultiRenameView.FocusMask();
            }
            else if (_viewModel.TransferDialog.IsOpen)
            {
                TransferDialogView.FocusTarget();
            }
            else if (_viewModel.Lister.IsOpen)
            {
                ListerView.FocusViewer();
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
