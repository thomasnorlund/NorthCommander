using NorthCommander.Core.Archives;
using NorthCommander.Core.Services;
using NorthCommander.Core.Sync;
using NorthCommander.UI.Services;
using NorthCommander.Core.Transfers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NorthCommander.UI.ViewModels;

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
        PromptDialog.PropertyChanged += OnDialogPropertyChanged;
        Lister.PropertyChanged += OnDialogPropertyChanged;
        MultiRename.PropertyChanged += OnDialogPropertyChanged;
        SyncDirectories.PropertyChanged += OnDialogPropertyChanged;

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

    public PromptDialogViewModel PromptDialog { get; } = new();

    public ListerViewModel Lister { get; } = new();

    public MultiRenameViewModel MultiRename { get; } = new();

    public SyncDirectoriesViewModel SyncDirectories { get; } = new();

    /// <summary>True while an overlay dialog owns the keyboard; the panels are disabled.</summary>
    public bool IsModalOpen => TransferDialog.IsOpen || ConflictDialog.IsOpen || PromptDialog.IsOpen || Lister.IsOpen || MultiRename.IsOpen || SyncDirectories.IsOpen;

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

    /// <summary>F3: opens the Lister for the file under the cursor.</summary>
    [RelayCommand]
    private async Task ViewAsync()
    {
        if (IsModalOpen || ActivePanel.SelectedTab is not { SelectedItem: { IsDirectory: false, IsParentLink: false } item } tab)
        {
            return;
        }

        var path = item.FullPath;
        var isTemporary = false;
        if (tab.IsInArchive)
        {
            // Files inside a ZIP are extracted to a temporary file that the Lister deletes on close.
            try
            {
                path = await ZipArchiveService.ExtractToTempAsync(item.FullPath);
                isTemporary = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                tab.ErrorMessage = $"Cannot extract '{Path.GetFileName(item.FullPath)}': {ex.Message}";
                return;
            }
        }

        if (Lister.Open(path, item.FullPath, isTemporary) is { } error)
        {
            tab.ErrorMessage = $"Cannot view '{Path.GetFileName(item.FullPath)}': {error}";
        }
    }

    /// <summary>Ctrl+M: multi-rename the marked items (or the item under the cursor).</summary>
    [RelayCommand]
    private void MultiRenameFiles()
    {
        if (IsModalOpen)
        {
            return;
        }

        var tab = ActivePanel.SelectedTab;
        var paths = tab.GetSelectedPaths();
        if (paths.Count == 0 || RejectInArchive(tab, "Multi-rename"))
        {
            return;
        }

        MultiRename.Open(paths, folders =>
        {
            if (folders.Count > 0)
            {
                tab.ClearMarks();
                _ = RefreshTabsShowingAsync(folders);
            }
        });
    }

    /// <summary>Raised by F10 / Alt+X; the window closes itself.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Text of the NC-style command line above the F-key bar.</summary>
    [ObservableProperty]
    public partial string CommandText { get; set; } = string.Empty;

    [RelayCommand]
    private void Quit()
    {
        if (!IsModalOpen)
        {
            QuitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Runs the command line in the active panel's folder; <c>cd</c> changes the panel's folder.</summary>
    [RelayCommand]
    private async Task ExecuteCommandLineAsync()
    {
        var command = CommandText.Trim();
        var tab = ActivePanel.SelectedTab;
        if (command.Length == 0 || IsModalOpen || RejectInArchive(tab, "Running commands"))
        {
            return;
        }

        CommandText = string.Empty;
        if (CommandRunner.TryParseChangeDirectory(command, tab.CurrentPath, out var target))
        {
            await tab.LoadAsync(target);
            return;
        }

        var folder = tab.CurrentPath;
        var output = Path.Combine(Directory.CreateTempSubdirectory("NorthCommander_").FullName, "output.txt");
        int exitCode;
        try
        {
            tab.ErrorMessage = null;
            exitCode = await CommandRunner.RunAsync(command, folder, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ZipArchiveService.DeleteTemporaryFile(output);
            tab.ErrorMessage = $"Cannot run '{command}': {ex.Message}";
            return;
        }

        await RefreshTabsShowingAsync([folder]);
        if (new FileInfo(output).Length == 0)
        {
            ZipArchiveService.DeleteTemporaryFile(output);
            if (exitCode != 0)
            {
                tab.ErrorMessage = $"'{command}' exited with code {exitCode}.";
            }

            return;
        }

        // Console output is shown in the Lister; the temp file is deleted when it closes.
        File.AppendAllText(output, $"{Environment.NewLine}[{folder}> {command}  -  exit code {exitCode}]{Environment.NewLine}");
        if (!IsModalOpen && Lister.Open(output, $"{folder}> {command}", deleteOnClose: true) is { } error)
        {
            tab.ErrorMessage = error;
        }
        else if (IsModalOpen)
        {
            ZipArchiveService.DeleteTemporaryFile(output);
        }
    }

    [RelayCommand]
    private void Edit()
    {
        if (IsModalOpen || ActivePanel.SelectedTab is not { SelectedItem: { IsDirectory: false, IsParentLink: false } item } tab || RejectInArchive(tab, "Editing"))
        {
            return;
        }

        try
        {
            ShellIntegration.Edit(item.FullPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            tab.ErrorMessage = $"Cannot edit '{Path.GetFileName(item.FullPath)}': {ex.Message}";
        }
    }

    /// <summary>F7: asks for a folder name (a/b creates nested folders) and selects the new folder.</summary>
    [RelayCommand]
    private void MakeDirectory()
    {
        var tab = ActivePanel.SelectedTab;
        if (IsModalOpen || RejectInArchive(tab, "Creating folders"))
        {
            return;
        }

        PromptDialog.Open("Create directory (F7)", $"Create new folder in {tab.CurrentPath}:", string.Empty, "Create", isDanger: false, name =>
        {
            try
            {
                var created = FileSystemOperations.CreateDirectory(tab.CurrentPath, name);
                var first = Path.Combine(tab.CurrentPath, Path.GetRelativePath(tab.CurrentPath, created).Split(Path.DirectorySeparatorChar)[0]);
                _ = tab.LoadAsync(tab.CurrentPath, pathToSelect: first);
                _ = RefreshTabsShowingAsync([tab.CurrentPath], except: tab);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return ex.Message;
            }
        });
    }

    /// <summary>Ctrl+Shift+S: compare/synchronize the folders of the left and right panel.</summary>
    [RelayCommand]
    private void OpenSyncDirectories()
    {
        if (!IsModalOpen)
        {
            SyncDirectories.Open(LeftPanel.SelectedTab.CurrentPath, RightPanel.SelectedTab.CurrentPath, EnqueueSync);
        }
    }

    /// <summary>Queues one copy per arrow; existing targets are overwritten (the newer file wins).</summary>
    private string? EnqueueSync(IReadOnlyList<SyncItem> items, string leftRoot, string rightRoot)
    {
        var leftIsArchive = ZipArchiveService.IsArchivePath(leftRoot);
        var rightIsArchive = ZipArchiveService.IsArchivePath(rightRoot);
        var copies = items.Where(i => i.Direction is SyncDirection.LeftToRight or SyncDirection.RightToLeft).ToList();
        if (leftIsArchive && rightIsArchive && copies.Count > 0)
        {
            return "Synchronizing two ZIP archives with each other is not supported yet.";
        }

        try
        {
            var batch = new FileTransferBatch(FileConflictResolution.Overwrite);
            foreach (var item in copies)
            {
                var toRight = item.Direction == SyncDirection.LeftToRight;
                var source = (toRight ? item.Left : item.Right)!.FullPath;
                var destination = DirectoryComparer.PathFor(toRight ? rightRoot : leftRoot, item.RelativePath);
                var sourceIsArchive = toRight ? leftIsArchive : rightIsArchive;
                var destinationIsArchive = toRight ? rightIsArchive : leftIsArchive;
                var type = sourceIsArchive ? FileTransferType.Extract : destinationIsArchive ? FileTransferType.Pack : FileTransferType.Copy;
                if (!destinationIsArchive)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                }

                Transfers.Enqueue(new FileTransferTask(type, source, destination, batch));
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    [RelayCommand]
    private void Delete() => ShowDeleteDialog(ActivePanel.SelectedTab, ActivePanel.SelectedTab.GetSelectedPaths());

    [RelayCommand]
    private void Rename()
    {
        if (ActivePanel.SelectedTab is { SelectedItem: { IsParentLink: false } item } tab)
        {
            ShowRenameDialog(tab, item.FullPath);
        }
    }

    /// <summary>Current folder of the panel opposite to <paramref name="panel"/> (default F5/F6 target).</summary>
    public string GetOppositePath(FilePanelViewModel panel) =>
        (ReferenceEquals(panel, LeftPanel) ? RightPanel : LeftPanel).SelectedTab.CurrentPath;

    /// <summary>Opens the F5/F6 confirmation for <paramref name="sources"/> (keyboard, context menu, drag &amp; drop).</summary>
    public void ShowTransferDialog(TabViewModel sourceTab, FileTransferType type, IReadOnlyList<string> sources, string target)
    {
        if (IsModalOpen || sources.Count == 0)
        {
            return;
        }

        TransferDialog.Open(type, sources, target, t => EnqueueTransfers(sourceTab, type, sources, t));
    }

    public void ShowDeleteDialog(TabViewModel tab, IReadOnlyList<string> paths)
    {
        if (IsModalOpen || paths.Count == 0 || RejectInArchive(tab, "Delete"))
        {
            return;
        }

        var what = paths.Count == 1 ? $"'{Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))}'" : $"{paths.Count} items";
        PromptDialog.Open("Delete (F8)", $"Permanently delete {what}? This cannot be undone.", null, "Delete", isDanger: true, _input =>
        {
            _ = DeleteAsync(tab, paths);
            return null;
        });
    }

    public void ShowRenameDialog(TabViewModel tab, string path)
    {
        if (IsModalOpen || RejectInArchive(tab, "Rename"))
        {
            return;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        PromptDialog.Open("Rename (Shift+F6)", $"Rename '{name}' to:", name, "Rename", isDanger: false, newName =>
        {
            try
            {
                var newPath = FileSystemOperations.Rename(path, newName);
                _ = RefreshAfterRenameAsync(tab, newPath);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return ex.Message;
            }
        });
    }

    private async Task DeleteAsync(TabViewModel tab, IReadOnlyList<string> paths)
    {
        try
        {
            await FileSystemOperations.DeleteAsync(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tab.ErrorMessage = $"Delete failed: {ex.Message}";
        }

        await RefreshTabsShowingAsync(paths.Select(p => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(p))).OfType<string>().ToList());
    }

    private async Task RefreshAfterRenameAsync(TabViewModel tab, string newPath)
    {
        await tab.LoadAsync(tab.CurrentPath, pathToSelect: newPath);
        if (Path.GetDirectoryName(newPath) is { } folder)
        {
            await RefreshTabsShowingAsync([folder], except: tab);
        }
    }

    private Task RefreshTabsShowingAsync(IReadOnlyList<string> folders, TabViewModel? except = null) =>
        Task.WhenAll(LeftPanel.Tabs.Concat(RightPanel.Tabs)
            .Where(tab => !ReferenceEquals(tab, except) && folders.Any(tab.IsShowingDirectory))
            .Select(tab => tab.RefreshAsync()));

    private void OpenTransferDialog(FileTransferType type) =>
        ShowTransferDialog(ActivePanel.SelectedTab, type, ActivePanel.SelectedTab.GetSelectedPaths(), InactivePanel.SelectedTab.CurrentPath);

    private string? EnqueueTransfers(TabViewModel panel, FileTransferType type, IReadOnlyList<string> sources, string target)
    {
        try
        {
            var targetInArchive = ZipArchiveService.TryParse(target, out var targetArchive, out _);
            if (panel.IsInArchive || targetInArchive)
            {
                return EnqueueArchiveTransfers(panel, type, sources, target, targetInArchive ? targetArchive : null);
            }

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

    /// <summary>F5 out of a ZIP extracts, F5 into a ZIP packs; both run in the background queue.</summary>
    private string? EnqueueArchiveTransfers(TabViewModel panel, FileTransferType type, IReadOnlyList<string> sources, string target, string? targetArchive)
    {
        if (panel.IsInArchive && targetArchive is not null)
        {
            return "Copying from one ZIP archive into another is not supported yet.";
        }

        if (type == FileTransferType.Move)
        {
            return "Move is not supported for ZIP archives yet - use Copy (F5).";
        }

        var targetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Trim()));
        if (targetArchive is null && !Directory.Exists(targetPath))
        {
            return $"Target folder not found: {targetPath}";
        }

        foreach (var source in sources)
        {
            if (targetArchive is not null && (PathsEqual(source, targetArchive) || targetArchive.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                return "Cannot add an archive (or a folder containing it) to itself.";
            }
        }

        var operation = panel.IsInArchive ? FileTransferType.Extract : FileTransferType.Pack;
        var batch = new FileTransferBatch();
        foreach (var source in sources)
        {
            var destination = Path.Combine(targetPath, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
            Transfers.Enqueue(new FileTransferTask(operation, source, destination, batch));
        }

        panel.ClearMarks();
        return null;
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), StringComparison.Ordinal);

    private static bool RejectInArchive(TabViewModel tab, string action)
    {
        if (!tab.IsInArchive)
        {
            return false;
        }

        tab.ErrorMessage = $"{action} is not supported inside ZIP archives yet.";
        return true;
    }

    private void OnDialogPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TransferDialogViewModel.IsOpen) or nameof(ConflictDialogViewModel.IsOpen) or nameof(PromptDialogViewModel.IsOpen))
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

        await RefreshTabsShowingAsync(affected);

        if (task.Status == FileTransferStatus.Failed)
        {
            var verb = task.TaskType switch
            {
                FileTransferType.Move => "Move",
                FileTransferType.Extract => "Extraction",
                FileTransferType.Pack => "Packing",
                _ => "Copy",
            };
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
