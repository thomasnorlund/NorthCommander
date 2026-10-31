using System.Diagnostics;
using NorthCommander.Core.Archives;
using NorthCommander.Core.Models;
using NorthCommander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NorthCommander.UI.ViewModels;

/// <summary>One folder tab of a file panel: directory state, cursor, marks and navigation.</summary>
public sealed partial class TabViewModel : ViewModelBase
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IDirectoryService _directoryService;
    private CancellationTokenSource? _loadCts;
    private DirectoryListing? _listing;
    private readonly Queue<string> _sizeQueue = new();
    private CancellationTokenSource? _sizeCts;

    /// <param name="initialPath">Shown on the tab header right away, before the first listing arrives.</param>
    public TabViewModel(IDirectoryService directoryService, string initialPath = "")
    {
        _directoryService = directoryService;
        CurrentPath = initialPath;
    }

    /// <summary>A locked tab never leaves its folder: navigating elsewhere asks the panel to open a new tab.</summary>
    public event EventHandler<(string Path, string? PathToSelect)>? OpenInNewTabRequested;

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial bool IsLocked { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>True while the tab shows a folder inside an archive.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayPath))]
    public partial bool IsInArchive { get; private set; }

    public string DisplayPath => IsInArchive && ArchiveService.TryParse(CurrentPath, out var archive, out _)
        ? $"[{ArchiveService.GetArchiveTag(archive)}] {CurrentPath}"
        : CurrentPath;

    /// <summary>False for the last tab of a panel.</summary>
    [ObservableProperty]
    public partial bool CanClose { get; set; }

    /// <summary>Folder name shown on the tab header; locked tabs get a '*' prefix like in Total Commander.</summary>
    public string Header
    {
        get
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(CurrentPath));
            if (string.IsNullOrEmpty(name))
            {
                name = CurrentPath;
            }

            return IsLocked ? $"*{name}" : name;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header), nameof(DisplayPath))]
    public partial string CurrentPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<FileItemViewModel> Items { get; private set; } = [];

    [ObservableProperty]
    public partial FileItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsCalculatingSizes { get; private set; }

    /// <summary>
    /// Full paths of the items an operation (F5/F6, ...) should act on: all marked items,
    /// or the item under the cursor when nothing is marked.
    /// </summary>
    public IReadOnlyList<string> GetSelectedPaths()
    {
        var marked = Items.Where(i => i.IsMarked).Select(i => i.FullPath).ToList();
        if (marked.Count > 0)
        {
            return marked;
        }

        return SelectedItem is { IsParentLink: false } item ? [item.FullPath] : [];
    }

    /// <summary>Insert/Space: toggles the mark of the cursor item and moves the cursor down.</summary>
    [RelayCommand]
    private void ToggleMark()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        if (!item.IsParentLink)
        {
            item.IsMarked = !item.IsMarked;
            UpdateStatusText();
        }

        var index = IndexOf(item);
        if (index >= 0 && index < Items.Count - 1)
        {
            SelectedItem = Items[index + 1];
        }
    }

    /// <summary>Space: like Insert, but a folder that becomes marked also gets its size calculated.</summary>
    public void ToggleMarkAndCalculateSize()
    {
        var item = SelectedItem;
        ToggleMark();
        if (item is { IsMarked: true, IsDirectory: true })
        {
            CalculateFolderSizes([item]);
        }
    }

    /// <summary>Queues folders for background size calculation (F3 on a folder, Space, Alt+Shift+Enter).</summary>
    public void CalculateFolderSizes(IEnumerable<FileItemViewModel> items)
    {
        foreach (var item in items)
        {
            if (item is { IsDirectory: true, IsParentLink: false, CalculatedSize: null } && !_sizeQueue.Contains(item.FullPath))
            {
                _sizeQueue.Enqueue(item.FullPath);
            }
        }

        if (_sizeCts is null && _sizeQueue.Count > 0)
        {
            _ = RunSizeQueueAsync();
        }
    }

    /// <summary>Esc: stops the size calculation. False when nothing was running.</summary>
    public bool CancelSizeCalculation()
    {
        _sizeQueue.Clear();
        if (_sizeCts is null)
        {
            return false;
        }

        _sizeCts.Cancel();
        return true;
    }

    private async Task RunSizeQueueAsync()
    {
        using var cts = new CancellationTokenSource();
        _sizeCts = cts;
        IsCalculatingSizes = true;
        UpdateStatusText();
        try
        {
            // Runs on the UI thread between awaits, so the queue needs no locking.
            while (_sizeQueue.TryDequeue(out var path))
            {
                try
                {
                    var size = await FolderSizeCalculator.CalculateAsync(path, cts.Token);
                    if (FindItem(Items, path) is { } item)
                    {
                        item.CalculatedSize = size.Bytes;
                        UpdateStatusText();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Vanished or unreadable folder: leave it as <DIR>.
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            _sizeCts = null;
            IsCalculatingSizes = false;
            UpdateStatusText();
        }
    }

    /// <summary>
    /// Items a mouse action on <paramref name="item"/> applies to: all marked items when it is marked
    /// (Total Commander behavior), otherwise just the item itself.
    /// </summary>
    public IReadOnlyList<string> GetContextPaths(FileItemViewModel item)
    {
        if (item.IsParentLink)
        {
            return [];
        }

        return item.IsMarked ? Items.Where(i => i.IsMarked).Select(i => i.FullPath).ToList() : [item.FullPath];
    }

    public void SetMark(FileItemViewModel item, bool isMarked)
    {
        if (item.IsParentLink || item.IsMarked == isMarked)
        {
            return;
        }

        item.IsMarked = isMarked;
        UpdateStatusText();
    }

    public void ClearMarks()
    {
        foreach (var item in Items)
        {
            item.IsMarked = false;
        }

        UpdateStatusText();
    }

    public bool IsShowingDirectory(string path) =>
        !string.IsNullOrEmpty(CurrentPath) &&
        PathComparer.Equals(Path.TrimEndingDirectorySeparator(CurrentPath), Path.TrimEndingDirectorySeparator(path));

    /// <summary>
    /// Loads <paramref name="path"/> without blocking the UI thread. A newer call cancels an
    /// in-flight one. On failure the previous listing stays visible and <see cref="ErrorMessage"/> is set.
    /// </summary>
    public async Task LoadAsync(string path, string? pathToSelect = null)
    {
        if (_loadCts is not null)
        {
            await _loadCts.CancelAsync();
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;

        try
        {
            var listing = await _directoryService.GetDirectoryListingAsync(path, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            ApplyListing(listing, pathToSelect);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                _loadCts = null;
            }

            cts.Dispose();
        }
    }

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        if (item.IsParentLink)
        {
            await GoUpAsync();
        }
        else if (item.IsDirectory)
        {
            await NavigateAsync(item.FullPath);
        }
        else if (IsInArchive)
        {
            await OpenArchivedFileAsync(item.FullPath);
        }
        else if (item.IsArchive)
        {
            await NavigateAsync(item.FullPath);
        }
        else
        {
            OpenWithShell(item.FullPath);
        }
    }

    private async Task OpenArchivedFileAsync(string path)
    {
        try
        {
            OpenWithShell(await ArchiveService.ExtractToTempAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Cannot extract '{Path.GetFileName(path)}': {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (Directory.GetParent(CurrentPath) is { } parent)
        {
            await NavigateAsync(parent.FullName, pathToSelect: CurrentPath);
        }
    }

    [RelayCommand]
    private void Lock() => IsLocked = true;

    [RelayCommand]
    private void Unlock() => IsLocked = false;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private Task NavigateAsync(string path, string? pathToSelect = null)
    {
        if (IsLocked && !IsShowingDirectory(path))
        {
            OpenInNewTabRequested?.Invoke(this, (path, pathToSelect));
            return Task.CompletedTask;
        }

        return LoadAsync(path, pathToSelect);
    }

    [RelayCommand]
    public Task RefreshAsync() =>
        LoadAsync(CurrentPath, pathToSelect: SelectedItem?.FullPath);

    private void ApplyListing(DirectoryListing listing, string? pathToSelect)
    {
        // Refreshing the same folder keeps the marks and calculated sizes of items that still exist.
        var sameFolder = PathComparer.Equals(listing.Path, CurrentPath);
        var keepMarked = sameFolder ? Items.Where(i => i.IsMarked).Select(i => i.FullPath).ToHashSet(PathComparer) : null;
        var keepSizes = sameFolder
            ? Items.Where(i => i.CalculatedSize is not null).ToDictionary(i => i.FullPath, i => i.CalculatedSize, PathComparer)
            : null;
        if (!sameFolder)
        {
            CancelSizeCalculation();
        }

        var items = new FileItemViewModel[listing.Entries.Count];
        for (var i = 0; i < items.Length; i++)
        {
            var entry = listing.Entries[i];
            items[i] = new FileItemViewModel(entry)
            {
                IsMarked = keepMarked?.Contains(entry.FullPath) == true,
                CalculatedSize = entry.IsDirectory ? keepSizes?.GetValueOrDefault(entry.FullPath) : null,
            };
        }

        ErrorMessage = null;
        IsInArchive = listing.IsArchive;
        CurrentPath = listing.Path;
        Items = items;
        SelectedItem = FindItem(items, pathToSelect) ?? items.FirstOrDefault();
        _listing = listing;
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (_listing is not { } listing)
        {
            return;
        }

        int markedFiles = 0, markedDirectories = 0;
        long markedBytes = 0, totalBytes = listing.TotalFileSize;
        foreach (var item in Items)
        {
            totalBytes += item.CalculatedSize ?? 0;
            if (!item.IsMarked)
            {
                continue;
            }

            if (item.IsDirectory)
            {
                markedDirectories++;
                markedBytes += item.CalculatedSize ?? 0;
            }
            else
            {
                markedFiles++;
                markedBytes += item.Entry.Size ?? 0;
            }
        }

        var calculating = IsCalculatingSizes ? "Calculating folder sizes (Esc cancels)... " : string.Empty;
        StatusText = calculating + (markedFiles + markedDirectories == 0
            ? $"{listing.DirectoryCount:N0} folder(s), {listing.FileCount:N0} file(s), {FormatSize(totalBytes)}"
            : $"Selected {FormatSize(markedBytes)} / {FormatSize(totalBytes)} in {markedFiles:N0} / {listing.FileCount:N0} file(s), "
              + $"{markedDirectories:N0} / {listing.DirectoryCount:N0} folder(s)");
    }

    private int IndexOf(FileItemViewModel item)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (ReferenceEquals(Items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private static FileItemViewModel? FindItem(IReadOnlyList<FileItemViewModel> items, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var normalized = Path.TrimEndingDirectorySeparator(path);
        return items.FirstOrDefault(i => !i.IsParentLink && PathComparer.Equals(i.FullPath, normalized));
    }

    private void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorMessage = ex.Message;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{size:0.#} {units[unit]}";
    }
}
