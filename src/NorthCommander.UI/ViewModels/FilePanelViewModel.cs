using NorthCommander.Core.Archives;
using NorthCommander.Core.Settings;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using NorthCommander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NorthCommander.UI.ViewModels;

/// <summary>Left or right panel: a set of folder tabs (always at least one) plus focus state.</summary>
public sealed partial class FilePanelViewModel : ViewModelBase
{
    private readonly IDirectoryService _directoryService;
    private readonly Dictionary<string, string> _lastPathByDrive = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _syncingDrive;
    private bool _refreshingDrives;

    public FilePanelViewModel(IDirectoryService directoryService, TransferQueueViewModel transfers)
    {
        _directoryService = directoryService;
        Transfers = transfers;
        Transfers.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TransferQueueViewModel.HasActiveTransfers))
            {
                OnPropertyChanged(nameof(ShowTransferStatus));
            }
        };
        Tabs.CollectionChanged += (_, _) =>
        {
            foreach (var tab in Tabs)
            {
                tab.CanClose = Tabs.Count > 1;
            }
        };

        SelectedTab = CreateTab(0, string.Empty);
        _ = RefreshDrivesAsync();
    }

    /// <summary>Raised when the view wants this panel to become the active one (e.g. on click/focus).</summary>
    public event EventHandler? ActivationRequested;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial TabViewModel SelectedTab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTransferStatus))]
    public partial bool IsActive { get; set; }

    public TransferQueueViewModel Transfers { get; }

    /// <summary>Drive dropdown entries, re-read in the background when it opens and after navigating.</summary>
    public ObservableCollection<DriveEntry> Drives { get; } = [];

    [ObservableProperty]
    public partial DriveEntry? SelectedDrive { get; set; }

    /// <summary>"[label]  12.3 GB of 476 GB free" next to the drive dropdown.</summary>
    [ObservableProperty]
    public partial string DriveInfoText { get; private set; } = string.Empty;

    /// <summary>Re-reads drives off the UI thread (a disconnected network drive can take seconds).</summary>
    public async Task RefreshDrivesAsync()
    {
        if (_refreshingDrives)
        {
            return;
        }

        _refreshingDrives = true;
        try
        {
            var drives = await Task.Run(() => DriveService.GetDrives());
            _syncingDrive = true;
            try
            {
                for (var i = 0; i < drives.Count; i++)
                {
                    if (i >= Drives.Count)
                    {
                        Drives.Add(drives[i]);
                    }
                    else if (Drives[i] != drives[i])
                    {
                        Drives[i] = drives[i];
                    }
                }

                while (Drives.Count > drives.Count)
                {
                    Drives.RemoveAt(Drives.Count - 1);
                }
            }
            finally
            {
                _syncingDrive = false;
            }

            UpdateCurrentDrive();
        }
        finally
        {
            _refreshingDrives = false;
        }
    }

    /// <summary>Switches the current tab to <paramref name="drive"/>: the folder last used there in this panel, else its root.</summary>
    public async Task OpenDriveAsync(DriveEntry drive)
    {
        if (!drive.IsReady)
        {
            SelectedTab.ErrorMessage = $"Drive {drive.Name} is not ready.";
            UpdateCurrentDrive();
            return;
        }

        var target = drive.RootPath;
        if (drive.Kind != DriveKind.Documents
            && _lastPathByDrive.TryGetValue(drive.RootPath, out var last)
            && (Directory.Exists(last) || ArchiveService.IsArchivePath(last)))
        {
            target = last;
        }

        await SelectedTab.NavigateAsync(target);
        UpdateCurrentDrive();
    }

    /// <summary>The "\" button: root of the current drive.</summary>
    [RelayCommand]
    public Task GoToRoot()
    {
        var path = SelectedTab.CurrentPath;
        var root = DriveService.FindDrive(Drives, path)?.RootPath ?? Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? Task.CompletedTask : SelectedTab.NavigateAsync(root);
    }

    partial void OnSelectedDriveChanged(DriveEntry? value)
    {
        if (!_syncingDrive && value is not null)
        {
            _ = OpenDriveAsync(value);
        }
    }

    private void UpdateCurrentDrive()
    {
        foreach (var tab in Tabs)
        {
            tab.DriveIcon = DriveService.FindDrive(Drives, tab.CurrentPath)?.Kind;
        }

        var path = SelectedTab.CurrentPath;
        var drive = DriveService.FindDrive(Drives, path);
        if (drive is not null)
        {
            _lastPathByDrive[drive.RootPath] = path;
        }

        _syncingDrive = true;
        try
        {
            SelectedDrive = drive;
        }
        finally
        {
            _syncingDrive = false;
        }

        DriveInfoText = drive switch
        {
            null => string.Empty,
            { IsReady: false } => $"[{drive.Label}]  not ready",
            _ => $"[{drive.Label}]  {TabViewModel.FormatSize(drive.FreeBytes)} of {TabViewModel.FormatSize(drive.TotalBytes)} free",
        };
    }

    private void OnSelectedTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.CurrentPath))
        {
            UpdateCurrentDrive();
            Dispatcher.UIThread.Post(() => _ = RefreshDrivesAsync());
        }
    }

    /// <summary>Transfer progress replaces the listing summary in the focused panel's status bar.</summary>
    public bool ShowTransferStatus => IsActive && Transfers.HasActiveTransfers;

    public void RequestActivation() => ActivationRequested?.Invoke(this, EventArgs.Empty);

    public Task LoadAsync(string path) => SelectedTab.LoadAsync(path);

    public PanelSettings CaptureSettings() => new()
    {
        Tabs = Tabs.Select(t => new TabSettings { Path = t.CurrentPath, IsLocked = t.IsLocked }).ToList(),
        SelectedIndex = Tabs.IndexOf(SelectedTab),
    };

    /// <summary>Reopens saved tabs; folders that no longer exist fall back to <paramref name="fallbackPath"/>.</summary>
    public async Task RestoreAsync(PanelSettings? settings, string fallbackPath)
    {
        var saved = settings?.Tabs.Where(t => !string.IsNullOrWhiteSpace(t.Path)).ToList() ?? [];
        if (saved.Count == 0)
        {
            await LoadAsync(fallbackPath);
            return;
        }

        var tabs = new List<TabViewModel> { SelectedTab };
        for (var i = 1; i < saved.Count; i++)
        {
            tabs.Add(CreateTab(Tabs.Count, string.Empty));
        }

        await Task.WhenAll(tabs.Select((tab, i) =>
            tab.LoadAsync(Directory.Exists(saved[i].Path) || ArchiveService.IsArchivePath(saved[i].Path) ? saved[i].Path : fallbackPath)));

        // Lock only after loading: a locked tab refuses to change its folder.
        for (var i = 0; i < tabs.Count; i++)
        {
            tabs[i].IsLocked = saved[i].IsLocked;
        }

        SelectedTab = tabs[Math.Clamp(settings!.SelectedIndex, 0, tabs.Count - 1)];
    }

    /// <summary>Ctrl+T: new tab next to the current one, showing the same folder.</summary>
    [RelayCommand]
    public void NewTab() => OpenTab(SelectedTab.CurrentPath, SelectedTab.SelectedItem?.FullPath);

    public void OpenTab(string path, string? pathToSelect = null)
    {
        var tab = CreateTab(Tabs.IndexOf(SelectedTab) + 1, path);
        SelectedTab = tab;
        _ = tab.LoadAsync(path, pathToSelect);
    }

    /// <summary>Ctrl+W: closes <paramref name="tab"/> (default: the current one) unless it is the last tab.</summary>
    [RelayCommand]
    public void CloseTab(TabViewModel? tab = null)
    {
        tab ??= SelectedTab;
        var index = Tabs.IndexOf(tab);
        if (Tabs.Count <= 1 || index < 0)
        {
            return;
        }

        if (ReferenceEquals(tab, SelectedTab))
        {
            SelectedTab = Tabs[index == Tabs.Count - 1 ? index - 1 : index + 1];
        }

        Tabs.RemoveAt(index);
        tab.CloseRequested -= OnTabCloseRequested;
        tab.OpenInNewTabRequested -= OnOpenInNewTabRequested;
    }

    [RelayCommand]
    public void NextTab() => CycleTab(1);

    [RelayCommand]
    public void PreviousTab() => CycleTab(-1);

    partial void OnSelectedTabChanged(TabViewModel oldValue, TabViewModel newValue)
    {
        // oldValue is null on the first assignment from the constructor.
        oldValue?.IsSelected = false;
        oldValue?.PropertyChanged -= OnSelectedTabPropertyChanged;
        newValue.IsSelected = true;
        newValue.PropertyChanged += OnSelectedTabPropertyChanged;
        UpdateCurrentDrive();
    }

    private void CycleTab(int step)
    {
        var index = Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(index + step + Tabs.Count) % Tabs.Count];
    }

    private TabViewModel CreateTab(int index, string initialPath)
    {
        var tab = new TabViewModel(_directoryService, initialPath);
        tab.CloseRequested += OnTabCloseRequested;
        tab.OpenInNewTabRequested += OnOpenInNewTabRequested;
        Tabs.Insert(index, tab);
        return tab;
    }

    private void OnTabCloseRequested(object? sender, EventArgs e)
    {
        if (sender is TabViewModel tab)
        {
            CloseTab(tab);
        }
    }

    private void OnOpenInNewTabRequested(object? sender, (string Path, string? PathToSelect) e) => OpenTab(e.Path, e.PathToSelect);
}
