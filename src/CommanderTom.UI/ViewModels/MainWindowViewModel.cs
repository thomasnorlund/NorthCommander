using CommanderTom.Core.Transfers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommanderTom.UI.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(
        FilePanelViewModel leftPanel,
        FilePanelViewModel rightPanel,
        TransferQueueViewModel transfers,
        ConflictDialogViewModel conflictDialog)
    {
        LeftPanel = leftPanel;
        RightPanel = rightPanel;
        Transfers = transfers;
        ConflictDialog = conflictDialog;

        TransferDialog.PropertyChanged += OnDialogPropertyChanged;
        ConflictDialog.PropertyChanged += OnDialogPropertyChanged;

        LeftPanel.ActivationRequested += OnPanelActivationRequested;
        RightPanel.ActivationRequested += OnPanelActivationRequested;
        Transfers.TransferFinished += OnTransferFinished;

        ActivePanel = LeftPanel;
    }

    public FilePanelViewModel LeftPanel { get; }

    public FilePanelViewModel RightPanel { get; }

    public TransferQueueViewModel Transfers { get; }

    public TransferDialogViewModel TransferDialog { get; } = new();

    public ConflictDialogViewModel ConflictDialog { get; }

    /// <summary>True while an overlay dialog owns the keyboard; the panels are disabled.</summary>
    public bool IsModalOpen => TransferDialog.IsOpen || ConflictDialog.IsOpen;

    [ObservableProperty]
    public partial FilePanelViewModel ActivePanel { get; set; }

    public FilePanelViewModel InactivePanel => ReferenceEquals(ActivePanel, LeftPanel) ? RightPanel : LeftPanel;

    public Task InitializeAsync() => Task.WhenAll(
        LeftPanel.LoadAsync(GetDefaultLeftPath()),
        RightPanel.LoadAsync(GetDefaultRightPath()));

    [RelayCommand]
    private void SwitchActivePanel() => ActivePanel = InactivePanel;

    [RelayCommand]
    private void Copy() => OpenTransferDialog(FileTransferType.Copy);

    [RelayCommand]
    private void Move() => OpenTransferDialog(FileTransferType.Move);

    [RelayCommand]
    private void NewTab() => WhenNoModal(ActivePanel.NewTab);

    [RelayCommand]
    private void CloseTab() => WhenNoModal(() => ActivePanel.CloseTab());

    [RelayCommand]
    private void NextTab() => WhenNoModal(ActivePanel.NextTab);

    [RelayCommand]
    private void PreviousTab() => WhenNoModal(ActivePanel.PreviousTab);

    private void WhenNoModal(Action action)
    {
        if (!IsModalOpen)
        {
            action();
        }
    }

    private void OpenTransferDialog(FileTransferType type)
    {
        if (IsModalOpen)
        {
            return;
        }

        var tab = ActivePanel.SelectedTab;
        var sources = tab.GetSelectedPaths();
        if (sources.Count == 0)
        {
            return;
        }

        TransferDialog.Open(type, sources, InactivePanel.SelectedTab.CurrentPath, target => EnqueueTransfers(tab, type, sources, target));
    }

    private string? EnqueueTransfers(TabViewModel panel, FileTransferType type, IReadOnlyList<string> sources, string target)
    {
        try
        {
            var plan = new List<(string Source, string Destination)>(sources.Count);
            foreach (var source in sources)
            {
                var destination = FileTransferPaths.ResolveDestination(source, target, sources.Count > 1);
                if (FileTransferPaths.Validate(source, destination) is { } error)
                {
                    return error;
                }

                plan.Add((source, destination));
            }

            // One batch per confirmation: "Overwrite All" / "Skip All" cover every item of this F5/F6.
            var batch = new FileTransferBatch();
            foreach (var (source, destination) in plan)
            {
                Transfers.Enqueue(new FileTransferTask(type, source, destination, batch));
            }

            panel.ClearMarks();
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    private void OnDialogPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TransferDialogViewModel.IsOpen) or nameof(ConflictDialogViewModel.IsOpen))
        {
            OnPropertyChanged(nameof(IsModalOpen));
        }
    }

    private async void OnTransferFinished(object? sender, FileTransferTask task)
    {
        var affected = new List<string>(2);
        if (Path.GetDirectoryName(task.DestinationPath) is { } destinationDir)
        {
            affected.Add(destinationDir);
        }

        if (task.TaskType == FileTransferType.Move && Path.GetDirectoryName(task.SourcePath) is { } sourceDir)
        {
            affected.Add(sourceDir);
        }

        var refreshes = LeftPanel.Tabs.Concat(RightPanel.Tabs)
            .Where(tab => affected.Exists(tab.IsShowingDirectory))
            .Select(tab => tab.RefreshAsync());
        await Task.WhenAll(refreshes);

        if (task.Status == FileTransferStatus.Failed)
        {
            var verb = task.TaskType == FileTransferType.Move ? "Move" : "Copy";
            ActivePanel.SelectedTab.ErrorMessage = $"{verb} of '{task.DisplayName}' failed: {task.ErrorMessage}";
        }
    }

    partial void OnActivePanelChanged(FilePanelViewModel oldValue, FilePanelViewModel newValue)
    {
        // oldValue is null on the first assignment from the constructor.
        oldValue?.IsActive = false;
        newValue.IsActive = true;
        OnPropertyChanged(nameof(InactivePanel));
    }

    private void OnPanelActivationRequested(object? sender, EventArgs e)
    {
        if (sender is FilePanelViewModel panel)
        {
            ActivePanel = panel;
        }
    }

    private static string GetDefaultLeftPath() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string GetDefaultRightPath() =>
        Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } systemRoot
            ? systemRoot
            : Path.GetPathRoot(GetDefaultLeftPath()) ?? "/";
}
