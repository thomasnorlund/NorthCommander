using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NorthCommander.Core.Sync;

namespace NorthCommander.UI.ViewModels;

/// <summary>Directory synchronization tool: compares two folders (or ZIP folders) and queues the copies that make them equal.</summary>
public sealed partial class SyncDirectoriesViewModel : ViewModelBase
{
    private Func<IReadOnlyList<SyncItem>, string, string, string?>? _synchronize;
    private IReadOnlyList<SyncItem> _results = [];
    private string _comparedLeft = string.Empty;
    private string _comparedRight = string.Empty;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string LeftPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RightPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IncludeSubdirectories { get; set; } = true;

    [ObservableProperty]
    public partial bool UseTimeTolerance { get; set; } = true;

    [ObservableProperty]
    public partial bool HideIdentical { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand), nameof(SynchronizeCommand))]
    public partial bool IsComparing { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SynchronizeCommand))]
    public partial int PendingCopies { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<SyncItemViewModel> Items { get; private set; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <param name="synchronize">Queues the copies for the compared roots; returns an error message or null.</param>
    public void Open(string leftPath, string rightPath, Func<IReadOnlyList<SyncItem>, string, string, string?> synchronize)
    {
        LeftPath = leftPath;
        RightPath = rightPath;
        _synchronize = synchronize;
        _results = [];
        Items = [];
        PendingCopies = 0;
        ErrorMessage = null;
        Summary = "Press Compare to scan both folders.";
        IsOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareAsync()
    {
        IsComparing = true;
        ErrorMessage = null;
        Summary = "Comparing…";
        try
        {
            var options = new SyncOptions { IncludeSubdirectories = IncludeSubdirectories, UseTimeTolerance = UseTimeTolerance };
            _comparedLeft = LeftPath.Trim();
            _comparedRight = RightPath.Trim();
            _results = await DirectoryComparer.CompareAsync(_comparedLeft, _comparedRight, options);
            ApplyFilter();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _results = [];
            ApplyFilter();
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsComparing = false;
        }
    }

    private bool CanCompare() => !IsComparing;

    [RelayCommand(CanExecute = nameof(CanSynchronize))]
    private void Synchronize()
    {
        if (_synchronize?.Invoke(_results, _comparedLeft, _comparedRight) is { } error)
        {
            ErrorMessage = error;
            return;
        }

        Close();
    }

    private bool CanSynchronize() => !IsComparing && PendingCopies > 0;

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        _results = [];
        Items = [];
        PendingCopies = 0;
    }

    partial void OnHideIdenticalChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        Items = _results.Where(r => !HideIdentical || r.Direction != SyncDirection.Equal).Select(r => new SyncItemViewModel(r)).ToList();
        int toRight = 0, toLeft = 0, conflicts = 0, equal = 0;
        foreach (var result in _results)
        {
            switch (result.Direction)
            {
                case SyncDirection.LeftToRight: toRight++; break;
                case SyncDirection.RightToLeft: toLeft++; break;
                case SyncDirection.Conflict: conflicts++; break;
                default: equal++; break;
            }
        }

        PendingCopies = toRight + toLeft;
        Summary = $"{_results.Count:N0} file(s):  {toRight:N0} ->   {toLeft:N0} <-   {conflicts:N0} != (skipped)   {equal:N0} identical";
    }
}
