using CommanderTom.Core.Transfers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommanderTom.UI.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(FilePanelViewModel leftPanel, FilePanelViewModel rightPanel, TransferQueueViewModel transfers)
    {
        LeftPanel = leftPanel;
        RightPanel = rightPanel;
        Transfers = transfers;

        LeftPanel.ActivationRequested += OnPanelActivationRequested;
        RightPanel.ActivationRequested += OnPanelActivationRequested;
        Transfers.TransferFinished += OnTransferFinished;

        ActivePanel = LeftPanel;
    }

    public FilePanelViewModel LeftPanel { get; }

    public FilePanelViewModel RightPanel { get; }

    public TransferQueueViewModel Transfers { get; }

    public TransferDialogViewModel TransferDialog { get; } = new();

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

    private void OpenTransferDialog(FileTransferType type)
    {
        if (TransferDialog.IsOpen)
        {
            return;
        }

        var sources = ActivePanel.GetSelectedPaths();
        if (sources.Count == 0)
        {
            return;
        }

        TransferDialog.Open(type, sources, InactivePanel.CurrentPath, target => EnqueueTransfers(type, sources, target));
    }

    private string? EnqueueTransfers(FileTransferType type, IReadOnlyList<string> sources, string target)
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

            foreach (var (source, destination) in plan)
            {
                Transfers.Enqueue(type, source, destination);
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return ex.Message;
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

        var refreshes = new[] { LeftPanel, RightPanel }
            .Where(panel => affected.Exists(panel.IsShowingDirectory))
            .Select(panel => panel.RefreshAsync());
        await Task.WhenAll(refreshes);

        if (task.Status == FileTransferStatus.Failed)
        {
            var verb = task.TaskType == FileTransferType.Move ? "Move" : "Copy";
            ActivePanel.ErrorMessage = $"{verb} of '{task.DisplayName}' failed: {task.ErrorMessage}";
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
