using System.Diagnostics;
using CommanderTom.Core.Models;
using CommanderTom.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommanderTom.UI.ViewModels;

public sealed partial class FilePanelViewModel(IDirectoryService directoryService) : ViewModelBase
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private CancellationTokenSource? _loadCts;

    /// <summary>Raised when the view wants this panel to become the active one (e.g. on click/focus).</summary>
    public event EventHandler? ActivationRequested;

    [ObservableProperty]
    public partial string CurrentPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<FileItemViewModel> Items { get; private set; } = [];

    [ObservableProperty]
    public partial FileItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public void RequestActivation() => ActivationRequested?.Invoke(this, EventArgs.Empty);

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
            var listing = await directoryService.GetDirectoryListingAsync(path, cts.Token);
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
            await LoadAsync(item.FullPath);
        }
        else
        {
            OpenWithShell(item.FullPath);
        }
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (Directory.GetParent(CurrentPath) is { } parent)
        {
            await LoadAsync(parent.FullName, pathToSelect: CurrentPath);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() =>
        LoadAsync(CurrentPath, pathToSelect: SelectedItem?.FullPath);

    private void ApplyListing(DirectoryListing listing, string? pathToSelect)
    {
        var items = new FileItemViewModel[listing.Entries.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new FileItemViewModel(listing.Entries[i]);
        }

        ErrorMessage = null;
        CurrentPath = listing.Path;
        Items = items;
        SelectedItem = FindItem(items, pathToSelect) ?? items.FirstOrDefault();
        StatusText = $"{listing.DirectoryCount:N0} folder(s), {listing.FileCount:N0} file(s), {FormatSize(listing.TotalFileSize)}";
    }

    private static FileItemViewModel? FindItem(FileItemViewModel[] items, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var normalized = Path.TrimEndingDirectorySeparator(path);
        return Array.Find(items, i => !i.IsParentLink && PathComparer.Equals(i.FullPath, normalized));
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
