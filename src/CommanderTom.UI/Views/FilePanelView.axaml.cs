using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommanderTom.UI.ViewModels;

namespace CommanderTom.UI.Views;

public partial class FilePanelView : UserControl
{
    private FilePanelViewModel? _viewModel;

    public FilePanelView()
    {
        InitializeComponent();

        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);
        FileList.DoubleTapped += OnFileListDoubleTapped;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as FilePanelViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is not { IsActive: true })
        {
            return;
        }

        // Keep keyboard focus on the selected row of the active panel so the arrow keys work
        // immediately after switching panels or loading a new directory.
        if (e.PropertyName is nameof(FilePanelViewModel.IsActive) or nameof(FilePanelViewModel.Items))
        {
            FocusSelectedItem();
        }
    }

    public void FocusSelectedItem()
    {
        // Post so the ListBox has applied the new ItemsSource/SelectedItem before we look for the container.
        Dispatcher.UIThread.Post(() =>
        {
            var index = Math.Max(FileList.SelectedIndex, 0);
            if (FileList.ItemCount == 0)
            {
                FileList.Focus(NavigationMethod.Directional);
                return;
            }

            FileList.ScrollIntoView(index);
            if (FileList.ContainerFromIndex(index) is { } container)
            {
                container.Focus(NavigationMethod.Directional);
            }
            else
            {
                FileList.Focus(NavigationMethod.Directional);
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnAnyGotFocus(object? sender, FocusChangedEventArgs e) => _viewModel?.RequestActivation();

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e) => _viewModel?.RequestActivation();

    private void OnFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel?.OpenSelectedCommand.CanExecute(null) == true)
        {
            _viewModel.OpenSelectedCommand.Execute(null);
        }
    }
}
