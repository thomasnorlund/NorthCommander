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
    private TabViewModel? _tab;

    public FilePanelView()
    {
        InitializeComponent();

        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);

        // Tunnel so the ListBox does not consume Space before we can use it for marking.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerPressedEvent, OnTabStripPointerPressed);
        FileList.DoubleTapped += OnFileListDoubleTapped;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as FilePanelViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        AttachSelectedTab();
    }

    private void AttachSelectedTab()
    {
        _tab?.PropertyChanged -= OnTabPropertyChanged;
        _tab = _viewModel?.SelectedTab;
        _tab?.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is { IsActive: true } && e.PropertyName == nameof(TabViewModel.Items))
        {
            FocusSelectedItem();
        }
    }

    private void OnTabStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || (e.Source as Avalonia.StyledElement)?.DataContext is not TabViewModel tab)
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsLeftButtonPressed)
        {
            _viewModel.SelectedTab = tab;
        }
        else if (point.IsMiddleButtonPressed)
        {
            _viewModel.CloseTab(tab);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePanelViewModel.SelectedTab))
        {
            AttachSelectedTab();
        }

        if (_viewModel is not { IsActive: true })
        {
            return;
        }

        // Keep keyboard focus on the selected row of the active panel so the arrow keys work
        // immediately after switching panels or loading a new directory.
        if (e.PropertyName is nameof(FilePanelViewModel.IsActive) or nameof(FilePanelViewModel.SelectedTab))
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

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Insert or Key.Space))
        {
            return;
        }

        _viewModel.SelectedTab.ToggleMarkCommand.Execute(null);
        FocusSelectedItem();
        e.Handled = true;
    }

    private void OnAnyGotFocus(object? sender, FocusChangedEventArgs e) => _viewModel?.RequestActivation();

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e) => _viewModel?.RequestActivation();

    private void OnFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel?.SelectedTab.OpenSelectedCommand.CanExecute(null) == true)
        {
            _viewModel.SelectedTab.OpenSelectedCommand.Execute(null);
        }
    }
}
