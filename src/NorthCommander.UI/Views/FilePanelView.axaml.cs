using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NorthCommander.Core.Transfers;
using NorthCommander.UI.Services;
using NorthCommander.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace NorthCommander.UI.Views;

public partial class FilePanelView : UserControl
{
    private const double DragThreshold = 6;

    private FilePanelViewModel? _viewModel;
    private TabViewModel? _tab;

    // Drags started by NorthCommander carry their source tab; drags from other apps only carry files.
    private static readonly DataFormat<PanelDrag> PanelDragFormat = DataFormat.CreateInProcessFormat<PanelDrag>("NorthCommander.PanelDrag");
    private static PanelDrag? s_activeDrag;

    // Left button: drag & drop to the other panel, onto a folder, or to Explorer and other apps.
    private FileItemViewModel? _dragItem;
    private PointerPressedEventArgs? _dragPress;
    private Point _dragStart;

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

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHighlight(false));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private sealed record PanelDrag(TabViewModel SourceTab, IReadOnlyList<string> Sources);

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
            _dragPress = e;
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

        var tab = _viewModel.SelectedTab;
        var sources = tab.GetContextPaths(_dragItem);
        var press = _dragPress;
        ResetPointerState();
        if (sources.Count > 0 && press is not null)
        {
            _ = StartDragAsync(press, tab, sources);
        }
    }

    private async Task StartDragAsync(PointerPressedEventArgs press, TabViewModel tab, IReadOnlyList<string> sources)
    {
        var drag = new PanelDrag(tab, sources);
        var first = new DataTransferItem();
        first.Set(PanelDragFormat, drag);
        var data = new DataTransfer();
        data.Add(first);

        // Real files also go out as platform file items (CF_HDROP on Windows), so Explorer and other apps accept them.
        // Entries inside an archive have no file on disk; they can only be dropped onto NorthCommander panels.
        if (!tab.IsInArchive && TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
        {
            var hasFile = false;
            foreach (var path in sources)
            {
                IStorageItem? file = Directory.Exists(path)
                    ? await storage.TryGetFolderFromPathAsync(path)
                    : await storage.TryGetFileFromPathAsync(path);
                if (file is null)
                {
                    continue;
                }

                if (hasFile)
                {
                    data.Add(DataTransferItem.CreateFile(file));
                }
                else
                {
                    first.SetFile(file);
                    hasFile = true;
                }
            }
        }

        s_activeDrag = drag;
        try
        {
            var effect = await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy | DragDropEffects.Move);

            // Our own panels always report Copy; Move means another app (e.g. Explorer) moved the files away.
            if (effect.HasFlag(DragDropEffects.Move))
            {
                await tab.RefreshAsync();
            }
        }
        finally
        {
            s_activeDrag = null;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var drop = ResolveDrop(e);
        SetDropHighlight(drop is not null);
        e.DragEffects = drop is null
            ? DragDropEffects.None
            : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        e.Handled = true;
        if (ResolveDrop(e) is not { } drop || MainViewModel is not { } main)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var type = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? FileTransferType.Move : FileTransferType.Copy;

        // Always report Copy: a Move result would let the source app (e.g. Explorer) delete the originals
        // before NorthCommander's queued, confirmable move has run. The queue does the move itself.
        e.DragEffects = DragDropEffects.Copy;
        main.ShowTransferDialog(drop.SourceTab, type, drop.Sources, drop.Target);
        if (_viewModel is { } panel)
        {
            main.ActivePanel = panel;
        }

        Dispatcher.UIThread.Post(() => (TopLevel.GetTopLevel(this) as Window)?.Activate());
    }

    /// <summary>
    /// Sources and target folder of a drop on this panel: a folder row under the pointer (not one of the dragged items),
    /// otherwise the panel's current folder. Null when the drop makes no sense (no files, or into the folder they come from).
    /// </summary>
    private (TabViewModel? SourceTab, IReadOnlyList<string> Sources, string Target)? ResolveDrop(DragEventArgs e)
    {
        if (_viewModel is null)
        {
            return null;
        }

        var drag = e.DataTransfer.TryGetValue(PanelDragFormat) ?? s_activeDrag;
        var sources = drag?.Sources
            ?? e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList()
            ?? [];
        if (sources.Count == 0)
        {
            return null;
        }

        var tab = _viewModel.SelectedTab;
        var target = ItemAt(e.GetPosition(FileList)) is { IsDirectory: true, IsParentLink: false } folder && !sources.Contains(folder.FullPath)
            ? folder.FullPath
            : tab.CurrentPath;
        if (sources.All(s => PathsEqual(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(s)), target)))
        {
            return null;
        }

        return (drag?.SourceTab, sources, target);
    }

    private static bool PathsEqual(string? first, string second) =>
        first is not null
        && string.Equals(
            Path.TrimEndingDirectorySeparator(first),
            Path.TrimEndingDirectorySeparator(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void SetDropHighlight(bool isTarget) => PanelFrame.Classes.Set("dropTarget", isTarget);

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

    private void ResetPointerState()
    {
        _rightItem = null;
        _isRightMarking = false;
        _rightLastIndex = -1;
        _dragItem = null;
        _dragPress = null;
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
