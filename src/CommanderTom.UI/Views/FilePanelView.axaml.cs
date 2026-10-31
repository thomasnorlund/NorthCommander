using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommanderTom.Core.Transfers;
using CommanderTom.UI.Services;
using CommanderTom.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace CommanderTom.UI.Views;

public partial class FilePanelView : UserControl
{
    private const double DragThreshold = 6;

    private FilePanelViewModel? _viewModel;
    private TabViewModel? _tab;

    // Left button: drag & drop to the other panel (or onto a folder).
    private FileItemViewModel? _dragItem;
    private IReadOnlyList<string> _dragSources = [];
    private Point _dragStart;
    private bool _isDragging;
    private FilePanelView? _dropTarget;

    // Right button: drag marks a range (Total Commander), a short click opens the context menu.
    private FileItemViewModel? _rightItem;
    private int _rightLastIndex = -1;
    private bool _rightMarkState;
    private bool _isRightMarking;

    public FilePanelView()
    {
        InitializeComponent();

        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);

        // Tunnel so the ListBox does not consume Space before we can use it for marking.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerPressedEvent, OnTabStripPointerPressed);

        FileList.DoubleTapped += OnFileListDoubleTapped;
        FileList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerCaptureLostEvent, (_, _) => ResetPointerState(), RoutingStrategies.Direct);
    }

    private MainWindowViewModel? MainViewModel => TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;

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
        if (_viewModel is null || (e.Source as StyledElement)?.DataContext is not TabViewModel tab)
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
        if (_viewModel is null)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Insert or Key.Space)
        {
            _viewModel.SelectedTab.ToggleMarkCommand.Execute(null);
            FocusSelectedItem();
            e.Handled = true;
        }
        else if ((e.Key == Key.Apps && e.KeyModifiers == KeyModifiers.None)
                 || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
        {
            if (_viewModel.SelectedTab.SelectedItem is { } item)
            {
                ShowContextMenu(item);
            }

            e.Handled = true;
        }
    }

    private void OnAnyGotFocus(object? sender, FocusChangedEventArgs e) => _viewModel?.RequestActivation();

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e) => _viewModel?.RequestActivation();

    private void OnFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemAt(e.GetPosition(FileList)) is not { } item || _viewModel is null)
        {
            return;
        }

        _viewModel.SelectedTab.SelectedItem = item;
        if (_viewModel.SelectedTab.OpenSelectedCommand.CanExecute(null))
        {
            _viewModel.SelectedTab.OpenSelectedCommand.Execute(null);
        }
    }

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || ItemAt(e.GetPosition(FileList)) is not { } item)
        {
            return;
        }

        var properties = e.GetCurrentPoint(FileList).Properties;
        if (properties.IsRightButtonPressed)
        {
            // Handled: the ListBox must not run its own right-click selection.
            e.Handled = true;
            _viewModel.SelectedTab.SelectedItem = item;
            FocusSelectedItem();
            _rightItem = item;
            _rightLastIndex = IndexOf(item);
            _rightMarkState = !item.IsMarked;
            _isRightMarking = false;
            e.Pointer.Capture(FileList);
        }
        else if (properties.IsLeftButtonPressed && e.ClickCount == 1 && !item.IsParentLink)
        {
            _dragItem = item;
            _dragStart = e.GetPosition(FileList);
        }
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_rightItem is not null)
        {
            ExtendRightMark(e);
            return;
        }

        if (_dragItem is null)
        {
            return;
        }

        if (!_isDragging)
        {
            var position = e.GetPosition(FileList);
            if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
            {
                ResetPointerState();
                return;
            }

            if (Math.Abs(position.X - _dragStart.X) < DragThreshold && Math.Abs(position.Y - _dragStart.Y) < DragThreshold)
            {
                return;
            }

            _dragSources = _viewModel.SelectedTab.GetContextPaths(_dragItem);
            if (_dragSources.Count == 0)
            {
                ResetPointerState();
                return;
            }

            _isDragging = true;
            e.Pointer.Capture(FileList);
        }

        var (view, target) = FindDropTarget(e);
        SetDropTarget(target is null ? null : view);
        SetCursor(target is null
            ? StandardCursorType.No
            : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? StandardCursorType.DragMove : StandardCursorType.DragCopy);
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_rightItem is { } rightItem && e.InitialPressMouseButton == MouseButton.Right)
        {
            var wasMarking = _isRightMarking;
            e.Handled = true;
            e.Pointer.Capture(null);
            ResetPointerState();
            if (!wasMarking)
            {
                ShowContextMenu(rightItem);
            }

            return;
        }

        if (_isDragging && e.InitialPressMouseButton == MouseButton.Left)
        {
            var (_, target) = FindDropTarget(e);
            var sources = _dragSources;
            var type = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? FileTransferType.Move : FileTransferType.Copy;
            e.Handled = true;
            e.Pointer.Capture(null);
            ResetPointerState();
            if (target is not null)
            {
                MainViewModel?.ShowTransferDialog(_viewModel.SelectedTab, type, sources, target);
            }

            return;
        }

        ResetPointerState();
    }

    private void ExtendRightMark(PointerEventArgs e)
    {
        var tab = _viewModel!.SelectedTab;
        if (ItemAt(e.GetPosition(FileList)) is not { } item)
        {
            return;
        }

        var index = IndexOf(item);
        if (index < 0 || (index == _rightLastIndex && !_isRightMarking))
        {
            return;
        }

        if (!_isRightMarking)
        {
            _isRightMarking = true;
            tab.SetMark(_rightItem!, _rightMarkState);
        }

        // Fast mouse moves skip rows: mark everything between the last row and this one.
        var step = Math.Sign(index - _rightLastIndex);
        for (var i = _rightLastIndex + step; step != 0 && i != index + step; i += step)
        {
            tab.SetMark(tab.Items[i], _rightMarkState);
        }

        _rightLastIndex = index;
        tab.SelectedItem = item;
    }

    /// <summary>
    /// Drop target under the pointer: a folder row (not one of the dragged items) or the other panel's current folder.
    /// </summary>
    private (FilePanelView? View, string? TargetPath) FindDropTarget(PointerEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel
            || topLevel.InputHitTest(e.GetPosition(topLevel)) is not Visual hit
            || hit.FindAncestorOfType<FilePanelView>(includeSelf: true) is not { _viewModel: { } panel } view)
        {
            return (null, null);
        }

        if (hit.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is FileItemViewModel { IsDirectory: true, IsParentLink: false } folder
            && !_dragSources.Contains(folder.FullPath))
        {
            return (view, folder.FullPath);
        }

        return ReferenceEquals(view, this) ? (null, null) : (view, panel.SelectedTab.CurrentPath);
    }

    private void SetDropTarget(FilePanelView? view)
    {
        if (ReferenceEquals(_dropTarget, view))
        {
            return;
        }

        _dropTarget?.PanelFrame.Classes.Set("dropTarget", false);
        _dropTarget = view;
        _dropTarget?.PanelFrame.Classes.Set("dropTarget", true);
    }

    private void SetCursor(StandardCursorType? type)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.Cursor = type is { } cursor ? new Cursor(cursor) : null;
        }
    }

    private void ResetPointerState()
    {
        _rightItem = null;
        _isRightMarking = false;
        _rightLastIndex = -1;
        _dragItem = null;
        _dragSources = [];
        if (_isDragging)
        {
            _isDragging = false;
            SetDropTarget(null);
            SetCursor(null);
        }
    }

    private void ShowContextMenu(FileItemViewModel item)
    {
        if (_viewModel is null || MainViewModel is not { } main)
        {
            return;
        }

        var panel = _viewModel;
        var tab = panel.SelectedTab;
        var paths = tab.GetContextPaths(item);
        if (paths.Count == 0)
        {
            return;
        }

        var single = paths.Count == 1;
        var items = new List<Control>
        {
            MenuEntry("Open", "Enter", () => tab.OpenSelectedCommand.Execute(null), single && ReferenceEquals(tab.SelectedItem, item)),
            new Separator(),
            MenuEntry(single ? "Copy…" : $"Copy {paths.Count} items…", "F5",
                () => main.ShowTransferDialog(tab, FileTransferType.Copy, paths, main.GetOppositePath(panel))),
            MenuEntry(single ? "Move…" : $"Move {paths.Count} items…", "F6",
                () => main.ShowTransferDialog(tab, FileTransferType.Move, paths, main.GetOppositePath(panel))),
            MenuEntry("Rename…", "Shift+F6", () => main.ShowRenameDialog(tab, paths[0]), single),
            MenuEntry(single ? "Delete" : $"Delete {paths.Count} items", "F8", () => main.ShowDeleteDialog(tab, paths)),
            new Separator(),
            MenuEntry("Copy full path", null, () => _ = CopyPathsAsync(paths)),
            MenuEntry(OperatingSystem.IsWindows() ? "Show in Explorer" : "Show in file manager", null,
                () => RunShellAction(tab, () => ShellIntegration.RevealInFileManager(paths[0])), single),
        };
        if (ShellIntegration.CanShowProperties)
        {
            items.Add(MenuEntry("Properties", "Alt+Enter", () => RunShellAction(tab, () => ShellIntegration.ShowProperties(paths[0])), single));
        }

        var menu = new ContextMenu { ItemsSource = items };
        menu.Closed += (_, _) => FocusSelectedItem();
        menu.Open(FileList);
    }

    private static MenuItem MenuEntry(string header, string? gesture, Action action, bool isEnabled = true) => new()
    {
        Header = header,
        InputGesture = gesture is null ? null : KeyGesture.Parse(gesture),
        Command = new RelayCommand(action),
        IsEnabled = isEnabled,
    };

    private static void RunShellAction(TabViewModel tab, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            tab.ErrorMessage = ex.Message;
        }
    }

    private async Task CopyPathsAsync(IReadOnlyList<string> paths)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, paths));
        }
    }

    private FileItemViewModel? ItemAt(Point position) =>
        (FileList.InputHitTest(position) as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as FileItemViewModel;

    private int IndexOf(FileItemViewModel item)
    {
        var items = _viewModel?.SelectedTab.Items ?? [];
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
