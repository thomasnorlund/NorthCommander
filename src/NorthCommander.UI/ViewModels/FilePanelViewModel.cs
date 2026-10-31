using NorthCommander.Core.Archives;
using NorthCommander.Core.Settings;
using System.Collections.ObjectModel;
using NorthCommander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NorthCommander.UI.ViewModels;

/// <summary>Left or right panel: a set of folder tabs (always at least one) plus focus state.</summary>
public sealed partial class FilePanelViewModel : ViewModelBase
{
    private readonly IDirectoryService _directoryService;

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
        newValue.IsSelected = true;
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
