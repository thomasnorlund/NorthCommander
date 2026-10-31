using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NorthCommander.Core.Rename;

namespace NorthCommander.UI.ViewModels;

/// <summary>Ctrl+M multi-rename tool: masks, counter, search &amp; replace, live preview and sequential execution.</summary>
public sealed partial class MultiRenameViewModel : ViewModelBase
{
    private Action<IReadOnlyList<string>>? _onClosed;
    private IReadOnlyList<RenamePreview> _plan = [];

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    public ObservableCollection<MultiRenameItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string NameMask { get; set; } = "[N]";

    [ObservableProperty]
    public partial string ExtensionMask { get; set; } = "[E]";

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReplaceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial decimal? CounterStart { get; set; } = 1;

    [ObservableProperty]
    public partial decimal? CounterStep { get; set; } = 1;

    [ObservableProperty]
    public partial decimal? CounterDigits { get; set; } = 1;

    [ObservableProperty]
    public partial string? MaskError { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CloseCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsFinished { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool HasRenamableItems { get; private set; }

    /// <summary>Masks can be edited until the rename has run.</summary>
    public bool IsEditable => !IsRunning && !IsFinished;

    /// <param name="onClosed">Receives the folders whose contents changed, so the panels can refresh.</param>
    public void Open(IReadOnlyList<string> paths, Action<IReadOnlyList<string>> onClosed)
    {
        Items.Clear();
        foreach (var path in paths)
        {
            Items.Add(new MultiRenameItemViewModel(path));
        }

        _onClosed = onClosed;
        IsFinished = false;
        IsOpen = true;
        UpdatePreview();
    }

    [RelayCommand]
    private void InsertToken(string token)
    {
        if (IsEditable)
        {
            NameMask += token;
        }
    }

    [RelayCommand]
    private void ResetMasks()
    {
        NameMask = "[N]";
        ExtensionMask = "[E]";
        SearchText = string.Empty;
        ReplaceText = string.Empty;
        CounterStart = CounterStep = CounterDigits = 1;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        UpdatePreview();
        if (!CanStart())
        {
            return;
        }

        IsRunning = true;
        var progress = new Progress<RenameOutcome>(ApplyOutcome);
        var outcomes = await MultiRename.ExecuteAsync(_plan, progress);
        foreach (var outcome in outcomes)
        {
            ApplyOutcome(outcome);
        }

        var renamed = outcomes.Count(o => o.Error is null && !_plan[o.Index].IsUnchanged);
        var failed = outcomes.Count(o => o.Error is not null);
        Summary = failed == 0 ? $"Renamed {renamed} item(s)." : $"Renamed {renamed} item(s), {failed} failed - see Status column.";
        IsRunning = false;
        IsFinished = true;
    }

    private bool CanStart() => !IsRunning && !IsFinished && HasRenamableItems;

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        IsOpen = false;
        var folders = IsFinished ? Items.Select(i => i.Folder).Distinct(StringComparer.Ordinal).ToList() : [];
        var onClosed = _onClosed;
        _onClosed = null;
        Items.Clear();
        onClosed?.Invoke(folders);
    }

    private bool CanClose() => !IsRunning;

    partial void OnNameMaskChanged(string value) => UpdatePreview();

    partial void OnExtensionMaskChanged(string value) => UpdatePreview();

    partial void OnSearchTextChanged(string value) => UpdatePreview();

    partial void OnReplaceTextChanged(string value) => UpdatePreview();

    partial void OnCounterStartChanged(decimal? value) => UpdatePreview();

    partial void OnCounterStepChanged(decimal? value) => UpdatePreview();

    partial void OnCounterDigitsChanged(decimal? value) => UpdatePreview();

    private void UpdatePreview()
    {
        if (!IsOpen || IsRunning || IsFinished)
        {
            return;
        }

        var options = new MultiRenameOptions
        {
            NameMask = NameMask,
            ExtensionMask = ExtensionMask,
            Search = SearchText,
            Replace = ReplaceText,
            CounterStart = (long)(CounterStart ?? 1),
            CounterStep = (long)(CounterStep ?? 1),
            CounterDigits = (int)(CounterDigits ?? 1),
        };

        _plan = MultiRename.Preview(Items.Select(i => i.SourcePath).ToList(), options);
        int changed = 0, errors = 0;
        string? maskError = null;
        for (var i = 0; i < _plan.Count; i++)
        {
            var preview = _plan[i];
            var item = Items[i];
            item.NewName = preview.NewName;
            item.HasError = preview.Error is not null;
            item.IsDone = false;
            item.Status = preview.Error ?? (preview.IsUnchanged ? "unchanged" : string.Empty);
            if (preview.Error is not null)
            {
                errors++;
                if (preview.NewName.Length == 0 && preview.Error.Contains('['))
                {
                    maskError ??= preview.Error;
                }
            }
            else if (!preview.IsUnchanged)
            {
                changed++;
            }
        }

        MaskError = maskError;
        HasRenamableItems = errors == 0 && changed > 0;
        Summary = $"{Items.Count} item(s), {changed} to rename" + (errors > 0 ? $", {errors} with errors - fix them to start." : ".");
    }

    private void ApplyOutcome(RenameOutcome outcome)
    {
        if (outcome.Index >= Items.Count)
        {
            return;
        }

        var item = Items[outcome.Index];
        if (outcome.Error is not null)
        {
            item.HasError = true;
            item.Status = outcome.Error;
        }
        else if (!_plan[outcome.Index].IsUnchanged)
        {
            item.IsDone = true;
            item.Status = "Renamed";
        }
    }
}
